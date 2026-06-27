using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Beholder.Core;
using Beholder.Ui.Models;
using Beholder.Ui.Services;

namespace Beholder.Ui;

/// <summary>
/// Owns the system-tray icon and the close-to-tray behavior. When the
/// <see cref="UiPreferences.CloseToTray"/> preference is on, the window's close
/// (X) hides to the tray instead of exiting — the daemon (a Windows service)
/// keeps monitoring regardless. The tray icon (left-click or "Open Beholder")
/// restores the window; "Exit" really shuts the app down.
/// </summary>
/// <remarks>
/// Cross-platform Avalonia (<see cref="TrayIcon"/> / <see cref="NativeMenu"/> /
/// <see cref="Window.Closing"/>), so no <c>#if PLATFORM_WINDOWS</c> is needed —
/// the only OS-specific bit, the first-run hint, routes through the
/// platform-abstracted <see cref="INotificationService"/>. The preference is
/// read live on each close so a Settings toggle takes effect immediately.
/// <see cref="ShutdownMode"/> stays the default (OnLastWindowClose): hiding the
/// window keeps the app alive; an explicit close exits.
/// </remarks>
internal sealed class TrayController : IDisposable {
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly Window _window;
    private readonly IUiPreferencesStore _preferencesStore;
    private readonly INotificationService _notifications;
    private readonly TrayIcon _trayIcon;
    private bool _realExitRequested;

    public TrayController(
        IClassicDesktopStyleApplicationLifetime desktop,
        Window window,
        IUiPreferencesStore preferencesStore,
        INotificationService notifications
    ) {
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(preferencesStore);
        ArgumentNullException.ThrowIfNull(notifications);
        _desktop = desktop;
        _window = window;
        _preferencesStore = preferencesStore;
        _notifications = notifications;

        _trayIcon = BuildTrayIcon();
        _window.Closing += OnWindowClosing;
    }

    private TrayIcon BuildTrayIcon() {
        var open = new NativeMenuItem("Open Beholder");
        open.Click += (_, _) => RestoreWindow();
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => RequestExit();

        var trayIcon = new TrayIcon {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Beholder.Ui/Assets/beholder.ico"))),
            ToolTipText = "Beholder NMT",
            Menu = new NativeMenu { Items = { open, exit } },
            IsVisible = true,
        };
        trayIcon.Clicked += (_, _) => RestoreWindow();   // left-click

        if (Application.Current is { } app) {
            TrayIcon.SetIcons(app, new TrayIcons { trayIcon });
        }
        return trayIcon;
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e) {
        // A real exit (tray "Exit") or the opt-out preference → let it close.
        if (_realExitRequested || !_preferencesStore.Load().CloseToTray) return;

        e.Cancel = true;
        _window.Hide();
        ShowFirstRunHint();
    }

    private void ShowFirstRunHint() {
        var prefs = _preferencesStore.Load();
        if (prefs.TrayHintShown) return;
        _notifications.NotifyInfo(
            "Beholder is still running",
            "Monitoring continues in the background — right-click the tray icon to reopen or exit.");
        _preferencesStore.Save(prefs with { TrayHintShown = true });
    }

    private void RestoreWindow() {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void RequestExit() {
        _realExitRequested = true;
        _desktop.Shutdown();
    }

    public void Dispose() {
        _window.Closing -= OnWindowClosing;
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
    }
}
