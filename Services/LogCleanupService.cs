using ClaudeUsageServer.Models;
using Microsoft.Extensions.Options;

namespace ClaudeUsageServer.Services;

internal sealed class LogCleanupService : BackgroundService
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ClaudeUsageServer", "logs");

    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly ILogger<LogCleanupService> _logger;

    public LogCleanupService(
        IOptionsMonitor<ServerOptions> options,
        ILogger<LogCleanupService> logger)
    {
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run once immediately on startup, then repeat every 24 hours.
        while (!stoppingToken.IsCancellationRequested)
        {
            TryEnforceTotal();
            try
            {
                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void TryEnforceTotal()
    {
        try
        {
            if (!Directory.Exists(LogDir)) return;

            var limitBytes = (long)_options.CurrentValue.LogMaxTotalSizeMB * 1024L * 1024L;

            // Order oldest-first so we delete the least-recent files first.
            var files = new DirectoryInfo(LogDir)
                .GetFiles("*.log")
                .OrderBy(f => f.CreationTimeUtc)
                .ToArray();

            var totalBytes = files.Sum(f => f.Length);
            if (totalBytes <= limitBytes) return;

            _logger.LogInformation(
                "Log directory total {TotalMB:F1} MB exceeds cap {LimitMB} MB; removing oldest files",
                totalBytes / (1024.0 * 1024.0),
                _options.CurrentValue.LogMaxTotalSizeMB);

            foreach (var file in files)
            {
                if (totalBytes <= limitBytes) break;

                var fileBytes = file.Length;
                try
                {
                    file.Delete();
                    totalBytes -= fileBytes;
                    _logger.LogInformation(
                        "Removed log file {FileName} ({SizeMB:F2} MB)",
                        file.Name, fileBytes / (1024.0 * 1024.0));
                }
                catch (Exception ex)
                {
                    // Most likely the file is currently open by Serilog (today's active log).
                    _logger.LogWarning(
                        "Could not remove log file {FileName}: {Reason}",
                        file.Name, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Log directory cleanup failed: {Reason}", ex.Message);
        }
    }
}
