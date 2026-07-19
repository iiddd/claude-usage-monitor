using System.Security.AccessControl;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ClaudeUsageServer.Models;
using Microsoft.Extensions.Options;

namespace ClaudeUsageServer.Services;

internal sealed class CredentialWatcherService : BackgroundService
{
    // Public OAuth client id the Claude Code CLI itself uses for its token grants.
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    // The CLI tries the platform host first and falls back to the legacy console host.
    private static readonly string[] TokenEndpoints =
    {
        "https://platform.claude.com/v1/oauth/token",
        "https://console.anthropic.com/v1/oauth/token",
    };

    private readonly CredentialStore _store;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<CredentialWatcherService> _logger;
    private FileSystemWatcher? _watcher;
    private bool _reauthWarningLogged;

    // FSW change events fired within this window of our own write are our own echo; ignore them.
    private long _suppressReloadUntilTicks;

    // Capacity-1 channel: multiple rapid FSW events collapse into one reload signal.
    private readonly Channel<bool> _reloadSignal = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    private enum RefreshResult { Success, InvalidGrant, TransientFailure }

    public CredentialWatcherService(
        CredentialStore store,
        IOptionsMonitor<ServerOptions> options,
        IHttpClientFactory httpFactory,
        ILogger<CredentialWatcherService> logger)
    {
        _store = store;
        _options = options;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ClaudeUsageServer starting");

        await TryLoadCredentialsAsync();
        SetupFileWatcher();

        while (!stoppingToken.IsCancellationRequested)
        {
            // Reauth required: the refresh token is dead. Retrying the grant cannot help —
            // only a fresh `claude login` can, which arrives as a file change. Park here.
            if (_store.ReauthRequired)
            {
                try { await _reloadSignal.Reader.ReadAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            var timeUntilRefresh = _store.GetTimeUntilRefresh(_options.CurrentValue.RefreshBufferMinutes);

            if (timeUntilRefresh > TimeSpan.Zero)
            {
                // Wait until the refresh window opens, but yield early if FSW fires.
                try
                {
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    waitCts.CancelAfter(timeUntilRefresh);
                    await _reloadSignal.Reader.ReadAsync(waitCts.Token);
                    // FSW fired: credentials already reloaded inside the handler — restart loop.
                    continue;
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Timeout reached: fall through to refresh.
                }
            }

            if (!stoppingToken.IsCancellationRequested)
                await TryRefreshWithRetryAsync(stoppingToken);
        }

        _logger.LogInformation("ClaudeUsageServer stopping");
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }

    // ── Credential loading ─────────────────────────────────────────────────

    private async Task TryLoadCredentialsAsync()
    {
        var path = _options.CurrentValue.ResolvedCredentialsPath;
        try
        {
            string json = await ReadWithRetryAsync(path);

            var file = JsonSerializer.Deserialize<CredentialsFile>(json);
            var oauth = file?.ClaudeAiOauth;

            if (string.IsNullOrWhiteSpace(oauth?.AccessToken))
            {
                _logger.LogWarning("Credentials file does not contain a valid access token");
                _store.MarkFileInvalid();
                return;
            }

            var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(oauth.ExpiresAt);
            _store.Update(oauth.AccessToken, oauth.RefreshToken, expiresAt);
            _reauthWarningLogged = false;
            _logger.LogInformation("Credentials loaded, token expires at {ExpiresAt:O}", expiresAt);
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning("Credentials file not found");
            _store.MarkFileNotFound();
        }
        catch (DirectoryNotFoundException)
        {
            _logger.LogWarning("Credentials directory not found");
            _store.MarkFileNotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error reading credentials file");
            _store.MarkFileInvalid();
        }
    }

    // Retries up to 3 times with a short delay to handle write-lock races.
    private static async Task<string> ReadWithRetryAsync(string path)
    {
        IOException? last = null;
        for (int i = 0; i < 3; i++)
        {
            try { return await File.ReadAllTextAsync(path); }
            catch (IOException ex) when (i < 2)
            {
                last = ex;
                await Task.Delay(200);
            }
        }
        throw last!;
    }

    // ── Token refresh ──────────────────────────────────────────────────────

    private async Task TryRefreshWithRetryAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Exit if the token now has ample time remaining (e.g. FSW delivered a fresh token
            // during the retry wait). Do NOT exit merely because the token is still technically
            // non-expired — we may be in the buffer window and a proactive refresh is needed.
            if (_store.GetTimeUntilRefresh(_options.CurrentValue.RefreshBufferMinutes) > TimeSpan.Zero)
                return;

            _store.SetRefreshing(true);
            RefreshResult result;
            try
            {
                result = await RefreshAccessTokenAsync(stoppingToken);
            }
            finally
            {
                _store.SetRefreshing(false);
            }

            if (result == RefreshResult.Success)
            {
                _logger.LogInformation("Token refresh succeeded");
                return;
            }

            if (result == RefreshResult.InvalidGrant)
            {
                _store.MarkReauthRequired();
                if (!_reauthWarningLogged)
                {
                    _reauthWarningLogged = true;
                    _logger.LogWarning(
                        "Refresh token is expired or revoked — re-authentication required. " +
                        "Run `claude login` in a terminal to restore access.");
                }
                return; // Do not retry: the refresh token will not become valid by retrying.
            }

            _logger.LogWarning(
                "Token refresh failed (network/transient), retrying in {Seconds}s",
                _options.CurrentValue.RetryIntervalSeconds);

            // Wait the retry interval; FSW can interrupt if it delivers a fresh token.
            try
            {
                using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                retryCts.CancelAfter(TimeSpan.FromSeconds(_options.CurrentValue.RetryIntervalSeconds));
                await _reloadSignal.Reader.ReadAsync(retryCts.Token);
                // If FSW delivered a fresh token with ample lifetime, exit the retry loop.
                if (_store.GetTimeUntilRefresh(_options.CurrentValue.RefreshBufferMinutes) > TimeSpan.Zero)
                    return;
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Retry interval elapsed.
            }
        }
    }

    /// <summary>
    /// Performs a direct OAuth <c>refresh_token</c> grant against the token endpoint (with a
    /// legacy-host fallback). This consumes no Claude usage. On success the rotated tokens are
    /// written back to the credentials file and loaded into the store.
    /// </summary>
    private async Task<RefreshResult> RefreshAccessTokenAsync(CancellationToken stoppingToken)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        RefreshResult aggregate = RefreshResult.TransientFailure;

        for (int i = 0; i < TokenEndpoints.Length; i++)
        {
            var endpoint = TokenEndpoints[i];

            if (!_store.TryCreateRefreshContent(ClientId, out var content) || content is null)
            {
                // No refresh token on hand — a fresh login is the only path forward.
                return RefreshResult.InvalidGrant;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };

            try
            {
                _logger.LogInformation("Requesting OAuth token refresh (endpoint {Index})", i + 1);

                using var response = await http.SendAsync(request, stoppingToken);
                var body = await response.Content.ReadAsStringAsync(stoppingToken);

                if (response.IsSuccessStatusCode)
                {
                    TokenRefreshResponse? parsed;
                    try { parsed = JsonSerializer.Deserialize<TokenRefreshResponse>(body); }
                    catch { parsed = null; }

                    if (string.IsNullOrEmpty(parsed?.AccessToken))
                    {
                        _logger.LogWarning("Refresh response contained no access token");
                        aggregate = RefreshResult.TransientFailure;
                        continue;
                    }

                    var expiresAt = DateTimeOffset.UtcNow.AddSeconds(
                        parsed.ExpiresIn > 0 ? parsed.ExpiresIn : 28800);

                    // Rotation: prefer the new refresh token, keep the old one if none returned.
                    _store.UpdateFromRefresh(parsed.AccessToken, parsed.RefreshToken, expiresAt);

                    await WriteCredentialsFileAsync(
                        parsed.AccessToken, parsed.RefreshToken, expiresAt.ToUnixTimeMilliseconds());

                    return RefreshResult.Success;
                }

                // 4xx → client-side failure (invalid/revoked grant). Retrying will not help.
                if ((int)response.StatusCode is >= 400 and < 500)
                {
                    var code = TryReadErrorCode(body);
                    _logger.LogWarning(
                        "Refresh grant rejected with HTTP {StatusCode}{Error}",
                        (int)response.StatusCode,
                        code is null ? string.Empty : " (" + code + ")");
                    return RefreshResult.InvalidGrant;
                }

                // 5xx → server-side transient; try the fallback host, else retry later.
                _logger.LogWarning("Refresh endpoint returned HTTP {StatusCode}", (int)response.StatusCode);
                aggregate = RefreshResult.TransientFailure;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Network error contacting refresh endpoint: {Reason}", ex.Message);
                aggregate = RefreshResult.TransientFailure;
            }
        }

        return aggregate;
    }

