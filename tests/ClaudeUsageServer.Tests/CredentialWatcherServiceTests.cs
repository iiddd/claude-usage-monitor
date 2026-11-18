using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageServer.Models;
using ClaudeUsageServer.Services;

namespace ClaudeUsageServer.Tests;

/// <summary>
/// Runs the real <see cref="CredentialWatcherService"/> against a temporary credentials file and
/// a stubbed OAuth token endpoint, covering the refresh flow end to end without any network.
/// </summary>
public sealed class CredentialWatcherServiceTests : IDisposable
{
    private const string OldAccess = "old-access-SENTINEL";
    private const string OldRefresh = "old-refresh-SENTINEL";
    private const string NewAccess = "new-access-SENTINEL";
    private const string NewRefresh = "new-refresh-SENTINEL";

    private readonly string _dir = Directory.CreateTempSubdirectory("claudeusage-tests-").FullName;
    private string CredentialsPath => Path.Combine(_dir, "credentials.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Expired_token_is_refreshed_and_written_back_preserving_unmanaged_fields()
    {
        WriteCredentials(OldAccess, OldRefresh, DateTimeOffset.UtcNow.AddMinutes(-5));
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
            $$"""{"access_token":"{{NewAccess}}","refresh_token":"{{NewRefresh}}","expires_in":3600}"""));

        await using var run = await StartServiceAsync(handler);
        await Wait.UntilAsync(() => run.Store.GetStatus().State == "ok", "the refresh succeeds");

