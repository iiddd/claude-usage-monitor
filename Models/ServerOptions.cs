namespace ClaudeUsageServer.Models;

internal sealed class ServerOptions
{
    public const string SectionName = "ClaudeUsageServer";

    public int Port { get; set; } = 41920;
    public string CredentialsPath { get; set; } = string.Empty;
    public string ClaudeCliPath { get; set; } = string.Empty;
    public int RefreshBufferMinutes { get; set; } = 5;
    public int RetryIntervalSeconds { get; set; } = 60;
    public int CacheTtlSeconds { get; set; } = 60;
    public int LogRetentionDays { get; set; } = 14;
    public int LogMaxFileSizeMB { get; set; } = 10;
    public int LogMaxTotalSizeMB { get; set; } = 100;

    public string ResolvedCredentialsPath =>
        string.IsNullOrWhiteSpace(CredentialsPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude",
                ".credentials.json")
            : CredentialsPath;

    /// <summary>
    /// Returns the configured CLI path, or a default derived from the credentials path
    /// (grandparent directory + AppData\Roaming\npm\claude.cmd). LocalSystem does not
    /// inherit the user's PATH, so an explicit path is required when running as a service.
    /// </summary>
    public string ResolvedClaudeCliPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ClaudeCliPath))
                return ClaudeCliPath;

            // Derive the user home from CredentialsPath (~\.claude\.credentials.json → grandparent),
            // falling back to %USERPROFILE% if CredentialsPath is not set.
            string home;
            if (!string.IsNullOrWhiteSpace(CredentialsPath))
            {
                var claudeDir = Path.GetDirectoryName(CredentialsPath) ?? string.Empty;
                home = Path.GetDirectoryName(claudeDir) ?? string.Empty;
            }
            else
            {
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            return Path.Combine(home, "AppData", "Roaming", "npm", "claude.cmd");
        }
    }
}
