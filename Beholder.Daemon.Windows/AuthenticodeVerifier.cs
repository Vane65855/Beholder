using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Beholder.Core;
using Microsoft.Extensions.Logging;

namespace Beholder.Daemon.Windows;

/// <summary>
/// Validates a Windows binary's Authenticode signature via
/// <c>WinVerifyTrust</c> and extracts the leaf certificate's Subject CN +
/// Issuer CN for the Phase 7.5 spoof-detection comparison. See ADR 007.
/// </summary>
/// <remarks>
/// Two phases per call:
/// <list type="number">
/// <item><c>WinVerifyTrust</c> with <see cref="WTD_REVOKE_WHOLECHAIN"/>
/// validates the chain against the system root store and checks revocation
/// (OCSP/CRL). Result is mapped to <see cref="SignatureValidationStatus"/>.</item>
/// <item><see cref="X509Certificate.CreateFromSignedFile"/> reads the
/// embedded leaf cert. We pull SubjectName and IssuerName for the
/// publisher-comparison key.</item>
/// </list>
/// Unsigned binaries return null (not an error). Validation failures are
/// logged at Warning per <c>docs/CODING_STANDARDS.md</c>.
/// </remarks>
internal static partial class AuthenticodeVerifier {
    // WinVerifyTrust structure tags
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_WHOLECHAIN = 1;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;

    // Result HRESULTs we map explicitly. Remaining failures land in Invalid.
    private const uint S_OK = 0;
    private const uint TRUST_E_NOSIGNATURE = 0x800B0100;
    private const uint TRUST_E_PROVIDER_UNKNOWN = 0x800B0001;
    private const uint TRUST_E_SUBJECT_FORM_UNKNOWN = 0x800B0003;
    private const uint TRUST_E_SUBJECT_NOT_TRUSTED = 0x800B0004;
    private const uint TRUST_E_BAD_DIGEST = 0x80096010;
    private const uint CERT_E_EXPIRED = 0x800B0101;
    private const uint CERT_E_REVOKED = 0x800B010C;
    private const uint CERT_E_UNTRUSTEDROOT = 0x800B0109;
    private const uint CERT_E_CHAINING = 0x800B010A;

    // {00AAC56B-CD44-11D0-8CC2-00C04FC295EE} — WINTRUST_ACTION_GENERIC_VERIFY_V2
    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    private static partial uint WinVerifyTrust(IntPtr hWnd, ref Guid pgActionID, ref WintrustData pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo {
        public uint StructSize;
        public IntPtr FilePath;       // LPCWSTR
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SIPClientData;
        public uint UIChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfoPtr;     // points to WintrustFileInfo for WTD_CHOICE_FILE
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr URLReference;
        public uint ProvFlags;
        public uint UIContext;
        public IntPtr SignatureSettings;
    }

    /// <summary>
    /// Reads the binary's Authenticode signature info. Returns null when the
    /// binary is unsigned, the file can't be read, or the binary is signed
    /// via Windows catalog (signature lives in a separate .cat file, no
    /// embedded cert to compare against — common for OS binaries like
    /// notepad.exe). Returns a non-null <see cref="AuthenticodeInfo"/> only
    /// when the embedded cert was extracted; callers should inspect
    /// <see cref="AuthenticodeInfo.Status"/> before trusting the Subject for
    /// spoof comparison.
    /// </summary>
    public static AuthenticodeInfo? Read(string path, ILogger logger) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);

        // File-existence check up front — WinVerifyTrust on a missing file
        // returns CRYPT_E_FILE_ERROR which we'd otherwise map to Invalid.
        // Treating it as "unreadable hence no signature info" matches the
        // PeVersionInfoReader's behavior.
        if (!File.Exists(path)) return null;

        var status = Verify(path, logger);
        if (status is null) return null;  // unsigned (TRUST_E_NOSIGNATURE)

