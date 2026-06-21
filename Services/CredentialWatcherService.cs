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
        // Make Windows OEM code pages (e.g. CP866 used by cmd.exe on Russian locale)
        // available via Encoding.GetEncoding() on .NET 5+.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
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

        _cliPathWarningLogged = false;

        try
        {
            _logger.LogInformation("Invoking claude CLI to trigger OAuth token refresh");

            // Minimal prompt: causes the CLI to make a real API call which refreshes the token.
            const string refreshArgs = "-p \"hi\"";

            var isCmdFile = cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                         || cliPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

            string fileName;
            string arguments;

            if (isCmdFile)
            {
                // Prefer the inner claude.exe to avoid batch-file invocation issues entirely.
                // claude.cmd lives in <npm-bin-dir>\ and delegates to:
                //   <npm-bin-dir>\node_modules\@anthropic-ai\claude-code\bin\claude.exe
                var cmdDir = Path.GetDirectoryName(cliPath) ?? string.Empty;
                var innerExePath = Path.Combine(
                    cmdDir, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");

                if (File.Exists(innerExePath))
                {
                    fileName  = innerExePath;
                    arguments = refreshArgs;
                    _logger.LogInformation("Using inner claude.exe directly: {Path}", innerExePath);
                }
                else
                {
                    // Fallback: invoke via cmd.exe /c.
                    // cmd.exe /c quoting rule: when the command path is quoted, the entire
                    // "command + args" string must be wrapped in an additional outer pair of
                    // quotes: cmd.exe /c ""path\file.cmd" arg1 arg2"
                    var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
                    fileName  = Path.Combine(systemRoot, "System32", "cmd.exe");
                    arguments = $"/c \"\"{cliPath}\" {refreshArgs}\"";
                    _logger.LogInformation(
                        "Inner claude.exe not found at {InnerPath}; falling back to cmd.exe /c wrapper",
                        innerExePath);
                }
            }
            else
            {
                fileName  = cliPath;
                arguments = refreshArgs;
            }

            // cmd.exe emits error messages in the OEM codepage (CP866 on Russian-locale
            // Windows). RegisterProvider was called in the constructor; GetEncoding(866)
            // should succeed. If it still throws (unexpected), fall back to UTF-8.
            System.Text.Encoding oemEncoding;
            try { oemEncoding = System.Text.Encoding.GetEncoding(866); }
            catch { oemEncoding = System.Text.Encoding.UTF8; }

            var psi = new ProcessStartInfo
            {
                FileName               = fileName,
                Arguments              = arguments,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding  = oemEncoding,
                CreateNoWindow         = true,
            };

            // When the service runs as LocalSystem, USERPROFILE/APPDATA point to the system
            // account's profile. Override them so the CLI finds credentials and npm binaries.
            var credPath = _options.CurrentValue.ResolvedCredentialsPath;
            var claudeDir = Path.GetDirectoryName(credPath) ?? string.Empty;
            var userHome  = Path.GetDirectoryName(claudeDir) ?? string.Empty;

            if (!string.IsNullOrEmpty(userHome))
            {
                var appData      = Path.Combine(userHome, "AppData", "Roaming");
                var localAppData = Path.Combine(userHome, "AppData", "Local");

                psi.Environment["USERPROFILE"]  = userHome;
                psi.Environment["HOME"]         = userHome;
                psi.Environment["HOMEDRIVE"]    = Path.GetPathRoot(userHome)?.TrimEnd('\\') ?? string.Empty;
                psi.Environment["HOMEPATH"]     = userHome.Contains('\\')
                    ? userHome[userHome.IndexOf('\\')..]
                    : userHome;
                psi.Environment["APPDATA"]      = appData;
                psi.Environment["LOCALAPPDATA"] = localAppData;

                var npmBin      = Path.Combine(appData, "npm");
                var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (!currentPath.Contains(npmBin, StringComparison.OrdinalIgnoreCase))
                    psi.Environment["PATH"] = npmBin + ";" + currentPath;

                _logger.LogInformation(
                    "Child process env override — USERPROFILE={UserProfile} APPDATA={AppData} LOCALAPPDATA={LocalAppData}",
                    userHome, appData, localAppData);
            }

            // Log the exact invocation shape before launch so the next error is diagnosable.
            _logger.LogInformation(
                "Process start: FileName={FileName} Arguments={Arguments} UseShellExecute={UseShellExecute} WorkingDirectory={WorkingDirectory}",
                psi.FileName, psi.Arguments, psi.UseShellExecute, psi.WorkingDirectory);

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Drain stdout/stderr concurrently with WaitForExitAsync to avoid pipe-buffer deadlock.
            var outTask = process.StandardOutput.ReadToEndAsync(stoppingToken);
            var errTask = process.StandardError.ReadToEndAsync(stoppingToken);
            await process.WaitForExitAsync(stoppingToken);
            await Task.WhenAll(outTask, errTask);

            if (process.ExitCode != 0)
            {
                var stderr = errTask.Result.Trim();
                _logger.LogWarning(
                    "Claude CLI exited with code {ExitCode}{Stderr}",
                    process.ExitCode,
                    string.IsNullOrEmpty(stderr) ? string.Empty : ": " + stderr);
            }

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
