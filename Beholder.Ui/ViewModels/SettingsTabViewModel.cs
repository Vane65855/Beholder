using System;

namespace Beholder.Ui.ViewModels;

internal partial class SettingsTabViewModel : ViewModelBase, IDisposable {
    // Stub placeholder for Phase 13 — the dedicated final UI phase that
    // surfaces every user-controllable configuration toggle (retention
    // preset, FilterSelfTraffic, the three DnsOptions toggles, EnableSniCapture,
    // plus whatever Phases 6.4 / 7 / 8 / 9 / 10 add) behind a Windows-11-style
    // sidebar + content-pane shell. See docs/phases.md §6 (Phase 13) for the
    // sub-phase breakdown and quality gate. IDisposable scaffolded now
    // (audit #37) so MainWindowViewModel can dispose symmetrically once
    // subscriptions land.
    public void Dispose() { }
}
