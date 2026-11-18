namespace ClaudeUsageServer.Services;

/// <summary>
/// Guards calls to the Anthropic usage endpoint:
///   • single flight — <see cref="Gate"/> lets one request call upstream at a time, so
///     concurrent cache misses (e.g. two widget instances polling together) share one call;
///   • rate-limit backoff — after an HTTP 429 no upstream call is made until the backoff
///     elapses: the server's <c>Retry-After</c> when given, otherwise 1, 2, 4, 8, then 15 min.
/// </summary>
internal sealed class UpstreamThrottle(TimeProvider time)
{
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);

    private readonly object _lock = new();
    private DateTimeOffset? _blockedUntil;
    private int _consecutiveRateLimits;

    /// <summary>Held while calling upstream; waiters re-check the cache once they get it.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>The time until which upstream calls are paused, or null when not rate-limited.</summary>
    public DateTimeOffset? BlockedUntil
    {
        get
        {
            lock (_lock)
            {
                return _blockedUntil is { } until && until > time.GetUtcNow() ? until : null;
            }
        }
    }

    /// <summary>How long until upstream calls resume, or null when not rate-limited.</summary>
    public TimeSpan? RetryIn
    {
        get
        {
            lock (_lock)
            {
                var remaining = _blockedUntil - time.GetUtcNow();
                return remaining > TimeSpan.Zero ? remaining : null;
            }
        }
    }

    /// <summary>
    /// Records an HTTP 429 and returns how long upstream calls are paused.
    /// <paramref name="retryAfter"/> is the server's hint, if it sent one.
    /// </summary>
    public TimeSpan RecordRateLimited(TimeSpan? retryAfter)
    {
        lock (_lock)
        {
            _consecutiveRateLimits++;

            var delay = retryAfter is { } hint && hint > TimeSpan.Zero
                ? (hint < MaxRetryAfter ? hint : MaxRetryAfter)
                : ExponentialBackoff(_consecutiveRateLimits);

            _blockedUntil = time.GetUtcNow() + delay;
            return delay;
        }
    }

    /// <summary>Records a successful upstream call — clears any backoff.</summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveRateLimits = 0;
            _blockedUntil = null;
        }
    }

    private static TimeSpan ExponentialBackoff(int attempt)
    {
        var minutes = BaseBackoff.TotalMinutes * Math.Pow(2, Math.Min(attempt - 1, 10));
        return minutes < MaxBackoff.TotalMinutes ? TimeSpan.FromMinutes(minutes) : MaxBackoff;
    }
}
