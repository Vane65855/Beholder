namespace Beholder.Core;

/// <summary>
/// A persisted firewall rule that the daemon enforces via the platform's
/// <c>IFirewallController</c>. Rules are uniquely identified by
/// (<see cref="ProcessPath"/>, <see cref="Direction"/>) in storage.
/// </summary>
public sealed record FirewallRule {
    /// <summary>Database row ID. Zero indicates a rule that has not yet been persisted.</summary>
    public int Id { get; }

    /// <summary>Full filesystem path of the binary the rule applies to.</summary>
    public string ProcessPath { get; }

    /// <summary>Whether the rule applies to inbound or outbound traffic.</summary>
    public Direction Direction { get; }

    /// <summary>Action the rule takes on a matched flow.</summary>
    public FirewallAction Action { get; }

    /// <summary>Origin of the rule.</summary>
    public RuleSource Source { get; }

    /// <summary>Wall-clock timestamp at which the rule was created.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Wall-clock timestamp at which the rule was last modified.</summary>
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>Constructs a validated firewall rule.</summary>
    public FirewallRule(
        int id,
        string processPath,
        Direction direction,
        FirewallAction action,
        RuleSource source,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);

        Id = id;
        ProcessPath = processPath;
        Direction = direction;
        Action = action;
        Source = source;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }
}
