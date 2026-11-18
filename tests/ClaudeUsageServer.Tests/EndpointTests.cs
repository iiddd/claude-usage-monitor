using System.Net;
using System.Text.Json;
using ClaudeUsageServer.Models;
using ClaudeUsageServer.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeUsageServer.Tests;

/// <summary>
/// Exercises the HTTP endpoints in-process. Background services are removed, the credential
/// store is seeded directly, and the Anthropic usage endpoint is stubbed.
/// </summary>
public class EndpointTests
{
    private const string AccessToken = "access-SENTINEL";
    private const string UsageJson =
        """{"five_hour":{"utilization":42.0,"resets_at":"2030-01-01T00:00:00Z"},"seven_day":{"utilization":10.0,"resets_at":"2030-01-05T00:00:00Z"}}""";

    [Fact]
    public async Task Health_returns_ok()
    {
        using var app = CreateApp(_ => { }, Upstream(HttpStatusCode.OK, UsageJson));
        using var client = app.CreateClient();

        Assert.Equal("ok", await client.GetStringAsync("/health"));
    }

    [Fact]
    public async Task Usage_proxies_upstream_with_server_side_auth_and_cors()
    {
        var upstream = Upstream(HttpStatusCode.OK, UsageJson);
        using var app = CreateApp(SeedValidToken, upstream);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/usage");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(UsageJson, await response.Content.ReadAsStringAsync());
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());

        var call = Assert.Single(upstream.Requests);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", call.Uri.ToString());
        Assert.Equal("Bearer " + AccessToken, call.Headers["Authorization"]);
        Assert.Equal("oauth-2025-04-20", call.Headers["anthropic-beta"]);
    }

    [Fact]
    public async Task Usage_is_served_from_cache_within_ttl()
    {
        var upstream = Upstream(HttpStatusCode.OK, UsageJson);
        using var app = CreateApp(SeedValidToken, upstream, cacheTtlSeconds: 60);
        using var client = app.CreateClient();

        await client.GetStringAsync("/usage");
        var second = await client.GetStringAsync("/usage");

        Assert.Equal(UsageJson, second);
        Assert.Single(upstream.Requests);
    }

    [Fact]
    public async Task Upstream_failure_falls_back_to_stale_cache()
    {
        var calls = 0;
        var upstream = new FakeHttpHandler(_ => Interlocked.Increment(ref calls) == 1
            ? FakeHttpHandler.Json(HttpStatusCode.OK, UsageJson)
            : FakeHttpHandler.Json(HttpStatusCode.InternalServerError, "{}"));
        using var app = CreateApp(SeedValidToken, upstream, cacheTtlSeconds: 0);
        using var client = app.CreateClient();

        await client.GetStringAsync("/usage");
        await Task.Delay(20);
        using var response = await client.GetAsync("/usage");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(UsageJson, await response.Content.ReadAsStringAsync());
        Assert.Equal("true", response.Headers.GetValues("X-Cache-Stale").Single());

        using var status = JsonDocument.Parse(await client.GetStringAsync("/status"));
        Assert.Equal("upstream_error", status.RootElement.GetProperty("state").GetString());
        Assert.Equal(500, status.RootElement.GetProperty("upstreamStatus").GetInt32());
    }

    [Fact]
    public async Task Upstream_failure_without_cache_returns_error_with_cors()
    {
        using var app = CreateApp(SeedValidToken, Upstream(HttpStatusCode.InternalServerError, "{}"));
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/usage");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("upstream_error", await ReadErrorAsync(response));
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("reauth_required")]
    [InlineData("credentials_missing")]
    public async Task Credential_problems_return_503_without_calling_upstream(string expected)
    {
        var upstream = Upstream(HttpStatusCode.OK, UsageJson);
        using var app = CreateApp(store =>
        {
            if (expected == "reauth_required")
            {
                SeedValidToken(store);
                store.MarkReauthRequired();
            }
        }, upstream);
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/usage");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(expected, await ReadErrorAsync(response));
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task Cors_preflight_is_answered()
    {
        using var app = CreateApp(_ => { }, Upstream(HttpStatusCode.OK, UsageJson));
        using var client = app.CreateClient();

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/usage"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("GET", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
    }

    [Fact]
    public async Task Status_reports_state_without_token_material()
    {
        using var app = CreateApp(SeedValidToken, Upstream(HttpStatusCode.OK, UsageJson));
        using var client = app.CreateClient();

        var body = await client.GetStringAsync("/status");

        using var status = JsonDocument.Parse(body);
        Assert.Equal("ok", status.RootElement.GetProperty("state").GetString());
        Assert.True(status.RootElement.GetProperty("tokenValid").GetBoolean());
        Assert.DoesNotContain("SENTINEL", body);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static void SeedValidToken(CredentialStore store) =>
        store.Update(AccessToken, "refresh-SENTINEL", DateTimeOffset.UtcNow.AddHours(1));

    private static FakeHttpHandler Upstream(HttpStatusCode status, string json) =>
        new(_ => FakeHttpHandler.Json(status, json));

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetString();
    }

    private static WebApplicationFactory<Program> CreateApp(
        Action<CredentialStore> seed, FakeHttpHandler upstream, int cacheTtlSeconds = 60)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // No background work: the watcher would read the real credentials file and the
                // cleanup service would touch the real log directory.
                services.RemoveAll<CredentialStore>();
                foreach (var hosted in services
                             .Where(d => d.ServiceType == typeof(IHostedService) &&
                                         (d.ImplementationType == typeof(CredentialWatcherService) ||
                                          d.ImplementationType == typeof(LogCleanupService)))
                             .ToList())
                {
                    services.Remove(hosted);
                }

                var store = new CredentialStore();
                seed(store);
                services.AddSingleton(store);
                services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(upstream));
                services.PostConfigure<ServerOptions>(o => o.CacheTtlSeconds = cacheTtlSeconds);

                // Keep test runs out of the service's real log files.
                services.RemoveAll<ILoggerFactory>();
                services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            }));
    }
}
