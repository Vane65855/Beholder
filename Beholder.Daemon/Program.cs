using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.GeoIp;
using Beholder.Daemon.Pipeline;
using Beholder.Daemon.Scanner;
using Beholder.Daemon.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
#if PLATFORM_WINDOWS
using Beholder.Daemon.Grpc;
using Beholder.Daemon.Windows;
using Beholder.Daemon.Windows.Scanner;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
#endif

#if PLATFORM_WINDOWS
// Phase 12.1: handle the one-shot service-control verbs (--install / --uninstall
// / --status) before building the host, then exit with their status code. The
// default verb, Run, falls through to start the daemon. See ADR 013.
if (OperatingSystem.IsWindows()) {
    switch (ServiceCommandLine.Parse(args)) {
        case ServiceCommand.Install: return WindowsServiceInstaller.Install();
        case ServiceCommand.Uninstall: return WindowsServiceInstaller.Uninstall();
        case ServiceCommand.Status: return WindowsServiceInstaller.Status();
        case ServiceCommand.Run: break;
    }
}
#endif

var builder = WebApplication.CreateBuilder(args);

// Phase 12.1: when launched by the Windows SCM, cooperate with the service
// lifetime (start/stop signals, no console) and route logging to the Windows
// Event Log. A no-op for a console `dotnet run`, so development is unaffected.
// See ADR 013.
builder.Host.UseWindowsService(options => options.ServiceName = "Beholder");

builder.Services.AddSingleton(TimeProvider.System);

// Phase 13.1.1: captures the daemon's start time so the Settings tab's
// MOTD strip can render an uptime label. Registered before any consumer
// so its StartedAt approximates "host builder ready, services about to
// begin work."
builder.Services.AddSingleton<IDaemonClock, DaemonClock>();

var databasePath = Path.Combine(DaemonPaths.WritableDataRoot, "beholder.db");
new DatabaseInitializer(databasePath).Initialize();

