# Solution Overview

**ClaudeUsageServer** is a single-machine tool that displays Claude Code token-usage statistics on a Corsair XENEON EDGE auxiliary display. It consists of two parts: a Windows Service that reads OAuth credentials from disk, keeps the access token fresh automatically, and exposes a CORS-enabled HTTP proxy on localhost; and an iCUE HTML widget that polls that proxy and renders the 5-hour and 7-day usage windows as segmented block bars with live countdown timers.

Target platform: Windows 11, Corsair XENEON EDGE S horizontal slot (840×344 px), iCUE 5.

---

## Projects

### ClaudeUsageServer (`ClaudeUsageServer.csproj`)

An ASP.NET Core Minimal API running as a Windows Service (`net8.0-windows`, `Microsoft.NET.Sdk.Web`). It has three responsibilities:

1. **Credential management** — reads `~/.claude/.credentials.json` on startup, watches the file for external changes via `FileSystemWatcher`, and schedules a proactive token refresh (by invoking `claude --version`) 5 minutes before the token expires. The token is stored only in memory inside a `sealed` `CredentialStore` class; it is never logged, never returned in any HTTP response, and injected into outgoing requests via `TryApplyToken(HttpRequestMessage)` to avoid appearing in stack traces.

2. **Response caching** — `UsageCache` holds the last successful Anthropic response body in memory. `GET /usage` returns the cached body immediately if it is younger than `CacheTtlSeconds` (default 60 s). On Anthropic error or network failure the stale cache is returned with an `X-Cache-Stale: true` header. The cache is never written to disk; it is thread-safe via a simple `lock`.

3. **HTTP proxy** — listens on `http://127.0.0.1:41920` (loopback only, asserted at startup). On cache miss, `GET /usage` forwards requests to `https://api.anthropic.com/api/oauth/usage` with the bearer token and the required `anthropic-beta` header added server-side. The widget sends **no credentials**. CORS headers are added manually per-endpoint so error responses also carry them.

4. **Status reporting** — `GET /status` returns `{ tokenValid, expiresAt, lastRefresh }` so the widget can show a connection indicator. `GET /health` returns `ok` for readiness checks.

**Key source files:**
- `Program.cs` — host setup, Kestrel config, endpoint definitions
- `Services/CredentialStore.cs` — thread-safe (`ReaderWriterLockSlim`) token store
- `Services/UsageCache.cs` — in-memory response cache with TTL and stale fallback
- `Services/CredentialWatcherService.cs` — `BackgroundService`; FSW, refresh loop, retry
- `Models/ServerOptions.cs` — configuration binding
- `Models/CredentialModels.cs` — JSON deserialization models and `StatusResponse`

**Key dependencies:** `Microsoft.Extensions.Hosting.WindowsServices` (service host), `Serilog.AspNetCore` + `Serilog.Sinks.File` + `Serilog.Sinks.EventLog` (logging).

---

### ClaudeUsageWidget (`widgets/ClaudeUsageWidget/`)

An iCUE HTML widget targeting `dashboard_lcd` (Xeneon Edge). The widget is responsible for:

- Reading the refresh interval, background color, and transparency from iCUE user-configurable properties. **No access token is configured in the widget** — authentication is handled entirely by the service. The port (`41920`) is hardcoded and not exposed as a widget property.
- Polling `http://localhost:41920/usage` and `http://localhost:41920/status` on the configured interval (no Authorization header). An `X-Cache-Stale: true` response header triggers an amber status dot.
- Rendering two rows (5-hour window, 7-day window), each with a 10-segment block bar, a color-coded percentage, and a countdown to the next reset.
- Showing a loading overlay on initial startup and an error overlay when the server is unreachable or returns an error (e.g. `credentials_not_found`, `token_expired`).

**Shared rendering module:** `scripts/claude-usage.js` exports a `ClaudeUsage` global with `renderData`, `renderError`, `renderLoading`, `renderStale`, `renderPlaceholder`, and `formatTimeUntil`. This module is used by both `index.html` (iCUE runtime) and `preview.html` (browser dev preview) — neither polling nor config resolution lives in this module.