        var grant = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, grant.Method);
        Assert.Equal("https://platform.claude.com/v1/oauth/token", grant.Uri.ToString());
        using (var body = JsonDocument.Parse(grant.Body!))
        {
            Assert.Equal("refresh_token", body.RootElement.GetProperty("grant_type").GetString());
            Assert.Equal(OldRefresh, body.RootElement.GetProperty("refresh_token").GetString());
            Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("client_id").GetString()));
        }

        await Wait.UntilAsync(() => File.ReadAllText(CredentialsPath).Contains(NewAccess),
            "the rotated tokens are written back");
        var written = JsonNode.Parse(File.ReadAllText(CredentialsPath))!;
        Assert.Equal(NewAccess, (string?)written["claudeAiOauth"]!["accessToken"]);
        Assert.Equal(NewRefresh, (string?)written["claudeAiOauth"]!["refreshToken"]);
        Assert.Equal("max", (string?)written["claudeAiOauth"]!["subscriptionType"]);
        Assert.Equal("kept", (string?)written["unmanagedTopLevel"]);
    }

    [Fact]
    public async Task Rejected_refresh_token_enters_reauth_required_without_retrying()
    {
        WriteCredentials(OldAccess, OldRefresh, DateTimeOffset.UtcNow.AddMinutes(-5));
        var handler = new FakeHttpHandler(_ =>
            FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));

        await using var run = await StartServiceAsync(handler);
        await Wait.UntilAsync(() => run.Store.GetStatus().State == "reauth_required", "the grant is rejected");
        await Task.Delay(300);

        Assert.Single(handler.Requests);
        Assert.Equal("invalid_grant", run.Store.GetStatus().LastRefreshError);
    }

    [Fact]
    public async Task Server_errors_try_the_fallback_host_then_schedule_a_retry()
    {
        WriteCredentials(OldAccess, OldRefresh, DateTimeOffset.UtcNow.AddMinutes(-5));
        var handler = new FakeHttpHandler(_ =>
            FakeHttpHandler.Json(HttpStatusCode.ServiceUnavailable, "{}"));

        await using var run = await StartServiceAsync(handler);
        await Wait.UntilAsync(() => run.Store.ConsecutiveFailures == 1, "the first attempt fails");

        var status = run.Store.GetStatus();
        Assert.Equal("refresh_failed", status.State);
        Assert.Equal("http_503", status.LastRefreshError);
        Assert.NotNull(status.NextRetryAt);
        Assert.Equal(
            new[] { "platform.claude.com", "console.anthropic.com" },
            handler.Requests.Select(r => r.Uri.Host));
    }

    [Fact]
    public async Task Valid_token_is_loaded_without_any_refresh_call()
    {
        WriteCredentials(OldAccess, OldRefresh, DateTimeOffset.UtcNow.AddHours(2));
        var handler = new FakeHttpHandler(_ => throw new InvalidOperationException("no call expected"));

        await using var run = await StartServiceAsync(handler);
        await Wait.UntilAsync(() => run.Store.GetStatus().State == "ok", "the credentials load");
        await Task.Delay(300);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Missing_credentials_file_reports_credentials_missing()
    {
        var handler = new FakeHttpHandler(_ => throw new InvalidOperationException("no call expected"));

        await using var run = await StartServiceAsync(handler);
        await Wait.UntilAsync(() => run.Store.GetStatus().State == "credentials_missing", "the load fails");

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task New_login_recovers_from_reauth_required_without_restart()
    {
        WriteCredentials(OldAccess, OldRefresh, DateTimeOffset.UtcNow.AddMinutes(-5));
        var handler = new FakeHttpHandler(_ =>
            FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));

        await using var run = await StartServiceAsync(handler);
        await Wait.UntilAsync(() => run.Store.GetStatus().State == "reauth_required", "the grant is rejected");

        SimulateClaudeLogin();

        await Wait.UntilAsync(() => run.Store.GetStatus().State == "ok", "the file watcher reloads");
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Tokens_never_appear_in_logs()
    {
        WriteCredentials(OldAccess, OldRefresh, DateTimeOffset.UtcNow.AddMinutes(-5));
        var calls = 0;
        var errorBodyEchoingToken = $$"""{"echo":"{{OldRefresh}}"}""";
        var handler = new FakeHttpHandler(_ => Interlocked.Increment(ref calls) == 1
            ? FakeHttpHandler.Json(HttpStatusCode.ServiceUnavailable, errorBodyEchoingToken)
            : FakeHttpHandler.Json(HttpStatusCode.OK,
                $$"""{"access_token":"{{NewAccess}}","refresh_token":"{{NewRefresh}}","expires_in":3600}"""));

        await using var run = await StartServiceAsync(handler, diagnosticLogging: true);
        await Wait.UntilAsync(() => run.Store.GetStatus().State == "ok", "the fallback host succeeds");

        Assert.NotEmpty(run.Logger.Messages);
        Assert.All(run.Logger.Messages, m => Assert.DoesNotContain("SENTINEL", m));
    }

    private void SimulateClaudeLogin() =>
        WriteCredentials(NewAccess, NewRefresh, DateTimeOffset.UtcNow.AddHours(8));

    private void WriteCredentials(string access, string refresh, DateTimeOffset expiresAt)
    {
        var json = new JsonObject
        {
            ["claudeAiOauth"] = new JsonObject
            {
                ["accessToken"] = access,
                ["refreshToken"] = refresh,
                ["expiresAt"] = expiresAt.ToUnixTimeMilliseconds(),
                ["subscriptionType"] = "max",
            },
            ["unmanagedTopLevel"] = "kept",
        };
        File.WriteAllText(CredentialsPath, json.ToJsonString());
    }

    private const int RetryIntervalLongerThanAnyTest = 3600;

    private async Task<ServiceRun> StartServiceAsync(FakeHttpHandler handler, bool diagnosticLogging = false)
    {
        var options = new ServerOptions
        {
            CredentialsPath = CredentialsPath,
            RefreshBufferMinutes = 5,
            RetryIntervalSeconds = RetryIntervalLongerThanAnyTest,
            DiagnosticLogging = diagnosticLogging,
        };
        var store = new CredentialStore();
        var logger = new CapturingLogger<CredentialWatcherService>();
        var service = new CredentialWatcherService(
            store, new StaticOptionsMonitor<ServerOptions>(options), new FakeHttpClientFactory(handler), logger);

        await service.StartAsync(CancellationToken.None);
        return new ServiceRun(service, store, logger);
    }

    private sealed class ServiceRun(
        CredentialWatcherService service,
        CredentialStore store,
        CapturingLogger<CredentialWatcherService> logger) : IAsyncDisposable
    {
        public CredentialStore Store => store;
        public CapturingLogger<CredentialWatcherService> Logger => logger;

        public async ValueTask DisposeAsync()
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
            store.Dispose();
        }
    }
}
