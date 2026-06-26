namespace Beholder.Core;

/// <summary>
/// The synthetic process-identity sentinels the platform process-path resolver
/// emits when an ETW-supplied PID can't be mapped to a real on-disk binary, plus
/// the policies that decide where they may appear. Centralised here so the
/// producer (the platform <c>ProcessPathResolver</c>) and every consumer (the
/// new-process detector, the Firewall and Traffic views) agree on the exact
/// strings rather than scattering literals.
/// </summary>
/// <remarks>
/// The two consumers deliberately apply <em>different</em> policies, so this type
/// exposes the policy as named predicates rather than a single "is excluded"
/// flag: an action surface (firewall rules, new-process alerts) can't act on
/// either sentinel, whereas a pure visibility surface (the Traffic list) still
/// shows kernel-attributable <see cref="System"/> traffic and hides only the
/// genuinely unidentifiable <see cref="Unknown"/>.
/// </remarks>
public static class ProcessSentinels {
    /// <summary>The kernel pseudo-process (PID 4). Surfaced by ETW; has no targetable on-disk binary.</summary>
    public const string System = "System";

    /// <summary>
    /// Placeholder for a PID whose executable path couldn't be resolved — the
    /// process exited between the ETW event and the lookup, or its
    /// <c>MainModule</c> was access-denied.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>True when <paramref name="processPath"/> is the unresolved-PID placeholder.</summary>
    public static bool IsUnknown(string processPath) =>
        string.Equals(processPath, Unknown, StringComparison.Ordinal);

    /// <summary>
    /// True when <paramref name="processPath"/> is a sentinel that does not
    /// correspond to a rule-targetable on-disk binary (<see cref="System"/> or
    /// <see cref="Unknown"/>) — i.e. nothing an action surface can block, rule,
    /// or meaningfully raise a "first seen" alert for.
    /// </summary>
    public static bool IsNonTargetable(string processPath) =>
        IsUnknown(processPath) || string.Equals(processPath, System, StringComparison.Ordinal);
}
