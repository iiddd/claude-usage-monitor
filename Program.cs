using System.Net;
using ClaudeUsageServer.Models;
using ClaudeUsageServer.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

Log.Logger = HostSetup.CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
    builder.Host.UseWindowsService(o => o.ServiceName = "ClaudeUsageServer");
    builder.Host.UseSerilog((context, _, cfg) => HostSetup.ConfigureLogging(context, cfg));

    builder.Services.Configure<ServerOptions>(
        builder.Configuration.GetSection(ServerOptions.SectionName));

    builder.Services.AddSingleton<CredentialStore>();
    builder.Services.AddSingleton<UsageCache>();
    builder.Services.TryAddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<UpstreamThrottle>();
    builder.Services.AddSingleton<UsageProxy>();
    builder.Services.AddHostedService<CredentialWatcherService>();
    builder.Services.AddHostedService<LogCleanupService>();
    builder.Services.AddHttpClient();

    builder.Services.ConfigureHttpJsonOptions(o =>
    {
        o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

    var serverOpts = builder.Configuration
        .GetSection(ServerOptions.SectionName)
        .Get<ServerOptions>() ?? new ServerOptions();

    builder.WebHost.ConfigureKestrel(k =>
    {
        k.Listen(IPAddress.Loopback, serverOpts.Port);
    });

    var app = builder.Build();

    HostSetup.StopUnlessLoopbackOnly(app);

    app.MapGet("/health", () => Results.Text("ok"));

    app.MapGet("/status", (CredentialStore store, UpstreamThrottle throttle) =>
        Results.Json(store.GetStatus() with
        {
            UpstreamRetryAt = throttle.BlockedUntil?.ToString("O"),
        }));

    app.MapMethods("/usage", ["OPTIONS"], (HttpContext ctx) =>
    {
        SetCorsHeaders(ctx.Response);
        return Results.Ok();
    });

    app.MapGet("/usage", (HttpContext ctx, UsageProxy proxy) =>
    {
        SetCorsHeaders(ctx.Response);
        return proxy.GetUsageAsync(ctx);
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
