using Avalonia;
using Avalonia.Media;
using System;

namespace Beholder.Ui;

sealed class Program {
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions {
                DefaultFamilyName = "avares://Beholder.Ui/Assets/Fonts#Inter",
            })
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}
