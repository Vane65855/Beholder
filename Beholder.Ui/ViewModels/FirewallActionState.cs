using Beholder.Protocol.Local;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// Three-state action for the per-direction pill: <c>Default</c> means "no
/// Beholder-managed rule exists" (Windows default = allow). Distinct from
/// <see cref="FirewallAction"/> in the wire protocol, which has only
/// Allow/Block — the absence of a rule is encoded in proto by simply not
/// returning one.
/// </summary>
internal enum FirewallActionState {
    Default = 0,
    Allow = 1,
    Block = 2,
}
