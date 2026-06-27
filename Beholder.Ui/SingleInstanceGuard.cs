using System;
#if PLATFORM_WINDOWS
using System.Runtime.InteropServices;
using System.Threading;
#endif

namespace Beholder.Ui;

/// <summary>
/// Ensures one UI instance per logon session. The first instance owns a named
/// mutex (<see cref="IsPrimary"/> true) and listens for an "activate" signal; a
/// later launch finds the mutex held, signals the primary to surface its window
/// via <see cref="SignalActivation"/>, then exits before building any UI — so a
/// duplicate never adds a second window or tray icon. Windows-only; elsewhere it
/// degrades to always-primary (the Linux UI is not shipped).
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable {
    private const string DefaultScope = "Beholder.Ui";

#if PLATFORM_WINDOWS
    private const uint AsfwAny = 0xFFFFFFFF;   // ASFW_ANY — any process may foreground

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _activate;
    private readonly Thread? _listener;
    private volatile bool _disposed;

    private SingleInstanceGuard(Mutex mutex, bool isPrimary, string eventName) {
        _mutex = mutex;
        IsPrimary = isPrimary;
        if (isPrimary) {
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            _listener = new Thread(ListenForActivation) {
                IsBackground = true,
                Name = "BeholderSingleInstanceActivation",
            };
            _listener.Start();
        } else {
            EventWaitHandle.TryOpenExisting(eventName, out _activate);
        }
    }

    /// <summary>True when this process is the first/primary instance.</summary>
    public bool IsPrimary { get; }

    /// <summary>Raised on a background thread when another instance asks to be activated (primary only).</summary>
    public event Action? Activated;

    /// <summary>Acquires the per-session guard. <paramref name="scope"/> names the mutex/event (override only in tests).</summary>
    public static SingleInstanceGuard Acquire(string scope = DefaultScope) {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{scope}.SingleInstance", out var createdNew);
        return new SingleInstanceGuard(mutex, createdNew, $@"Local\{scope}.Activate");
    }

    /// <summary>Secondary path: ask the running primary to surface its window. The caller should then exit.</summary>
    public void SignalActivation() {
        if (_activate is null) return;
        // The primary is a background process; grant it the right to foreground
        // its window before we signal, past the OS foreground-stealing block.
        AllowSetForegroundWindow(AsfwAny);
        _activate.Set();
    }

    private void ListenForActivation() {
        while (!_disposed) {
            try {
                _activate!.WaitOne();
            } catch (ObjectDisposedException) {
                return;
            }
            if (_disposed) return;
            Activated?.Invoke();
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        if (_activate is not null) {
            try { _activate.Set(); } catch (ObjectDisposedException) { }   // wake the listener so it exits
            _listener?.Join(TimeSpan.FromSeconds(1));
            _activate.Dispose();
        }
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    // DllImport (not LibraryImport) so the UI project needn't enable unsafe
    // code project-wide for a single trivial P/Invoke.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
#else
    private SingleInstanceGuard() { }
    public bool IsPrimary => true;
    public event Action? Activated { add { } remove { } }
    public static SingleInstanceGuard Acquire(string scope = DefaultScope) => new();
    public void SignalActivation() { }
    public void Dispose() { }
#endif
}
