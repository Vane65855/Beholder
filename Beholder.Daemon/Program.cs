using Beholder.Core;
using Beholder.Daemon;
#if PLATFORM_WINDOWS
using Beholder.Daemon.Windows;
#endif

var builder = Host.CreateApplicationBuilder(args);

#if PLATFORM_WINDOWS
if (OperatingSystem.IsWindows()) {
    builder.Services.AddSingleton<IFlowSource, EtwFlowSource>();
}
#endif

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
