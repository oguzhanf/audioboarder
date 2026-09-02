using System.IO;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using AudioBoarder.App.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AudioBoarder.App.Auth;

public sealed class AzureCredentialProvider
{
    private const string CacheName = "AudioBoarder";
    private static readonly string AuthRecordPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioBoarder", "auth-record.json");

    private readonly AudioBoarderSettings _settings;
    private readonly ILogger<AzureCredentialProvider> _logger;
    private readonly object _gate = new();
    private TokenCredential? _credential;
    private string? _signedInAs;
    private string? _userObjectId;

    public AzureCredentialProvider(
        IOptions<AudioBoarderSettings> settings,
        ILogger<AzureCredentialProvider>? logger = null)
    {
        _settings = settings.Value;
        _logger = logger ?? NullLogger<AzureCredentialProvider>.Instance;
    }

    public string? SignedInAs => _signedInAs;
    public string? TenantId => _settings.AzureOpenAI.TenantId;
    /// <summary>
    /// Object ID (GUID) of the signed-in user. Used for ARM role assignments.
    /// Derived from AuthenticationRecord.HomeAccountId which has form "{oid}.{tid}".
    /// </summary>
    public string? UserObjectId => _userObjectId;

    public TokenCredential Get()
    {
        lock (_gate)
        {
            return _credential ??= BuildCachedChain();
        }
    }

    public async Task<(bool Success, string Message)> SignInInteractiveAsync(CancellationToken ct)
    {
        try
        {
            var tenant = _settings.AzureOpenAI.TenantId;
            var opts = new InteractiveBrowserCredentialOptions
            {
                TenantId = string.IsNullOrWhiteSpace(tenant) ? null : tenant,
                TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = CacheName },
                AdditionallyAllowedTenants = { "*" },
            };
            var browser = new InteractiveBrowserCredential(opts);

            // AuthenticateAsync opens the browser, performs sign-in, and returns an
            // AuthenticationRecord we can persist. Without this, MSAL has no way to
            // identify which account to use on a subsequent silent restore.
            var record = await browser.AuthenticateAsync(
                new TokenRequestContext(new[] { "https://management.azure.com/.default" }),
                ct).ConfigureAwait(false);

            // Verify we can actually get a token now (and warm the cache)
            var token = await browser.GetTokenAsync(
                new TokenRequestContext(new[] { "https://management.azure.com/.default" }), ct).ConfigureAwait(false);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AuthRecordPath)!);
                await using var fs = File.Create(AuthRecordPath);
                await record.SerializeAsync(fs, ct).ConfigureAwait(false);
                _logger.LogInformation("Persisted AuthenticationRecord");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist AuthenticationRecord; silent restore will not work next launch");
            }

            _logger.LogInformation("Interactive sign-in succeeded (expires {Exp:HH:mm:ss})",
                token.ExpiresOn);

            lock (_gate)
            {
                _credential = browser;
                _signedInAs = record.Username;
                _userObjectId = ExtractObjectId(record.HomeAccountId);
            }
            return (true, $"Signed in as {record.Username}. Re-running health checks.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Interactive sign-in failed");
            return (false, ex.Message);
        }
    }

    private TokenCredential BuildCachedChain()
    {
        var tenant = _settings.AzureOpenAI.TenantId;
        var opts = new DefaultAzureCredentialOptions
        {
            TenantId = string.IsNullOrWhiteSpace(tenant) ? null : tenant,
            ExcludeInteractiveBrowserCredential = true,
            ExcludeAzurePowerShellCredential = true,
            AdditionallyAllowedTenants = { "*" },
        };
        return new DefaultAzureCredential(opts);
    }

    /// <summary>
    /// Try to silently restore a previously-cached interactive sign-in. Requires a
    /// persisted AuthenticationRecord on disk (written by <see cref="SignInInteractiveAsync"/>).
    /// </summary>
    public async Task<bool> TryRestoreAsync(CancellationToken ct)
    {
        if (!File.Exists(AuthRecordPath))
        {
            _logger.LogInformation("No persisted AuthenticationRecord; interactive sign-in required");
            return false;
        }
        AuthenticationRecord record;
        try
        {
            await using var fs = File.OpenRead(AuthRecordPath);
            record = await AuthenticationRecord.DeserializeAsync(fs, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load AuthenticationRecord; user must sign in again");
            try { File.Delete(AuthRecordPath); } catch { }
            return false;
        }

        var tenant = _settings.AzureOpenAI.TenantId;
        try
        {
            var browser = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
            {
                TenantId = string.IsNullOrWhiteSpace(tenant) ? null : tenant,
                TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = CacheName },
                AuthenticationRecord = record,
                DisableAutomaticAuthentication = true,
                AdditionallyAllowedTenants = { "*" },
            });
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(8));
            var token = await browser.GetTokenAsync(
                new TokenRequestContext(new[] { "https://management.azure.com/.default" }),
                probeCts.Token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(token.Token))
            {
                lock (_gate)
                {
                    _credential = browser;
                    _signedInAs = record.Username;
                    _userObjectId = ExtractObjectId(record.HomeAccountId);
                }
                _logger.LogInformation("Restored persisted Azure sign-in (expires {Exp:HH:mm:ss})",
                    token.ExpiresOn);
                return true;
            }
        }
        catch (AuthenticationRequiredException)
        {
            _logger.LogInformation("Cached token expired; sign in again");
        }
        catch (OperationCanceledException) { _logger.LogWarning("Silent token restore timed out after 8s"); }
        catch (Exception ex) { _logger.LogWarning(ex, "Silent token restore failed"); }
        return false;
    }

    /// <summary>
    /// AuthenticationRecord.HomeAccountId is "{oid}.{tid}". Return the oid.
    /// </summary>
    private static string? ExtractObjectId(string? homeAccountId)
    {
        if (string.IsNullOrEmpty(homeAccountId)) return null;
        var dot = homeAccountId.IndexOf('.');
        return dot > 0 ? homeAccountId.Substring(0, dot) : homeAccountId;
    }
}
