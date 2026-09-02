using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AudioBoarder.App.Updates;

internal interface IFileHashVerifier
{
    Task<bool> VerifySha256Async(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken = default);
}

internal interface IAuthenticodeVerifier
{
    SignatureVerificationResult Verify(string path, string allowedSignerCertificateSha256);
}

internal sealed record SignatureVerificationResult(
    bool IsValid,
    string? SignerCertificateSha256,
    string? FailureReason)
{
    public static SignatureVerificationResult Valid(string signerCertificateSha256) =>
        new(true, signerCertificateSha256, null);

    public static SignatureVerificationResult Invalid(string reason) =>
        new(false, null, reason);
}

internal sealed class Sha256FileVerifier : IFileHashVerifier
{
    public async Task<bool> VerifySha256Async(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            return false;

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(
            actual, Convert.FromHexString(expectedSha256));
    }
}

internal sealed class WindowsAuthenticodeVerifier : IAuthenticodeVerifier
{
    public SignatureVerificationResult Verify(string path, string allowedSignerCertificateSha256)
    {
        if (!SignerIdentity.TryParseCertificateSha256Allowlist(
                allowedSignerCertificateSha256, out var allowedHashes))
        {
            return SignatureVerificationResult.Invalid(
                "Allowed signer certificate SHA-256 identity is not configured or is invalid.");
        }
        if (!OperatingSystem.IsWindows())
            return SignatureVerificationResult.Invalid("Authenticode verification requires Windows.");

        var fileInfo = new WinTrustFileInfo(path);
        var data = new WinTrustData(fileInfo);
        try
        {
            var action = WinTrustActionGenericVerifyV2;
            var status = WinVerifyTrust(IntPtr.Zero, action, data);
            if (status != 0)
                return SignatureVerificationResult.Invalid(
                    $"WinVerifyTrust rejected the signature (0x{status:X8}).");

#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(
                X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var signerCertificateSha256 = SignerIdentity.GetCertificateSha256(certificate);
            if (!allowedHashes.Contains(signerCertificateSha256))
            {
                return SignatureVerificationResult.Invalid(
                    "The Authenticode signer certificate is not in the exact SHA-256 allowlist.");
            }

            return SignatureVerificationResult.Valid(signerCertificateSha256);
        }
        catch (CryptographicException)
        {
            return SignatureVerificationResult.Invalid(
                "The file is unsigned or its Authenticode signature is invalid.");
        }
        finally
        {
            data.Dispose();
            fileInfo.Dispose();
        }
    }

    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo : IDisposable
    {
        private readonly uint _structSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
        private IntPtr _filePath;
        private IntPtr _fileHandle = IntPtr.Zero;
        private IntPtr _knownSubject = IntPtr.Zero;

        public WinTrustFileInfo(string filePath) =>
            _filePath = Marshal.StringToCoTaskMemUni(filePath);

        public void Dispose()
        {
            if (_filePath == IntPtr.Zero)
                return;
            Marshal.FreeCoTaskMem(_filePath);
            _filePath = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustData : IDisposable
    {
        private readonly uint _structSize = (uint)Marshal.SizeOf<WinTrustData>();
        private IntPtr _policyCallbackData = IntPtr.Zero;
        private IntPtr _sipClientData = IntPtr.Zero;
        private readonly uint _uiChoice = 2;
        private readonly uint _revocationChecks = 0;
        private readonly uint _unionChoice = 1;
        private IntPtr _fileInfo;
        private readonly uint _stateAction = 0;
        private IntPtr _stateData = IntPtr.Zero;
        private IntPtr _urlReference = IntPtr.Zero;
        private readonly uint _providerFlags = 0x00000040;
        private readonly uint _uiContext = 0;

        public WinTrustData(WinTrustFileInfo fileInfo)
        {
            _fileInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, _fileInfo, false);
        }

        public void Dispose()
        {
            if (_fileInfo == IntPtr.Zero)
                return;
            Marshal.DestroyStructure<WinTrustFileInfo>(_fileInfo);
            Marshal.FreeCoTaskMem(_fileInfo);
            _fileInfo = IntPtr.Zero;
        }
    }
}

internal static class SignerIdentity
{
    public static bool TryParseCertificateSha256Allowlist(
        string? value,
        out HashSet<string> hashes)
    {
        hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var entries = value.Split(
            [',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0)
            return false;

        foreach (var entry in entries)
        {
            var hash = entry.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? entry["sha256:".Length..]
                : entry;
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            {
                hashes.Clear();
                return false;
            }

            hashes.Add(hash.ToUpperInvariant());
        }

        return hashes.Count > 0;
    }

    public static string GetCertificateSha256(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));
}

internal static class ReleaseBuildMetadata
{
    public static string PackageVersion =>
        Read("PackageVersion")
        ?? Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "0.0.0";

    public static string AllowedSignerCertificateSha256 =>
        Read("UpdateAllowedSignerCertificateSha256") ?? string.Empty;

    public static bool IsPortable =>
        bool.TryParse(Read("PortableBuild"), out var portable) && portable;

    public static string? SourceCommit => Read("SourceCommit");

    private static string? Read(string key) =>
        Assembly.GetEntryAssembly()?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Key, key, StringComparison.OrdinalIgnoreCase))
            ?.Value;
}
