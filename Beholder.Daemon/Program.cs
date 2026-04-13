using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.GeoIp;
using Beholder.Daemon.Pipeline;
using Beholder.Daemon.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    builder.Services.AddSingleton<IDnsCache>(sp => sp.GetRequiredService<EtwDnsCache>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<EtwDnsCache>());
    builder.Services.AddSingleton<IFirewallController, WfpFirewallController>();

    builder.Services.AddSingleton(new ConnectionFactory(databasePath));
    builder.Services.AddSingleton<SqliteEventStore>();
    builder.Services.AddSingleton<IEventStore>(sp => sp.GetRequiredService<SqliteEventStore>());
    builder.Services.AddSingleton<SqliteFirewallRuleStore>();
    builder.Services.AddSingleton<IFirewallRuleStore>(sp => sp.GetRequiredService<SqliteFirewallRuleStore>());
    builder.Services.AddSingleton<SqliteAlertStore>();
    builder.Services.AddSingleton<IAlertStore>(sp => sp.GetRequiredService<SqliteAlertStore>());
    builder.Services.AddSingleton<SqliteTrafficStore>();
    builder.Services.AddSingleton<ITrafficStore>(sp => sp.GetRequiredService<SqliteTrafficStore>());
    builder.Services.AddSingleton<SqliteDnsCacheStore>();
    builder.Services.AddSingleton<IDnsCacheStore>(sp => sp.GetRequiredService<SqliteDnsCacheStore>());
    builder.Services.AddSingleton<TrafficStorageOptions>();

    // Broadcast service must be registered BEFORE the pipeline so its StartAsync
    // runs first and subscribes to ISnapshotBatchSource.OnSnapshotBatch before
    // the pipeline publishes its first tick.
    builder.Services.AddSingleton<BroadcastService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<BroadcastService>());

    builder.Services.AddSingleton<FlowEventPipeline>();
    builder.Services.AddSingleton<ISnapshotBatchSource>(sp => sp.GetRequiredService<FlowEventPipeline>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<FlowEventPipeline>());

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
