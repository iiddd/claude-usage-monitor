# ClaudeUsageServer

A minimal Windows Service that reads your Claude Code OAuth credentials from disk,
keeps the access token fresh, and exposes a CORS-enabled HTTP proxy on `localhost:41920`
so the iCUE widget running in a browser sandbox can fetch usage data without ever
touching credentials.

## How it works

```
~/.claude/.credentials.json  ──reads──►  ClaudeUsageServer (Windows Service)
                                                  │
                               GET http://localhost:41920/usage
                                                  │
                                 iCUE ClaudeUsageWidget (XENEON EDGE)
```

The service owns the token entirely. The widget sends no credentials; it just calls
`/usage` and receives the proxied Anthropic response.

---

## Prerequisites

- .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`)
- Claude Code installed and authenticated (`~/.claude/.credentials.json` exists)
- The `claude` CLI must be reachable from the service account (see **Token refresh** below)

---

## Build

### Build everything in one go: publish service + build MSI

```powershell
dotnet publish ClaudeUsageServer.csproj -c Release -r win-x64 --self-contained false -o publish\
dotnet build ClaudeUsageServer.Installer\ClaudeUsageServer.Installer.wixproj -c Release -p:Platform=x64
```

For a self-contained single-file executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\
```

---

## Install as Windows Service

Run the following commands in an **elevated** (Administrator) PowerShell prompt.

### Register the Windows Event Log source (one-time)

The service writes warnings and errors to the Windows Application Event Log.
The event source must exist before the service first logs anything:

```powershell
New-EventLog -Source "ClaudeUsageServer" -LogName Application
```

### Create the service

```powershell
sc.exe create ClaudeUsageServer `
    binPath= "C:\path\to\publish\ClaudeUsageServer.exe" `
    DisplayName= "Claude Usage Server" `
    start= auto
```

> Replace `C:\path\to\publish\` with the actual publish output directory.

### Set a description (optional)

```powershell
sc.exe description ClaudeUsageServer "Proxies Claude Code OAuth usage data to the iCUE widget."
```

### Start the service

```powershell
sc.exe start ClaudeUsageServer
```

### Verify it is running

```powershell
sc.exe query ClaudeUsageServer
```

The service listens on `http://127.0.0.1:41920`. Verify with:

```powershell
curl http://127.0.0.1:41920/health
# → ok

curl http://127.0.0.1:41920/status
# → {"tokenValid":true,"expiresAt":"...","lastRefresh":"..."}
```

---

## Uninstall

```powershell
sc.exe stop ClaudeUsageServer
sc.exe delete ClaudeUsageServer
Remove-EventLog -Source "ClaudeUsageServer"
```

---

## Configuration

All settings live in `appsettings.json` next to the executable.
Every value is also overridable via environment variable using the standard
.NET configuration prefix `ClaudeUsageServer__<Key>`.

| Key | Default | Description |
|-----|---------|-------------|
| `Port` | `41920` | TCP port Kestrel binds on (127.0.0.1 only) |
| `CredentialsPath` | *(empty)* | Path to the credentials JSON file. Empty = `%USERPROFILE%\.claude\.credentials.json` |
| `ClaudeCliPath` | *(empty)* | Full path to `claude.cmd`. Empty = auto-derived from `CredentialsPath` or `%USERPROFILE%`. **Must be set explicitly when running as LocalSystem** (see Token refresh). |
| `RefreshBufferMinutes` | `5` | How many minutes before token expiry to trigger a proactive refresh |
| `RetryIntervalSeconds` | `60` | How long to wait before retrying a failed refresh attempt |
| `CacheTtlSeconds` | `60` | How long a successful Anthropic response is served from cache before a fresh upstream call is made |
| `LogRetentionDays` | `14` | Number of log files Serilog keeps before deleting the oldest. With daily rolling this equals calendar days; with size-based intra-day rolling it may be fewer. See **Log retention** below. |
| `LogMaxFileSizeMB` | `10` | Maximum size of a single log file in MB. When reached, Serilog rolls to a new file with a numeric suffix (e.g. `claudeusageserver-20260614_001.log`). |
| `LogMaxTotalSizeMB` | `100` | Safety-net cap on the total size of all log files combined. A background task checks this on startup and every 24 hours, deleting oldest files until back under the limit regardless of `LogRetentionDays`. |

**Example — custom port via environment variable:**