    // Reads only the machine-readable error code; never logs the raw body (may echo a token).
    private static string? TryReadErrorCode(string body)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<TokenRefreshResponse>(body);
            return string.IsNullOrEmpty(doc?.Error) ? null : doc!.Error;
        }
        catch { return null; }
    }

    // ── Credentials file write-back (atomic, permission-preserving) ─────────

    /// <summary>
    /// Writes the refreshed tokens back into the credentials file in the exact structure
    /// Claude Code expects, preserving every field the service does not manage. Uses a
    /// temp-file + rename to avoid a torn write, and copies the original file's ACL onto
    /// the replacement so restrictive permissions survive.
    /// </summary>
    private async Task WriteCredentialsFileAsync(
        string accessToken, string? refreshToken, long expiresAtMs)
    {
        var path = _options.CurrentValue.ResolvedCredentialsPath;
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
        {
            _logger.LogWarning("Cannot resolve credentials directory; skipping write-back");
            return;
        }

        try
        {
            // Load the existing document so unmanaged fields (refreshTokenExpiresAt, scopes,
            // subscriptionType, rateLimitTier, …) round-trip untouched.
            JsonObject root;
            if (File.Exists(path))
            {
                var existing = await File.ReadAllTextAsync(path);
                root = JsonNode.Parse(existing) as JsonObject ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            if (root["claudeAiOauth"] is not JsonObject oauth)
            {
                oauth = new JsonObject();
                root["claudeAiOauth"] = oauth;
            }

            oauth["accessToken"] = accessToken;
            if (!string.IsNullOrEmpty(refreshToken)) oauth["refreshToken"] = refreshToken;
            oauth["expiresAt"] = expiresAtMs;

            var serialized = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            // Mark our own imminent write so the FSW echo is ignored.
            Interlocked.Exchange(
                ref _suppressReloadUntilTicks,
                DateTimeOffset.UtcNow.AddSeconds(2).UtcTicks);

            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, serialized);
            CopyAclIfPresent(path, tmp);

            // Atomic replace (same volume). Overwrite in case a stale temp exists.
            File.Move(tmp, path, overwrite: true);

            _logger.LogInformation("Credentials file updated with refreshed token");
        }
        catch (Exception ex)
        {
            // In-memory tokens are already updated, so the widget keeps working this session;
            // Claude Code may need to refresh again on its own if the on-disk copy is stale.
            _logger.LogError("Failed to write refreshed credentials to disk: {Reason}", ex.Message);
        }
    }

    // Copies owner + DACL from the original file onto the replacement so restrictive
    // permissions are preserved. Best-effort: never fails the write.
    private void CopyAclIfPresent(string sourcePath, string targetPath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(sourcePath)) return;
        try
        {
            var src = new FileInfo(sourcePath).GetAccessControl();
            var security = new FileSecurity();
            security.SetSecurityDescriptorSddlForm(
                src.GetSecurityDescriptorSddlForm(
                    AccessControlSections.Access | AccessControlSections.Owner));
            new FileInfo(targetPath).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not copy credentials file ACL: {Reason}", ex.Message);
        }
    }

    // ── FileSystemWatcher ──────────────────────────────────────────────────

    private void SetupFileWatcher()
    {
        var path = _options.CurrentValue.ResolvedCredentialsPath;
        var dir = Path.GetDirectoryName(path);
        var file = Path.GetFileName(path);

        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            _logger.LogWarning("Credentials directory does not exist; file change watching is disabled");
            return;
        }

        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnCredentialsChanged;
        _watcher.Created += OnCredentialsChanged;
        _watcher.Renamed += OnCredentialsChanged;

        _logger.LogInformation("Watching credentials file for external changes");
    }

    private void OnCredentialsChanged(object sender, FileSystemEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            // Debounce: the file may still be mid-write.
            await Task.Delay(500);

            // Ignore the echo of our own atomic write-back.
            var suppressUntil = Interlocked.Read(ref _suppressReloadUntilTicks);
            if (DateTimeOffset.UtcNow.UtcTicks < suppressUntil)
                return;

            await TryLoadCredentialsAsync();
            _reloadSignal.Writer.TryWrite(true);
        });
    }
}
