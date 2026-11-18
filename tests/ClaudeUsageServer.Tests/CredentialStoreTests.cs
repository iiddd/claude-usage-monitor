using System.Text.Json;
using ClaudeUsageServer.Services;

namespace ClaudeUsageServer.Tests;

public class CredentialStoreTests
{
    private const string AccessToken = "test-access-token-SENTINEL";
    private const string RefreshToken = "test-refresh-token-SENTINEL";

    private static DateTimeOffset InOneHour => DateTimeOffset.UtcNow.AddHours(1);

    [Fact]
    public void New_store_reports_credentials_missing()
    {
        using var store = new CredentialStore();

        Assert.Equal("credentials_missing", store.GetStatus().State);
        Assert.Equal("credentials_missing", store.GetUsageError());
    }

    [Fact]
    public void Valid_token_reports_ok_and_allows_upstream_calls()
    {
        using var store = new CredentialStore();
        store.Update(AccessToken, RefreshToken, InOneHour);

        var status = store.GetStatus();
        Assert.Equal("ok", status.State);
        Assert.True(status.TokenValid);
        Assert.Null(store.GetUsageError());
    }

    [Fact]
    public void Expired_token_is_not_applied_and_reports_refresh_failed()
    {
        using var store = new CredentialStore();
        store.Update(AccessToken, RefreshToken, DateTimeOffset.UtcNow.AddMinutes(-1));

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid");
        Assert.False(store.TryApplyToken(request));
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.Equal("refresh_failed", store.GetStatus().State);
        Assert.Equal("refresh_failed", store.GetUsageError());
    }

    [Fact]
    public void Valid_token_is_injected_as_bearer_header()
    {
        using var store = new CredentialStore();
        store.Update(AccessToken, RefreshToken, InOneHour);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid");
        Assert.True(store.TryApplyToken(request));
        Assert.Equal("Bearer " + AccessToken, request.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public void Reauth_required_takes_precedence_and_is_cleared_by_new_credentials()
    {
        using var store = new CredentialStore();
        store.Update(AccessToken, RefreshToken, InOneHour);

        store.MarkReauthRequired();
        Assert.Equal("reauth_required", store.GetStatus().State);
        Assert.Equal("reauth_required", store.GetUsageError());

        store.Update("new-access", "new-refresh", InOneHour);
        Assert.Equal("ok", store.GetStatus().State);
        Assert.False(store.ReauthRequired);
    }

    [Fact]
    public void Transient_refresh_failure_reports_retry_time_and_streak()
    {
        using var store = new CredentialStore();
        store.Update(AccessToken, RefreshToken, InOneHour);
        var nextRetry = DateTimeOffset.UtcNow.AddSeconds(60);

        store.MarkRefreshFailed("http_503", nextRetry);

        var status = store.GetStatus();
        Assert.Equal("refresh_failed", status.State);
        Assert.Equal(1, status.ConsecutiveFailures);
        Assert.Equal("http_503", status.LastRefreshError);
        Assert.Equal(nextRetry.ToString("O"), status.NextRetryAt);
    }

    [Fact]
    public void Upstream_error_is_reported_and_cleared_on_success()
    {
        using var store = new CredentialStore();
        store.Update(AccessToken, RefreshToken, InOneHour);

        store.MarkUpstreamError(500);
        var failing = store.GetStatus();
        Assert.Equal("upstream_error", failing.State);
        Assert.Equal(500, failing.UpstreamStatus);

        store.MarkUpstreamHealthy();
        var healthy = store.GetStatus();
        Assert.Equal("ok", healthy.State);
        Assert.Null(healthy.UpstreamStatus);
    }

    [Fact]
    public async Task Refresh_grant_body_carries_grant_type_client_id_and_refresh_token()
    {
        using var store = new CredentialStore();
        store.Update(AccessToken, RefreshToken, InOneHour);

        Assert.True(store.TryCreateRefreshContent("client-123", out var content));
        using var doc = JsonDocument.Parse(await content!.ReadAsStringAsync());

        Assert.Equal("refresh_token", doc.RootElement.GetProperty("grant_type").GetString());
        Assert.Equal("client-123", doc.RootElement.GetProperty("client_id").GetString());
        Assert.Equal(RefreshToken, doc.RootElement.GetProperty("refresh_token").GetString());
    }

    [Fact]
    public void No_refresh_grant_without_refresh_token()
    {
        using var store = new CredentialStore();
        store.Update(AccessToken, refreshToken: null, InOneHour);

        Assert.False(store.TryCreateRefreshContent("client-123", out var content));
        Assert.Null(content);
    }

    [Fact]
    public async Task Refresh_without_rotation_keeps_previous_refresh_token()
    {
        using var store = new CredentialStore();
        store.Update(AccessToken, RefreshToken, InOneHour);

        store.UpdateFromRefresh("new-access", refreshToken: null, InOneHour);

        Assert.True(store.TryCreateRefreshContent("client-123", out var content));
        Assert.Contains(RefreshToken, await content!.ReadAsStringAsync());
    }

    [Fact]
    public void Status_never_contains_token_material()
    {
        using var store = new CredentialStore();
        store.Update(AccessToken, RefreshToken, InOneHour);
        store.MarkRefreshFailed("http_503", DateTimeOffset.UtcNow.AddSeconds(60));

        var json = JsonSerializer.Serialize(store.GetStatus());

        Assert.DoesNotContain("SENTINEL", json);
    }

    [Fact]
    public void Transition_listener_fires_only_on_state_change()
    {
        using var store = new CredentialStore();
        var transitions = new List<(string Old, string New)>();
        store.SetTransitionListener((o, n, _) => transitions.Add((o, n)));

        store.Update(AccessToken, RefreshToken, InOneHour);
        store.MarkUpstreamHealthy(); // already healthy: no state change

        Assert.Single(transitions);
        Assert.Equal("ok", transitions[0].New);
    }
}
