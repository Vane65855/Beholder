using System;
using System.Collections.Generic;
using System.Reflection;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// Static facts surfaced by the Settings tab's About section: version,
/// build identifier, license, and third-party attributions. Captured once
/// at construction; no observable properties needed because none of this
/// changes during the application's lifetime.
/// </summary>
internal sealed class AboutInfo {
    /// <summary>
    /// Multi-line ASCII art for the project's logo, rendered in the About
    /// section's header. A big circle (the all-seeing eye, on-brand for a
    /// "Beholder" product) containing a bigger inner almond-shaped iris
    /// with a vertical-slit pupil — a cat eye. Seven lines tall, 21
    /// characters wide; designed to render correctly in any monospace
    /// font. Uses Unicode box-drawing characters: ╭╮╰╯ for curved outer
    /// corners, ╱╲ for "shoulder" diagonals, │ for straight sides, ▔▁ for
    /// the iris's thin top/bottom curves, and ┃ (heavy vertical) for the
    /// pupil slit.
    /// </summary>
    public const string AsciiEyeArt =
        "      ╭─────────╮    \n" +
        "     ╱           ╲   \n" +
        "    │  ╱▔▔▔▔▔▔▔╲  │  \n" +
        "    │  │   ┃   │  │  \n" +
        "    │  ╲▁▁▁▁▁▁▁╱  │  \n" +
        "     ╲           ╱   \n" +
        "      ╰─────────╯    \n";

    /// <summary>e.g. "0.13.1.0".</summary>
    public string Version { get; }

    /// <summary>
    /// Informational version string from <see cref="AssemblyInformationalVersionAttribute"/>
    /// when the build sets it (typically a git-derived value like
    /// "0.13.1+abc1234"), otherwise the literal "(dev build)". Wiring an
    /// actual git-describe value into the attribute is a separate small
    /// task; this class just surfaces whatever the assembly already carries.
    /// </summary>
    public string BuildInfo { get; }

    public string LicenseLabel => "AGPL-3.0-or-later";
    public string LicenseUrl => "https://www.gnu.org/licenses/agpl-3.0.html";
    public string ProjectName => "Beholder NMT";
    public string ProjectUrl => "https://github.com/Vane65855/Beholder";

    /// <summary>
    /// One row per upstream attribution required by an in-use third-party
    /// asset's license. Mirrors README.md's Third-Party Attributions block.
    /// </summary>
    public IReadOnlyList<Attribution> Attributions { get; }

    public AboutInfo(string version, string buildInfo, IReadOnlyList<Attribution> attributions) {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildInfo);
        ArgumentNullException.ThrowIfNull(attributions);
        Version = version;
        BuildInfo = buildInfo;
        Attributions = attributions;
    }

    /// <summary>
    /// Builds an <see cref="AboutInfo"/> from the running entry assembly's
    /// metadata + the project's known attribution list.
    /// </summary>
    public static AboutInfo FromRunningAssembly() {
        var assembly = Assembly.GetExecutingAssembly();
        var build = BuildVersion.FromRunningAssembly();
        var assemblyVersion = assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        // When the project doesn't set <Version> / <InformationalVersion>
        // in csproj, dotnet stamps a default like "1.0.0+<sha>" that's
        // useless to users. Strip the metadata suffix when it matches the
        // 4-segment version (i.e. nothing meaningful was set) and surface
        // a clear placeholder instead.
        var buildInfo = string.IsNullOrWhiteSpace(informational) || informational == assemblyVersion
            ? "(dev build)"
            : informational;

        return new AboutInfo(
            version: build.DisplayVersion,
            buildInfo: buildInfo,
            attributions: [
                new Attribution(
                    Label: "IP geolocation data by DB-IP",
                    LicenseLabel: "CC BY 4.0",
                    Url: "https://db-ip.com"),
                new Attribution(
                    Label: "Windows toast notifications via Microsoft.Toolkit.Uwp.Notifications",
                    LicenseLabel: "MIT",
                    Url: "https://github.com/CommunityToolkit/WindowsCommunityToolkit"),
            ]);
    }
}

/// <summary>
/// Single third-party attribution row. Label + license + clickable URL.
/// </summary>
internal sealed record Attribution(string Label, string LicenseLabel, string Url);