**Preview harness:** `preview.html` is a standalone browser page that renders all four states (Normal, Critical, Error, Loading) side-by-side in scaled 840×344 iframes, each driven via postMessage from mock data. It requires no running service and is used for CSS/layout iteration without launching iCUE.

**Package artifact:** `widgets/claudeusage.icuewidget` is the ZIP-based package produced by `icuewidget package`, ready to import into iCUE.

**Key dependencies:** vanilla ES5 JavaScript, no external libraries. CSS layout uses a single `--u` baseline variable scaled from `100vw / 840`. Localized into 12 languages via `translation.json`.

---

## Architecture

```
~/.claude/.credentials.json
      │  claudeAiOauth.{ accessToken, expiresAt }
      │  read on startup; watched for external changes (FileSystemWatcher)
      ▼
┌──────────────────────────────────────────┐
│  ClaudeUsageServer  (Windows Service)    │
│  127.0.0.1:41920  ·  net8.0-windows             │
│                                          │
│  CredentialWatcherService                │
│  • loads token into CredentialStore      │
│  • reschedules refresh at expiresAt-5m   │
│  • runs `claude --version` to refresh    │
│  • retries every 60s on failure          │
│                                          │
│  GET /health  → "ok"                     │
│  GET /status  → { tokenValid, expiresAt }│
│  GET /usage   → proxy to Anthropic       │
│  OPTIONS /usage → CORS preflight         │
└──────────────┬───────────────────────────┘
               │  GET http://localhost:41920/usage
               │  (no credentials sent by widget)
               ▼
┌──────────────────────────────────────────┐
│  iCUE Widget  (ClaudeUsageWidget)        │
│  HTML/CSS/JS inside iCUE browser sandbox │
│  840 × 344 px  ·  XENEON EDGE S-H slot  │
│                                          │
│  • reads serverPort, refreshInterval     │
│    from iCUE props (no token property)   │
│  • polls proxy every N seconds           │
│  • renders block bars + countdown text   │
└──────────────────────────────────────────┘

Service → Anthropic:
  GET https://api.anthropic.com/api/oauth/usage
  Authorization: Bearer {token}   ← injected by service; never seen by widget
  anthropic-beta: oauth-2025-04-20
```

The service is the only component that handles credentials. The widget is credential-free; it calls localhost and the service injects the bearer token server-side. Token expiry is handled automatically — no manual re-entry in iCUE settings needed.

---

## Key Conventions

**Target framework:** `net8.0-windows` (`Microsoft.NET.Sdk.Web`). NuGet packages: `Microsoft.Extensions.Hosting.WindowsServices`, `Serilog.AspNetCore`, `Serilog.Sinks.File`, `Serilog.Sinks.EventLog`.

**Coding style:**
- Minimal API style — no controllers, no startup class. Top-level statements in `Program.cs`.
- Implicit usings enabled; nullable reference types enabled.
- PascalCase for types and properties; camelCase for locals. Async/await throughout.
- DI: `CredentialStore` registered as singleton; `CredentialWatcherService` as hosted service; `IHttpClientFactory` via `AddHttpClient()`. No manual constructor injection in endpoints — Minimal API parameter injection is used.
- Service classes live in `Services/`; configuration and data models in `Models/`.

**Configuration:** `appsettings.json` with a `ClaudeUsageServer` section — `Port`, `CredentialsPath`, `RefreshBufferMinutes`, `RetryIntervalSeconds`, `CacheTtlSeconds`. All values are overridable via environment variables using the `ClaudeUsageServer__<Key>` prefix. `CredentialsPath` is never logged.

**Logging:** Serilog as the MEL backend. Two sinks: rolling daily file at `logs/claudeusageserver-.log` (14-day retention), and Windows Event Log at Warning+ (source `ClaudeUsageServer`). Token values, Authorization headers, raw HTTP headers, and file paths are explicitly excluded from all log messages.

