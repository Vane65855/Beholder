using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.Windows;

/// <summary>
/// Reads PE VersionInfo strings (CompanyName, ProductName) from a Windows
/// binary on disk via <c>GetFileVersionInfoExW</c> + <c>VerQueryValueW</c>.
/// Used by <see cref="WindowsBinaryIdentityProvider"/> to drive the Phase
/// 7.5 logical-app dedup model — see ADR 007.
/// </summary>
/// <remarks>
/// VersionInfo blocks may carry multiple language/codepage tables for
/// localized binaries. We pick the first available pair from
/// <c>\VarFileInfo\Translation</c>; the English-US-Unicode pair
/// (<c>040904B0</c>) is preferred when present.
/// </remarks>
internal static partial class PeVersionInfoReader {
    private const string VersionDll = "version.dll";

    /// <summary>Default flag set: don't load resources we don't need.</summary>
    private const uint FILE_VER_GET_NEUTRAL = 0x02;

    /// <summary>Preferred LangCodepage for the English-US Unicode block.</summary>
    private const uint EnUsUnicode = 0x040904B0;

    [LibraryImport(VersionDll, EntryPoint = "GetFileVersionInfoSizeExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFileVersionInfoSizeExW(uint dwFlags, string lpwstrFilename, out uint lpdwHandle);

    [LibraryImport(VersionDll, EntryPoint = "GetFileVersionInfoExW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileVersionInfoExW(
        uint dwFlags, string lpwstrFilename, uint dwHandle, uint dwLen, IntPtr lpData);

    [LibraryImport(VersionDll, EntryPoint = "VerQueryValueW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VerQueryValueW(
        IntPtr pBlock, string lpSubBlock, out IntPtr lplpBuffer, out uint puLen);

    /// <summary>
    /// Reads the binary's CompanyName + ProductName from PE VersionInfo.
    /// Returns <c>(null, null)</c> if the file has no VersionInfo block,
    /// the strings are missing, or a Win32 call fails. Failure modes are
    /// logged at Warning so noisy environments are visible to the operator.
    /// </summary>
    public static (string? CompanyName, string? ProductName) Read(string path, ILogger logger) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);

        var size = GetFileVersionInfoSizeExW(FILE_VER_GET_NEUTRAL, path, out _);
        if (size == 0) {
            // No VersionInfo resource. Common for indie tools, malware,
            // some legacy binaries. Not an error — just no metadata.
            return (null, null);
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try {
            if (!GetFileVersionInfoExW(FILE_VER_GET_NEUTRAL, path, 0, size, buffer)) {
                logger.LogWarning(
                    "PeVersionInfoReader: GetFileVersionInfoEx failed for {Path}", path);
                return (null, null);
            }

            var langCodepage = ResolveLangCodepage(buffer);
            if (langCodepage is null) {
                // No \VarFileInfo\Translation block. Defensive: try the
                // English-US-Unicode block speculatively.
                langCodepage = EnUsUnicode;
            }

            var prefix = $@"\StringFileInfo\{langCodepage:X8}\";
            var company = QueryString(buffer, prefix + "CompanyName");
            var product = QueryString(buffer, prefix + "ProductName");
            return (company, product);
        } catch (Exception ex) {
            logger.LogWarning(ex,
                "PeVersionInfoReader: unexpected failure reading {Path}", path);
            return (null, null);
        } finally {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Picks a LangCodepage from <c>\VarFileInfo\Translation</c>. Prefers
    /// the English-US Unicode pair if present; otherwise returns the first
    /// pair the binary advertises. Returns null when the Translation block
    /// is absent.
    /// </summary>
    private static uint? ResolveLangCodepage(IntPtr buffer) {
        if (!VerQueryValueW(buffer, @"\VarFileInfo\Translation", out var translationsPtr, out var translationsLen)) {
            return null;
        }
        if (translationsLen == 0 || translationsPtr == IntPtr.Zero) return null;

        // Each translation entry is two ushorts: { wLanguage, wCodepage }.
        // Pack as (lang << 16) | codepage to form the lookup key.
        var entryCount = translationsLen / 4;
        uint? first = null;
        for (var i = 0; i < entryCount; i++) {
            var entryPtr = translationsPtr + (i * 4);
            var lang = (ushort)Marshal.ReadInt16(entryPtr);
            var codepage = (ushort)Marshal.ReadInt16(entryPtr + 2);
            var key = ((uint)lang << 16) | codepage;
            first ??= key;
            if (key == EnUsUnicode) return key;
        }
        return first;
    }

    /// <summary>
    /// Resolves a single string entry from the version-info block. Returns
    /// null when the key is absent or the string is empty/whitespace.
    /// </summary>
    private static string? QueryString(IntPtr buffer, string subBlock) {
        if (!VerQueryValueW(buffer, subBlock, out var stringPtr, out var stringLen)) return null;
        if (stringLen == 0 || stringPtr == IntPtr.Zero) return null;
        // stringLen is in characters (UTF-16 wchars), including the trailing NUL.
        var copy = Marshal.PtrToStringUni(stringPtr, (int)stringLen);
        var trimmed = copy?.TrimEnd('\0').Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
