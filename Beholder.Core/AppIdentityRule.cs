namespace Beholder.Core;

/// <summary>
/// User-configured manual rule that tells the daemon "any binary at exactly
/// one folder below <see cref="AnchorPath"/> with filename
/// <see cref="Filename"/> is the same logical app." Backs Phase 13.6's
/// Application Identity Overrides section in the Settings tab. Closes the
/// gap left by ADR 007's automatic logical-identity dedup (which requires
/// VersionInfo + Authenticode signature, neither of which unsigned /
/// sideloaded apps have).
/// </summary>
/// <remarks>
/// <para>
/// Match semantics are strict depth-1: a candidate path <c>P</c> matches the
/// rule iff <c>Path.GetFileName(P)</c> equals <see cref="Filename"/> AND
/// <c>Path.GetDirectoryName(Path.GetDirectoryName(P))</c> equals
/// <see cref="AnchorPath"/>. Both checks are case-insensitive on Windows
/// (NTFS), case-sensitive elsewhere. Examples for
/// <c>AnchorPath = C:\Users\Vane\AppData\Local\Discord</c> +
/// <c>Filename = Discord.exe</c>:
/// </para>
/// <list type="bullet">
///   <item><c>…\Discord\app-1.0.9235\Discord.exe</c> → matches (grandparent
///     equals anchor; one variable segment between).</item>
///   <item><c>…\Discord\Discord.exe</c> → no match (zero variable segments,
///     too shallow).</item>
///   <item><c>…\Discord\v2\dist\Discord.exe</c> → no match (two segments
///     between anchor and filename, too deep).</item>
///   <item><c>…\Discord\app-1.0.9235\Setup.exe</c> → no match (filename
///     mismatch).</item>
/// </list>
/// <para>
/// The model is deliberately "stupid in / stupid out": the daemon trusts the
/// user's explicit instruction. The UI's only hard guard rail is that the
/// file the user picks must validate (file is exactly one segment below
/// the configured anchor). Beyond that, if the user picks an anchor that's
/// too broad (e.g., <c>C:\Program Files</c>) the daemon shrugs and trusts it.
/// </para>
/// </remarks>
public sealed record AppIdentityRule(
    int Id,
    string AnchorPath,
    string Filename,
    string? DisplayName,
    DateTimeOffset CreatedAt);
