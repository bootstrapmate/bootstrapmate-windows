using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace BootstrapMate.Core;

/// <summary>
/// Verifies the Authenticode provenance of installer files (MSI/EXE) before they
/// are executed elevated.
///
/// The download only guarantees the bytes came from the configured URL — it does
/// not prove the file was produced by a trusted publisher. If the manifest or its
/// host is compromised, an attacker-supplied installer would otherwise run as an
/// elevated/SYSTEM process. WinVerifyTrust confirms the embedded signature is
/// present, intact, and chains to a trusted root; the signer certificate's common
/// name is then matched against an optionally-configured expected publisher.
///
/// Windows counterpart of the macOS SignatureVerifier (pkgutil --check-signature).
/// </summary>
public static class SignatureVerifier
{
    public enum Status
    {
        /// <summary>Valid signature that chains to a trusted root.</summary>
        Trusted,
        /// <summary>Unsigned, tampered, or not trusted by Windows.</summary>
        Untrusted,
        /// <summary>Trusted signature, but the publisher does not match the expected value.</summary>
        PublisherMismatch
    }

    public readonly record struct Result(Status Status, string? Publisher, string Detail);

    public enum Decision { Allow, Deny }

    /// <summary>
    /// Inspect an installer file's Authenticode signature.
    /// </summary>
    /// <param name="filePath">Path to the MSI/EXE on disk.</param>
    /// <param name="expectedPublisher">
    /// When set, the signer certificate's common name (or full subject) must contain this value.
    /// </param>
    public static Result VerifyFile(string filePath, string? expectedPublisher)
    {
        uint trust = WinVerifyTrustFile(filePath);
        if (trust != 0)
        {
            // Non-zero HRESULT means unsigned, tampered, or untrusted chain.
            return new Result(Status.Untrusted, null, $"WinVerifyTrust=0x{trust:X8}");
        }

        string? publisher = TryGetPublisher(filePath);

        if (!string.IsNullOrWhiteSpace(expectedPublisher))
        {
            if (publisher is null ||
                publisher.IndexOf(expectedPublisher, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return new Result(Status.PublisherMismatch, publisher, $"expected publisher '{expectedPublisher}'");
            }
        }

        return new Result(Status.Trusted, publisher, "signature trusted");
    }

    /// <summary>
    /// Apply administrator policy to a verification result.
    /// <paramref name="allowUnsigned"/> permits an untrusted/unsigned file (with a
    /// warning). A publisher *mismatch* is never bypassed — an explicit mismatch is
    /// a strong tampering signal.
    /// </summary>
    public static (Decision Decision, string Reason) Decide(Result result, bool allowUnsigned)
    {
        return result.Status switch
        {
            Status.Trusted => (Decision.Allow,
                result.Publisher is { Length: > 0 } p ? $"signature trusted ({p})" : "signature trusted"),
            Status.Untrusted => allowUnsigned
                ? (Decision.Allow, $"untrusted signature ({result.Detail}) — allowed because AllowUnsigned is set")
                : (Decision.Deny, $"untrusted or missing signature ({result.Detail})"),
            Status.PublisherMismatch => (Decision.Deny,
                $"publisher mismatch — found '{result.Publisher ?? "none"}', {result.Detail}"),
            _ => (Decision.Deny, "unknown verification status")
        };
    }

    private static string? TryGetPublisher(string filePath)
    {
        try
        {
            var cert = X509Certificate.CreateFromSignedFile(filePath);
            var subject = cert.Subject; // e.g. "CN=Example Corp, O=Example, C=US"
            return ExtractCommonName(subject) ?? subject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extract the CN value from an X.500 subject string (best-effort).</summary>
    internal static string? ExtractCommonName(string subject)
    {
        foreach (var part in subject.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                return trimmed[3..].Trim().Trim('"');
        }
        return null;
    }

    // ── WinVerifyTrust P/Invoke ───────────────────────────────────────

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_SAFER_FLAG = 0x100;

    private static uint WinVerifyTrustFile(string filePath)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = filePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };

        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFile,
                dwStateAction = WTD_STATEACTION_VERIFY,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = IntPtr.Zero,
                dwProvFlags = WTD_SAFER_FLAG,
                dwUIContext = 0,
                pSignatureSettings = IntPtr.Zero
            };

            Guid action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            uint result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            // Always release the state data, regardless of the verify result.
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            return result;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);
}
