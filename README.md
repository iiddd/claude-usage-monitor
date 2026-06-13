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
- The `claude` CLI must be on `PATH` (used to trigger automatic token refresh)

---

## Build

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o publish\
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
| `RefreshBufferMinutes` | `5` | How many minutes before token expiry to trigger a proactive refresh |
| `RetryIntervalSeconds` | `60` | How long to wait before retrying a failed refresh attempt |
| `CacheTtlSeconds` | `60` | How long a successful Anthropic response is served from cache before a fresh upstream call is made |

**Example — custom port via environment variable:**

```powershell
$env:ClaudeUsageServer__Port = "41921"
sc.exe start ClaudeUsageServer
```

**Note:** `CredentialsPath` is never written to logs even when set via environment variable.

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

On startup the service reads `~/.claude/.credentials.json` and schedules a refresh
at `expiresAt − RefreshBufferMinutes`. At refresh time it runs `claude --version`,
which causes the Claude CLI to silently check and renew the token, then re-reads the
credentials file.

If the CLI is unavailable or the refresh fails, the service logs a warning and retries
every `RetryIntervalSeconds` seconds without crashing.

The service also installs a `FileSystemWatcher` on the credentials file. If Claude Code
(or any other tool) refreshes the token externally, the service picks up the new token
within ~500 ms — no service restart required.

---

## iCUE widget setup

1. Build and install the service (see above).
2. In iCUE, import `widgets/claudeusage.icuewidget`.
3. Assign the widget to a XENEON EDGE S slot.
4. In the widget settings panel, set **Refresh Interval** (default 60 s).
   The port is hardcoded to `41920` and is not configurable in the widget.
5. No access token is needed in the widget. The service handles all authentication.
