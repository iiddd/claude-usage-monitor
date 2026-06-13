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

    [JsonPropertyName("expiresAt")]
    public long ExpiresAt { get; set; }
}

internal sealed record StatusResponse(
    bool TokenValid,
    string? ExpiresAt,
    string? LastRefresh);
