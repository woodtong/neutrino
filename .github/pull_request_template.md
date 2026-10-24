## What changed

<!-- One or two sentences. Link the issue. -->

## How I tested

- [ ] `dotnet build GhostScreen.sln -c Release`
- [ ] `dotnet run --project tests/GhostScreen.Tests -c Release`
- [ ] GUI integration (attach evidence/logs)

## Safety checklist

- [ ] Does not steal foreground, move the real cursor, or touch the clipboard in default mode.
- [ ] Does not return a blank/stale frame as success.
- [ ] Restores windows/processes on close, error, and panic.
- [ ] Adds no telemetry, network access, or background service.
- [ ] Does not require administrator rights for the default path.

## Docs / compatibility

- [ ] README/docs updated if behavior or limitations changed.
- [ ] Compatibility matrix updated (if an app was tested).
- [ ] CHANGELOG entry added.

## Screenshots / evidence

<!-- Redact personal data. -->