        var (subject, issuer) = ExtractCertSubjects(path, logger);
        if (subject is null || issuer is null) {
            // No embedded cert. Catalog-signed binaries land here — they are
            // signed, but the signature lives in a separate .cat file and
            // X509Certificate.CreateFromSignedFile only reads embedded
            // signatures. For spoof-detection purposes (which compares
            // SubjectCn strings) we can't reason about catalog-signed
            // binaries, so treat as unsigned.
            return null;
        }
        return new AuthenticodeInfo(subject, issuer, status.Value);
    }

    /// <summary>
    /// Runs <c>WinVerifyTrust</c>. Returns null when the binary is unsigned;
    /// otherwise returns the mapped <see cref="SignatureValidationStatus"/>.
    /// </summary>
    private static SignatureValidationStatus? Verify(string path, ILogger logger) {
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var fileInfo = new WintrustFileInfo {
            StructSize = (uint)Marshal.SizeOf<WintrustFileInfo>(),
            FilePath = pathPtr,
            FileHandle = IntPtr.Zero,
            KnownSubject = IntPtr.Zero,
        };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

        var data = new WintrustData {
            StructSize = (uint)Marshal.SizeOf<WintrustData>(),
            PolicyCallbackData = IntPtr.Zero,
            SIPClientData = IntPtr.Zero,
            UIChoice = WTD_UI_NONE,
            RevocationChecks = WTD_REVOKE_WHOLECHAIN,
            UnionChoice = WTD_CHOICE_FILE,
            FileInfoPtr = fileInfoPtr,
            StateAction = WTD_STATEACTION_VERIFY,
            StateData = IntPtr.Zero,
            URLReference = IntPtr.Zero,
            ProvFlags = 0,
            UIContext = 0,
            SignatureSettings = IntPtr.Zero,
        };

        try {
            var actionId = WintrustActionGenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref actionId, ref data);

            // Always close the verify state (releases provider-allocated chains).
            data.StateAction = WTD_STATEACTION_CLOSE;
            _ = WinVerifyTrust(IntPtr.Zero, ref actionId, ref data);

            return MapResult(result, path, logger);
        } finally {
            Marshal.FreeHGlobal(fileInfoPtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    private static SignatureValidationStatus? MapResult(uint result, string path, ILogger logger) => result switch {
        S_OK => SignatureValidationStatus.Valid,
        TRUST_E_NOSIGNATURE => null,                                        // unsigned — not an error
        // SUBJECT_FORM_UNKNOWN means "this isn't a recognized signed-file
        // format" — common for plain text files (hosts, .ini, .txt). Treat
        // as unsigned rather than invalid so callers fall back to path-
        // based dedup.
        TRUST_E_SUBJECT_FORM_UNKNOWN => null,
        CERT_E_EXPIRED => SignatureValidationStatus.Expired,
        CERT_E_REVOKED => SignatureValidationStatus.Revoked,
        CERT_E_UNTRUSTEDROOT or CERT_E_CHAINING => SignatureValidationStatus.UntrustedRoot,
        TRUST_E_PROVIDER_UNKNOWN
            or TRUST_E_SUBJECT_NOT_TRUSTED
            or TRUST_E_BAD_DIGEST => SignatureValidationStatus.Invalid,
        _ => LogAndReturnInvalid(result, path, logger),
    };

    private static SignatureValidationStatus LogAndReturnInvalid(uint result, string path, ILogger logger) {
        logger.LogWarning(
            "AuthenticodeVerifier: unmapped WinVerifyTrust HRESULT 0x{Result:X8} for {Path}; treating as Invalid",
            result, path);
        return SignatureValidationStatus.Invalid;
    }

    /// <summary>
    /// Extracts the leaf certificate's Subject CN + Issuer CN from the
    /// signed binary. Returns (null, null) on failure (file missing,
    /// malformed PE, no embedded cert despite signature_status != null).
    /// </summary>
    private static (string? Subject, string? Issuer) ExtractCertSubjects(string path, ILogger logger) {
        try {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the only API that reads embedded Authenticode certs
            using var cert = X509Certificate.CreateFromSignedFile(path);
            return (cert.Subject, cert.Issuer);
#pragma warning restore SYSLIB0057
        } catch (Exception ex) {
            logger.LogWarning(ex,
                "AuthenticodeVerifier: failed to read embedded certificate from {Path}", path);
            return (null, null);
        }
    }
}
