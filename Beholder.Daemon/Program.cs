using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.GeoIp;
using Beholder.Daemon.Pipeline;
using Beholder.Daemon.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
#if PLATFORM_WINDOWS
using Beholder.Daemon.Grpc;
using Beholder.Daemon.Windows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
#endif

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);

var databasePath = Path.Combine(AppContext.BaseDirectory, "data", "beholder.db");
new DatabaseInitializer(databasePath).Initialize();

#if PLATFORM_WINDOWS
if (OperatingSystem.IsWindows()) {
    builder.WebHost.ConfigureKestrel(options => {
        options.ListenLocalhost(50051, listenOptions => {
            listenOptions.Protocols = HttpProtocols.Http2;
        });
    });

    builder.Services.AddGrpc();

    builder.Services.AddSingleton<EtwFlowSource>();
    builder.Services.AddSingleton<IGeoIpResolver>(sp => {
        var logger = sp.GetRequiredService<ILogger<DbIpProvider>>();
        var mmdbPath = Path.Combine(AppContext.BaseDirectory, "data", "dbip-country-lite.mmdb");
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
        options: sp.GetRequiredService<IOptionsMonitor<DnsOptions>>(),
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
    builder.Services.AddSingleton<SqliteFirewallRuleStore>();
    builder.Services.AddSingleton<IFirewallRuleStore>(sp => sp.GetRequiredService<SqliteFirewallRuleStore>());
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

    builder.Services.AddSingleton<IFirewallEnforcementState, FirewallEnforcementState>();
    builder.Services.AddHostedService<Beholder.Daemon.Pipeline.FirewallEnforcementService>();

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

    // RollupService must start after FlowEventPipeline so the first rollup
    // tick runs against raw data the engine has already begun producing.
    builder.Services.AddSingleton<RollupService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RollupService>());

    builder.Services.AddSingleton<BeholderLocalService>();
}
#endif

builder.Services.AddHostedService<Worker>();

var app = builder.Build();

#if PLATFORM_WINDOWS
if (OperatingSystem.IsWindows()) {
    app.MapGrpcService<BeholderLocalService>();
}
#endif

app.Run();
