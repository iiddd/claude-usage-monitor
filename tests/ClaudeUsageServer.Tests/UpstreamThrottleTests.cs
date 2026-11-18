using ClaudeUsageServer.Services;

namespace ClaudeUsageServer.Tests;

public class UpstreamThrottleTests
{
    [Fact]
    public void Not_rate_limited_initially()
    {
        var throttle = new UpstreamThrottle(new ManualTimeProvider());

        Assert.Null(throttle.RetryIn);
        Assert.Null(throttle.BlockedUntil);
    }

    [Fact]
    public void Backoff_without_server_hint_doubles_up_to_fifteen_minutes()
    {
        var throttle = new UpstreamThrottle(new ManualTimeProvider());

        var delays = Enumerable.Range(0, 6)
            .Select(_ => throttle.RecordRateLimited(retryAfter: null).TotalMinutes)
            .ToArray();

        Assert.Equal(new double[] { 1, 2, 4, 8, 15, 15 }, delays);
    }

    [Fact]
    public void Server_retry_after_is_honoured_and_capped_at_one_hour()
    {
        var throttle = new UpstreamThrottle(new ManualTimeProvider());

        Assert.Equal(TimeSpan.FromSeconds(90), throttle.RecordRateLimited(TimeSpan.FromSeconds(90)));
        Assert.Equal(TimeSpan.FromHours(1), throttle.RecordRateLimited(TimeSpan.FromHours(5)));
    }

    [Fact]
    public void Backoff_expires_with_time()
    {
        var time = new ManualTimeProvider();
        var throttle = new UpstreamThrottle(time);
        throttle.RecordRateLimited(retryAfter: null);

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(TimeSpan.FromSeconds(1), throttle.RetryIn);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(throttle.RetryIn);
        Assert.Null(throttle.BlockedUntil);
    }

    [Fact]
    public void Success_resets_the_backoff_sequence()
    {
        var throttle = new UpstreamThrottle(new ManualTimeProvider());
        throttle.RecordRateLimited(retryAfter: null);
        throttle.RecordRateLimited(retryAfter: null);

        throttle.RecordSuccess();

        Assert.Null(throttle.RetryIn);
        Assert.Equal(TimeSpan.FromMinutes(1), throttle.RecordRateLimited(retryAfter: null));
    }
}
