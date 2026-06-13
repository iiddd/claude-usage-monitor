namespace ClaudeUsageServer.Models;

internal sealed class ServerOptions
{
    public const string SectionName = "ClaudeUsageServer";

    public int Port { get; set; } = 41920;
    public string CredentialsPath { get; set; } = string.Empty;
    public int RefreshBufferMinutes { get; set; } = 5;
    public int RetryIntervalSeconds { get; set; } = 60;
    public int CacheTtlSeconds { get; set; } = 60;

    public string ResolvedCredentialsPath =>
        string.IsNullOrWhiteSpace(CredentialsPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude",
                ".credentials.json")
            : CredentialsPath;
}
