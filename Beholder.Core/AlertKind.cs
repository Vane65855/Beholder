namespace Beholder.Core;

/// <summary>
/// Categorizes alerts emitted by the daemon. The three meaningful values map to the
/// alert taxonomy described in the architecture document; <see cref="Unknown"/> exists
/// only as a forward-compatible default for deserialized values.
/// </summary>
public enum AlertKind {
    /// <summary>Reserved default. Indicates an unrecognized or uninitialized value.</summary>
    Unknown = 0,

    /// <summary>A binary path accessed the network for the first time.</summary>
    NewProcess = 1,

    /// <summary>A tracked binary's SHA-256 differs from the previously stored value.</summary>
    HashChanged = 2,

    /// <summary>Hash chain verification detected a mismatch or gap.</summary>
    ChainError = 3,
}
