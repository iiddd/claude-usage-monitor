# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

> **Status:** the application to SignPath Foundation is pending. Releases up to and including
> v1.1.1 are not code-signed; verify them with the checksums and build provenance described in
> the [README](README.md#option-a--msi-installer-recommended). This page will be updated when
> signed releases start.

## What is signed

Only files built by this project from this repository:

- `ClaudeUsageServer.exe` and `ClaudeUsageServer.dll`, the Windows service
- `ClaudeUsageServer.msi`, the installer

Third-party libraries shipped inside the installer (for example Serilog and Microsoft.Extensions)
are not signed by this project. The iCUE widget package (`claudeusage.icuewidget`) is a ZIP archive
and cannot be Authenticode-signed; it is covered by the release checksums and build provenance.

Every signed file is built by the [release workflow](.github/workflows/release.yml) on
GitHub-hosted runners from a tagged commit of this repository. Each signing request is approved
manually before the certificate is used.

## Team roles

This project has one maintainer, who holds all three roles:

| Role | Responsibility | Members |
|------|----------------|---------|
| Committer / author | May change the source without further review | [Andrey Merkulov (@iiddd)](https://github.com/iiddd) |
| Reviewer | Reviews every change from contributors outside the team (pull requests) | [Andrey Merkulov (@iiddd)](https://github.com/iiddd) |
| Approver | Approves every signing request | [Andrey Merkulov (@iiddd)](https://github.com/iiddd) |

All team members use multi-factor authentication for GitHub and SignPath.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically
requested by the user or the person installing or operating it.

What it does contact, and why:

- **Anthropic's usage endpoint** (`https://api.anthropic.com/api/oauth/usage`), to read the
  Claude Code usage you asked it to display. The request carries your own Claude Code OAuth access
  token, read from your local `~/.claude/.credentials.json`.
- **Anthropic's OAuth token endpoints** (`https://platform.claude.com/v1/oauth/token`, falling
  back to `https://console.anthropic.com/v1/oauth/token`), to refresh that access token before it
  expires, the same way the Claude Code CLI does.

These calls go only to Anthropic and only on your behalf. Their handling of data is covered by
[Anthropic's Privacy Policy](https://www.anthropic.com/legal/privacy). The project has no
telemetry, analytics or update checks, and it sends nothing to the maintainer.

The service listens only on `127.0.0.1` (your own computer). The iCUE widget talks only to that
local service and never sees your credentials. To stop all network activity, stop or uninstall the
**Claude Usage Server** Windows service.
