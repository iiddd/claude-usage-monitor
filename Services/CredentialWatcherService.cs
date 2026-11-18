using System.Security.AccessControl;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ClaudeUsageServer.Models;
using Microsoft.Extensions.Options;

namespace ClaudeUsageServer.Services;

internal sealed class CredentialWatcherService : BackgroundService
{
    /// <summary>Public OAuth client id the Claude Code CLI itself uses for its token grants.</summary>
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    /// <summary>Token hosts in the order the CLI tries them: the platform host, then the legacy console host.</summary>
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

    /// <summary>How long a credentials file change may still be mid-write when the watcher reports it.</summary>
    private static readonly TimeSpan WriteSettleDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Window after our own write-back during which watcher events are treated as its echo.</summary>
    private static readonly TimeSpan OwnWriteEchoWindow = TimeSpan.FromSeconds(2);

    /// <summary>Watcher events fired before this time (UTC ticks) are the echo of our own write-back.</summary>
    private long _suppressReloadUntilTicks;

    /// <summary>Capacity-1 channel: several rapid watcher events collapse into one reload signal.</summary>
    private readonly Channel<bool> _reloadSignal = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    private enum RefreshResult { Success, InvalidGrant, TransientFailure }

    /// <summary>
    /// Outcome of one refresh grant, carrying just enough (never token material, never a raw
    /// body) to log a diagnostic line and set the machine-readable <c>lastRefreshError</c>.
    /// </summary>
    private readonly record struct RefreshOutcome(
        RefreshResult Result,
        int? HttpStatus,
        string? OAuthError,
        string? ExceptionType,
        bool RefreshTokenRotated,
        DateTimeOffset? NewExpiresAt)
    {
        /// <summary>Short machine-readable reason stored as <c>lastRefreshError</c>.</summary>
        public string Reason => OAuthError
            ?? (HttpStatus is not null ? "http_" + HttpStatus : null)
            ?? ExceptionType
            ?? "unknown";
    }

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

    /// <summary>
    /// Loads the credentials, then keeps the token fresh: waits until the refresh window opens
    /// (or the file changes) and runs a refresh grant. While re-authentication is required no
    /// grant is attempted, since only a fresh <c>claude login</c>, arriving as a file change, can help.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ClaudeUsageServer starting");

        _store.SetTransitionListener((oldState, newState, reason) =>
            _logger.LogDebug("state transition {Old} -> {New} reason={Reason}", oldState, newState, reason));

        await TryLoadCredentialsAsync(NewCid());

