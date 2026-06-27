using Avalonia;
using Avalonia.Media;
using System;

namespace Beholder.Ui;

sealed class Program {
    [STAThread]
    public static void Main(string[] args) {
        using var singleInstance = SingleInstanceGuard.Acquire();
        if (!singleInstance.IsPrimary) {
            // A duplicate launch surfaces the already-running instance, then exits
            // — no second window or tray icon.
            singleInstance.SignalActivation();
            return;
        }
        BuildAvaloniaApp(singleInstance).StartWithClassicDesktopLifetime(args);
    }

    // Avalonia's design-time previewer calls this parameterless overload; it gets
    // no guard since single-instance is a runtime-only concern.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(singleInstance: null);

    private static AppBuilder BuildAvaloniaApp(SingleInstanceGuard? singleInstance)
        => AppBuilder.Configure(() => new App(singleInstance))
            .UsePlatformDetect()
            .With(new FontManagerOptions {
                DefaultFamilyName = "avares://Beholder.Ui/Assets/Fonts#Inter",
            })
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}
