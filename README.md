# Claude Usage Monitor

[![CI](https://github.com/iiddd/claude-usage-monitor/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/iiddd/claude-usage-monitor/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/iiddd/claude-usage-monitor)](https://github.com/iiddd/claude-usage-monitor/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/iiddd/claude-usage-monitor/badge)](https://scorecard.dev/viewer/?uri=github.com/iiddd/claude-usage-monitor)
[![OpenSSF Best Practices](https://www.bestpractices.dev/projects/16875/badge)](https://www.bestpractices.dev/projects/16875)

> **Unofficial.** This is a free, open-source community project. It is not affiliated with,
> endorsed by, or supported by Anthropic or Corsair. It reads usage data from an undocumented
> Anthropic endpoint (`/api/oauth/usage`) that Claude Code itself uses, so it may stop working
> if Anthropic changes that endpoint.

Shows your Claude Code usage limits live on the **Corsair XENEON EDGE** auxiliary screen.
Two parts ship together: a small Windows service that signs in with your Claude Code credentials,
and an iCUE widget that draws your usage on the display.

![Widget — normal state](docs/preview-normal.png)
![Widget — error state](docs/preview-error.png)

## What it shows

- The **5-hour** and **7-day** usage windows, each as a 10-segment bar
- The percentage used, colored green, amber, then red as you approach the limit
- A countdown to the next reset of each window
- A status dot that turns amber when the numbers are from cache because Anthropic can't be reached

## Requirements

- Windows 10 or 11
- Corsair XENEON EDGE with **iCUE 5.52 or newer**
- Claude Code installed and signed in (`claude login`)
- **[ASP.NET Core Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0) (x64).** The
  installer does not include it; without it the service cannot start.

## Install

Download `ClaudeUsageServer.msi` and `claudeusage.icuewidget` from the
[latest release](https://github.com/iiddd/claude-usage-monitor/releases/latest).

1. Run `ClaudeUsageServer.msi` and accept the UAC prompt. The installer sets up the Windows service
   and starts it.
2. In iCUE, go to **Widgets → Import** and select `claudeusage.icuewidget`. When iCUE asks, allow the
   widget to access `localhost:41920`; without that it shows "Service offline".
3. Assign the widget to a XENEON EDGE S slot.

The widget shows live usage as soon as the service is running.

### Check that a download is genuine

The MSI is not code-signed yet, so Windows SmartScreen may warn on first run. Every release is built
by GitHub Actions from the tagged commit, with signed build provenance. To verify a file (needs the
[GitHub CLI](https://cli.github.com)):

```powershell
gh attestation verify ClaudeUsageServer.msi --repo iiddd/claude-usage-monitor
gh attestation verify claudeusage.icuewidget --repo iiddd/claude-usage-monitor
```

Each release also lists SHA-256 checksums in `SHA256SUMS.txt`.

### Upgrade and uninstall

To upgrade, run the newer MSI; it replaces the old version and restarts the service. You don't need
to re-import the widget unless the release notes say it changed.

To uninstall, use **Settings → Apps → Claude Usage Monitor → Uninstall**, and remove the widget in
iCUE. Logs stay in `%ProgramData%\ClaudeUsageServer\logs\`; delete that folder to remove them too.

## Widget settings

Change these in the iCUE settings panel; they apply immediately.

| Setting | Default | Description |
|---------|---------|-------------|
| Refresh Interval | 60 s | How often the widget asks the service for new numbers |
| Threshold Color Coding | On | Colors the bar and percentage green/amber/red by usage, instead of the fixed colors below |
| Text Color | `#FFFFFF` | Color of the countdown text |
| Block Color | `#3B82F6` | Filled block color (when threshold coding is off) |
| Empty Block Color | `#484848` | Unfilled block color |
| Percentage Color | `#FFFFFF` | Percentage color (when threshold coding is off) |
| Low / Medium / High Color | green / amber / red | Colors used when threshold coding is on |
| Background Color | `#161616` | Widget background |
| Widget Transparency | 0% | Background transparency |

## Troubleshooting

First check what the service reports:

```powershell
curl http://127.0.0.1:41920/status
```

| The widget shows | What to do |
|------------------|------------|
| **Service offline** | The widget can't reach the service. Check that **Claude Usage Monitor** is running in `services.msc`. If it won't start, install the [ASP.NET Core Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0) (x64). If the service is running, check that the widget's permission for `localhost:41920` is still allowed in iCUE. |
| **No credentials** | `/status` says `credentials_missing`. Install Claude Code and run `claude login`. The service picks up the new credentials on its own, without a restart. |
| **Login required** | Your Claude Code sign-in has expired. Run `claude login` in a terminal; the widget recovers by itself. |
| **Anthropic API error, HTTP 429** | Anthropic is rate-limiting the usage endpoint. The service waits (1, 2, 4, 8, then 15 minutes) before trying again, and recovers on its own. `upstreamRetryAt` in `/status` shows when it will try next. |
| **Token refresh failing** | The service couldn't reach Anthropic to renew the token and retries every minute. Check your internet connection. |

**The service won't start after installing.** Check the logs in `%ProgramData%\ClaudeUsageServer\logs\`
and the Windows Event Log (Application, source `ClaudeUsageServer`). Besides a missing ASP.NET Core
Runtime, the usual cause is a wrong credentials path. Check it with:

```powershell
Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\ClaudeUsageServer" -Name Environment
# Expected: ClaudeUsageServer__CredentialsPath=C:\Users\<you>\.claude\.credentials.json
```

To fix it (as Administrator):

```powershell
$svcKey = "HKLM:\SYSTEM\CurrentControlSet\Services\ClaudeUsageServer"
Set-ItemProperty $svcKey -Name Environment -Type MultiString -Value @(
    "ClaudeUsageServer__CredentialsPath=$env:USERPROFILE\.claude\.credentials.json"
)
sc.exe stop ClaudeUsageServer
sc.exe start ClaudeUsageServer
```

**Port 41920 is already in use.** The widget has this port built in, so changing it means rebuilding
the widget. See [Changing the port](docs/reference.md#changing-the-port).

## How it works

```
~/.claude/.credentials.json
      │  read on startup, watched for changes
      ▼
┌──────────────────────────────────────────┐
│  Claude Usage Monitor (Windows service)  │
│  127.0.0.1:41920 — your computer only    │
│                                          │
│  • Keeps your Claude Code token fresh    │
│  • Asks Anthropic for your usage         │
│  • Caches it, backs off when rate-limited│
└──────────────┬───────────────────────────┘
               │  http://localhost:41920/usage
               │  (no credentials in the request)
               ▼
┌──────────────────────────────────────────┐
│  Claude Usage widget  (iCUE)             │
│  • Asks the service every N seconds      │
│  • Draws the bars and countdowns         │
└──────────────────────────────────────────┘
```

Only the service handles credentials. The widget never sees your token; it only talks to the local
service, which adds the token when it calls Anthropic. Refreshing the token uses the same OAuth
mechanism as the Claude Code CLI and **does not count against your usage**, so it keeps working even
at 100%.

### Endpoints

The service answers on `http://127.0.0.1:41920`:

| Path | Returns |
|------|---------|
| `/health` | `ok` |
| `/status` | The service state, for example `ok`, `reauth_required` or `upstream_error`. No credentials. |
| `/usage` | Your usage from Anthropic, or an error code the widget understands |

Configuration, response shapes, error codes, caching, rate limiting, logging and the token refresh
details are in the **[reference](docs/reference.md)**.

## Privacy

The service sends nothing anywhere except to Anthropic, and only to do what you installed it for:

- **Anthropic's usage endpoint** (`https://api.anthropic.com/api/oauth/usage`), to read the Claude Code usage the widget displays. The request carries your own Claude Code OAuth access token, read from your local `~/.claude/.credentials.json`.
- **Anthropic's OAuth token endpoints** (`https://platform.claude.com/v1/oauth/token`, falling back to `https://console.anthropic.com/v1/oauth/token`), to refresh that token before it expires, the same way the Claude Code CLI does.

Anthropic's handling of that data is covered by [Anthropic's Privacy Policy](https://www.anthropic.com/legal/privacy). There is no telemetry, analytics, or update check, and nothing is sent to the maintainer. The service listens only on `127.0.0.1`; the iCUE widget talks only to that local service and never sees your credentials. To stop all network activity, stop or uninstall the **Claude Usage Monitor** Windows service.

## Development

### Build

```powershell
dotnet publish ClaudeUsageServer.csproj -c Release -r win-x64 --self-contained false -o publish\
dotnet build ClaudeUsageServer.Installer\ClaudeUsageServer.Installer.wixproj -c Release -p:Platform=x64
```

### Tests

```powershell
dotnet test tests\ClaudeUsageServer.Tests
```

The tests run offline and run in CI on every push and pull request. Packaging the widget, manual
installation and what the tests cover are in the [reference](docs/reference.md#build).

## Contributing

Bug reports and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for how to report a problem, the test policy, and the code style.

## Security

See [SECURITY.md](SECURITY.md) for the security model and how to report a vulnerability privately.

## License

[MIT](LICENSE). Free to use, modify, and share.