```powershell
$env:ClaudeUsageServer__Port = "41921"
sc.exe start ClaudeUsageServer
```

**Note:** `CredentialsPath` and `ClaudeCliPath` are never written to logs even when set via environment variable.

---

## Log retention

Log files are written to `%ProgramData%\ClaudeUsageServer\logs\`. Two independent layers prevent unbounded disk growth:

**Layer 1 — Serilog per-file and retention limits** (`LogRetentionDays`, `LogMaxFileSizeMB`):

- A new log file is started each calendar day (`claudeusageserver-YYYYMMDD.log`).
- If a single day's file reaches `LogMaxFileSizeMB` (default 10 MB), Serilog rolls to a new file with a numeric suffix (`claudeusageserver-20260614_001.log`, `_002.log`, …) rather than growing unbounded.
- Serilog automatically deletes the oldest files once the total count exceeds `LogRetentionDays` (default 14). With daily rolling and no intra-day size spikes, 14 files = 14 calendar days. If size-based rolling produces extra files within a day, fewer calendar days are retained — the total-size cap below handles that edge case.

**Layer 2 — Total-size safety net** (`LogMaxTotalSizeMB`):

- A background task runs once on service startup and again every 24 hours.
- It sums the sizes of all `*.log` files in the log directory.
- If the total exceeds `LogMaxTotalSizeMB` (default 100 MB), it deletes the oldest files (by creation time) until back under the limit, regardless of `LogRetentionDays`.
- Only filenames and sizes are logged during cleanup — file contents are never read or logged.
- The currently-active log file (held open by Serilog) cannot be deleted on Windows; the cleanup task logs a warning and skips it if encountered.

---

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/health` | Returns `ok` (plain text). Used for readiness checks. |
| `GET` | `/status` | Returns `{ tokenValid, expiresAt, lastRefresh }`. No credential data. |
| `GET` | `/usage` | Proxies `https://api.anthropic.com/api/oauth/usage`. Returns usage JSON or a 503 error object. |
| `OPTIONS` | `/usage` | CORS preflight — returns 200 with CORS headers. |

All endpoints are reachable only from `127.0.0.1`. The service refuses to bind to any
other address and will stop itself on startup if it detects one.

### Response caching

`/usage` caches the last successful Anthropic response in memory. On each request:

1. **Fresh cache hit** (response younger than `CacheTtlSeconds`) — returned immediately, no upstream call.
2. **Cache miss** — upstream is called; on success the response is cached and returned.
3. **Anthropic error** (non-200 or network failure) — if any previous response is cached, it is returned with the header `X-Cache-Stale: true`; if no cache exists, the error is returned as-is.

The cache is in-memory only and is not written to disk. Polling the widget as frequently as desired will not trigger rate limiting against the Anthropic API.

### Error responses from `/usage`

| Condition | Status | Additional header | Body |
|-----------|--------|--------------------|------|
| Credentials file not found | 503 | — | `{"error":"credentials_not_found"}` |
| Token expired, refresh pending | 503 | — | `{"error":"token_expired"}` |
| Anthropic non-200, no cache | same as upstream | — | upstream body forwarded as-is |
| Anthropic non-200, stale cache available | 200 | `X-Cache-Stale: true` | last cached body |
| Network failure, stale cache available | 200 | `X-Cache-Stale: true` | last cached body |
| Network failure, no cache | 503 | — | `{"error":"upstream_unavailable"}` |

---

## Logging

Logs are written to two sinks simultaneously:

- **Rolling file:** `logs/claudeusageserver-YYYYMMDD.log` next to the executable.
  14 days of files are retained.
- **Windows Event Log:** Application log, source `ClaudeUsageServer`.
  Only warnings and errors are written here.

**What is never logged:** token values, Authorization header contents, raw HTTP headers,
file contents, or environment variable values.

---

## Token refresh

### Mechanism

On startup the service reads `~/.claude/.credentials.json` and schedules a proactive
refresh at `expiresAt − RefreshBufferMinutes` (default: 5 minutes before expiry). The
OAuth token is valid for approximately **26 hours** after each successful refresh, so
the CLI is invoked roughly once per day under normal operation.

At refresh time the service runs:

```
claude -p "hi" --model haiku --tools "" --no-session-persistence --effort low
```