**Widget JavaScript:** ES5 only (no transpiler, no bundler). IIFE module pattern for `claude-usage.js`. The iCUE runtime exposes properties as global variables; the widget reads them via `typeof x !== 'undefined'` guards.

**CSS layout unit:** A single `--u: calc(100vw / 840)` variable drives all sizes. A `@media (min-aspect-ratio: 2.43) and (max-aspect-ratio: 2.45)` query switches `--u` to `1px` on the actual 840 px-wide device panel.

---

## External Dependencies

### Anthropic OAuth Usage Endpoint

| Field | Value |
|-------|-------|
| URL | `https://api.anthropic.com/api/oauth/usage` |
| Method | `GET` |
| Required headers | `Authorization: Bearer <token>`, `anthropic-beta: oauth-2025-04-20` |
| Response shape | `{ "five_hour": { "utilization": <0–100 float>, "resets_at": "<ISO8601>" }, "seven_day": { "utilization": <0–100 float>, "resets_at": "<ISO8601>" } }` |

`utilization` is a percentage (0–100); the widget divides by 100 before rendering. `resets_at` is an ISO 8601 timestamp used for the countdown display.

### Claude Code Credentials File

| Field | Value |
|-------|-------|
| Path | `%USERPROFILE%\.claude\.credentials.json` (i.e. `~/.claude/.credentials.json`) |
| Fields used | `claudeAiOauth.accessToken` (string), `claudeAiOauth.expiresAt` (Unix ms timestamp) |

The service reads this file directly on startup and re-reads it whenever `FileSystemWatcher` detects a change. The widget never sees the file or the token.

### iCUE Widget Runtime

The widget runs in an embedded Chromium context inside iCUE 5. Constraints:

- No filesystem access — all assets must be bundled in the `.icuewidget` package.
- No direct HTTPS requests to cross-origin APIs (hence the local proxy).
- iCUE property values arrive as global JavaScript variables before `DOMContentLoaded`; the widget reads them via the `iCUE_initialized` / `icueEvents` lifecycle hooks.
- Config fields are declared as `<meta name="x-icue-property">` tags in `index.html`; groups are declared in a `<script type="application/json" id="x-icue-groups">` block.
- `translation.json` must cover every `tr('...')` string used in `index.html` meta tags.

---

## Port Registry

| Port | Component | Purpose |
|------|-----------|---------|
| 41920 | ClaudeUsageServer | CORS-enabled HTTP proxy for Anthropic usage API. Bound to `127.0.0.1` only. Configured via `appsettings.json` → `ClaudeUsageServer.Port`. The widget hardcodes this port; it is not a configurable iCUE property. |

---

## Common Tasks

### Add a new data field to the widget display

1. Confirm the new field is present in the Anthropic API response: `curl http://127.0.0.1:41920/usage` (service must be running).
2. Add a new row block in `widgets/ClaudeUsageWidget/index.html` (copy an existing `.usage-row` div, assign new IDs).
3. Update `renderData()` in `scripts/claude-usage.js` to read the new field and call `renderRow()` with the new IDs.
4. Update `renderPlaceholder()` with representative mock values.
5. Re-run `icuewidget package` to rebuild `claudeusage.icuewidget`, then reinstall in iCUE.

### Change the response cache TTL

Edit `appsettings.json` and change `"CacheTtlSeconds"`. Restart the service for the new value to take effect.
Setting it to `0` effectively disables caching (every request calls upstream).

### Change the proxy port

1. Edit `appsettings.json`: change `"Port": 41920` to the desired value.
2. Edit `widgets/ClaudeUsageWidget/index.html`: change all occurrences of `41920` in the JavaScript (the `fetch` call and the loading subtitle) to the new port.
3. Edit `widgets/ClaudeUsageWidget/preview.html`: update the port in mock fetch calls if present.
4. Rebuild the server (`dotnet build`) and repackage the widget (`icuewidget package`).
5. Reinstall the widget in iCUE so the updated package is picked up.
6. Restart the Windows Service: `sc.exe stop ClaudeUsageServer && sc.exe start ClaudeUsageServer`.

