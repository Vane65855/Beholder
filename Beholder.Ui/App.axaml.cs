using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Beholder.Core;
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
    private INotificationService? _notifications;

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

            // Single AvaloniaDispatcher instance shared by every VM that
            // marshals event-handler callbacks from background threads to the
            // UI thread. See IDispatcher / AvaloniaDispatcher / SyncDispatcher
            // for the abstraction's rationale.
            var dispatcher = new AvaloniaDispatcher();

            // OS-native notifications. Windows gets a real toast impl that
            // hits Action Center; everyone else gets a no-op until a real
            // Linux/macOS impl ships. The OperatingSystem.IsWindows() guard
            // inside the PLATFORM_WINDOWS block handles the edge case of
            // a Windows-built binary running under WSL/Mono. The Windows
            // impl lives inline in Beholder.Ui.Services per ADR 008 (UI
            // single-project policy) — the platform delta is too small to
            // justify a separate project.
#if PLATFORM_WINDOWS
            _notifications = OperatingSystem.IsWindows()
                ? new WindowsNotificationService(
                    loggerFactory.CreateLogger<WindowsNotificationService>())
                : new NoopNotificationService();
#else
            _notifications = new NoopNotificationService();
#endif

            var statusStripVm = new StatusStripViewModel(_processStateService, dispatcher);
            var historicalChartLoader = new HistoricalChartLoader(_daemonClient);

            _mainWindowVm = new MainWindowViewModel(
                _daemonClient, _processStateService, _streamSubscriber,
                statusStripVm, historicalChartLoader, dispatcher, _notifications);
            var mainWindow = new MainWindow { DataContext = _mainWindowVm };
            desktop.MainWindow = mainWindow;

            // Notification click → restore window + deep-link to alert. App
            // is the composition root, so this is the one place we can use
            // Dispatcher.UIThread directly (the IDispatcher abstraction is
            // for VMs to stay testable). The handler must marshal because
            // AlertActivated fires on the OS callback thread.
            _notifications.AlertActivated += seq => Dispatcher.UIThread.Post(() => {
                if (mainWindow.WindowState == WindowState.Minimized)
                    mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
                _ = _mainWindowVm.NavigateToAlertAsync(seq);
            });

            // Fire-and-forget — both loops run indefinitely until shutdown
            _ = _daemonClient.ConnectAsync(CancellationToken.None);
            _ = _streamSubscriber.StartAsync(CancellationToken.None);

            desktop.ShutdownRequested += async (_, _) => {
                // Dispose subscribers first so their -= handlers run against
                // still-live publishers, then tear down the publishers.
                _mainWindowVm?.Dispose();
                _processStateService?.Dispose();
                if (_notifications is IDisposable disposableNotifications)
                    disposableNotifications.Dispose();
                if (_streamSubscriber is not null)
                    await _streamSubscriber.DisposeAsync();
                if (_daemonClient is not null)
                    await _daemonClient.DisposeAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
