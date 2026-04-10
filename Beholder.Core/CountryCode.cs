namespace Beholder.Core;

/// <summary>
/// An ISO 3166-1 alpha-2 country code, normalized to uppercase. Two synthetic sentinel
/// values exist: <see cref="Local"/> for private/reserved address ranges and
/// <see cref="Unknown"/> for addresses the GeoIP database cannot resolve.
/// </summary>
public readonly record struct CountryCode {
    /// <summary>The two-character code, always uppercase for real codes.</summary>
    public string Value { get; } = "";

    private CountryCode(string value, bool validated) {
        if (!validated) {
            Value = value;
            return;
        }
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 2) throw new ArgumentException("Country code must be exactly 2 characters.", nameof(value));
        if (!char.IsAsciiLetter(value[0]) || !char.IsAsciiLetter(value[1])) {
            throw new ArgumentException("Country code must contain only ASCII letters.", nameof(value));
        }
        Value = value.ToUpperInvariant();
    }

    /// <summary>Sentinel value for private/reserved address ranges that bypass MMDB lookup.</summary>
    public static CountryCode Local { get; } = new("--", validated: false);

    /// <summary>Sentinel value for addresses not present in the GeoIP database.</summary>
    public static CountryCode Unknown { get; } = new("??", validated: false);

    /// <summary>True when this code is a real country code, not a synthetic sentinel.</summary>
    public bool IsReal => Value != Local.Value && Value != Unknown.Value;

    /// <summary>
    /// Creates a country code from an ISO 3166-1 alpha-2 string. Input is validated for
    /// length and ASCII-letter content, then normalized to uppercase.
    /// </summary>
    public static CountryCode FromAlpha2(string code) => new(code, validated: true);

    /// <inheritdoc />
    public override string ToString() => Value;
}
