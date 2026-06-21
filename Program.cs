using System.Net;
using ClaudeUsageServer.Models;
using ClaudeUsageServer.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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

        cfg.MinimumLevel.Information()
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

    app.MapGet("/status", (CredentialStore store) =>
        Results.Json(store.GetStatus()));

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
        IOptionsMonitor<ServerOptions> options,
        IHttpClientFactory factory,
        ILogger<Program> logger) =>
    {
        SetCorsHeaders(ctx.Response);

        // Fresh cache hit — serve immediately without an upstream call.
        var ttl = options.CurrentValue.CacheTtlSeconds;
        if (cache.TryGetFresh(ttl, out var fresh))
            return Results.Content(fresh, "application/json");

        // Cache miss — credentials must be valid to call upstream.
        var state = store.State;
        if (state == CredentialState.NotFound)
            return Results.Json(new { error = "credentials_not_found" }, statusCode: 503);
        if (state == CredentialState.Expired)
            return Results.Json(new { error = "token_expired" }, statusCode: 503);

        using var upstream = new HttpRequestMessage(
            HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");

        if (!store.TryApplyToken(upstream))
            return Results.Json(new { error = "token_expired" }, statusCode: 503);

        upstream.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

        logger.LogInformation("Forwarding request to Anthropic usage endpoint");

        var http = factory.CreateClient();

        try
        {
            using var response = await http.SendAsync(upstream);
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                cache.Store(body);
                return Results.Content(body, "application/json");
            }

            // Non-200: fall back to stale cache if available, otherwise forward.
            logger.LogError("Anthropic returned HTTP {StatusCode}", (int)response.StatusCode);

            if (cache.TryGetStale(out var stale))
            {
                ctx.Response.Headers["X-Cache-Stale"] = "true";
                return Results.Content(stale, "application/json");
            }

            return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Network error reaching Anthropic: {Reason}", ex.Message);

            if (cache.TryGetStale(out var stale))
            {
                ctx.Response.Headers["X-Cache-Stale"] = "true";
                return Results.Content(stale, "application/json");
            }

            return Results.Json(new { error = "upstream_unavailable" }, statusCode: 503);
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
