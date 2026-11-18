using System.Globalization;
using System.Net;
using ClaudeUsageServer.Models;
using Microsoft.Extensions.Options;

namespace ClaudeUsageServer.Services;

/// <summary>
/// Serves <c>GET /usage</c>. Answers from <see cref="UsageCache"/> while it is fresh; otherwise
/// forwards one request at a time to the Anthropic usage endpoint with the bearer token added
/// server-side. Error bodies use the <c>/status</c> state names, so the widget branches on one
/// vocabulary. When Anthropic fails, the last good body is served with <c>X-Cache-Stale: true</c>.
/// </summary>
internal sealed class UsageProxy(
    CredentialStore store,
    UsageCache cache,
    UpstreamThrottle throttle,
    IOptionsMonitor<ServerOptions> options,
    IHttpClientFactory httpFactory,
    ILogger<UsageProxy> logger)
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";

    public async Task<IResult> GetUsageAsync(HttpContext ctx)
    {
        var ttl = options.CurrentValue.CacheTtlSeconds;
        if (cache.TryGetFresh(ttl, out var fresh))
        {
            LogCacheDecision("hit");
            return Json(fresh);
        }

        var usageError = store.GetUsageError();
        if (usageError is not null)
        {
            LogCacheDecision("miss:" + usageError);
            return Error(usageError, StatusCodes.Status503ServiceUnavailable);
        }

        if (throttle.RetryIn is { } retryIn)
            return RateLimited(ctx, retryIn);

        await throttle.Gate.WaitAsync(ctx.RequestAborted);
        try
        {
            return await CallUpstreamUnlessAnsweredMeanwhileAsync(ctx, ttl);
        }
        finally
        {
            throttle.Gate.Release();
        }
    }

    /// <summary>
    /// Runs while holding the single-flight gate. A request that waited for the gate re-checks
    /// the cache and the backoff first, so concurrent pollers share the result of the call they
    /// waited on instead of each calling Anthropic.
    /// </summary>
    private async Task<IResult> CallUpstreamUnlessAnsweredMeanwhileAsync(HttpContext ctx, int ttl)
    {
        if (cache.TryGetFresh(ttl, out var fresh))
        {
            LogCacheDecision("hit:coalesced");
            return Json(fresh);
        }

        if (throttle.RetryIn is { } retryIn)
            return RateLimited(ctx, retryIn);

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        if (!store.TryApplyToken(request))
        {
            LogCacheDecision("miss:refresh_failed");
            return Error("refresh_failed", StatusCodes.Status503ServiceUnavailable);
        }

        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

        LogCacheDecision("miss:upstream");
        logger.LogInformation("Forwarding request to Anthropic usage endpoint");

        try
        {
            using var response = await httpFactory.CreateClient().SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            return response.IsSuccessStatusCode
                ? StoreAndServe(body)
                : HandleUpstreamFailure(ctx, response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            store.MarkUpstreamError(httpStatus: null);
            logger.LogError("Network error reaching Anthropic: {Reason}", ex.Message);
            return StaleOrUpstreamError(ctx, StatusCodes.Status503ServiceUnavailable);
        }
    }

    private IResult StoreAndServe(string body)
    {
        throttle.RecordSuccess();
        store.MarkUpstreamHealthy();
        cache.Store(body);
        return Json(body);
    }

    /// <summary>
    /// Handles a non-success answer from Anthropic: the token is fine but the usage endpoint is
    /// failing. An HTTP 429 starts the backoff; any other status is passed on to the widget.
    /// </summary>
    private IResult HandleUpstreamFailure(HttpContext ctx, HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        store.MarkUpstreamError(status);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var backoff = throttle.RecordRateLimited(GetRetryAfter(response));
            logger.LogWarning(
                "Anthropic rate limit (HTTP 429): pausing usage requests for {Seconds:F0}s",
                backoff.TotalSeconds);
            return RateLimited(ctx, backoff);
        }

        logger.LogError("Anthropic returned HTTP {StatusCode}", status);
        return StaleOrUpstreamError(ctx, status);
    }

    /// <summary>
    /// Answers a request while Anthropic is rate-limiting the service: the stale cache when there
    /// is one, otherwise the <c>upstream_error</c> the widget already understands. The
    /// <c>Retry-After</c> header tells the caller when the next upstream call will be made.
    /// </summary>
    private IResult RateLimited(HttpContext ctx, TimeSpan retryIn)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryIn.TotalSeconds));
        ctx.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        if (cache.TryGetStale(out var stale))
        {
            LogCacheDecision("stale:rate_limited");
            return Stale(ctx, stale);
        }

        LogCacheDecision("miss:rate_limited");
        return Error("upstream_error", StatusCodes.Status429TooManyRequests);
    }

    private IResult StaleOrUpstreamError(HttpContext ctx, int statusCode)
    {
        if (cache.TryGetStale(out var stale))
        {
            LogCacheDecision("stale:upstream_error");
            return Stale(ctx, stale);
        }

        return Error("upstream_error", statusCode);
    }

    /// <summary>The server's <c>Retry-After</c> hint as a delay; it may be sent as seconds or as a date.</summary>
    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    private static IResult Stale(HttpContext ctx, string body)
    {
        ctx.Response.Headers["X-Cache-Stale"] = "true";
        return Json(body);
    }

    private static IResult Json(string body) => Results.Content(body, "application/json");

    private static IResult Error(string code, int statusCode) =>
        Results.Json(new { error = code }, statusCode: statusCode);

    /// <summary>
    /// Logs the per-request cache decision at Debug, so it is silent unless
    /// <c>DiagnosticLogging</c> is on. At the default 60 s poll interval this is about one line a minute.
    /// </summary>
    private void LogCacheDecision(string decision)
    {
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("/usage cache decision={Decision}", decision);
    }
}
