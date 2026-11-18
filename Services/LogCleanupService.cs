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

    /// <summary>Enforces the total-size cap at startup, then every 24 hours.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

    /// <summary>
    /// Deletes the oldest log files until the directory fits within <c>LogMaxTotalSizeMB</c>.
    /// A file that cannot be deleted (usually today's log, which Serilog holds open) is skipped
    /// with a warning.
    /// </summary>
    private void TryEnforceTotal()
    {
        try
        {
            if (!Directory.Exists(LogDir)) return;

            var limitBytes = (long)_options.CurrentValue.LogMaxTotalSizeMB * 1024L * 1024L;

            var filesOldestFirst = new DirectoryInfo(LogDir)
                .GetFiles("*.log")
                .OrderBy(f => f.CreationTimeUtc)
                .ToArray();

            var totalBytes = filesOldestFirst.Sum(f => f.Length);
            if (totalBytes <= limitBytes) return;

            _logger.LogInformation(
                "Log directory total {TotalMB:F1} MB exceeds cap {LimitMB} MB; removing oldest files",
                totalBytes / (1024.0 * 1024.0),
                _options.CurrentValue.LogMaxTotalSizeMB);

            foreach (var file in filesOldestFirst)
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
