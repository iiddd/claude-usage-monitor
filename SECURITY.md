# Security Policy

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Report them privately through
GitHub: **Security → Report a vulnerability** on this repository. Include steps to reproduce and
the version you tested.

You can expect an acknowledgement within a week. This is a hobby project maintained by one
person, so fixes are best effort.

## Supported versions

Only the latest release receives fixes.

## Security model

ClaudeUsageServer handles your Claude Code OAuth tokens, so its design keeps them contained:

- **Loopback only.** The service binds to `127.0.0.1:41920` and stops itself if it detects any
  other binding. It is not reachable from your network.
- **Tokens stay inside the service.** The access and refresh tokens live in memory in
  `CredentialStore`. They are never logged, never returned by any endpoint, and never sent
  to the iCUE widget. The widget sends no credentials at all.
- **Tokens go only to Anthropic.** The access token is sent only to
  `api.anthropic.com`, and the refresh token only to Anthropic's OAuth token endpoints
  (`platform.claude.com`, falling back to `console.anthropic.com`).
- **Credentials file.** The service reads `~/.claude/.credentials.json` and, after a token
  refresh, writes the rotated tokens back atomically, preserving the file's permissions and
  any fields it does not manage.
- **No telemetry.** The service stores no usage history and sends nothing anywhere else.

The automated tests check several of these guarantees, including that no token appears in
logs or in `/status` output.

## Scope notes

Anything running as your Windows user can already read your credentials file directly, so a
local process reading `http://127.0.0.1:41920/usage` gains nothing beyond your usage
percentages. Reports about that are out of scope unless they expose token material.
