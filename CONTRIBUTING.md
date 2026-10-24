# Contributing to GhostScreen

Thanks for helping. GhostScreen is a local-first Windows tool; correctness,
safety, and honest limitations matter more than feature count.

## Before you start

- **OS**: Windows 11 22H2+ (build 22621+), x64.
- **SDK**: .NET 8 SDK (`dotnet --version` should be 8.x).
- **Browser**: Microsoft Edge is used for headless web targets.
- Read `PLAN.md` and `doc/07-开源项目规划.md` before a large change.

## Build and test

```bash
dotnet restore GhostScreen.sln
dotnet build GhostScreen.sln -c Release
dotnet run --project tests/GhostScreen.Tests -c Release
```

GUI integration tests require an interactive desktop session. They are not run
on GitHub-hosted runners (Session 0 has no user desktop). Use the self-hosted
runner or run the scripts locally and attach the evidence to the PR.

## Pull requests

- Keep changes focused; one concern per PR.
- Add or update tests for behavior changes.
- Update docs, compatibility notes, and CHANGELOG when behavior changes.
- Run `dotnet build` and the unit test runner before pushing.
- Use DCO sign-off: `git commit -s`.
- Never commit screenshots or logs containing personal data, tokens, or secrets.

## Feature proposals

Open an issue first for:

- new capture/input backends,
- new API surface or MCP tools,
- changes to the security model,
- anything that broadens the supported-application matrix.

Use the compatibility issue template when reporting an app that does not work.

## Code style

- Nullable enabled; warnings are errors in the build.
- Prefer small, testable classes; keep Win32 interop isolated.
- Do not add telemetry, network calls, or background services.
- Do not use `SendInput` unless the user explicitly enabled interactive mode.
- Comments should explain constraints, not narrate the code.

## Security

Please do not report vulnerabilities in public issues. See `SECURITY.md`.
