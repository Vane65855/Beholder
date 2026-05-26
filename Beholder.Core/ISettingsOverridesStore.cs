namespace Beholder.Core;

/// <summary>
/// Persistence layer for user-mutated settings overrides. The daemon's
/// <c>appsettings.json</c> remains the source of *defaults*; this store holds
/// the overrides the user has applied via the Settings tab. Keys are dotted
/// section strings (see <see cref="SettingsKeys"/>) and values are JSON-encoded
/// scalars — booleans today, plus integers / strings / enums as future
/// sections add new knob types.
/// </summary>
public interface ISettingsOverridesStore {
    /// <summary>
    /// Returns the persisted JSON value for <paramref name="name"/>, or null
    /// when no override has been set. The caller deserializes — this store is
    /// type-agnostic by design.
    /// </summary>
    Task<string?> GetAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Persists (or replaces) an override for <paramref name="name"/>.
    /// <paramref name="valueJson"/> is stored verbatim — the caller serializes.
    /// </summary>
    Task UpsertAsync(string name, string valueJson, CancellationToken cancellationToken);

    /// <summary>
    /// Returns every persisted override as a dictionary keyed on setting name.
    /// Called once at daemon startup by <c>SettingsOverridesService</c> to
    /// apply persisted state to the in-memory singletons before any consumer
    /// reads them.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ListAllAsync(CancellationToken cancellationToken);
}
