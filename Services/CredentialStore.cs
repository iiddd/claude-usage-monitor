using ClaudeUsageServer.Models;

namespace ClaudeUsageServer.Services;

internal enum CredentialState { NotFound, Expired, Valid }

/// <summary>
/// Thread-safe in-memory holder for the OAuth token.
/// The token is never exposed as a return value; callers inject it directly into
/// an outgoing HttpRequestMessage via TryApplyToken.
/// </summary>
internal sealed class CredentialStore : IDisposable
{
    private string? _accessToken;
    private DateTimeOffset? _expiresAt;
    private DateTimeOffset? _lastRefresh;
    private bool _fileFound;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    public CredentialState State
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                if (!_fileFound || _accessToken is null) return CredentialState.NotFound;
                if (_expiresAt is null || _expiresAt.Value <= DateTimeOffset.UtcNow)
                    return CredentialState.Expired;
                return CredentialState.Valid;
            }
            finally { _lock.ExitReadLock(); }
        }
    }

    public bool HasValidToken => State == CredentialState.Valid;

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

    public void Update(string accessToken, DateTimeOffset expiresAt)
    {
        _lock.EnterWriteLock();
        try
        {
            _fileFound = true;
            _accessToken = accessToken;
            _expiresAt = expiresAt;
            _lastRefresh = DateTimeOffset.UtcNow;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void MarkFileNotFound()
    {
        _lock.EnterWriteLock();
        try
        {
            _fileFound = false;
            _accessToken = null;
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
            return new StatusResponse(
                TokenValid: valid,
                ExpiresAt: _expiresAt?.ToString("O"),
                LastRefresh: _lastRefresh?.ToString("O"));
        }
        finally { _lock.ExitReadLock(); }
    }

    public void Dispose() => _lock.Dispose();
}
