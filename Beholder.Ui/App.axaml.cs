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
    private ProcessStateService? _processStateService;
    private MainWindowViewModel? _mainWindowVm;

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
                TimeProvider.System,
                loggerFactory.CreateLogger<DaemonStreamSubscriber>());

            _processStateService = new ProcessStateService(
                _streamSubscriber, _daemonClient, TimeProvider.System);
            _streamSubscriber.OnConnected = ct => _processStateService.SeedAsync(ct);
            var statusStripVm = new StatusStripViewModel(_processStateService);
            var historicalChartLoader = new HistoricalChartLoader(_daemonClient);

            _mainWindowVm = new MainWindowViewModel(
                _daemonClient, _processStateService, _streamSubscriber,
                statusStripVm, historicalChartLoader);
            desktop.MainWindow = new MainWindow { DataContext = _mainWindowVm };

            // Fire-and-forget — both loops run indefinitely until shutdown
            _ = _daemonClient.ConnectAsync(CancellationToken.None);
            _ = _streamSubscriber.StartAsync(CancellationToken.None);

            desktop.ShutdownRequested += async (_, _) => {
                // Dispose subscribers first so their -= handlers run against
                // still-live publishers, then tear down the publishers.
                _mainWindowVm?.Dispose();
                _processStateService?.Dispose();
                if (_streamSubscriber is not null)
                    await _streamSubscriber.DisposeAsync();
                if (_daemonClient is not null)
                    await _daemonClient.DisposeAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