This sends a minimal prompt to the Anthropic API. The act of making a real API call
causes the Claude CLI to silently renew the OAuth token and write the updated
credentials back to `~/.claude/.credentials.json`. The service detects the file change
(via `FileSystemWatcher`) and loads the new token within ~500 ms.

> **Note:** `claude --version` does **not** refresh the OAuth token. Only actual API
> calls trigger a renewal. This is why the CLI invocation sends a real (minimal) prompt.

### Cost trade-off

Each token refresh makes a real Anthropic API call using the Haiku model with a
one-word prompt and minimal output. This counts against your Claude Code usage limits
(the same 5-hour and 7-day windows that this widget displays). The cost is negligible
in practice — one micro-request per ~26 hours — but it is non-zero. A direct
OAuth `refresh_token` grant would avoid this overhead, but that mechanism is not
exposed to third-party tools via the Claude Code CLI.

### Retry behaviour

If the CLI call fails (not found, non-zero exit, or the token is still expired after
the call), the service logs a warning and retries every `RetryIntervalSeconds` (default:
60 s). Once a refresh succeeds and the new token has ample lifetime remaining, retries
stop and the service returns to passive watching until the next scheduled refresh.

The `FileSystemWatcher` remains active at all times. If Claude Code (or any other tool)
refreshes the token externally, the service picks up the new token within ~500 ms —
no service restart required, and the next scheduled refresh is recalculated from the
updated `expiresAt`.

### Running as LocalSystem (important)

When installed as a Windows Service the process runs as **LocalSystem**, which does not
inherit your user's `PATH`. The `claude` CLI is typically installed as a `.cmd` wrapper
in `%APPDATA%\npm\` — that directory is not on LocalSystem's PATH and `.cmd` files
cannot be launched as a direct process target regardless.

**You must set `ClaudeCliPath` explicitly in `appsettings.json`:**

```json
"ClaudeCliPath": "C:\\Users\\YourName\\AppData\\Roaming\\npm\\claude.cmd"
```

To find the correct path:

```powershell
(Get-Command claude).Source
```

If `ClaudeCliPath` is left empty the service attempts to infer the path from
`CredentialsPath` (or `%USERPROFILE%`). If the inferred path does not exist it logs
a one-time warning and skips CLI-based refresh until the setting is corrected — the
service continues running and the `FileSystemWatcher` still picks up external refreshes.

---

## MSI installer (ClaudeUsageServer.Installer)

The `ClaudeUsageServer.Installer` project produces a self-contained MSI that installs the service, creates the log directory, and registers the Windows Event Log source in one step.

### What the installer does

1. Copies all service files to `%ProgramFiles%\ClaudeUsageServer\`.
2. Creates `%ProgramData%\ClaudeUsageServer\logs\` for log files.
3. Registers the Windows Event Log source `ClaudeUsageServer` so Serilog's EventLog sink can write without a custom message DLL.
4. Installs the Windows Service (`ClaudeUsageServer`, auto-start, account: LocalSystem).
5. Runs a custom action (PowerShell) that writes the installing user's `%USERPROFILE%` and `%APPDATA%` paths into `appsettings.json` so the LocalSystem service can find the credentials file and Claude CLI.
6. Starts the service.

**Service account:** LocalSystem. Running as the interactive user requires knowing their Windows password at install time, which is unavailable for Microsoft accounts and domain accounts. LocalSystem is used instead. To bridge the gap, step 5 writes the installing user's paths into the service's environment registry entry (`HKLM\...\Services\ClaudeUsageServer\Environment`). The Windows SCM injects these as environment variables into the service process at every startup, and the .NET configuration system picks them up as overrides for `CredentialsPath` and `ClaudeCliPath`. `appsettings.json` is not modified.

### Build the MSI

Publish the service first, then build the installer:

```powershell
# Step 1 — publish the service (produces publish\ next to the solution)
dotnet publish ClaudeUsageServer.csproj -c Release -r win-x64 --self-contained false -o publish\

