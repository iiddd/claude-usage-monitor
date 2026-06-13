namespace ClaudeUsageServer.Services;

/// <summary>
/// Thread-safe in-memory cache for the last successful Anthropic usage response.
/// Cache entries are never written to disk.
/// </summary>
internal sealed class UsageCache
{
    private string? _body;
    private DateTimeOffset? _fetchedAt;
    private readonly object _lock = new();

    /// <summary>
    /// Returns true and the cached body when a fresh entry (younger than
    /// <paramref name="ttlSeconds"/>) is available. Returns false otherwise.
    /// </summary>
    public bool TryGetFresh(int ttlSeconds, out string body)
    {
        lock (_lock)
        {
            if (_body is null || _fetchedAt is null)
            {
                body = string.Empty;
                return false;
            }

            if (DateTimeOffset.UtcNow - _fetchedAt.Value > TimeSpan.FromSeconds(ttlSeconds))
            {
                body = string.Empty;
                return false;
            }

            body = _body;
            return true;
        }
    }

    /// <summary>
    /// Returns true and the most recently stored body regardless of age.
    /// Used as a stale fallback when the upstream returns an error.
    /// </summary>
    public bool TryGetStale(out string body)
    {
        lock (_lock)
        {
            if (_body is null)
            {
                body = string.Empty;
                return false;
            }

            body = _body;
            return true;
        }
    }

    /// <summary>Stores a successful response body and records the current UTC time.</summary>
    public void Store(string body)
    {
        lock (_lock)
        {
            _body = body;
            _fetchedAt = DateTimeOffset.UtcNow;
        }
    }
}
