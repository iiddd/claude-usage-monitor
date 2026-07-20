using System.Text;
using System.Text.Json;
using ClaudeUsageServer.Models;

namespace ClaudeUsageServer.Services;

/// <summary>
/// Thread-safe in-memory holder for the OAuth access and refresh tokens AND the single source
/// of truth for the service's coarse state (exposed via <c>GET /status</c>).
///
/// Neither token is ever exposed as a return value: the access token is injected into an
/// outgoing request via <see cref="TryApplyToken"/>, and the refresh token only ever leaves
/// this class already sealed inside the refresh-grant request body built by
/// <see cref="TryCreateRefreshContent"/>. Both are held in memory only, never written to a log.
///
/// State model (see <see cref="ComputeStateUnsafe"/>):
///   refreshing          — a refresh grant is currently in flight
///   reauth_required     — the refresh token was rejected (invalid_grant); not retryable
///   credentials_missing — the credentials file is absent/unreadable (no access token)
///   refresh_failed      — the last refresh attempt failed for a retryable reason
///   upstream_error      — token is fine but the Anthropic usage endpoint is failing
///   ok                  — token valid and upstream healthy
/// </summary>
internal sealed class CredentialStore : IDisposable
{
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset? _expiresAt;
    private DateTimeOffset? _lastRefresh;
    private DateTimeOffset? _lastRefreshAttempt;
    private string? _lastRefreshError;
    private int _consecutiveFailures;
    private DateTimeOffset? _nextRetryAt;
    private bool _fileFound;
    private bool _reauthRequired;
    private bool _refreshing;

    // Upstream (Anthropic usage endpoint) health — recorded by the /usage endpoint.
    private bool _upstreamHealthy = true;
    private int? _upstreamStatus;

    // Last computed state — used to fire the transition listener only on an actual change.
    private string _lastState = "startup";
    private Action<string, string, string>? _onTransition;

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    // ── Transition notification ────────────────────────────────────────────

    /// <summary>
    /// Registers a callback fired (old, new, reason) whenever a mutation changes the computed
    /// state. Time-based transitions (e.g. a valid token silently crossing its expiry) are not
    /// reported here — reads always compute the live state; only mutation-driven changes log.
    /// </summary>
    public void SetTransitionListener(Action<string, string, string> listener) => _onTransition = listener;

    /// <summary>Runs a write-locked mutation, then fires the transition listener if state changed.</summary>
    private void MutateAndNotify(string reason, Action mutation)
    {
        string oldState, newState;
        _lock.EnterWriteLock();
        try
        {
            oldState = _lastState;
            mutation();
            newState = ComputeStateUnsafe();
            _lastState = newState;
        }
        finally { _lock.ExitWriteLock(); }

        if (oldState != newState)
            _onTransition?.Invoke(oldState, newState, reason);
    }

    // Assumes the caller holds a lock. Mirrors the precedence documented on the class.
    private string ComputeStateUnsafe()
    {
        bool tokenValid = _accessToken is not null
            && _expiresAt is not null
            && _expiresAt.Value > DateTimeOffset.UtcNow;

        if (_refreshing)                            return "refreshing";
        if (_reauthRequired)                        return "reauth_required";
        if (!_fileFound || _accessToken is null)    return "credentials_missing";
        if (_consecutiveFailures > 0)               return "refresh_failed";
        if (!_upstreamHealthy)                      return "upstream_error";
        if (tokenValid)                             return "ok";
        return "refresh_failed"; // token present but expired and no explicit failure recorded yet
    }

    // ── Read accessors (no token material) ─────────────────────────────────