        LogStartupBreadcrumb();
        SetupFileWatcher();

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_store.ReauthRequired)
            {
                if (!await CredentialsChangedAsync(stoppingToken)) break;
                continue;
            }

            var buffer = _options.CurrentValue.RefreshBufferMinutes;
            var timeUntilRefresh = _store.GetTimeUntilRefresh(buffer);

            if (timeUntilRefresh > TimeSpan.Zero)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug(
                        "refresh scheduled at {At:O} (in {Mins:F1} min) from expiresAt {ExpiresAt:O} minus {Buffer}m buffer",
                        DateTimeOffset.UtcNow + timeUntilRefresh, timeUntilRefresh.TotalMinutes,
                        _store.ExpiresAt, buffer);

                if (await CredentialsChangedWithinAsync(timeUntilRefresh, stoppingToken)) continue;
                await TryRefreshWithRetryAsync("scheduled", stoppingToken);
            }
            else
            {
                await TryRefreshWithRetryAsync("startup-expired", stoppingToken);
            }
        }

        _logger.LogInformation("ClaudeUsageServer stopping");
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Waits for the file watcher to report reloaded credentials. Returns false when the service
    /// is stopping instead.
    /// </summary>
    private async Task<bool> CredentialsChangedAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _reloadSignal.Reader.ReadAsync(stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the file watcher to report reloaded credentials.
    /// Returns true when they changed (already reloaded by the watcher), false when the time ran out.
    /// </summary>
    private async Task<bool> CredentialsChangedWithinAsync(TimeSpan timeout, CancellationToken stoppingToken)
    {
        try
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            waitCts.CancelAfter(timeout);
            await _reloadSignal.Reader.ReadAsync(waitCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Short correlation id tying together the log lines of one reload or refresh cycle.</summary>
    private static string NewCid() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Self-describing header written once per service start (Information, always — independent
    /// of DiagnosticLogging) so any log file can be correlated to an incident after the fact.
    /// Never logs paths or any token material.
    /// </summary>
    private void LogStartupBreadcrumb()
    {
        var o = _options.CurrentValue;
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString() ?? "unknown";
        var status = _store.GetStatus();

        _logger.LogInformation(
            "startup breadcrumb: version={Version} Port={Port} RefreshBufferMinutes={Buffer} " +
            "RetryIntervalSeconds={Retry} CacheTtlSeconds={Ttl} DiagnosticLogging={Diag} " +
            "credentialsPresent={Present} expiresAt={ExpiresAt} initialState={State}",
            version, o.Port, o.RefreshBufferMinutes, o.RetryIntervalSeconds, o.CacheTtlSeconds,
            o.DiagnosticLogging, _store.FileFound, status.ExpiresAt ?? "none", status.State);
    }

    /// <summary>
    /// Reads the credentials file into the store. The log line records only whether each token is
    /// present, never the values, lengths or prefixes of any token.
    /// </summary>
    private async Task TryLoadCredentialsAsync(string cid)
    {
        var path = _options.CurrentValue.ResolvedCredentialsPath;
        try
        {
            var lastWrite = File.Exists(path)
                ? File.GetLastWriteTimeUtc(path)
                : (DateTime?)null;

            string json = await ReadWithRetryAsync(path);

            var file = JsonSerializer.Deserialize<CredentialsFile>(json);
            var oauth = file?.ClaudeAiOauth;

            var expiresAt = oauth is not null && oauth.ExpiresAt > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(oauth.ExpiresAt)
                : (DateTimeOffset?)null;

            _logger.LogDebug(
                "[{Cid}] credentials read: lastWriteUtc={LastWrite:O} expiresAt={ExpiresAt} hasAccessToken={HasAccess} hasRefreshToken={HasRefresh}",
                cid, lastWrite, expiresAt?.ToString("O") ?? "none",
                !string.IsNullOrWhiteSpace(oauth?.AccessToken),
                !string.IsNullOrWhiteSpace(oauth?.RefreshToken));

            if (string.IsNullOrWhiteSpace(oauth?.AccessToken))
            {
                _logger.LogWarning("Credentials file does not contain a valid access token");
                _store.MarkFileInvalid();
                return;
            }

            _store.Update(oauth.AccessToken, oauth.RefreshToken,
                expiresAt ?? DateTimeOffset.UtcNow, reason: "file-loaded");
            _reauthWarningLogged = false;
            _logger.LogInformation("Credentials loaded, token expires at {ExpiresAt:O}", expiresAt);
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning("Credentials file not found");
            _logger.LogDebug("[{Cid}] credentials read: file not found", cid);
            _store.MarkFileNotFound();
        }
        catch (DirectoryNotFoundException)
        {
            _logger.LogWarning("Credentials directory not found");
            _logger.LogDebug("[{Cid}] credentials read: directory not found", cid);
            _store.MarkFileNotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error reading credentials file");
            _logger.LogDebug("[{Cid}] credentials read failed: {ExType}", cid, ex.GetType().Name);
            _store.MarkFileInvalid();
        }
    }

    /// <summary>Reads the file, retrying up to 3 times with a short delay to ride out write-lock races.</summary>
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

    /// <summary>
    /// Runs refresh grants until one succeeds, the refresh token is rejected (retrying cannot
    /// revive it), or the token no longer needs refreshing. Transient failures (network, 5xx) are
    /// retried every <c>RetryIntervalSeconds</c>; a file change can end the wait early.
    /// </summary>
    private async Task TryRefreshWithRetryAsync(string trigger, CancellationToken stoppingToken)
    {
        var cid = NewCid();

        while (!stoppingToken.IsCancellationRequested)
        {
            if (TokenOutsideRefreshWindow())
                return;

            var attempt = _store.ConsecutiveFailures + 1;
            var attemptTrigger = attempt == 1 ? trigger : "retry";
            _logger.LogDebug(
                "[{Cid}] refresh attempt start trigger={Trigger} attempt={Attempt}",
                cid, attemptTrigger, attempt);

            _store.MarkRefreshStarted("refresh-start:" + attemptTrigger);
            RefreshOutcome outcome;
            try
            {
                outcome = await RefreshAccessTokenAsync(cid, stoppingToken);
            }
            finally
            {
                ClearRefreshingFlag();
            }

            if (outcome.Result == RefreshResult.Success)
            {
                _logger.LogDebug(
                    "[{Cid}] refresh outcome=success newExpiresAt={ExpiresAt:O} refreshTokenRotated={Rotated}",
                    cid, outcome.NewExpiresAt, outcome.RefreshTokenRotated);
                _logger.LogInformation("Token refresh succeeded");
                return;
            }

            if (outcome.Result == RefreshResult.InvalidGrant)
            {
                _logger.LogDebug(
                    "[{Cid}] refresh outcome=invalid_grant httpStatus={Status} oauthError={Error}",
                    cid, outcome.HttpStatus, outcome.OAuthError ?? "none");
                _store.MarkReauthRequired(outcome.Reason);
                if (!_reauthWarningLogged)
                {
                    _reauthWarningLogged = true;
                    _logger.LogWarning(
                        "Refresh token is expired or revoked — re-authentication required. " +
                        "Run `claude login` in a terminal to restore access.");
                }
                return;
            }

            var retrySeconds = _options.CurrentValue.RetryIntervalSeconds;
            var nextRetryAt = DateTimeOffset.UtcNow.AddSeconds(retrySeconds);
            _store.MarkRefreshFailed(outcome.Reason, nextRetryAt);

            _logger.LogDebug(
                "[{Cid}] refresh outcome=transient reason={Reason} httpStatus={Status} exType={ExType} nextRetryAt={Next:O} streak={Streak}",
                cid, outcome.Reason, outcome.HttpStatus, outcome.ExceptionType ?? "none",
                nextRetryAt, _store.ConsecutiveFailures);
            _logger.LogWarning(
                "Token refresh failed (network/transient), retrying in {Seconds}s", retrySeconds);

            await CredentialsChangedWithinAsync(TimeSpan.FromSeconds(retrySeconds), stoppingToken);
        }
    }

    /// <summary>
    /// True when the token has time left before its refresh window opens, e.g. because the file
    /// watcher delivered a fresh token. A token that is merely unexpired does not count: inside the
    /// buffer window a proactive refresh is still due.
    /// </summary>
    private bool TokenOutsideRefreshWindow() =>
        _store.GetTimeUntilRefresh(_options.CurrentValue.RefreshBufferMinutes) > TimeSpan.Zero;

    /// <summary>
    /// Safety net after a grant: clears the in-flight flag in case some path left it set.
    /// A no-op when it is already clear or re-authentication is required.
    /// </summary>
    private void ClearRefreshingFlag()
    {
        if (!_store.ReauthRequired)
            _store.SetRefreshing(false);
    }

    /// <summary>
    /// Performs a direct OAuth <c>refresh_token</c> grant against the token endpoint (with a
    /// legacy-host fallback). This consumes no Claude usage. On success the rotated tokens are
    /// written back to the credentials file and loaded into the store; a response without a new
    /// refresh token keeps the current one. A 4xx means the grant was rejected and retrying cannot
    /// help; a 5xx or network error moves on to the next host. Without a refresh token on hand
    /// only a fresh login can help, so that also counts as a rejected grant.
    /// </summary>
    private async Task<RefreshOutcome> RefreshAccessTokenAsync(string cid, CancellationToken stoppingToken)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        RefreshOutcome aggregate = new(RefreshResult.TransientFailure, null, null, null, false, null);

        for (int i = 0; i < TokenEndpoints.Length; i++)
        {
            var endpoint = TokenEndpoints[i];

            if (!_store.TryCreateRefreshContent(ClientId, out var content) || content is null)
            {
                return new RefreshOutcome(
                    RefreshResult.InvalidGrant, null, "no_refresh_token", null, false, null);
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
                        aggregate = new RefreshOutcome(
                            RefreshResult.TransientFailure, (int)response.StatusCode,
                            "no_access_token", null, false, null);
                        continue;
                    }

                    var expiresAt = DateTimeOffset.UtcNow.AddSeconds(
                        parsed.ExpiresIn > 0 ? parsed.ExpiresIn : 28800);
                    bool rotated = !string.IsNullOrEmpty(parsed.RefreshToken);

                    _store.UpdateFromRefresh(parsed.AccessToken, parsed.RefreshToken, expiresAt);

                    await WriteCredentialsFileAsync(
                        cid, parsed.AccessToken, parsed.RefreshToken, expiresAt.ToUnixTimeMilliseconds());

                    return new RefreshOutcome(
                        RefreshResult.Success, (int)response.StatusCode, null, null, rotated, expiresAt);
                }

                if (IsClientError(response.StatusCode))
                {
                    var code = TryReadErrorCode(body);
                    _logger.LogWarning(
                        "Refresh grant rejected with HTTP {StatusCode}{Error}",
                        (int)response.StatusCode,
                        code is null ? string.Empty : " (" + code + ")");
                    return new RefreshOutcome(
                        RefreshResult.InvalidGrant, (int)response.StatusCode,
                        code ?? "invalid_grant", null, false, null);
                }

                _logger.LogWarning("Refresh endpoint returned HTTP {StatusCode}", (int)response.StatusCode);
                aggregate = new RefreshOutcome(
                    RefreshResult.TransientFailure, (int)response.StatusCode, null, null, false, null);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Network error contacting refresh endpoint: {Reason}", ex.Message);
                aggregate = new RefreshOutcome(
                    RefreshResult.TransientFailure, null, null, ex.GetType().Name, false, null);
            }
        }

        return aggregate;
    }

    private static bool IsClientError(System.Net.HttpStatusCode status) => (int)status is >= 400 and < 500;

    /// <summary>
    /// Reads only the machine-readable OAuth error code from a grant response. The raw body is
    /// never logged, because a misbehaving server may echo a token in it.
    /// </summary>
    private static string? TryReadErrorCode(string body)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<TokenRefreshResponse>(body);
            return string.IsNullOrEmpty(doc?.Error) ? null : doc!.Error;
        }
        catch { return null; }
    }

    /// <summary>
    /// Writes the refreshed tokens back into the credentials file in the exact structure
    /// Claude Code expects, preserving every field the service does not manage. Uses a
    /// temp-file + rename (same volume) to avoid a torn write, and copies the original file's ACL
    /// onto the replacement so restrictive permissions survive. If the write fails, the in-memory
    /// tokens still serve the widget this session; Claude Code may refresh again on its own.
    /// </summary>
    private async Task WriteCredentialsFileAsync(
        string cid, string accessToken, string? refreshToken, long expiresAtMs)
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
            var root = await LoadCredentialsDocumentAsync(path);

            if (root["claudeAiOauth"] is not JsonObject oauth)
            {
                oauth = new JsonObject();
                root["claudeAiOauth"] = oauth;
            }

            oauth["accessToken"] = accessToken;
            if (!string.IsNullOrEmpty(refreshToken)) oauth["refreshToken"] = refreshToken;
            oauth["expiresAt"] = expiresAtMs;

            var serialized = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            SuppressReloadEchoOfOwnWrite();

            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, serialized);
            CopyAclIfPresent(path, tmp);

            File.Move(tmp, path, overwrite: true);

            _logger.LogDebug("[{Cid}] credentials file write-back complete (FSW echo suppressed {Seconds}s)", cid, OwnWriteEchoWindow.TotalSeconds);
            _logger.LogInformation("Credentials file updated with refreshed token");
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to write refreshed credentials to disk: {Reason}", ex.Message);
        }
    }

    /// <summary>
    /// Loads the existing credentials document so fields the service does not manage
    /// (<c>refreshTokenExpiresAt</c>, <c>scopes</c>, <c>subscriptionType</c>, …) round-trip untouched.
    /// </summary>
    private static async Task<JsonObject> LoadCredentialsDocumentAsync(string path)
    {
        if (!File.Exists(path)) return new JsonObject();

        var existing = await File.ReadAllTextAsync(path);
        return JsonNode.Parse(existing) as JsonObject ?? new JsonObject();
    }

    private void SuppressReloadEchoOfOwnWrite() =>
        Interlocked.Exchange(
            ref _suppressReloadUntilTicks,
            DateTimeOffset.UtcNow.Add(OwnWriteEchoWindow).UtcTicks);

    private bool IsEchoOfOwnWrite() =>
        DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _suppressReloadUntilTicks);

    /// <summary>
    /// Copies owner and DACL from the original file onto the replacement so restrictive
    /// permissions are preserved. Best-effort: never fails the write.
    /// </summary>
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
            var cid = NewCid();

            await Task.Delay(WriteSettleDelay);

            if (IsEchoOfOwnWrite())
            {
                _logger.LogDebug(
                    "[{Cid}] FSW event {ChangeType}: ignored (self-write echo)", cid, e.ChangeType);
                return;
            }

            _logger.LogDebug("[{Cid}] FSW event {ChangeType}: processing reload", cid, e.ChangeType);
            await TryLoadCredentialsAsync(cid);
            _logger.LogDebug(
                "[{Cid}] FSW reload complete: expiresAt={ExpiresAt:O}", cid, _store.ExpiresAt);
            _reloadSignal.Writer.TryWrite(true);
        });
    }
}
