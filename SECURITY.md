# Security Policy

## Supported versions

GhostScreen is pre-1.0. Security fixes are applied to the latest published
release and `main`.

## Reporting a vulnerability

Please use GitHub's **Private vulnerability reporting** for this repository
(Security tab → Report a vulnerability). Do not open a public issue with
exploit details.

Include:

- affected version/commit,
- Windows build,
- what an attacker can do,
- reproduction steps,
- whether the issue requires a malicious local process or a malicious agent.

We will acknowledge as soon as possible; this is a volunteer project, so no
fixed SLA is promised.

## Threat model (summary)

GhostScreen can see and operate local windows. That is its purpose, and it is
inherently powerful:

- The daemon listens only on loopback and a per-user named pipe.
- A random token protects the local HTTP/WS API.
- `attach` and `interactive` input are disabled by default.
- There is no telemetry and no cloud dependency.
- Screenshots are held in memory unless the caller asks to save them.
- A panic hotkey stops capture/input and restores parked windows.

Known non-goals: defending against a malicious process running as the same
user; capturing protected/DRM content; operating elevated windows without an
explicit elevated daemon.

## Out of scope

- Prompt injection inside a target application (the agent may be tricked by
  content it reads). This is a model/agent problem; report it if GhostScreen
  makes it substantially worse.
- Denial of service caused by a user intentionally launching thousands of
  targets.
- Issues that require administrator privileges the daemon does not have.
