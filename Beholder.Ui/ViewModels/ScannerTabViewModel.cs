using System;

namespace Beholder.Ui.ViewModels;

internal partial class ScannerTabViewModel : ViewModelBase, IDisposable {
    // Stub placeholder for Phase 9 — Scanner. The feature set is
    // intentionally undefined (port scan / vulnerability lookup / anomaly
    // detection / network discovery are all candidates) and gets scoped
    // by an ADR before implementation begins. See docs/phases.md §6
    // (Phase 9). IDisposable scaffolded now (audit #37) so
    // MainWindowViewModel can dispose symmetrically once subscriptions land.
    public void Dispose() { }
}
