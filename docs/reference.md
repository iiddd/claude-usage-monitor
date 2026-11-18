# Reference

Technical details of Claude Usage Monitor for developers and anyone who wants to know exactly
what it does. For installing and using the widget, see the [README](../README.md).

- [Configuration](#configuration)
- [Endpoints](#endpoints)
- [Response caching](#response-caching)
- [Rate limiting](#rate-limiting)
- [Error responses](#error-responses)
- [Token refresh](#token-refresh)
- [Logging](#logging)
- [Manual install](#manual-install)
- [MSI installer details](#msi-installer-details)
- [Build](#build)
- [Tests](#tests)

---

## Configuration

All settings live in `appsettings.json` next to the executable.
Every value can also be overridden with an environment variable named `ClaudeUsageServer__<Key>`.

| Key | Default | Description |
|-----|---------|-------------|
| `Port` | `41920` | TCP port Kestrel binds on (127.0.0.1 only) |
| `CredentialsPath` | *(empty)* | Path to the credentials JSON. Empty = `%USERPROFILE%\.claude\.credentials.json` |
| `RefreshBufferMinutes` | `5` | Minutes before token expiry to trigger a proactive refresh |
| `RetryIntervalSeconds` | `60` | Wait between failed refresh retries |
| `CacheTtlSeconds` | `60` | How long a successful Anthropic response is served from cache |
| `LogRetentionDays` | `14` | Number of daily log files to keep |
| `LogMaxFileSizeMB` | `10` | Max size of a single log file before rolling to a new one |
| `LogMaxTotalSizeMB` | `100` | Safety-net cap on total log directory size |
| `DiagnosticLogging` | `false` | Temporary troubleshooting switch that traces the token lifecycle (see [Diagnostic logging](#diagnostic-logging)) |

`CredentialsPath` is never written to logs.

`appsettings.json` is overwritten when the MSI upgrades. To keep a setting across upgrades, put it
in the service's environment instead:

```powershell
$svcKey = "HKLM:\SYSTEM\CurrentControlSet\Services\ClaudeUsageServer"
$existing = (Get-ItemProperty $svcKey).Environment
Set-ItemProperty $svcKey -Name Environment -Value (
    @("ClaudeUsageServer__CacheTtlSeconds=120") + $existing
)
```

### Changing the port

The widget has the port `41920` built in, and iCUE only lets it reach that port. Changing `Port`
therefore also means editing the widget (`index.html` and the `localhost` permission in
`manifest.json`), repackaging it, and importing it again. The steps are in
[CLAUDE.md](../CLAUDE.md#change-the-proxy-port).

---

## Endpoints

All endpoints are reachable only from `127.0.0.1`.

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/health` | Returns `ok`. Used for readiness checks. |
| `GET` | `/status` | Returns the service state (see below). No credential data. |
| `GET` | `/usage` | Proxies `https://api.anthropic.com/api/oauth/usage`. Returns usage JSON or an error object. |
| `OPTIONS` | `/usage` | CORS preflight; returns 200 with CORS headers. |

### `/status`

```json
{
  "state": "ok",
  "tokenValid": true,
  "expiresAt": "2026-07-21T18:00:00.0000000+00:00",
  "lastRefresh": "2026-07-21T13:00:00.0000000+00:00",
  "lastRefreshAttempt": "2026-07-21T13:00:00.0000000+00:00",
  "lastRefreshError": null,
  "consecutiveFailures": 0,
  "nextRetryAt": null,
  "upstreamStatus": null,
  "upstreamRetryAt": null
}
```

| `state` | Meaning |
|---------|---------|
| `ok` | Token valid and the last upstream call succeeded |
| `refreshing` | A refresh grant is currently in flight |
| `refresh_failed` | Last refresh failed for a **retryable** reason (network / 5xx). `lastRefreshError` and `nextRetryAt` are set |
| `reauth_required` | Refresh token rejected (`invalid_grant`); **not** retryable, `claude login` needed |
| `credentials_missing` | Credentials file absent or unreadable |
| `upstream_error` | Token is fine but the Anthropic usage endpoint is failing; `upstreamStatus` carries the last HTTP status |

`lastRefreshAttempt`, `lastRefreshError` (null when none) and `consecutiveFailures` are always
present. `nextRetryAt` is set only in `refresh_failed`, and `upstreamStatus` only in
`upstream_error`. `upstreamRetryAt` is set only while usage requests are paused after an Anthropic
HTTP 429 (see [Rate limiting](#rate-limiting)); otherwise it is `null`. **No token material ever
appears in `/status`.**

---

## Response caching

`/usage` caches the last successful Anthropic response in memory:

1. **Fresh cache hit** (younger than `CacheTtlSeconds`): returned immediately, no upstream call.
2. **Cache miss:** upstream is called; on success the response is cached and returned.
3. **Anthropic error with a stale cache:** the stale body is returned with an `X-Cache-Stale: true` header.
4. **Anthropic error with no cache:** the error is returned.

Only one request calls Anthropic at a time. Requests that arrive while a call is in flight (for
example two widget instances polling together) wait for it and reuse its result.

## Rate limiting

When Anthropic answers HTTP 429, the service stops calling the usage endpoint for a while: for the
`Retry-After` the server sends (capped at 1 hour), or otherwise 1, 2, 4, 8, then 15 minutes for
consecutive 429s. During the pause `/usage` makes no upstream call and returns the stale cache (or
`upstream_error` with status 429 when there is none), with a `Retry-After` header saying when the
next call will be made. `/status` shows that time in `upstreamRetryAt`. The first successful call
clears the backoff.

## Error responses

The `error` field uses the same names as the `/status` states, so the widget branches on one set
of codes.

| Condition | Status | Body |
|-----------|--------|------|
| Credentials file absent/unreadable | 503 | `{"error":"credentials_missing"}` |
| Refresh token expired/revoked; login needed | 503 | `{"error":"reauth_required"}` |
| Token expired / refresh failing | 503 | `{"error":"refresh_failed"}` |
| Anthropic non-200, no cache | same as upstream | `{"error":"upstream_error"}` |
| Rate-limited (HTTP 429), no cache | 429 + `Retry-After` | `{"error":"upstream_error"}` |
| Rate-limited (HTTP 429), stale cache | 200 + `X-Cache-Stale: true` + `Retry-After` | last cached body |
| Anthropic error, stale cache | 200 + `X-Cache-Stale: true` | last cached body |
| Network failure, stale cache | 200 + `X-Cache-Stale: true` | last cached body |
| Network failure, no cache | 503 | `{"error":"upstream_error"}` |

---

## Token refresh

On startup the service reads `~/.claude/.credentials.json` and schedules a proactive refresh at
`expiresAt − 5 minutes`. At that time it performs a direct OAuth **`refresh_token` grant**, the
same mechanism the Claude Code CLI uses:

```
POST https://platform.claude.com/v1/oauth/token      (falls back to https://console.anthropic.com/v1/oauth/token)
Content-Type: application/json

{ "grant_type": "refresh_token", "refresh_token": "…", "client_id": "9d1c250a-e61b-44d9-88ed-5944d1962f5e" }
```

The response returns a new access token, a new expiry, and usually a **new refresh token** (token
rotation). The service writes these back into `~/.claude/.credentials.json` in the structure Claude
Code expects, keeping every field it does not manage (`refreshTokenExpiresAt`, `scopes`,
`subscriptionType`, …). It writes a temp file and renames it, so the file is never left half
written, and it ignores the file-watcher event its own write causes.

> **Consumes no usage.** The `refresh_token` grant does **not** count against your 5-hour or 7-day
> limits, so the token can still refresh when usage is at 100%, which is exactly when the widget
> matters most.

**Retry.** On a network or 5xx failure the service logs a warning and retries every 60 s
(`RetryIntervalSeconds`). If Claude Code or another tool refreshes the token in the meantime, the
file watcher picks up the change within about half a second.

**Re-authentication required.** If the refresh token itself is expired or revoked (after a long
idle period, or a manual logout), the grant returns a client error. Retrying cannot help, so the
service stops retrying, logs one warning, and enters the **`reauth_required`** state: `/status`
reports it, `/usage` returns `503 {"error":"reauth_required"}`, and the widget shows **"Login
required: run `claude login` in a terminal"**. Running `claude login` writes a fresh credentials
file; the file watcher picks it up and the service recovers on its own, with no restart.

---

## Logging

Logs are written to `%ProgramData%\ClaudeUsageServer\logs\`, for both the MSI and a manual install.

- **Rolling file:** one file per day (`claudeusageserver-YYYYMMDD.log`), with a numeric suffix if a
  single day exceeds `LogMaxFileSizeMB`.
- **Windows Event Log:** Application log, source `ClaudeUsageServer`. Warnings and errors only.
- **Total-size cap:** on startup and every 24 hours, the oldest files are deleted if the directory
  exceeds `LogMaxTotalSizeMB`.

**Never logged, at any log level:** token values, Authorization headers, raw HTTP headers, raw
OAuth response bodies, file contents, file paths, or environment variable values.

### Startup breadcrumb

Every service start writes one line that describes the running configuration, so any log file can
be matched to an incident later:

```
startup breadcrumb: version=1.1.2.0 Port=41920 RefreshBufferMinutes=5 RetryIntervalSeconds=60 CacheTtlSeconds=60 DiagnosticLogging=False credentialsPresent=True expiresAt=2026-07-21T18:00:00.0000000+00:00 initialState=ok
```

It contains the version, the effective settings, whether the credentials file was found, the
`expiresAt` it read, and the initial state. No paths, no token material.

### Diagnostic logging

Set `DiagnosticLogging` to `true` (in `appsettings.json` or with the
`ClaudeUsageServer__DiagnosticLogging=true` environment variable) to trace the **token lifecycle**
while diagnosing an intermittent problem. It logs one line per event at Debug level, each tagged
with a short **correlation id** (`[a1b2c3d4]`) so one refresh cycle can be followed through
interleaved lines:

- **credentials read:** file last-write time, parsed `expiresAt`, and whether the `accessToken` /
  `refreshToken` fields were present (yes/no only, never values, lengths or prefixes)
- **file watcher event:** change type, whether it was processed or ignored as the echo of the
  service's own write, and the resulting `expiresAt`
- **refresh scheduling:** when the next proactive refresh is due and the `expiresAt` it is based on
- **refresh attempt:** trigger (`scheduled` | `startup-expired` | `retry`) and the attempt number
- **refresh outcome:** success (new `expiresAt`; whether the refresh token rotated) or failure
  (HTTP status, OAuth `error` code, or exception type, never the raw body)
- **state transitions:** `old -> new` with a reason
- **/usage cache decisions:** hit, miss or stale, one line per request

> **Meant to be temporary.** Debug logging is more verbose, but the usual `LogMaxFileSizeMB` and
> `LogMaxTotalSizeMB` limits still apply. At the default 60 s poll interval the busiest event (cache
> decisions) is about one line a minute. Set it back to `false` once the problem is understood.

---

## Manual install

Use this instead of the MSI if you want to run a build of your own. Requires the
[ASP.NET Core Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0) (x64).

**Register the Windows Event Log source (one-time, as Administrator):**

```powershell
New-EventLog -Source "ClaudeUsageServer" -LogName Application
```

**Publish the service:**

```powershell
dotnet publish ClaudeUsageServer.csproj -c Release -r win-x64 --self-contained false -o publish\
```

**Register and start the service (as Administrator):**

```powershell
sc.exe create ClaudeUsageServer `
    binPath= "C:\path\to\publish\ClaudeUsageServer.exe" `
    DisplayName= "Claude Usage Monitor" `
    start= auto
sc.exe description ClaudeUsageServer "Proxies Claude Code OAuth usage data to the iCUE widget."
sc.exe start ClaudeUsageServer
```

The service runs as LocalSystem, so tell it where your credentials file is:

```powershell
$svcKey = "HKLM:\SYSTEM\CurrentControlSet\Services\ClaudeUsageServer"
Set-ItemProperty $svcKey -Name Environment -Type MultiString -Value @(
    "ClaudeUsageServer__CredentialsPath=$env:USERPROFILE\.claude\.credentials.json"
)
sc.exe stop ClaudeUsageServer
sc.exe start ClaudeUsageServer
```

**Import the widget:** in iCUE, **Widgets → Import**, select `widgets/claudeusage.icuewidget`.

**Check the service:**

```powershell
curl http://127.0.0.1:41920/health   # → ok
curl http://127.0.0.1:41920/status   # → {"state":"ok",...}
```

---

## MSI installer details

### What the installer does

1. Copies the service files to `%ProgramFiles%\ClaudeUsageServer\`.
2. Creates `%ProgramData%\ClaudeUsageServer\logs\` for log files.
3. Registers the Windows Event Log source `ClaudeUsageServer`.
4. Installs the Windows service (automatic start, LocalSystem account).
5. Writes `ClaudeUsageServer__CredentialsPath` (pointing at the installing user's
   `%USERPROFILE%\.claude\.credentials.json`) into the service's environment, so the LocalSystem
   service can find that user's credentials.
6. Starts the service.

### Uninstall

**Settings → Apps → Claude Usage Monitor → Uninstall.** This stops and removes the service, deletes
`%ProgramFiles%\ClaudeUsageServer\`, and removes the Event Log source.

The logs in `%ProgramData%\ClaudeUsageServer\logs\` are kept. To remove them too:

```powershell
Remove-Item -Recurse -Force "$env:ProgramData\ClaudeUsageServer"
```

### Upgrade

Run a newer MSI over an existing installation. It performs a major upgrade: the service is stopped,
the old version removed, the new one installed and started. See [Configuration](#configuration) for
keeping settings across upgrades.

---

## Build

```powershell
# Service
dotnet publish ClaudeUsageServer.csproj -c Release -r win-x64 --self-contained false -o publish\

# MSI (after publishing the service)
dotnet build ClaudeUsageServer.Installer\ClaudeUsageServer.Installer.wixproj -c Release -p:Platform=x64

# Widget package (needs icuewidget-cli >= 0.5: npm install -g icuewidget-cli)
icuewidget package -o widgets/claudeusage.icuewidget widgets/ClaudeUsageWidget
```

The MSI is written to `ClaudeUsageServer.Installer\bin\x64\Release\ClaudeUsageServer.msi`.

## Tests

```powershell
dotnet test tests\ClaudeUsageServer.Tests
```

The tests run fully offline: the Anthropic endpoints are stubbed and the credentials file is a
temporary copy. They cover the credential state machine, the response cache, rate-limit backoff and
request sharing, the token-refresh flow (success with write-back, rejected refresh token, server
errors with fallback host, recovery after `claude login`), the HTTP endpoints including CORS and
stale-cache fallback, and checks that no token ever appears in logs or `/status`.

[CI](../.github/workflows/ci.yml) runs the tests on every push and pull request, fails on NuGet
packages with known vulnerabilities, validates the widget with `icuewidget-cli`, and runs CodeQL.
