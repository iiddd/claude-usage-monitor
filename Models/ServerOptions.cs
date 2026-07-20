namespace ClaudeUsageServer.Models;

internal sealed class ServerOptions
{
    public const string SectionName = "ClaudeUsageServer";

    public int Port { get; set; } = 41920;
    public string CredentialsPath { get; set; } = string.Empty;
    public int RefreshBufferMinutes { get; set; } = 5;
    public int RetryIntervalSeconds { get; set; } = 60;
    public int CacheTtlSeconds { get; set; } = 60;
    public int LogRetentionDays { get; set; } = 14;
    public int LogMaxFileSizeMB { get; set; } = 10;
    public int LogMaxTotalSizeMB { get; set; } = 100;

    /// <summary>
    /// Temporary troubleshooting switch. When true the token/credential subsystem logs at
    /// Debug (correlation-tagged token-lifecycle events). Larger log volume; still bounded by
    /// the rotation/size caps above. Off by default.
    /// </summary>
    public bool DiagnosticLogging { get; set; } = false;

    public string ResolvedCredentialsPath =>
        string.IsNullOrWhiteSpace(CredentialsPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude",
                ".credentials.json")
            : CredentialsPath;
}