# Step 2 — build the MSI (outputs to ClaudeUsageServer.Installer\bin\x64\Release\)
dotnet build ClaudeUsageServer.Installer\ClaudeUsageServer.Installer.wixproj -c Release -p:Platform=x64
```

Or build both from the solution in one pass:

```powershell
dotnet publish ClaudeUsageServer.csproj -c Release -r win-x64 --self-contained false -o publish\
dotnet build ClaudeUsageServer.sln -c Release -p:Platform=x64
```

The MSI is produced at `ClaudeUsageServer.Installer\bin\x64\Release\ClaudeUsageServer.msi`.

### Install

Double-click `ClaudeUsageServer.msi`. Accept the UAC elevation prompt. The installer copies files, registers the service, and starts it. No further configuration is needed for a standard single-user Windows 10/11 machine.

After installation, verify the service is running:

```powershell
curl http://127.0.0.1:41920/health   # → ok
curl http://127.0.0.1:41920/status   # → {"tokenValid":true,...}
```

### Uninstall

**Settings → Apps → Installed apps → Claude Usage Server → Uninstall**, or via Add/Remove Programs.

The uninstaller performs these actions in order:

| Action | Detail |
|--------|--------|
| Stop service | Waits for the service process to exit before proceeding |
| Delete service | Removes the `ClaudeUsageServer` Windows Service registration from the SCM |
| Remove files | Deletes all files under `%ProgramFiles%\ClaudeUsageServer\` |
| Remove install directory | Removes `%ProgramFiles%\ClaudeUsageServer\` itself (empty after file removal) |
| Remove Event Log source | Deletes the `HKLM\...\EventLog\Application\ClaudeUsageServer` registry key and all its values |

**Deliberately kept after uninstall:**

`%ProgramData%\ClaudeUsageServer\logs\` and all log files inside it are **not** removed. This preserves diagnostic history across accidental uninstall/reinstall cycles. To do a full wipe:

```powershell
Remove-Item -Recurse -Force "$env:ProgramData\ClaudeUsageServer"
```

### Upgrade

Install a new MSI over an existing installation. The installer automatically removes the old version first (major upgrade), then installs the new one. The service is stopped before removal and restarted after install.

**appsettings.json is overwritten on upgrade.** If you have changed settings, use environment variable overrides instead so they survive upgrades:

```powershell
# Set on the service via the registry (persists across upgrades)
$svcKey = "HKLM:\SYSTEM\CurrentControlSet\Services\ClaudeUsageServer"
$existing = (Get-ItemProperty $svcKey).Environment
Set-ItemProperty $svcKey -Name Environment -Value (
    @("ClaudeUsageServer__CredentialsPath=C:\Users\You\.claude\.credentials.json") + $existing
)
```

Or set a system environment variable before restarting the service:
```powershell
[System.Environment]::SetEnvironmentVariable("ClaudeUsageServer__Port", "41921", "Machine")
```

### Troubleshooting

**Service won't start after install**

Check `%ProgramData%\ClaudeUsageServer\logs\` for Serilog log files. The most common cause is the installer failing to write the user paths into the service's environment registry entry.

Verify the entry exists:

```powershell
Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\ClaudeUsageServer" -Name Environment
# Expected output:
# ClaudeUsageServer__CredentialsPath=C:\Users\YourName\.claude\.credentials.json
# ClaudeUsageServer__ClaudeCliPath=C:\Users\YourName\AppData\Roaming\npm\claude.cmd
```

To fix manually (run as Administrator):

```powershell
$svcKey = "HKLM:\SYSTEM\CurrentControlSet\Services\ClaudeUsageServer"
Set-ItemProperty $svcKey -Name Environment -Type MultiString -Value @(
    "ClaudeUsageServer__CredentialsPath=$env:USERPROFILE\.claude\.credentials.json",
    "ClaudeUsageServer__ClaudeCliPath=$env:APPDATA\npm\claude.cmd"
)
sc.exe stop ClaudeUsageServer
sc.exe start ClaudeUsageServer
```

**Port 41920 already in use**

Change `Port` in `appsettings.json` and update the `serverPort` in the iCUE widget settings panel, then restart the service.

**Claude Code not installed or not authenticated**

The service starts but `/status` returns `tokenValid: false`. Install and authenticate Claude Code (`claude login`), then restart the service or wait for the `FileSystemWatcher` to pick up the new credentials file (within ~500 ms of Claude Code writing it).

---

## iCUE widget setup

1. Build and install the service (see above).
2. In iCUE, import `widgets/claudeusage.icuewidget`.
3. Assign the widget to a XENEON EDGE S slot.
4. In the widget settings panel, set **Refresh Interval** (default 60 s).
   The port is hardcoded to `41920` and is not configurable in the widget.
5. No access token is needed in the widget. The service handles all authentication.
