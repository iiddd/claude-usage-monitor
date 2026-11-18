using System.Globalization;
using System.Net;
using ClaudeUsageServer.Models;
using ClaudeUsageServer.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

// Bootstrap logger captures startup failures before full Serilog is configured.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Warning()
    .WriteTo.EventLog("ClaudeUsageServer", manageEventSource: false)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Machine-local overrides (gitignored): paths, ports, secrets.
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

    // ── Windows Service host ───────────────────────────────────────────────
    builder.Host.UseWindowsService(o => o.ServiceName = "ClaudeUsageServer");

    // ── Serilog ────────────────────────────────────────────────────────────
    builder.Host.UseSerilog((context, _, cfg) =>
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ClaudeUsageServer", "logs");
        Directory.CreateDirectory(logDir);

        // Read log-sizing options early from configuration so Serilog picks them up
        // before the DI container finishes building (LogCleanupService uses the DI
        // values at runtime for the total-size sweep).
        var section = context.Configuration.GetSection(ServerOptions.SectionName);
        var retentionDays  = section.GetValue<int>("LogRetentionDays",  14);
        var maxFileSizeMB  = section.GetValue<int>("LogMaxFileSizeMB",  10);

        // DiagnosticLogging drops our own minimum to Debug so the token/credential subsystem's
        // correlation-tagged Debug events surface. Framework noise stays capped via the overrides
        // below. Off = Information (the Debug calls compile to cheap no-ops).
        var diagnostic = section.GetValue<bool>("DiagnosticLogging", false);

        cfg.MinimumLevel.Is(diagnostic ? LogEventLevel.Debug : LogEventLevel.Information)
           .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
           .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
           .Enrich.FromLogContext()
           .WriteTo.File(
               path: Path.Combine(logDir, "claudeusageserver-.log"),
               rollingInterval: RollingInterval.Day,
               retainedFileCountLimit: retentionDays,
               fileSizeLimitBytes: (long)maxFileSizeMB * 1024L * 1024L,
               rollOnFileSizeLimit: true,
               outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
           .WriteTo.EventLog(
               source: "ClaudeUsageServer",
               logName: "Application",
               manageEventSource: false,
               restrictedToMinimumLevel: LogEventLevel.Warning);
    });

    // ── Configuration ──────────────────────────────────────────────────────
    builder.Services.Configure<ServerOptions>(
        builder.Configuration.GetSection(ServerOptions.SectionName));

    // ── Application services ───────────────────────────────────────────────
    builder.Services.AddSingleton<CredentialStore>();
    builder.Services.AddSingleton<UsageCache>();
    builder.Services.TryAddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<UpstreamThrottle>();
    builder.Services.AddHostedService<CredentialWatcherService>();
    builder.Services.AddHostedService<LogCleanupService>();
    builder.Services.AddHttpClient();

    builder.Services.ConfigureHttpJsonOptions(o =>
    {
        o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

    // ── Kestrel — loopback only ────────────────────────────────────────────
    var serverOpts = builder.Configuration
        .GetSection(ServerOptions.SectionName)
        .Get<ServerOptions>() ?? new ServerOptions();

    builder.WebHost.ConfigureKestrel(k =>
    {
        k.Listen(IPAddress.Loopback, serverOpts.Port);
    });

    var app = builder.Build();

    // ── Startup assertion: verify no non-loopback address was bound ────────
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var server = app.Services.GetRequiredService<IServer>();
        var feature = server.Features.Get<IServerAddressesFeature>();
        if (feature is null) return;

        foreach (var addr in feature.Addresses)
        {
            if (!addr.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                !addr.StartsWith("http://[::1]", StringComparison.OrdinalIgnoreCase))
            {
                Log.Fatal("Non-loopback binding detected — stopping service immediately");
                app.Lifetime.StopApplication();
                return;
            }
        }

        Log.Information("ClaudeUsageServer listening on {Addresses}",
            string.Join(", ", feature.Addresses));
    });

    // ── Endpoints ──────────────────────────────────────────────────────────

    app.MapGet("/health", () => Results.Text("ok"));

    app.MapGet("/status", (CredentialStore store, UpstreamThrottle throttle) =>
        Results.Json(store.GetStatus() with
        {
            UpstreamRetryAt = throttle.BlockedUntil?.ToString("O"),
        }));

    // OPTIONS preflight for the iCUE widget's browser context.
    app.MapMethods("/usage", ["OPTIONS"], (HttpContext ctx) =>
    {
        SetCorsHeaders(ctx.Response);
        return Results.Ok();
    });

    app.MapGet("/usage", async (
        HttpContext ctx,
        CredentialStore store,
        UsageCache cache,
        UpstreamThrottle throttle,
        IOptionsMonitor<ServerOptions> options,
        IHttpClientFactory factory,
        ILogger<Program> logger) =>
    {
        SetCorsHeaders(ctx.Response);

        // Fresh cache hit — serve immediately without an upstream call.
        var ttl = options.CurrentValue.CacheTtlSeconds;
        if (cache.TryGetFresh(ttl, out var fresh))
        {
            LogCacheDecision(logger, "hit");
            return Results.Content(fresh, "application/json");
        }

        // Cache miss — credentials must be valid to call upstream. The error vocabulary matches
        // the /status state names so the widget branches on one set of tokens.
        var usageError = store.GetUsageError();
        if (usageError is not null)
        {
            LogCacheDecision(logger, "miss:" + usageError);
            return Results.Json(new { error = usageError }, statusCode: 503);
        }

        // Rate-limited by Anthropic: make no upstream call until the backoff elapses.
        if (throttle.RetryIn is { } retryIn)
            return RateLimited(ctx, cache, logger, retryIn);

        // Single flight: one upstream call at a time. A request that waited re-checks the cache
        // and the backoff, so concurrent pollers share the result of the call they waited on.
        await throttle.Gate.WaitAsync(ctx.RequestAborted);
        try
        {
            if (cache.TryGetFresh(ttl, out fresh))
            {
                LogCacheDecision(logger, "hit:coalesced");
                return Results.Content(fresh, "application/json");
            }

            if (throttle.RetryIn is { } waitedRetryIn)
                return RateLimited(ctx, cache, logger, waitedRetryIn);

            using var upstream = new HttpRequestMessage(
                HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");

            if (!store.TryApplyToken(upstream))
            {
                LogCacheDecision(logger, "miss:refresh_failed");
                return Results.Json(new { error = "refresh_failed" }, statusCode: 503);
            }

            upstream.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

            LogCacheDecision(logger, "miss:upstream");
            logger.LogInformation("Forwarding request to Anthropic usage endpoint");

            var http = factory.CreateClient();

            try
            {
                using var response = await http.SendAsync(upstream);
                var body = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    throttle.RecordSuccess();
                    store.MarkUpstreamHealthy();
                    cache.Store(body);
                    return Results.Content(body, "application/json");
                }

                // Non-200: token itself is fine, the usage endpoint is failing.
                store.MarkUpstreamError((int)response.StatusCode);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var backoff = throttle.RecordRateLimited(GetRetryAfter(response));
                    logger.LogWarning(
                        "Anthropic rate limit (HTTP 429): pausing usage requests for {Seconds:F0}s",
                        backoff.TotalSeconds);
                    return RateLimited(ctx, cache, logger, backoff);
                }

                logger.LogError("Anthropic returned HTTP {StatusCode}", (int)response.StatusCode);

                if (cache.TryGetStale(out var stale))
                {
                    LogCacheDecision(logger, "stale:upstream_error");
                    ctx.Response.Headers["X-Cache-Stale"] = "true";
                    return Results.Content(stale, "application/json");
                }

                return Results.Json(new { error = "upstream_error" }, statusCode: (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Network-level failure reaching Anthropic — no HTTP status to record.
                store.MarkUpstreamError(null);
                logger.LogError("Network error reaching Anthropic: {Reason}", ex.Message);

                if (cache.TryGetStale(out var stale))
                {
                    LogCacheDecision(logger, "stale:upstream_error");
                    ctx.Response.Headers["X-Cache-Stale"] = "true";
                    return Results.Content(stale, "application/json");
                }

                return Results.Json(new { error = "upstream_error" }, statusCode: 503);
            }
        }
        finally
        {
            throttle.Gate.Release();
        }
    });

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Service terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;

static void SetCorsHeaders(HttpResponse response)
{
    response.Headers["Access-Control-Allow-Origin"] = "*";
    response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
    response.Headers["Access-Control-Allow-Headers"] = "*";
}

// Answers a request while Anthropic is rate-limiting us: the stale cache when there is one,
// otherwise the upstream_error the widget already understands. Retry-After tells the caller
// when an upstream call will next be made.
static IResult RateLimited(
    HttpContext ctx, UsageCache cache, Microsoft.Extensions.Logging.ILogger logger, TimeSpan retryIn)
{
    var seconds = Math.Max(1, (int)Math.Ceiling(retryIn.TotalSeconds));
    ctx.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

    if (cache.TryGetStale(out var stale))
    {
        LogCacheDecision(logger, "stale:rate_limited");
        ctx.Response.Headers["X-Cache-Stale"] = "true";
        return Results.Content(stale, "application/json");
    }

    LogCacheDecision(logger, "miss:rate_limited");
    return Results.Json(new { error = "upstream_error" }, statusCode: StatusCodes.Status429TooManyRequests);
}

// The server's Retry-After hint as a delay (it may be sent as seconds or as a date).
static TimeSpan? GetRetryAfter(HttpResponseMessage response)
{
    var header = response.Headers.RetryAfter;
    if (header?.Delta is { } delta) return delta;
    if (header?.Date is { } date) return date - DateTimeOffset.UtcNow;
    return null;
}

// Per-request cache hit/miss/stale decision. Debug-only, so it is silent unless
// DiagnosticLogging is on. At the default 60s poll interval this is ~1 line/min.
static void LogCacheDecision(Microsoft.Extensions.Logging.ILogger logger, string decision)
{
    if (logger.IsEnabled(LogLevel.Debug))
        logger.LogDebug("/usage cache decision={Decision}", decision);
}
