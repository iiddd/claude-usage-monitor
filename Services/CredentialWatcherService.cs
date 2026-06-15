using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using ClaudeUsageServer.Models;
using Microsoft.Extensions.Options;

namespace ClaudeUsageServer.Services;

internal sealed class CredentialWatcherService : BackgroundService
{
    private readonly CredentialStore _store;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly ILogger<CredentialWatcherService> _logger;
    private FileSystemWatcher? _watcher;
    private bool _cliPathWarningLogged;

    // Capacity-1 channel: multiple rapid FSW events collapse into one reload signal.
    private readonly Channel<bool> _reloadSignal = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    public CredentialWatcherService(
        CredentialStore store,
        IOptionsMonitor<ServerOptions> options,
        ILogger<CredentialWatcherService> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ClaudeUsageServer starting");

        await TryLoadCredentialsAsync();
        SetupFileWatcher();

        while (!stoppingToken.IsCancellationRequested)
        {
            var timeUntilRefresh = _store.GetTimeUntilRefresh(_options.CurrentValue.RefreshBufferMinutes);

            if (timeUntilRefresh > TimeSpan.Zero)
            {
                // Wait until the refresh window opens, but yield early if FSW fires.
                try
                {
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    waitCts.CancelAfter(timeUntilRefresh);
                    await _reloadSignal.Reader.ReadAsync(waitCts.Token);
                    // FSW fired: credentials already reloaded inside the handler — restart loop.
                    continue;
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Timeout reached: fall through to refresh.
                }
            }

            if (!stoppingToken.IsCancellationRequested)
                await TryRefreshWithRetryAsync(stoppingToken);
        }

        _logger.LogInformation("ClaudeUsageServer stopping");
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }

    // ── Credential loading ─────────────────────────────────────────────────

    private async Task TryLoadCredentialsAsync()
    {
        var path = _options.CurrentValue.ResolvedCredentialsPath;
        try
        {
            string json = await ReadWithRetryAsync(path);

            var file = JsonSerializer.Deserialize<CredentialsFile>(json);
            var oauth = file?.ClaudeAiOauth;

            if (string.IsNullOrWhiteSpace(oauth?.AccessToken))
            {
                _logger.LogWarning("Credentials file does not contain a valid access token");
                _store.MarkFileInvalid();
                return;
            }

            var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(oauth.ExpiresAt);
            _store.Update(oauth.AccessToken, expiresAt);
            _logger.LogInformation("Credentials loaded, token expires at {ExpiresAt:O}", expiresAt);
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning("Credentials file not found");
            _store.MarkFileNotFound();
        }
        catch (DirectoryNotFoundException)
        {
            _logger.LogWarning("Credentials directory not found");
            _store.MarkFileNotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error reading credentials file");
            _store.MarkFileInvalid();
        }
    }

    // Retries up to 3 times with a short delay to handle write-lock races.
    private static async Task<string> ReadWithRetryAsync(string path)
    {
        IOException? last = null;
        for (int i = 0; i < 3; i++)
        {
            try { return await File.ReadAllTextAsync(path); }
            catch (IOException ex) when (i < 2)
            {
                last = ex;
                await Task.Delay(200);
            }
        }
        throw last!;
    }

    // ── Token refresh ──────────────────────────────────────────────────────

