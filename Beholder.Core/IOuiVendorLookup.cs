namespace Beholder.Core;

/// <summary>
/// Resolves a MAC address to the vendor name registered to its OUI prefix in
/// the IEEE Organizationally Unique Identifier registry. Synchronous because
/// the implementation is an in-memory dictionary lookup, not I/O.
/// </summary>
public interface IOuiVendorLookup {
    /// <summary>
    /// Returns the vendor name registered to the OUI prefix (first 24 bits) of
    /// <paramref name="mac"/>, or <see langword="null"/> if the prefix is not
    /// in the loaded table or <paramref name="mac"/> is malformed (shorter than
    /// six hex characters after stripping <c>:</c> / <c>-</c> separators, or
    /// contains non-hex characters in the first six positions).
    /// </summary>
    string? GetVendor(string mac);
}