#if PLATFORM_WINDOWS
if (OperatingSystem.IsWindows()) {
    // ADR 014: serve the control RPC over a named pipe DACL'd to the
    // `beholder-users` group (SYSTEM + Administrators full control; an
    // INTERACTIVE fallback in dev when the group isn't present) instead of an
    // unauthenticated TCP localhost socket any local process could reach. The
    // UI dials the same pipe (IpcEndpoint.PipeName).
    // Build the DACL outside the options lambda: CA1416's platform-guard flow
    // analysis doesn't reach into lambdas, so calling the Windows-only
    // BeholderPipeSecurity.Create() out here — still inside the
    // OperatingSystem.IsWindows() guard — keeps the analyzer satisfied.
    var pipeSecurity = BeholderPipeSecurity.Create();
    builder.WebHost.UseNamedPipes(options => {
        // We supply an explicit DACL, so the transport's default "current user
        // only" restriction (which would lock the pipe to LocalSystem and shut
        // the UI out) must be turned off.
        options.CurrentUserOnly = false;
        options.PipeSecurity = pipeSecurity;
    });
    builder.WebHost.ConfigureKestrel(options => {
        options.ListenNamedPipe(Beholder.Protocol.IpcEndpoint.PipeName, listenOptions => {
            listenOptions.Protocols = HttpProtocols.Http2;
        });
    });

    // Phase 11.3: a full chain export can exceed gRPC's default 4 MB message
    // limit on a long-running install. Raise the cap to 64 MB on both
    // directions; the UI channel sets a matching MaxReceiveMessageSize.
    // Streaming export is the v2 escape hatch if real exports ever exceed this.
    const int MaxGrpcMessageBytes = 64 * 1024 * 1024;
    builder.Services.AddGrpc(options => {
        options.MaxReceiveMessageSize = MaxGrpcMessageBytes;
        options.MaxSendMessageSize = MaxGrpcMessageBytes;
    });

    builder.Services.AddSingleton<EtwFlowSource>();
    builder.Services.AddSingleton<IGeoIpResolver>(sp => {
        var logger = sp.GetRequiredService<ILogger<DbIpProvider>>();
        var mmdbPath = Path.Combine(DaemonPaths.ReadOnlyAssetRoot, "dbip-country-lite.mmdb");
        if (!File.Exists(mmdbPath)) {
            logger.LogWarning(
                "DB-IP MMDB file not found at {Path}, using null GeoIP resolver — all flow events will be tagged as Unknown",
                mmdbPath);
            return new NullGeoIpResolver();
        }
        return new DbIpProvider(mmdbPath, logger);
    });
    builder.Services.AddSingleton<IFlowSource>(sp => new GeoIpFlowSourceDecorator(
        sp.GetRequiredService<EtwFlowSource>(),
        sp.GetRequiredService<IGeoIpResolver>(),
        sp.GetRequiredService<ILogger<GeoIpFlowSourceDecorator>>()));
    builder.Services.AddSingleton<EtwDnsCache>();
    builder.Services.AddSingleton<IDnsCacheIngest>(sp => sp.GetRequiredService<EtwDnsCache>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<EtwDnsCache>());

    // Reverse-DNS fallback: decorator wrapping EtwDnsCache. IDnsCache
    // consumers (TrafficEngine) get the decorator transparently. See ADR 005.
    builder.Services.AddSingleton<IReverseDnsResolver, SystemReverseDnsResolver>();
    builder.Services.AddSingleton<ReverseDnsFallbackCache>(sp => new ReverseDnsFallbackCache(
        inner: sp.GetRequiredService<EtwDnsCache>(),
        ingest: sp.GetRequiredService<IDnsCacheIngest>(),
        backfill: sp.GetRequiredService<IDnsHostnameBackfill>(),
        resolver: sp.GetRequiredService<IReverseDnsResolver>(),
        state: sp.GetRequiredService<IHostnameResolutionSettingsState>(),
        timeProvider: sp.GetRequiredService<TimeProvider>(),
        logger: sp.GetRequiredService<ILogger<ReverseDnsFallbackCache>>()));
    builder.Services.AddSingleton<IDnsCache>(sp => sp.GetRequiredService<ReverseDnsFallbackCache>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ReverseDnsFallbackCache>());

    // SNI capture: closes the long-lived-connection hostname gap that DNS
    // observation + reverse-DNS fallback can't reach. Feeds resolved
    // (hostname, dest IP) pairs into IDnsCacheIngest. See ADR 006.
    builder.Services.AddSingleton<PktmonSniSource>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<PktmonSniSource>());

    builder.Services.AddSingleton<IFirewallController, WfpFirewallController>();

    builder.Services.AddSingleton(new ConnectionFactory(databasePath));
    builder.Services.AddSingleton<SqliteEventStore>();
    builder.Services.AddSingleton<IEventStore>(sp => sp.GetRequiredService<SqliteEventStore>());

    // Phase 13.1: in-memory cache of the most recent chain-verification
    // outcome. Written by both ChainIntegrityMonitor (periodic) and the
    // user-triggered VerifyChain RPC; read by the GetStorageStats RPC for
    // the Settings tab's Maintenance section.
    builder.Services.AddSingleton<IChainStatusCache, ChainStatusCache>();
    // Phase 11.2: chain verifier that anchors on the latest signed checkpoint
    // (written by CheckpointSignerService) to skip re-walking attested rows on
    // the periodic re-verify + the user-triggered VerifyChain RPC. Composes
    // IEventStore + ICheckpointStore + ICheckpointKeyProvider (the latter two
    // are registered in the Phase 11.1 block below; DI resolution is lazy so
    // registration order doesn't matter).
    builder.Services.AddSingleton<IChainVerifier, ChainVerifier>();
    // Phase 11.3: signed chain exporter. Builds a self-verifying JSON envelope
    // of the event log signed with the same Ed25519 key the checkpoints use.
    builder.Services.AddSingleton<IChainExporter, ChainExporter>();
    // Phase 13.1: SQLite per-table row counts + database file size, bundled
    // with the cached chain status, for the Settings tab's Data Storage
    // section. The factory lambda passes databasePath directly (rather than
    // resolving via DI) because the variable is already in scope and the
    // single-use primitive doesn't earn a Configure<T> binding.
    builder.Services.AddSingleton<IStorageStatsProvider>(sp => new SqliteStorageStatsProvider(
        connectionFactory: sp.GetRequiredService<ConnectionFactory>(),
        chainStatusCache: sp.GetRequiredService<IChainStatusCache>(),
        checkpointStore: sp.GetRequiredService<ICheckpointStore>(),
        daemonClock: sp.GetRequiredService<IDaemonClock>(),
        databasePath: databasePath));
    builder.Services.AddSingleton<SqliteFirewallRuleStore>();
    builder.Services.AddSingleton<IFirewallRuleStore>(sp => sp.GetRequiredService<SqliteFirewallRuleStore>());
    // Phase 13.6 (ADR 011): manual application-identity rules — NewProcessDetector's
    // Tier 2.5 fallback for unsigned / no-VersionInfo apps that ADR 007's automatic
    // logical-identity dedup can't cover (Squirrel auto-updaters, sideloaded tools).
    builder.Services.AddSingleton<SqliteAppIdentityRuleStore>(sp => new SqliteAppIdentityRuleStore(
        sp.GetRequiredService<ConnectionFactory>(),
        sp.GetRequiredService<TimeProvider>()));
    builder.Services.AddSingleton<IAppIdentityRuleStore>(sp => sp.GetRequiredService<SqliteAppIdentityRuleStore>());
    builder.Services.AddSingleton<SqliteAlertStore>();
    builder.Services.AddSingleton<IAlertStore>(sp => sp.GetRequiredService<SqliteAlertStore>());
    builder.Services.AddSingleton<SqliteProcessRegistry>();
    builder.Services.AddSingleton<IProcessRegistry>(sp => sp.GetRequiredService<SqliteProcessRegistry>());
    // Phase 7.5: Windows-only PE VersionInfo + Authenticode reader. Linux
    // daemon registers no IBinaryIdentityProvider; NewProcessDetector
    // accepts a nullable dependency and falls back to path-based dedup.
    // See ADR 007.
    builder.Services.AddSingleton<IBinaryIdentityProvider, WindowsBinaryIdentityProvider>();
    builder.Services.AddSingleton<SqliteTrafficStore>();
    builder.Services.AddSingleton<ITrafficStore>(sp => sp.GetRequiredService<SqliteTrafficStore>());
    builder.Services.AddSingleton<IDnsHostnameBackfill>(sp => sp.GetRequiredService<SqliteTrafficStore>());
    builder.Services.AddSingleton<SqliteDnsCacheStore>();
    builder.Services.AddSingleton<IDnsCacheStore>(sp => sp.GetRequiredService<SqliteDnsCacheStore>());

    // Phase 9.1 (ADR 009): LAN device storage.
    builder.Services.AddSingleton<SqliteLanDeviceStore>();
    builder.Services.AddSingleton<ILanDeviceStore>(sp => sp.GetRequiredService<SqliteLanDeviceStore>());
    var ouiPath = Path.Combine(DaemonPaths.ReadOnlyAssetRoot, "oui.csv");
    builder.Services.AddSingleton<IOuiVendorLookup>(sp => new OuiVendorLookup(
        ouiPath, sp.GetRequiredService<ILogger<OuiVendorLookup>>()));

    // Phase 9.2 (ADR 009): Windows ARP-based LAN device probe. Linux daemon
    // will register no ILanDeviceProbe — LanScannerService takes a nullable
    // probe per ADR 007's pattern and skips scanning when the dependency is
    // unresolved. Until the Linux daemon exists, the scanner registration
    // below lives in the same #if PLATFORM_WINDOWS block as its
    // dependencies (ILanDeviceStore, IOuiVendorLookup, IEventStore are all
    // Windows-only today); when the Linux daemon stabilizes, both the
    // 9.1 storage block and this 9.2 scheduler block can be hoisted outside
    // the #if since they are platform-agnostic.
    builder.Services.AddSingleton<ArpScanProbe>();

    // Phase 9.2.5 (ADR 009): mDNS + NetBIOS hostname-resolution probes.
    // Phase 9.2.6 (ADR 009): mDNS service-discovery (DNS-SD) browse probe.
    // All registered unconditionally; the kill-switch is honored at the
    // WindowsLanDeviceProbe registration below, which passes nulls when
    // ScannerOptions.EnableHostnameResolution is false. This keeps
    // WindowsLanDeviceProbe free of any ScannerOptions dependency
    // (Beholder.Daemon.Windows can't reference Beholder.Daemon — circular).
    builder.Services.AddSingleton<MdnsHostnameProbe>();
    builder.Services.AddSingleton<NetbiosHostnameProbe>();
    builder.Services.AddSingleton<MdnsServiceDiscoveryProbe>();
    builder.Services.AddSingleton<RouterDnsHostnameProbe>();
    // Probe priority: mDNS-PTR (device's own advertisement) > NetBIOS
    // (Windows / NAS) > router DNS (router's view of DHCP-supplied
    // hostnames). First non-null wins per-IP via HostnameResolutionLadder.
    builder.Services.AddSingleton<HostnameResolutionLadder>(sp => new HostnameResolutionLadder(
        probes: [
            sp.GetRequiredService<MdnsHostnameProbe>(),
            sp.GetRequiredService<NetbiosHostnameProbe>(),
            sp.GetRequiredService<RouterDnsHostnameProbe>(),
        ],
        logger: sp.GetRequiredService<ILogger<HostnameResolutionLadder>>()));

    builder.Services.AddSingleton<ILanDeviceProbe>(sp => {
        // Phase 13.4: probes are always injected — the IScannerSettingsState
        // toggle gates whether they run inside WindowsLanDeviceProbe.ScanAsync.
        // This pattern (live read at scan time) makes the toggle take effect
        // on the next scan tick AND sidesteps the construction-vs-StartAsync
        // ordering problem: the factory previously read the state singleton's
        // value at construction time, but SettingsOverridesService.StartAsync
        // applies persisted overrides AFTER all hosted services are
        // constructed but BEFORE any StartAsync runs. Reading at scan time
        // (which is always after StartAsync) lands the user's override
        // correctly.
        return new WindowsLanDeviceProbe(
            arpProbe: sp.GetRequiredService<ArpScanProbe>(),
            timeProvider: sp.GetRequiredService<TimeProvider>(),
            logger: sp.GetRequiredService<ILogger<WindowsLanDeviceProbe>>(),
            hostnameResolutionLadder: sp.GetRequiredService<HostnameResolutionLadder>(),
            mdnsServiceDiscoveryProbe: sp.GetRequiredService<MdnsServiceDiscoveryProbe>(),
            scannerSettings: sp.GetRequiredService<IScannerSettingsState>());
    });

    builder.Services.Configure<ScannerOptions>(builder.Configuration.GetSection("Scanner"));
    builder.Services.AddSingleton<LanScannerService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<LanScannerService>());
    builder.Services.Configure<TrafficStorageOptions>(
        builder.Configuration.GetSection("TrafficStorage"));
    builder.Services.Configure<RecordingOptions>(
        builder.Configuration.GetSection("Recording"));
    builder.Services.Configure<RollupOptions>(
        builder.Configuration.GetSection("Rollup"));
    builder.Services.Configure<DnsOptions>(
        builder.Configuration.GetSection("Dns"));
    builder.Services.Configure<SniOptions>(
        builder.Configuration.GetSection("Sni"));
    builder.Services.Configure<FirewallOptions>(
        builder.Configuration.GetSection("Firewall"));
    builder.Services.Configure<AlertOptions>(
        builder.Configuration.GetSection("Alert"));
    builder.Services.Configure<CheckpointOptions>(
        builder.Configuration.GetSection("Checkpoint"));
    builder.Services.Configure<DiagnosticsOptions>(
        builder.Configuration.GetSection("Diagnostics"));

    builder.Services.AddSingleton<IFirewallEnforcementState, FirewallEnforcementState>();
    builder.Services.AddHostedService<Beholder.Daemon.Pipeline.FirewallEnforcementService>();

    // Phase 13.2: runtime-mutable Settings state.
    // - State singletons seed from IOptions<T> defaults at construction.
    // - SqliteSettingsOverridesStore is the persistence layer.
    // - SettingsOverridesService runs FIRST among hosted services to apply
    //   persisted overrides to the singletons before any consumer reads them.
    builder.Services.AddSingleton<IRecordingSettingsState, RecordingSettingsState>();
    builder.Services.AddSingleton<IHostnameResolutionSettingsState, HostnameResolutionSettingsState>();
    builder.Services.AddSingleton<IAlertSettingsState, AlertSettingsState>();
    builder.Services.AddSingleton<IScannerSettingsState, ScannerSettingsState>();
    builder.Services.AddSingleton<ISettingsOverridesStore>(sp => new SqliteSettingsOverridesStore(
        sp.GetRequiredService<ConnectionFactory>(),
        sp.GetRequiredService<TimeProvider>()));
    builder.Services.AddHostedService<Beholder.Daemon.Pipeline.SettingsOverridesService>();

    // Phase 12.5: reconcile the OS firewall against the rule store (the
    // chain-audited source of truth) so manual wf.msc edits or orphan rules can't
    // drift silently — a first pass on startup, then every FirewallOptions
    // .ReconcileIntervalMinutes. As a BackgroundService its first pass runs after
    // every StartAsync (incl. SettingsOverridesService), so the master enforcement
    // state is final before it reads it.
    builder.Services.AddHostedService<Beholder.Daemon.Pipeline.FirewallReconciliationService>();

    // Broadcast service must be registered BEFORE the pipeline so its StartAsync
    // runs first and subscribes to ISnapshotBatchSource.OnSnapshotBatch before
    // the pipeline publishes its first tick.
    builder.Services.AddSingleton<BroadcastService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<BroadcastService>());

    builder.Services.AddSingleton<FlowEventPipeline>();
    builder.Services.AddSingleton<ISnapshotBatchSource>(sp => sp.GetRequiredService<FlowEventPipeline>());
    builder.Services.AddSingleton<IProcessFirstNetworkFlowSource>(
        sp => sp.GetRequiredService<FlowEventPipeline>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<FlowEventPipeline>());

    // Phase 7 alert pipeline. AlertEmitter wraps IEventStore.AppendAsync +
    // BroadcastService.BroadcastAlert; the three detectors below consume
    // it. NewProcessDetector subscribes to the engine's first-flow event;
    // BinaryHashMonitor + ChainIntegrityMonitor run on PeriodicTimer
    // loops governed by AlertOptions.
    builder.Services.AddSingleton<IAlertEmitter, AlertEmitter>();
    builder.Services.AddHostedService<NewProcessDetector>();
    builder.Services.AddHostedService<BinaryHashMonitor>();
    builder.Services.AddHostedService<ChainIntegrityMonitor>();

    // Phase 11.1: signed chain checkpoints. The signer service writes a
    // signed row into the `checkpoint` table on each tick when the chain
    // has advanced; the key provider lazy-loads or generates the Ed25519
    // keypair from `<dataDir>/keys/` on first use. Registered as a hosted
    // service alongside the rest of the alert pipeline — order doesn't
    // strictly matter (the signer is a writer, the alert pipeline is the
    // reader), but grouping it with ChainIntegrityMonitor keeps the
    // chain-integrity concerns colocated. See ADR 012 for the trust model.
    builder.Services.AddSingleton<ICheckpointKeyProvider>(sp => new FileCheckpointKeyProvider(
        keyFolder: Path.Combine(DaemonPaths.WritableDataRoot, "keys"),
        logger: sp.GetRequiredService<ILogger<FileCheckpointKeyProvider>>()));
    builder.Services.AddSingleton<ICheckpointStore>(sp => new SqliteCheckpointStore(
        sp.GetRequiredService<ConnectionFactory>()));
    builder.Services.AddHostedService<CheckpointSignerService>();

    // RollupService must start after FlowEventPipeline so the first rollup
    // tick runs against raw data the engine has already begun producing.
    builder.Services.AddSingleton<RollupService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RollupService>());

    // Phase 12.3: opt-in resource sampler for performance soaks (off by default).
    builder.Services.AddHostedService<Beholder.Daemon.Pipeline.DiagnosticSampler>();

    builder.Services.AddSingleton<BeholderLocalService>();
}
#endif

var app = builder.Build();

#if PLATFORM_WINDOWS
if (OperatingSystem.IsWindows()) {
    app.MapGrpcService<BeholderLocalService>();
    app.Logger.LogInformation("Local control pipe '{Pipe}': {Mode}",
        Beholder.Protocol.IpcEndpoint.PipeName,
        BeholderPipeSecurity.BeholderUsersGroupExists()
            ? "restricted to the 'beholder-users' group"
            : "no 'beholder-users' group found — admitting INTERACTIVE users (run --install to restrict)");
}
#endif

app.Run();

// The service-control verbs above return their own exit codes; a normal host
// run returns 0 on clean shutdown. An explicit terminal return is required
// because those early returns make this entry point int-returning.
return 0;
