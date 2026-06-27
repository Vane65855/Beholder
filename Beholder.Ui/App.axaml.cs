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
    private TrayController? _trayController;
    private readonly SingleInstanceGuard? _singleInstance;

    // Avalonia's design-time tooling may construct App parameterless; the real
    // run goes through the factory in Program.BuildAvaloniaApp with the guard.
    public App() : this(null) { }
    internal App(SingleInstanceGuard? singleInstance) => _singleInstance = singleInstance;

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

            var shellOpener = new ShellOpener();
            // The clipboard writer needs a TopLevel to reach the OS clipboard,
            // but TopLevel is the MainWindow we're about to construct. Pass a
            // Func that returns the live window — the lambda captures the
            // local by reference, so updating `mainWindow` after this point
            // makes the Func resolve to the constructed window when invoked.
            MainWindow? mainWindowRef = null;
            var clipboardWriter = new AvaloniaClipboardWriter(() => mainWindowRef);
            // Phase 13.6: file picker for Application Identity Overrides.
            // Same Func<MainWindow?> capture-by-reference pattern as the
            // clipboard writer above — resolves to the live window when the
            // picker is invoked from a VM command.
            var filePicker = new AvaloniaFilePicker(() => mainWindowRef);
            // Phase 11.3: file writer persists the signed chain-export bytes
            // to the user-chosen path (Settings → Maintenance → Export chain).
            var fileWriter = new FileWriter();
            // UI-local preferences (close-to-tray, etc.) — the only client-side
            // persistence the UI has, deliberately NOT a daemon setting (ADR 010).
            var uiPreferencesStore = new JsonUiPreferencesStore(
                loggerFactory.CreateLogger<JsonUiPreferencesStore>());

            _mainWindowVm = new MainWindowViewModel(
                _daemonClient, _processStateService, _streamSubscriber,
                statusStripVm, historicalChartLoader, dispatcher, _notifications,
                shellOpener, clipboardWriter, filePicker, fileWriter, uiPreferencesStore);
            var mainWindow = new MainWindow { DataContext = _mainWindowVm };
            mainWindowRef = mainWindow;
            desktop.MainWindow = mainWindow;

            // Close-to-tray: when the preference is on, the window's X hides it to
            // the system tray (the daemon service keeps monitoring) rather than
            // exiting; the tray menu restores or exits. See TrayController.
            _trayController = new TrayController(desktop, mainWindow, uiPreferencesStore, _notifications);

            // Login auto-start (the installer's Startup shortcut passes --tray)
            // comes up hidden in the tray instead of popping the window on every
            // sign-in. Avalonia auto-shows MainWindow after this method returns,
            // so start it minimized + unfocused to limit the flash, then hide it
            // on the next dispatcher turn. ShutdownMode stays OnLastWindowClose —
            // a hidden window keeps the app alive, the invariant TrayController's
            // close-to-tray already relies on.
            if (StartupOptions.StartMinimizedToTray(desktop.Args)) {
                mainWindow.WindowState = WindowState.Minimized;
                mainWindow.ShowActivated = false;
                Dispatcher.UIThread.Post(mainWindow.Hide);
            }

            // Show + un-minimize + focus the main window — shared by the
            // notification deep-link and the single-instance signal (a second
            // launch surfaces this window instead of starting a new app).
            void SurfaceMainWindow() {
                mainWindow.Show();   // restore if it was hidden to the tray
                if (mainWindow.WindowState == WindowState.Minimized)
                    mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
            }

            // Notification click → restore window + deep-link to alert. App
            // is the composition root, so this is the one place we can use
            // Dispatcher.UIThread directly (the IDispatcher abstraction is
            // for VMs to stay testable). The handler must marshal because
            // AlertActivated fires on the OS callback thread.
            _notifications.AlertActivated += seq => Dispatcher.UIThread.Post(() => {
                SurfaceMainWindow();
                _ = _mainWindowVm.NavigateToAlertAsync(seq);
            });

            // A second launch (e.g. double-clicking the Desktop icon) signals
            // this instance instead of starting a duplicate process — bring the
            // window forward. Activated fires on the guard's listener thread.
            if (_singleInstance is { } guard)
                guard.Activated += () => Dispatcher.UIThread.Post(SurfaceMainWindow);

            // Fire-and-forget — both loops run indefinitely until shutdown
            _ = _daemonClient.ConnectAsync(CancellationToken.None);
            _ = _streamSubscriber.StartAsync(CancellationToken.None);

            desktop.ShutdownRequested += async (_, _) => {
                // Dispose subscribers first so their -= handlers run against
                // still-live publishers, then tear down the publishers.
                _trayController?.Dispose();
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
