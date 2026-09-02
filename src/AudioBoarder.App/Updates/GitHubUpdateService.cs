using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AudioBoarder.App.Updates;

public sealed record UpdateRelease(
    SemanticVersion Version,
    string TagName,
    string Name,
    string ReleaseNotes,
    Uri MsiUrl,
    string Sha256,
    long Size);

public sealed class GitHubUpdateService
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/oguzhanf/audioboarder/releases/latest";
    private const string ReleasesUrl =
        "https://api.github.com/repos/oguzhanf/audioboarder/releases?per_page=20";

    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubUpdateService> _logger;
    private readonly IFileHashVerifier _hashVerifier;
    private readonly IAuthenticodeVerifier _signatureVerifier;
    private readonly string _allowedSignerCertificateSha256;
    private readonly bool _isPortableBuild;
    private readonly SemanticVersion _currentVersion;
    private readonly Func<bool> _isMsiInstallation;
    private readonly string _updateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioBoarder", "updates");
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioBoarder", "update-state.json");

    public GitHubUpdateService(HttpClient httpClient, ILogger<GitHubUpdateService> logger)
        : this(
            httpClient,
            logger,
            new Sha256FileVerifier(),
            new WindowsAuthenticodeVerifier(),
            ReleaseBuildMetadata.AllowedSignerCertificateSha256,
            ReleaseBuildMetadata.IsPortable,
            CurrentVersion)
    {
    }

    internal GitHubUpdateService(
        HttpClient httpClient,
        ILogger<GitHubUpdateService> logger,
        IFileHashVerifier hashVerifier,
        IAuthenticodeVerifier signatureVerifier,
        string allowedSignerCertificateSha256,
        bool isPortableBuild = false,
        SemanticVersion? currentVersion = null,
        Func<bool>? isMsiInstallation = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _hashVerifier = hashVerifier;
        _signatureVerifier = signatureVerifier;
        _allowedSignerCertificateSha256 = allowedSignerCertificateSha256;
        _isPortableBuild = isPortableBuild;
        _currentVersion = currentVersion ?? CurrentVersion;
        _isMsiInstallation = isMsiInstallation ?? IsMsiInstallation;
    }

    public async Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_isPortableBuild || !_isMsiInstallation())
            return null;
        if (!SignerIdentity.TryParseCertificateSha256Allowlist(
                _allowedSignerCertificateSha256, out _))
        {
            _logger.LogError(
                "Automatic update is disabled because no valid signer certificate SHA-256 allowlist is embedded.");
            return null;
        }

        // The check now gates startup, so it must never hang it. The shared HttpClient
        // has no timeout (the installer download needs that), so bound this call here.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var ct = timeout.Token;

        // GitHub's /latest endpoint excludes prereleases. Preview installations must
        // inspect the release collection so preview.1 can discover preview.2 and the
        // eventual stable release. Stable installations stay on the stable-only path.
        var currentVersion = _currentVersion;
        var endpoint = currentVersion.IsPrerelease ? ReleasesUrl : LatestReleaseUrl;
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.UserAgent.ParseAdd($"AudioBoarder/{CurrentVersion}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var release = document.RootElement.ValueKind == JsonValueKind.Array
            ? ParseReleases(document.RootElement, currentVersion, includePrereleases: true)
            : ParseRelease(document.RootElement, currentVersion);
        return release is not null && ShouldOffer(release.TagName) ? release : null;
    }

    public async Task<string> DownloadAsync(
        UpdateRelease release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var updateDirectory = Path.Combine(_updateRoot, release.TagName);
        Directory.CreateDirectory(updateDirectory);

        var fileName = Path.GetFileName(release.MsiUrl.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName) ||
            !fileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The release does not contain a valid MSI filename.");
        }

        var destination = Path.Combine(updateDirectory, fileName);
        var temporary = destination + ".download";
        if (File.Exists(destination) &&
            await VerifyUpdateAsync(destination, release.Sha256, cancellationToken).ConfigureAwait(false))
        {
            progress?.Report(1d);
            return destination;
        }

        if (File.Exists(temporary))
            File.Delete(temporary);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, release.MsiUrl);
            request.Headers.UserAgent.ParseAdd($"AudioBoarder/{CurrentVersion}");
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? release.Size;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[128 * 1024];
            long downloaded = 0;
            long lastReported = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                downloaded += read;
                if (totalBytes > 0 &&
                    (downloaded - lastReported >= Math.Max(totalBytes / 100, buffer.Length) ||
                     downloaded == totalBytes))
                {
                    progress?.Report(Math.Clamp((double)downloaded / totalBytes, 0d, 1d));
                    lastReported = downloaded;
                }
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            await output.DisposeAsync().ConfigureAwait(false);
            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(release.Sha256)))
            {
                throw new InvalidDataException("The downloaded update failed SHA-256 verification.");
            }

            EnsureSignatureIsValid(temporary);
            File.Move(temporary, destination, overwrite: true);
            progress?.Report(1d);
            return destination;
        }
        catch
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            throw;
        }
    }

    public void BeginInstallAndRestart(string msiPath, UpdateRelease release)
    {
        if (!_hashVerifier.VerifySha256Async(msiPath, release.Sha256)
                .GetAwaiter().GetResult())
        {
            throw new InvalidDataException(
                "The update changed after download and failed SHA-256 verification.");
        }
        EnsureSignatureIsValid(msiPath);

        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the application path.");
        var updateDirectory = Path.GetDirectoryName(msiPath)
            ?? throw new InvalidOperationException("Could not determine the update directory.");
        var logPath = Path.Combine(updateDirectory, "install.log");
        var installPath = GetInstalledPath()?
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            MsiPath = msiPath,
            release.Sha256,
            release.TagName,
            AllowedSignerCertificateSha256 = _allowedSignerCertificateSha256,
            ExecutablePath = executablePath,
            LogPath = logPath,
            InstallPath = installPath
        }));
        var elevatedScript = """
            $ErrorActionPreference = 'Stop'
            $payload = [Text.Encoding]::UTF8.GetString(
                [Convert]::FromBase64String('__PAYLOAD__')) | ConvertFrom-Json
            $secureParent = Join-Path $env:ProgramData 'AudioBoarder\updates-secure'
            if (Test-Path -LiteralPath $secureParent) {
                $parentItem = Get-Item -LiteralPath $secureParent -Force
                if (($parentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    exit 17
                }
            } else {
                New-Item -ItemType Directory -Path $secureParent | Out-Null
            }

            function Set-SecureDirectoryAcl([string]$path) {
                $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
                $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
                $acl = [Security.AccessControl.DirectorySecurity]::new()
                $acl.SetAccessRuleProtection($true, $false)
                $acl.SetOwner($administrators)
                $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
                $propagation = [Security.AccessControl.PropagationFlags]::None
                $allow = [Security.AccessControl.AccessControlType]::Allow
                $rights = [Security.AccessControl.FileSystemRights]::FullControl
                $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                    $system, $rights, $inheritance, $propagation, $allow))
                $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                    $administrators, $rights, $inheritance, $propagation, $allow))
                Set-Acl -LiteralPath $path -AclObject $acl
            }

            Set-SecureDirectoryAcl $secureParent
            $securedParent = Get-Item -LiteralPath $secureParent -Force
            if (($securedParent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                exit 17
            }

            # The random child is created only after the parent DACL has been
            # replaced, so an unelevated process cannot predict, pre-create, or
            # modify the path between verification and msiexec opening it.
            $stageRoot = Join-Path $secureParent ([Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $stageRoot | Out-Null
            Set-SecureDirectoryAcl $stageRoot
            $stageItem = Get-Item -LiteralPath $stageRoot -Force
            if (($stageItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                exit 17
            }
            $stagedMsi = Join-Path $stageRoot 'AudioBoarder-update.msi'
            $stagedLog = Join-Path $stageRoot 'install.log'
            Copy-Item -LiteralPath $payload.MsiPath -Destination $stagedMsi -Force
            $actualHash = (Get-FileHash -LiteralPath $stagedMsi -Algorithm SHA256).Hash
            if ($actualHash -ine $payload.Sha256) { exit 13 }
            if ([string]::IsNullOrWhiteSpace($payload.AllowedSignerCertificateSha256)) { exit 14 }
            $signature = Get-AuthenticodeSignature -LiteralPath $stagedMsi
            if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) { exit 15 }
            $allowedSignerHashes = @($payload.AllowedSignerCertificateSha256 -split '[,;]' |
                ForEach-Object { $_.Trim() } |
                Where-Object { $_ } |
                ForEach-Object {
                    if ($_.StartsWith('sha256:', [StringComparison]::OrdinalIgnoreCase)) {
                        $_.Substring(7)
                    } else { $_ }
                })
            if ($allowedSignerHashes.Count -eq 0 -or
                @($allowedSignerHashes | Where-Object { $_ -notmatch '^[0-9A-Fa-f]{64}$' }).Count -gt 0) {
                exit 14
            }
            $sha256 = [Security.Cryptography.SHA256]::Create()
            try {
                $signerHash = [BitConverter]::ToString(
                    $sha256.ComputeHash($signature.SignerCertificate.RawData)).Replace('-', '')
            } finally {
                $sha256.Dispose()
            }
            if ($allowedSignerHashes -inotcontains $signerHash) { exit 16 }

            $arguments = @('/i', "`"$stagedMsi`"", '/passive', '/norestart', '/L*v', "`"$stagedLog`"")
            if ($payload.InstallPath) {
                $arguments += "INSTALLFOLDER=`"$($payload.InstallPath)`""
            }
            $installer = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" `
                -ArgumentList $arguments -Wait -PassThru
            $exitCode = $installer.ExitCode
            Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
            exit $exitCode
            """.Replace("__PAYLOAD__", payload, StringComparison.Ordinal);
        var elevatedCommand = Convert.ToBase64String(
            Encoding.Unicode.GetBytes(elevatedScript));

        var outerPayload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            ProcessId = Environment.ProcessId,
            ElevatedCommand = elevatedCommand,
            release.TagName,
            ExecutablePath = executablePath
        }));
        var outerScript = """
            $payload = [Text.Encoding]::UTF8.GetString(
                [Convert]::FromBase64String('__PAYLOAD__')) | ConvertFrom-Json
            $exitCode = 1603
            try {
                try {
                    Wait-Process -Id $payload.ProcessId -Timeout 60 -ErrorAction Stop
                } catch {
                    if (Get-Process -Id $payload.ProcessId -ErrorAction SilentlyContinue) {
                        exit 1618
                    }
                }

                $powerShell = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
                $elevated = Start-Process -FilePath $powerShell -Verb RunAs `
                    -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
                        '-EncodedCommand', $payload.ElevatedCommand) -Wait -PassThru
                $exitCode = $elevated.ExitCode
            } catch {
                if ($_.Exception.NativeErrorCode -eq 1223) {
                    $exitCode = 1602
                }
            }

            $restartPath = $payload.ExecutablePath
            if ($exitCode -in @(0, 1641, 3010)) {
                $installPath = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\AudioBoarder' `
                    -Name InstallPath -ErrorAction SilentlyContinue).InstallPath
                $installedExecutable = if ($installPath) {
                    Join-Path $installPath 'AudioBoarder.exe'
                } else { $null }
                if ($installedExecutable -and (Test-Path -LiteralPath $installedExecutable)) {
                    $restartPath = $installedExecutable
                }
                Start-Process -FilePath $restartPath
            } else {
                Start-Process -FilePath $restartPath -ArgumentList @(
                    "--update-failed=$exitCode", "--update-tag=$($payload.TagName)")
            }
            exit $exitCode
            """.Replace("__PAYLOAD__", outerPayload, StringComparison.Ordinal);
        var outerCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(outerScript));

        var powerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powerShell,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(outerCommand);

        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the update installer.");
        _logger.LogInformation("Update installer started for {MsiPath}", msiPath);
    }

    public void Defer(string tagName)
    {
        var current = ReadState();
        WriteState(current with
        {
            DeferredTag = tagName,
            DeferredUntilUtc = DateTimeOffset.UtcNow.AddHours(24)
        });
    }

    public void RecordFailure(string tagName)
    {
        var current = ReadState();
        WriteState(current with
        {
            FailedTag = tagName,
            FailedAtUtc = DateTimeOffset.UtcNow
        });
    }

    private bool ShouldOffer(string tagName)
    {
        var state = ReadState();
        if (string.Equals(state.DeferredTag, tagName, StringComparison.OrdinalIgnoreCase) &&
            state.DeferredUntilUtc > DateTimeOffset.UtcNow)
        {
            return false;
        }

        return !string.Equals(state.FailedTag, tagName, StringComparison.OrdinalIgnoreCase) ||
               state.FailedAtUtc <= DateTimeOffset.UtcNow.AddHours(-24);
    }

    private bool IsMsiInstallation()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;

        var installPath = GetInstalledPath();
        if (!string.IsNullOrWhiteSpace(installPath))
        {
            return string.Equals(
                Path.GetFullPath(Path.GetDirectoryName(executablePath)!).
                    TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(installPath).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return HasInstalledMarker() && executablePath.StartsWith(
            programFiles.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasInstalledMarker() =>
        ReadRegistryValue("Installed") is { } value && Convert.ToInt32(value) == 1;

    private static string? GetInstalledPath() =>
        ReadRegistryValue("InstallPath") as string;

    private static object? ReadRegistryValue(string name)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\AudioBoarder");
            var value = key?.GetValue(name);
            if (value is not null)
                return value;
        }

        return null;
    }

    private UpdateState ReadState()
    {
        try
        {
            if (!File.Exists(_statePath))
                return new UpdateState(null, null, null, null);
            return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(_statePath))
                   ?? new UpdateState(null, null, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read update state from {StatePath}", _statePath);
            return new UpdateState(null, null, null, null);
        }
    }

    private void WriteState(UpdateState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temporary = _statePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, _statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist update state to {StatePath}", _statePath);
        }
    }

    internal async Task<bool> VerifyUpdateAsync(
        string path, string expectedHash, CancellationToken cancellationToken = default)
    {
        if (!SignerIdentity.TryParseCertificateSha256Allowlist(
                _allowedSignerCertificateSha256, out _))
            return false;
        if (!await _hashVerifier.VerifySha256Async(
                path, expectedHash, cancellationToken).ConfigureAwait(false))
            return false;
        return _signatureVerifier.Verify(path, _allowedSignerCertificateSha256).IsValid;
    }

    private void EnsureSignatureIsValid(string path)
    {
        if (!SignerIdentity.TryParseCertificateSha256Allowlist(
                _allowedSignerCertificateSha256, out _))
        {
            throw new InvalidDataException(
                "The update signer certificate SHA-256 allowlist is missing or invalid.");
        }

        var result = _signatureVerifier.Verify(path, _allowedSignerCertificateSha256);
        if (!result.IsValid)
            throw new InvalidDataException(
                $"The update failed Authenticode verification: {result.FailureReason}");
    }

    internal static SemanticVersion CurrentVersion =>
        SemanticVersion.TryParse(ReleaseBuildMetadata.PackageVersion, out var version)
            ? version
            : new SemanticVersion(0, 0, 0);

    internal static UpdateRelease? ParseRelease(
        JsonElement root,
        SemanticVersion currentVersion)
    {
        if (!root.TryGetProperty("tag_name", out var tagElement))
            return null;

        var tagName = tagElement.GetString();
        if (string.IsNullOrWhiteSpace(tagName) ||
            !SemanticVersion.TryParse(tagName, out var releaseVersion) ||
            releaseVersion <= currentVersion)
        {
            return null;
        }

        if (!root.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameElement))
                continue;
            var name = nameElement.GetString() ?? string.Empty;
            if (!name.EndsWith("-win-x64.msi", StringComparison.OrdinalIgnoreCase))
                continue;

            var digest = asset.TryGetProperty("digest", out var digestElement)
                ? digestElement.GetString()
                : null;
            if (digest is null ||
                !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                digest.Length != 71)
            {
                continue;
            }

            var hash = digest[7..];
            if (!hash.All(Uri.IsHexDigit))
                continue;
            if (!asset.TryGetProperty("browser_download_url", out var downloadElement) ||
                !Uri.TryCreate(downloadElement.GetString(),
                    UriKind.Absolute, out var downloadUrl) ||
                downloadUrl.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(downloadUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new UpdateRelease(
                releaseVersion,
                tagName,
                root.TryGetProperty("name", out var releaseName)
                    ? releaseName.GetString() ?? tagName
                    : tagName,
                root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
                downloadUrl,
                hash,
                asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0);
        }

        return null;
    }

    internal static UpdateRelease? ParseRelease(JsonElement root, Version currentVersion) =>
        ParseRelease(root, SemanticVersion.FromVersion(currentVersion));

    internal static UpdateRelease? ParseReleases(
        JsonElement root,
        SemanticVersion currentVersion,
        bool includePrereleases)
    {
        if (root.ValueKind != JsonValueKind.Array) return null;

        return root.EnumerateArray()
            .Where(release =>
                !release.TryGetProperty("draft", out var draft) || !draft.GetBoolean())
            .Where(release =>
                includePrereleases ||
                !release.TryGetProperty("prerelease", out var prerelease) ||
                !prerelease.GetBoolean())
            .Select(release => ParseRelease(release, currentVersion))
            .Where(release => release is not null)
            .Cast<UpdateRelease>()
            .OrderByDescending(release => release.Version)
            .FirstOrDefault();
    }

    private sealed record UpdateState(
        string? DeferredTag,
        DateTimeOffset? DeferredUntilUtc,
        string? FailedTag,
        DateTimeOffset? FailedAtUtc);
}
