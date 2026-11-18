# Contributing

Thanks for your interest. This is a small hobby project with one maintainer, so changes are
reviewed as time allows. Bug reports, fixes, and focused improvements are all welcome.

## Reporting bugs and requesting features

- **Bugs and feature requests:** open a [GitHub issue](https://github.com/iiddd/claude-usage-monitor/issues).
  For a bug, include the service version (shown in Settings → Apps, and in the `startup breadcrumb`
  log line), your iCUE version, what you expected, and what happened. The service log in
  `C:\ProgramData\ClaudeUsageServer\logs\` and the output of `curl http://127.0.0.1:41920/status`
  usually help. **Remove any tokens before posting**; the service never logs them, but other
  tools might.
- **Security problems:** do **not** open a public issue. Report them privately as described in
  [SECURITY.md](SECURITY.md).

## Making a change

1. Fork the repository and create a branch from `main`.
2. Make your change, with tests (see below).
3. Run the checks locally:

   ```powershell
   dotnet test tests\ClaudeUsageServer.Tests
   icuewidget validate widgets/ClaudeUsageWidget   # if you changed the widget; needs icuewidget-cli >= 0.5
   ```

4. Open a pull request against `main` that explains **what** changed and **why**.

CI must pass before a pull request is merged: it builds, runs the tests, checks that the MSI
installs every published file, fails on NuGet packages with known vulnerabilities, validates the
widget, and runs CodeQL. Dependency updates arrive through Dependabot.

## Requirements for contributions

### Tests

**New functionality and bug fixes must come with automated tests** in
`tests/ClaudeUsageServer.Tests`. A bug fix should include a test that fails without the fix.

- Tests run fully offline. Stub Anthropic with the fake `IHttpClientFactory` in `TestDoubles.cs`;
  never call the real API.
- Every fake token contains the string `SENTINEL`, so leak checks can assert it never appears in
  logs or responses. Keep that convention.
- For time-dependent behaviour, inject a `TimeProvider` and use `ManualTimeProvider` rather than
  sleeping.

The widget's JavaScript has no automated tests yet. For widget changes, check all states in
`widgets/ClaudeUsageWidget/preview.html` in a browser and describe what you checked in the
pull request.

### Code style

- **No inline comments.** Only documentation comments are allowed: `///` XML docs in C#, JSDoc
  (`/** */`) in JavaScript, and comment-based help in PowerShell. If code needs explaining,
  extract a well-named method, constant, or variable, or explain it in the member's doc comment.
- C#: Minimal API style, nullable reference types enabled, async/await throughout, and no
  compiler warnings.
- Widget JavaScript is ES5 with no build step and no external libraries.
- Keep pull requests focused: one topic per pull request.

### Security rules

- Token values, `Authorization` headers, raw HTTP bodies from the token endpoint, and file paths
  must never be logged or returned by any endpoint.
- The service must stay bound to loopback (`127.0.0.1`) only.
- The widget must stay credential-free.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
