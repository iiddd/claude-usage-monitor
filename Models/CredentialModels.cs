using System.Text.Json.Serialization;

namespace ClaudeUsageServer.Models;

internal sealed class CredentialsFile
{
    [JsonPropertyName("claudeAiOauth")]
    public OAuthSection? ClaudeAiOauth { get; set; }
}

internal sealed class OAuthSection
{
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expiresAt")]
    public long ExpiresAt { get; set; }
}

/// <summary>
/// Response body of the OAuth <c>refresh_token</c> grant. Only the fields the service
/// consumes are modelled; <c>error</c>/<c>error_description</c> are safe to read (they
/// carry no token material). The raw body is never logged.
/// </summary>
internal sealed class TokenRefreshResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}

/// <summary>
/// Shape returned by <c>GET /status</c>. <c>State</c> is the coarse service state
/// ("ok" | "refreshing" | "reauth_required" | "error"); the remaining fields are kept
/// for backward compatibility with existing widget builds.
/// </summary>
internal sealed record StatusResponse(
    string State,
    bool TokenValid,
    string? ExpiresAt,
    string? LastRefresh);
