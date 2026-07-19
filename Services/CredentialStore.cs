using System.Text;
using System.Text.Json;
using ClaudeUsageServer.Models;

namespace ClaudeUsageServer.Services;

internal enum CredentialState { NotFound, Expired, Valid, ReauthRequired }

/// <summary>
/// Thread-safe in-memory holder for the OAuth access and refresh tokens.
/// Neither token is ever exposed as a return value: the access token is injected into an
/// outgoing request via <see cref="TryApplyToken"/>, and the refresh token only ever leaves
/// this class already sealed inside the refresh-grant request body built by
/// <see cref="TryCreateRefreshContent"/>. Both are held in memory only, never written to a log.
/// </summary>
internal sealed class CredentialStore : IDisposable
{
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset? _expiresAt;
    private DateTimeOffset? _lastRefresh;
    private bool _fileFound;
    private bool _reauthRequired;
    private bool _refreshing;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    public CredentialState State
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                if (_reauthRequired) return CredentialState.ReauthRequired;
                if (!_fileFound || _accessToken is null) return CredentialState.NotFound;
                if (_expiresAt is null || _expiresAt.Value <= DateTimeOffset.UtcNow)
                    return CredentialState.Expired;
                return CredentialState.Valid;
            }
            finally { _lock.ExitReadLock(); }
        }
    }

    public bool HasValidToken => State == CredentialState.Valid;

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

    /// <summary>Loads tokens read from the credentials file. Clears any reauth-required state.</summary>
    public void Update(string accessToken, string? refreshToken, DateTimeOffset expiresAt)
    {
        _lock.EnterWriteLock();
        try
        {
            _fileFound = true;
            _accessToken = accessToken;
            if (!string.IsNullOrEmpty(refreshToken)) _refreshToken = refreshToken;
            _expiresAt = expiresAt;
            _lastRefresh = DateTimeOffset.UtcNow;
            _reauthRequired = false;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Applies the result of a successful direct refresh grant (token rotation-aware).</summary>
    public void UpdateFromRefresh(string accessToken, string? refreshToken, DateTimeOffset expiresAt)
        => Update(accessToken, refreshToken, expiresAt);

    public void MarkFileNotFound()
    {
        _lock.EnterWriteLock();
        try
        {
            _fileFound = false;
            _accessToken = null;
            _refreshToken = null;
            _expiresAt = null;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void MarkFileInvalid()
    {
        _lock.EnterWriteLock();
        try
        {
            _fileFound = true;
            _accessToken = null;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Records that the refresh token itself is no longer valid: only a fresh
    /// <c>claude login</c> (picked up via the file watcher) can recover from here.
    /// </summary>
    public void MarkReauthRequired()
    {
        _lock.EnterWriteLock();
        try
        {
            _reauthRequired = true;
            _refreshing = false;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void SetRefreshing(bool refreshing)
    {
        _lock.EnterWriteLock();
        try { _refreshing = refreshing; }
        finally { _lock.ExitWriteLock(); }
    }

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

    public StatusResponse GetStatus()
    {
        _lock.EnterReadLock();
        try
        {
            bool valid = _accessToken is not null
                && _expiresAt is not null
                && _expiresAt.Value > DateTimeOffset.UtcNow;

            string state =
                _reauthRequired ? "reauth_required"
                : _refreshing   ? "refreshing"
                : valid         ? "ok"
                :                 "error";

            return new StatusResponse(
                State: state,
                TokenValid: valid,
                ExpiresAt: _expiresAt?.ToString("O"),
                LastRefresh: _lastRefresh?.ToString("O"));
        }
        finally { _lock.ExitReadLock(); }
    }

    public void Dispose() => _lock.Dispose();
}
