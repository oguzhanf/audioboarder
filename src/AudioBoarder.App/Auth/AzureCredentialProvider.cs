using System.IO;
using Azure.Core;
using Azure.Identity;
using AudioBoarder.App.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AudioBoarder.App.Auth;

public enum AzureCredentialState
{
    Unknown,
    Restoring,
    SignedIn,
    SignInRequired,
    Failed,
}

public sealed record AzureCredentialSnapshot(
    AzureCredentialState State,
    string? Username = null,
    string? FailureCategory = null);

public interface IAzureCredentialProvider
{
    AzureCredentialSnapshot Snapshot { get; }
    string? SignedInAs { get; }
    string? TenantId { get; }
    string? UserObjectId { get; }
    event EventHandler<AzureCredentialSnapshot>? StateChanged;
    TokenCredential Get();
    bool TryGetSignedInCredential(out TokenCredential? credential);
    Task<bool> TryRestoreAsync(CancellationToken ct);
    Task<(bool Success, string Message)> SignInInteractiveAsync(CancellationToken ct);
}

internal sealed record AzureCredentialSession(
    TokenCredential Credential,
    string? Username,
    string? HomeAccountId);

internal interface IAzureCredentialBackend
{
    TokenCredential CreateDefaultCredential();
    Task<AzureCredentialSession?> RestoreAsync(CancellationToken ct);
    Task<AzureCredentialSession> SignInAsync(CancellationToken ct);
}

public sealed class AzureCredentialProvider : IAzureCredentialProvider
{
    private readonly AudioBoarderSettings _settings;
    private readonly ILogger<AzureCredentialProvider> _logger;
    private readonly IAzureCredentialBackend _backend;
    private readonly object _gate = new();
    private TokenCredential? _credential;
    private string? _signedInAs;
    private string? _userObjectId;
    private AzureCredentialSnapshot _snapshot = new(AzureCredentialState.Unknown);

    public AzureCredentialProvider(
        IOptions<AudioBoarderSettings> settings,
        ILogger<AzureCredentialProvider>? logger = null)
        : this(settings, new AzureCredentialBackend(settings.Value), logger)
    {
    }

    internal AzureCredentialProvider(
        IOptions<AudioBoarderSettings> settings,
        IAzureCredentialBackend backend,
        ILogger<AzureCredentialProvider>? logger = null)
    {
        _settings = settings.Value;
        _backend = backend;
        _logger = logger ?? NullLogger<AzureCredentialProvider>.Instance;
    }

    public AzureCredentialSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public string? SignedInAs
    {
        get { lock (_gate) return _signedInAs; }
    }

    public string? TenantId => _settings.AzureOpenAI.TenantId;

    public string? UserObjectId
    {
        get { lock (_gate) return _userObjectId; }
    }

    public event EventHandler<AzureCredentialSnapshot>? StateChanged;

    public TokenCredential Get()
    {
        lock (_gate)
        {
            // A DefaultAzureCredential is useful for non-interactive deployments, but
            // creating one is not evidence that this desktop user has an interactive
            // Azure session. Snapshot remains unchanged until a token is verified.
            return _credential ??= _backend.CreateDefaultCredential();
        }
    }

    public bool TryGetSignedInCredential(out TokenCredential? credential)
    {
        lock (_gate)
        {
            credential = _snapshot.State == AzureCredentialState.SignedIn ? _credential : null;
            return credential is not null;
        }
    }

    public async Task<(bool Success, string Message)> SignInInteractiveAsync(CancellationToken ct)
    {
        try
        {
            var session = await _backend.SignInAsync(ct).ConfigureAwait(false);
            SetSignedIn(session);
            _logger.LogInformation("Interactive Azure sign-in succeeded");
            var username = SafeUsername(session.Username);
            return (true, username is null
                ? "Signed in to Azure. Re-running health checks."
                : $"Signed in as {username}. Re-running health checks.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Interactive Azure sign-in failed; category={Category}", SafeCategory(ex));
            SetSnapshot(new AzureCredentialSnapshot(AzureCredentialState.Failed, FailureCategory: SafeCategory(ex)));
            return (false, SafeSignInFailure(ex));
        }
    }

    public async Task<bool> TryRestoreAsync(CancellationToken ct)
    {
        SetSnapshot(new AzureCredentialSnapshot(AzureCredentialState.Restoring));
        try
        {
            var session = await _backend.RestoreAsync(ct).ConfigureAwait(false);
            if (session is null)
            {
                _logger.LogInformation("No persisted AuthenticationRecord; interactive sign-in required");
                ClearCredential();
                SetSnapshot(new AzureCredentialSnapshot(AzureCredentialState.SignInRequired));
                return false;
            }

            SetSignedIn(session);
            _logger.LogInformation("Restored persisted Azure sign-in");
            return true;
        }
        catch (AuthenticationRequiredException)
        {
            _logger.LogInformation("Cached Azure token requires interactive authentication");
            ClearCredential();
            SetSnapshot(new AzureCredentialSnapshot(AzureCredentialState.SignInRequired, FailureCategory: "authentication_required"));
            return false;
        }
        catch (CredentialUnavailableException)
        {
            _logger.LogInformation("Cached Azure credential is unavailable; interactive sign-in required");
            ClearCredential();
            SetSnapshot(new AzureCredentialSnapshot(AzureCredentialState.SignInRequired, FailureCategory: "credential_unavailable"));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Silent Azure token restore failed; category={Category}", SafeCategory(ex));
            ClearCredential();
            SetSnapshot(new AzureCredentialSnapshot(AzureCredentialState.Failed, FailureCategory: SafeCategory(ex)));
            return false;
        }
    }