### Install / reinstall the Windows Service after a rebuild

```powershell
# Stop and delete the old registration
sc.exe stop ClaudeUsageServer
sc.exe delete ClaudeUsageServer

# Publish fresh binary
dotnet publish -c Release -r win-x64 --self-contained false -o publish\

# Re-register (run as Administrator)
sc.exe create ClaudeUsageServer binPath= "C:\full\path\to\publish\ClaudeUsageServer.exe" DisplayName= "Claude Usage Server" start= auto
sc.exe start ClaudeUsageServer
```

### Test the usage endpoint without the iCUE widget

**Option A — curl (service running):**
```powershell
curl http://127.0.0.1:41920/health
curl http://127.0.0.1:41920/status
curl http://127.0.0.1:41920/usage
```
No token needed — the service adds it.

**Option B — browser preview (mock, no service):**
Open `widgets/ClaudeUsageWidget/preview.html` in a browser. **Mock** mode uses hardcoded data and requires no running service.

**Option C — browser preview (live, service running):**
Select **Live** in `preview.html`. It calls `http://localhost:41920/usage` through the service.

### Repackage and reinstall the widget after changes

```powershell
icuewidget package -o widgets/claudeusage.icuewidget widgets/ClaudeUsageWidget
```

Then in iCUE: **Widgets → Import** → select `widgets/claudeusage.icuewidget`.

Reinstalling replaces the existing widget; configured properties (port, interval) are preserved if the property names have not changed.

### Validate the widget package before distributing

```powershell
icuewidget validate widgets/ClaudeUsageWidget
```

Fix any reported errors before packaging. The `icuewidget` CLI must be installed — check with `icuewidget --version`.

---

## Known Constraints

- **iCUE widget sandbox — no filesystem.** The widget cannot read credential files directly. All credential access is handled by the Windows Service, which runs outside the sandbox.
- **Token refresh requires `claude` on PATH.** The refresh strategy calls `claude --version`. If the Claude CLI is not on the system PATH (e.g. the service runs as a different user), the refresh will fail and the service will log a warning and retry. Ensure the service account has `claude` accessible or configure a full path via a wrapper script.
- **Windows Event Log source must be pre-registered.** `Serilog.Sinks.EventLog` with `manageEventSource: false` will silently skip Event Log writes if the `ClaudeUsageServer` source does not exist. Run `New-EventLog -Source "ClaudeUsageServer" -LogName Application` once before starting the service.
- **Port conflict.** If port 41920 is in use, Kestrel fails to bind and the service stops. Change `Port` in `appsettings.json` and update the widget's `serverPort` setting to match.
- **Loopback only — enforced at startup.** The service asserts that Kestrel bound to `127.0.0.1` and will call `StopApplication()` if any other address is detected. Do not override the Kestrel binding via environment variables.
- **Single device, single slot.** The widget is designed for `dashboard_lcd` at 840×344 (Xeneon Edge S-H). The `manifest.json` does not declare other device types or slots.
- **No HTTPS on the proxy.** The server listens on plain HTTP (`http://127.0.0.1`). This is intentional for a localhost-only service; adding TLS is unnecessary and would complicate the iCUE widget's `fetch()` calls.

---

## Out of Scope

- **Multi-user / multi-account.** The service reads one credentials file and targets one Anthropic account. There is no support for multiple users or token rotation.
- **Remote monitoring.** The proxy binds to `127.0.0.1` only. There is no intent to expose it on a LAN or over the internet.
- **Cloud sync / persistence.** No database, no telemetry, no stored history of usage over time.
- **Usage from other Anthropic products.** The widget shows only the Claude Code OAuth usage counters. API-key-based usage (Claude.ai, direct API calls with `x-api-key`) is not surfaced.
- **Other iCUE device types.** Pump LCD, keyboard LCD, and Xeneon Edge sizes other than S-H are not designed or tested.