    public bool HasValidToken
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _accessToken is not null
                    && _expiresAt is not null
                    && _expiresAt.Value > DateTimeOffset.UtcNow;
            }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>True once a refresh has failed with a client-side (invalid_grant) error.</summary>
    public bool ReauthRequired
    {
        get
        {
            _lock.EnterReadLock();
            try { return _reauthRequired; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public bool FileFound
    {
        get { _lock.EnterReadLock(); try { return _fileFound; } finally { _lock.ExitReadLock(); } }
    }

    public DateTimeOffset? ExpiresAt
    {
        get { _lock.EnterReadLock(); try { return _expiresAt; } finally { _lock.ExitReadLock(); } }
    }

    public int ConsecutiveFailures
    {
        get { _lock.EnterReadLock(); try { return _consecutiveFailures; } finally { _lock.ExitReadLock(); } }
    }

    /// <summary>
    /// The machine-readable error string to return from <c>GET /usage</c>, or null when it is
    /// OK to call upstream. Vocabulary matches <c>/status</c> state names.
    /// </summary>
    public string? GetUsageError()
    {
        _lock.EnterReadLock();
        try
        {
            if (_reauthRequired) return "reauth_required";
            if (!_fileFound || _accessToken is null) return "credentials_missing";
            bool tokenValid = _expiresAt is not null && _expiresAt.Value > DateTimeOffset.UtcNow;
            if (!tokenValid) return "refresh_failed";
            return null;
        }
        finally { _lock.ExitReadLock(); }
    }

    // ── Token injection (never returns a token as a string) ────────────────

    /// <summary>
    /// Injects the bearer token directly into <paramref name="request"/> without
    /// returning it as a string. Returns false if no valid token is available.
    /// </summary>
    public bool TryApplyToken(HttpRequestMessage request)
    {
        _lock.EnterReadLock();
        try
        {
            if (_accessToken is null) return false;
            if (_expiresAt is not null && _expiresAt.Value <= DateTimeOffset.UtcNow) return false;
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _accessToken);
            return true;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Builds the <c>refresh_token</c> grant request body (JSON) with the in-memory refresh
    /// token, so the token is serialized here and never handed back to callers as a bare string.
    /// Returns false when no refresh token is available.
    /// </summary>
    public bool TryCreateRefreshContent(string clientId, out HttpContent? content)
    {
        _lock.EnterReadLock();
        try
        {
            if (string.IsNullOrEmpty(_refreshToken))
            {
                content = null;
                return false;
            }

            var payload = new
            {
                grant_type = "refresh_token",
                refresh_token = _refreshToken,
                client_id = clientId,
            };
            var json = JsonSerializer.Serialize(payload);
            content = new StringContent(json, Encoding.UTF8, "application/json");
            return true;
        }
        finally { _lock.ExitReadLock(); }
    }

    // ── Mutations ──────────────────────────────────────────────────────────

    /// <summary>
    /// Loads tokens read from the credentials file (or a successful refresh grant). Clears
    /// reauth-required and any transient-failure bookkeeping — a fresh token is a clean slate.
    /// </summary>
    public void Update(string accessToken, string? refreshToken, DateTimeOffset expiresAt, string reason = "loaded")
        => MutateAndNotify(reason, () =>
        {
            _fileFound = true;
            _accessToken = accessToken;
            if (!string.IsNullOrEmpty(refreshToken)) _refreshToken = refreshToken;
            _expiresAt = expiresAt;
            _lastRefresh = DateTimeOffset.UtcNow;
            _reauthRequired = false;
            _refreshing = false;
            _consecutiveFailures = 0;
            _lastRefreshError = null;
            _nextRetryAt = null;
        });

    /// <summary>Applies the result of a successful direct refresh grant (token rotation-aware).</summary>
    public void UpdateFromRefresh(string accessToken, string? refreshToken, DateTimeOffset expiresAt)
        => Update(accessToken, refreshToken, expiresAt, reason: "refresh-success");

    public void MarkFileNotFound()
        => MutateAndNotify("file-not-found", () =>
        {
            _fileFound = false;
            _accessToken = null;
            _refreshToken = null;
            _expiresAt = null;
        });

    public void MarkFileInvalid()
        => MutateAndNotify("file-invalid", () =>
        {
            _fileFound = true;
            _accessToken = null;
        });

    /// <summary>Marks a refresh grant as in flight and records the attempt time.</summary>
    public void MarkRefreshStarted(string reason)
        => MutateAndNotify(reason, () =>
        {
            _refreshing = true;
            _lastRefreshAttempt = DateTimeOffset.UtcNow;
        });

    /// <summary>Records a retryable refresh failure (network / 5xx) and when the next retry is due.</summary>
    public void MarkRefreshFailed(string error, DateTimeOffset nextRetryAt)
        => MutateAndNotify("refresh-failed:" + error, () =>
        {
            _refreshing = false;
            _consecutiveFailures++;
            _lastRefreshError = error;
            _nextRetryAt = nextRetryAt;
        });

    /// <summary>
    /// Records that the refresh token itself is no longer valid: only a fresh
    /// <c>claude login</c> (picked up via the file watcher) can recover from here.
    /// </summary>
    public void MarkReauthRequired(string error = "invalid_grant")
        => MutateAndNotify("reauth-required:" + error, () =>
        {
            _reauthRequired = true;
            _refreshing = false;
            _lastRefreshError = error;
            _nextRetryAt = null;
        });

    public void SetRefreshing(bool refreshing)
        => MutateAndNotify(refreshing ? "refreshing" : "refresh-done", () => _refreshing = refreshing);

    /// <summary>Records that the Anthropic usage endpoint failed (token itself may be fine).</summary>
    public void MarkUpstreamError(int? httpStatus)
        => MutateAndNotify("upstream-error", () =>
        {
            _upstreamHealthy = false;
            _upstreamStatus = httpStatus;
        });

    /// <summary>Records a successful Anthropic usage call — clears any upstream_error state.</summary>
    public void MarkUpstreamHealthy()
        => MutateAndNotify("upstream-ok", () =>
        {
            _upstreamHealthy = true;
            _upstreamStatus = null;
        });

    /// <summary>
    /// Returns the time remaining until the token should be refreshed (expiresAt minus buffer).
    /// Returns <see cref="TimeSpan.Zero"/> or negative when refresh is overdue.
    /// </summary>
    public TimeSpan GetTimeUntilRefresh(int bufferMinutes)
    {
        _lock.EnterReadLock();
        try
        {
            if (_expiresAt is null) return TimeSpan.Zero;
            return _expiresAt.Value - DateTimeOffset.UtcNow - TimeSpan.FromMinutes(bufferMinutes);
        }
        finally { _lock.ExitReadLock(); }
    }

    // ── Status snapshot ────────────────────────────────────────────────────

    public StatusResponse GetStatus()
    {
        _lock.EnterReadLock();
        try
        {
            bool valid = _accessToken is not null
                && _expiresAt is not null
                && _expiresAt.Value > DateTimeOffset.UtcNow;

            var state = ComputeStateUnsafe();

            return new StatusResponse(
                State:              state,
                TokenValid:         valid,
                ExpiresAt:          _expiresAt?.ToString("O"),
                LastRefresh:        _lastRefresh?.ToString("O"),
                LastRefreshAttempt: _lastRefreshAttempt?.ToString("O"),
                LastRefreshError:   _lastRefreshError,
                ConsecutiveFailures: _consecutiveFailures,
                NextRetryAt:        state == "refresh_failed" ? _nextRetryAt?.ToString("O") : null,
                UpstreamStatus:     state == "upstream_error" ? _upstreamStatus : null);
        }
        finally { _lock.ExitReadLock(); }
    }

    public void Dispose() => _lock.Dispose();
}