    private void SetSignedIn(AzureCredentialSession session)
    {
        var username = SafeUsername(session.Username);
        lock (_gate)
        {
            _credential = session.Credential;
            _signedInAs = username;
            _userObjectId = ExtractObjectId(session.HomeAccountId);
        }
        SetSnapshot(new AzureCredentialSnapshot(AzureCredentialState.SignedIn, username));
    }

    private void ClearCredential()
    {
        lock (_gate)
        {
            _credential = null;
            _signedInAs = null;
            _userObjectId = null;
        }
    }

    private void SetSnapshot(AzureCredentialSnapshot snapshot)
    {
        lock (_gate) _snapshot = snapshot;
        StateChanged?.Invoke(this, snapshot);
    }

    private static string? SafeUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        var safe = new string(username.Trim().Where(c => !char.IsControl(c)).Take(128).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? null : safe;
    }

    private static string SafeSignInFailure(Exception ex) => ex switch
    {
        OperationCanceledException => "Sign-in was cancelled.",
        AuthenticationFailedException => "Azure authentication failed. Try signing in again.",
        _ => "Could not sign in to Azure. Try again.",
    };

    private static string SafeCategory(Exception ex) => ex switch
    {
        OperationCanceledException => "cancelled",
        AuthenticationRequiredException => "authentication_required",
        CredentialUnavailableException => "credential_unavailable",
        AuthenticationFailedException => "authentication_failed",
        IOException => "cache_unavailable",
        _ => "unavailable",
    };

    private static string? ExtractObjectId(string? homeAccountId)
    {
        if (string.IsNullOrEmpty(homeAccountId)) return null;
        var dot = homeAccountId.IndexOf('.');
        return dot > 0 ? homeAccountId[..dot] : homeAccountId;
    }
}

internal sealed class AzureCredentialBackend : IAzureCredentialBackend
{
    private const string CacheName = "AudioBoarder";
    private static readonly string AuthRecordPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioBoarder", "auth-record.json");

    private static readonly TokenRequestContext ManagementContext =
        new(new[] { "https://management.azure.com/.default" });

    private readonly AudioBoarderSettings _settings;

    public AzureCredentialBackend(AudioBoarderSettings settings) => _settings = settings;

    public TokenCredential CreateDefaultCredential()
    {
        var tenant = _settings.AzureOpenAI.TenantId;
        return new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            TenantId = string.IsNullOrWhiteSpace(tenant) ? null : tenant,
            ExcludeInteractiveBrowserCredential = true,
            ExcludeAzurePowerShellCredential = true,
            AdditionallyAllowedTenants = { "*" },
        });
    }

    public async Task<AzureCredentialSession?> RestoreAsync(CancellationToken ct)
    {
        if (!File.Exists(AuthRecordPath)) return null;

        AuthenticationRecord record;
        try
        {
            await using var fs = File.OpenRead(AuthRecordPath);
            record = await AuthenticationRecord.DeserializeAsync(fs, ct).ConfigureAwait(false);
        }
        catch
        {
            try { File.Delete(AuthRecordPath); } catch { }
            return null;
        }

        var tenant = _settings.AzureOpenAI.TenantId;
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
        _ = await browser.GetTokenAsync(ManagementContext, probeCts.Token).ConfigureAwait(false);
        return new AzureCredentialSession(browser, record.Username, record.HomeAccountId);
    }

    public async Task<AzureCredentialSession> SignInAsync(CancellationToken ct)
    {
        var tenant = _settings.AzureOpenAI.TenantId;
        var browser = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
        {
            TenantId = string.IsNullOrWhiteSpace(tenant) ? null : tenant,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = CacheName },
            AdditionallyAllowedTenants = { "*" },
        });

        var record = await browser.AuthenticateAsync(ManagementContext, ct).ConfigureAwait(false);
        _ = await browser.GetTokenAsync(ManagementContext, ct).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AuthRecordPath)!);
            await using var fs = File.Create(AuthRecordPath);
            await record.SerializeAsync(fs, ct).ConfigureAwait(false);
        }
        catch
        {
            // A valid current session is still useful even when persistence fails.
        }

        return new AzureCredentialSession(browser, record.Username, record.HomeAccountId);
    }
}
