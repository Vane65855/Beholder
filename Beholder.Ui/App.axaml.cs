using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Beholder.Ui.Services;
using Beholder.Ui.ViewModels;
using Beholder.Ui.Views;
using Microsoft.Extensions.Logging;

namespace Beholder.Ui;

public partial class App : Application {
    private DaemonClient? _daemonClient;
    private DaemonStreamSubscriber? _streamSubscriber;

    public override void Initialize() {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted() {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            var loggerFactory = LoggerFactory.Create(builder => {
                builder.SetMinimumLevel(LogLevel.Information);
                builder.AddDebug();
            });

            _daemonClient = new DaemonClient(
                TimeProvider.System,
                loggerFactory.CreateLogger<DaemonClient>());

            _streamSubscriber = new DaemonStreamSubscriber(
                _daemonClient,
                loggerFactory.CreateLogger<DaemonStreamSubscriber>());

            var processStateService = new ProcessStateService(_streamSubscriber, _daemonClient);
            _streamSubscriber.OnConnected = ct => processStateService.SeedAsync(ct);
            var statusStripVm = new StatusStripViewModel(processStateService);
            var historicalChartLoader = new HistoricalChartLoader(_daemonClient);

            desktop.MainWindow = new MainWindow {
                DataContext = new MainWindowViewModel(
                    _daemonClient, processStateService, statusStripVm, historicalChartLoader),
            };

            // Fire-and-forget — both loops run indefinitely until shutdown
            _ = _daemonClient.ConnectAsync(CancellationToken.None);
            _ = _streamSubscriber.StartAsync(CancellationToken.None);

            desktop.ShutdownRequested += async (_, _) => {
                if (_streamSubscriber is not null)
                    await _streamSubscriber.DisposeAsync();
                if (_daemonClient is not null)
                    await _daemonClient.DisposeAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
