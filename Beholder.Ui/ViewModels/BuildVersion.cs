using System;
using System.Reflection;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// The running build's identity for display: the product version (e.g. "0.1.1")
/// and the short git commit it was built from (e.g. "9835198"). Both come from the
/// entry assembly's <see cref="AssemblyInformationalVersionAttribute"/>, which the
/// .NET SDK stamps as "version+full-sha" at build time. The parse is isolated in
/// <see cref="Parse"/> so it can be unit-tested without a real assembly.
/// </summary>
internal sealed class BuildVersion {
    private const int ShortCommitLength = 7;

    /// <summary>The product version, e.g. "0.1.1". Never empty.</summary>
    public string DisplayVersion { get; }

    /// <summary>
    /// Short git commit the build came from, e.g. "9835198", or "" when the build
    /// carries no commit metadata (a bare <c>dotnet build</c> outside a git tree).
    /// </summary>
    public string ShortCommit { get; }

    /// <summary>
    /// Status-strip build tag: "DEV-&lt;commit&gt;", or "DEV-local" for a build with
    /// no commit metadata.
    /// </summary>
    public string DeviceLabel => ShortCommit.Length == 0 ? "DEV-local" : $"DEV-{ShortCommit}";

    private BuildVersion(string displayVersion, string shortCommit) {
        DisplayVersion = displayVersion;
        ShortCommit = shortCommit;
    }

    public static BuildVersion FromRunningAssembly() {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return Parse(informational, assembly.GetName().Version);
    }

    /// <summary>
    /// Parses the SDK informational version ("version+sha") into a display version
    /// and short commit. Falls back to the three-part assembly version (and an empty
    /// commit) when no usable "+sha" metadata is present.
    /// </summary>
    internal static BuildVersion Parse(string? informationalVersion, Version? assemblyVersion) {
        var fallbackVersion = assemblyVersion is null
            ? "0.0.0"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(0, assemblyVersion.Build)}";

        if (string.IsNullOrWhiteSpace(informationalVersion))
            return new BuildVersion(fallbackVersion, "");

        var plus = informationalVersion.IndexOf('+');
        if (plus < 0)
            return new BuildVersion(informationalVersion, "");

        var version = plus == 0 ? fallbackVersion : informationalVersion[..plus];
        var sha = informationalVersion[(plus + 1)..];
        var shortCommit = sha.Length <= ShortCommitLength ? sha : sha[..ShortCommitLength];
        return new BuildVersion(version, shortCommit);
    }
}
