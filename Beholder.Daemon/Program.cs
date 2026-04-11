using Beholder.Core;
using Beholder.Daemon;
using Beholder.Daemon.GeoIp;
using Beholder.Daemon.Pipeline;
using Microsoft.Extensions.Logging;
#if PLATFORM_WINDOWS
using Beholder.Daemon.Windows;
#endif

var builder = Host.CreateApplicationBuilder(args);

#if PLATFORM_WINDOWS
if (OperatingSystem.IsWindows()) {
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
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<IFirewallController, WfpFirewallController>();
    builder.Services.AddHostedService<FlowEventPipeline>();
}
#endif

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