    private async Task TryRefreshWithRetryAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Exit if the token now has ample time remaining (e.g. FSW delivered a fresh token
            // during the retry wait). Do NOT exit merely because the token is still technically
            // non-expired — we may be in the buffer window and a proactive refresh is needed.
            if (_store.GetTimeUntilRefresh(_options.CurrentValue.RefreshBufferMinutes) > TimeSpan.Zero)
                return;

            bool cliOk = await TryRunClaudeCliAsync(stoppingToken);
            if (cliOk)
            {
                await TryLoadCredentialsAsync();
                if (_store.GetTimeUntilRefresh(_options.CurrentValue.RefreshBufferMinutes) > TimeSpan.Zero)
                {
                    _logger.LogInformation("Token refresh succeeded");
                    return;
                }
            }

            _logger.LogWarning(
                "Token refresh did not produce a valid credential, retrying in {Seconds}s",
                _options.CurrentValue.RetryIntervalSeconds);

            // Wait the retry interval; FSW can interrupt if it delivers a fresh token.
            try
            {
                using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                retryCts.CancelAfter(TimeSpan.FromSeconds(_options.CurrentValue.RetryIntervalSeconds));
                await _reloadSignal.Reader.ReadAsync(retryCts.Token);
                // If FSW delivered a fresh token with ample lifetime, exit the retry loop.
                if (_store.GetTimeUntilRefresh(_options.CurrentValue.RefreshBufferMinutes) > TimeSpan.Zero)
                    return;
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Retry interval elapsed.
            }
        }
    }

    private async Task<bool> TryRunClaudeCliAsync(CancellationToken stoppingToken)
    {
        var cliPath = _options.CurrentValue.ResolvedClaudeCliPath;

        if (!File.Exists(cliPath))
        {
            if (!_cliPathWarningLogged)
            {
                _cliPathWarningLogged = true;
                _logger.LogWarning(
                    "Claude CLI not found at expected path. " +
                    "Set ClaudeUsageServer:ClaudeCliPath in appsettings.json to the full path " +
                    "of claude.cmd (run 'Get-Command claude' in PowerShell to find it). " +
                    "Token refresh will not work until this is resolved.");
            }
            return false;
        }

        // Reset warning flag so it fires again if the path is later removed.
        _cliPathWarningLogged = false;

        try
        {
            _logger.LogInformation("Invoking claude CLI to trigger OAuth token refresh");

            // Send a minimal prompt so the CLI makes a real API call and refreshes the token.
            // --version does NOT cause a token refresh; only actual API calls do.
            // --model haiku / --tools "" / --effort low minimise cost and latency.
            // --no-session-persistence avoids writing session state to disk.
            const string refreshArgs =
                "-p \"hi\" --model haiku --tools \"\" --no-session-persistence --effort low";

            // .cmd files cannot be launched directly as a process target;
            // they must be invoked via cmd.exe /c.
            var isCmdFile = cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                         || cliPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

            var psi = isCmdFile
                ? new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{cliPath}\" {refreshArgs}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
                : new ProcessStartInfo
                {
                    FileName = cliPath,
                    Arguments = refreshArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Drain stdout and stderr concurrently with WaitForExitAsync to prevent
            // the pipe buffer from filling and blocking the child process.
            // The response text is intentionally discarded — only exit code matters.
            var drainOut = process.StandardOutput.ReadToEndAsync(stoppingToken);
            var drainErr = process.StandardError.ReadToEndAsync(stoppingToken);
            await process.WaitForExitAsync(stoppingToken);
            await Task.WhenAll(drainOut, drainErr);

            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError("claude CLI invocation failed: {Reason}", ex.Message);
            return false;
        }
    }

    // ── FileSystemWatcher ──────────────────────────────────────────────────

    private void SetupFileWatcher()
    {
        var path = _options.CurrentValue.ResolvedCredentialsPath;
        var dir = Path.GetDirectoryName(path);
        var file = Path.GetFileName(path);

        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            _logger.LogWarning("Credentials directory does not exist; file change watching is disabled");
            return;
        }

        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnCredentialsChanged;
        _watcher.Created += OnCredentialsChanged;
        _watcher.Renamed += OnCredentialsChanged;

        _logger.LogInformation("Watching credentials file for external changes");
    }

    private void OnCredentialsChanged(object sender, FileSystemEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            // Debounce: the file may still be mid-write.
            await Task.Delay(500);
            await TryLoadCredentialsAsync();
            _reloadSignal.Writer.TryWrite(true);
        });
    }
}
