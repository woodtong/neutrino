# Changelog

All notable changes are documented here. The format follows Keep a Changelog;
the project aims to use Semantic Versioning once 1.0 ships.

## [0.6.0-dev] - 2026-11-19

Window visibility & end turn: the agent now declares where each attached
window belongs and the human can always override in both directions.

- `attach_window` accepts `visibility`: `"return-to-user"` (default — the
  window is a deliverable, restored in place on close/exit) or `"agent-only"`
  (an intermediate tool window nobody needs to see — parked for its whole
  lifetime and closed in place via `WM_CLOSE` when released; if the app
  refuses within the 4s grace period, e.g. a save prompt, it is restored to
  the user's desktop instead and `close_target` returns a
  `WINDOW_CLOSE_REFUSED` warning).
- New tools (21 → 25): `show_to_user` (restore a parked window in place, no
  confirm, idempotent), `park_window` (park a user-visible window again,
  requires `confirm=true`), `set_visibility` (change the verdict without
  moving the window), and `end_turn` (aggregated end-of-turn handoff:
  restore every parked return-to-user window in one call, keep agent-only
  windows parked; all targets stay attached).
- Target DTO exposes `visibility` (declared intent) and `zone`
  (`parked` | `user-visible`, actual position) as independent fields.
- The journal tracks only parked windows: restoring removes the entry,
  re-parking re-adds it, so crash recovery semantics are unchanged.
- Server instructions now carry the visibility decision rule and the
  end_turn closing habit for MCP client agents; 40/40 tests.
- Consent prompt is now content-sized: the dialog lays out with a
  `TableLayoutPanel` of auto-sized rows and scales its fixed metrics by
  `DeviceDpi/96`, so the text, countdown and buttons no longer overlap and
  nothing is clipped at non-100% DPI (the old fixed `380×154` client area was
  14 px shorter than its own content).
- Consent prompt countdown is now visible and deferrable: under the text the
  dialog draws a bar that shrinks with the remaining time, and a new `延时`
  button pushes the auto-allow deadline back by 30 s (up to 3 times, +90 s in
  total), so someone still reading is not authorized mid-sentence. The cap
  keeps an agent from being stalled indefinitely; a defer only moves the
  pending ticket's deadline and leaves the "one use within 30 s after allow"
  semantics untouched. Server side: `POST /api/consent?decision=defer` plus
  `total_ms` / `deferrals` / `max_deferrals` in the status payload; 42/42
  tests.
- The consent prompt now asks for permission to *interrupt*, not permission to
  act: the ticket already exists because the human asked the agent for this
  action, so the copy names the target and the action, says that the step needs
  the foreground, states what the human is doing right now, and tells them what
  to stop (`允许打断` / `延时` / `拒绝`).
- The gate now reports *why* it blocked, without reading input: `activity`
  (`typing` | `pointing` | `active` | `none`), `activity_detail` (foreground
  window title) and `interruption` (`foreground`) travel on `USER_ACTIVE`,
  `AWAITING_USER` and `status.consents[]`. `typing` is inferred from the
  foreground thread's caret plus recent input; `pointing` from
  `GUI_INMENUMODE` / `GUI_INMOVESIZE`. Content is never inspected and the
  pass thresholds are unchanged (2 s window, 3 attempts); 43/43 tests.
- Notifications moved out of the MCP process into a standalone module
  (`GhostScreen.Notifier`, new project). A prompt shown from inside the
  process that failed could never report its own failure, so: a named pipe
  (`GhostScreen.Notifier`, one JSON line each way) carries `toast` and
  `prompt` messages; the tray hosts the pipe server when it is running, and
  otherwise the caller spawns `GhostScreen.Notifier.exe` on demand and retries
  once. A missing notifier returns `null` and never changes a tool result. The
  consent copy now lives in Core (`ConsentPromptCopy`), so both host paths show
  the same words, and `PromptForm` replaces the tray-only dialog.
- The notifier also reports what used to be silent: `user-active:{target}` (a
  toast on the second `USER_ACTIVE` block — "the agent is waiting for you to
  let go", 60 s cooldown), `degraded:{target}` (`TARGET_HUNG`),
  `close-refused:{target}` (`WINDOW_CLOSE_REFUSED`, window already restored),
  `recovery-restored` (crash-recovery summary at startup), `mcp-down` (the tray
  notices the status endpoint vanished) and `mcp-fatal` (unhandled exception in
  the MCP process). Every prompt still uses
  `ShowWithoutActivation` + `WS_EX_NOACTIVATE`, so a notification never takes
  the keyboard away; 45/45 tests.
- Notifications now follow the system UI language (`NotifierText`, English
  fallback, `GHOSTSCREEN_NOTIFY_LANG=zh|en` to override). They are the only
  localised surface: the rest of the product stays Chinese, because the person
  being interrupted may not read it.
- The card picks its monitor instead of always using the primary one: the screen
  with the foreground window first, then the one under the pointer, and it
  re-fits on `DpiChanged` so a mixed-DPI setup does not clip or overlap. The
  tray poll backs off to 5 s while the status endpoint is gone and returns to
  2 s on the first successful read.
- `USER_ACTIVE`, `AWAITING_USER` and `status.consents[]` now carry `idle_ms`
  (time since the last human input), so an agent can tell "the human just
  paused" from "the human walked away" without guessing from the 2 s window;
  47/47 tests.

## [0.2.0] - 2026-11-21

Second GitHub release: the 0.6.0-dev development snapshot. Release-line versions
stay independent of the development line.

See `doc/17-0.2.0发布说明.md`; the zip and SHA256 are attached to the GitHub
release. The 0.1.0 notes moved to `doc/archive/17-0.1.0发布说明.md`.

- Window visibility (P4-12): `attach_window` takes `visibility: agent-only`
  (parked off-screen for its whole life, closed in place with `WM_CLOSE` on
  release, refusing apps restored with `WINDOW_CLOSE_REFUSED` after a 4s grace)
  or `return-to-user` (default). Four new tools - `show_to_user`,
  `park_window`, `set_visibility`, `end_turn` - take the surface from 21 to 25.
- Consent prompt: content-sized layout (no more 380x154 against 168px of
  content), visible countdown with a shrinking bar, and a "defer" action that
  pushes the server-side deadline 30s at a time, three times at most.
- Consent prompt copy now asks for permission to interrupt rather than to
  operate, and names what the human is doing; `activity`/`activity_detail`/
  `interruption`/`idle_ms` travel through `USER_ACTIVE`, `AWAITING_USER`, the
  MCP error payload and `status.consents[]`. The gate still never reads input
  content: `typing` is inferred from a caret plus recent input.
- Notifications moved out of the MCP process into `src/GhostScreen.Notifier`
  (named pipe, `toast` and serialised `prompt` messages, tray hosts it when
  running, spawned on demand otherwise, failures return null). Silent events now
  speak: user-active waiting, degraded, close-refused, recovery-restored,
  mcp-down, mcp-fatal. Copy follows the system UI language.
- 47/47 tests, build 0x0 on seven projects.

## [0.1.0] - 2026-11-07

First GitHub release. Release-line versions start at 0.1.0 and are numbered
independently of the development line (currently 0.5.0-dev); this package is
the 0.5.0-dev development snapshot plus the `gs doctor` work (P2-07).

See `doc/archive/17-0.1.0发布说明.md`; the zip and SHA256 are attached to the GitHub
release.

- Human Consent Gate (strict default), focus-safe native element actions,
  `gs doctor` + troubleshooting guide, window-fact caching; 34/34 tests.
- Packaging: `scripts/publish.ps1` now passes the release version to
  `dotnet publish -p:Version=...` (default remains the development version from
  `Directory.Build.props`), so released binaries report the release version.

## [0.5.0-dev] - 2026-10-24

Human Consent Gate (strict default): focus-requiring UIA actions now ask instead
of silently stealing the human's foreground. Packaged as the 0.1.0 release.

- New `InteractionPolicy`: `strict` (default), `balanced`, `force`. Strict blocks
  focus-requiring UIA actions while a human has produced input in the last 2s.
  Window-message and CDP paths are never gated.
- `click_element`, `set_text`, and `click` now return structured
  `USER_ACTIVE` (retryable, attempts/max_attempts/retry_after_ms) after a
  blocked attempt. After 3 attempts they create a consent request and return
  `AWAITING_USER` with a one-time `consent_id` and `expires_at`.
- A consent request is shown as a non-activating tray prompt with Allow/Deny and
  a 15s countdown. The tray never takes focus. If nobody responds, the server
  auto-authorizes at the countdown; the agent retries with `consent_id`.
  `USER_DENIED` is terminal for that request; `CONSENT_EXPIRED` means request a
  new one.
- Consent tickets are bound to the exact target/action/element, one-time, and
  expire 30s after authorization. The server only authorizes; the agent executes,
  so stale actions are never replayed.
- MCP clientInfo name is shown in the prompt; `instructions` now explain the
  retry/consent protocol.
- Status endpoint exposes pending consents and `POST /api/consent` with the
  loopback token; `StatusClient` parses consents and decides them.
- Performance: added lightweight child-window enumeration for message input,
  cached process names (30s TTL), removed a duplicate UIA tree walk for point
  clicks, and skipped a duplicate element resolve for `click_element(name)`.
  Local microbench: `list_windows` 29.15 → 1.04 ms/op, child-hit click
  0.32 → 0.18 ms/op.
- Docs: added `doc/18-技术栈.md`; archived market research and 0.1–0.3 release
  notes under `doc/archive/`; README now documents the implemented stack instead
  of the old proposed stack.
- Cleanup: removed unpacked release directories (zips/SHA256 kept) and generated
  `spike/out` screenshots.
- Tests: 32/32 default (ConsentManager unit tests); 32/32 with
  `GHOSTSCREEN_UIA_TESTS=1` (WPF balanced execution + strict consent gate).
- Version: 0.5.0-dev. The 0.4.0-beta artifact remains the published package.

## [0.4.1-dev] - 2026-10-24

Focus-safety fix: native element actions no longer steal the foreground by
default. Not packaged yet.

- `click_element` and `set_text` on native targets now resolve the UIA element's
  bounds to an HWND-backed child and use window messages (`BM_CLICK`,
  `WM_LBUTTONDOWN/UP`, `WM_SETTEXT`). These messages do not activate the parked
  window. Verified with a WinForms fixture: `BM_CLICK`/`WM_SETTEXT` leave the
  foreground window unchanged, while UIA `InvokePattern.Invoke` and
  `ValuePattern.SetValue` moved the foreground to the parked fixture.
- When no HWND-backed control exists (WPF/UWP/custom), the UIA fallback now
  executes automatically and restores the previous foreground instead of
  refusing while the human is active. `click_element` tries Invoke, Toggle,
  SelectionItem, or ExpandCollapse; `set_text` uses ValuePattern. Only a control
  with none of these patterns returns `ELEMENT_NOT_ACTIONABLE`.
- Coordinate `click` on single-HWND WPF/UWP surfaces now falls back to UIA
  hit-testing (`AtPoint`) and restores focus; classic HWND children still use
  window messages.
- `strict_no_focus_steal=true` is available for users who prefer refusal over a
  brief UIA activation; it returns `FOCUS_STEAL_NOT_ALLOWED`. The old
  `USER_ACTIVE` refusal and `allow_focus_steal` requirement were removed.
- New `FocusGuard` replaces `ForegroundGuard`: it waits up to 500ms for delayed
  UIA activation, restores with `SetForegroundWindow`/`AttachThreadInput`, never
  uses the forced `SwitchToThisWindow`, verifies the foreground stays restored,
  and reports `stolen` instead of silently leaving focus on the invisible target
  (`FOCUS_STOLEN` warning). If the human moved to a third window, that choice is
  left alone.
- Removed the old foreground guard from all window-message paths. It added up to
  160ms per action and could itself switch the foreground.
- Test fixtures are now created off-screen by default; the WGC visible-window
  test is opt-in via `GHOSTSCREEN_VISIBLE_TESTS=1`. A new WPF fixture test
  (opt-in via `GHOSTSCREEN_UIA_TESTS=1`, because it briefly activates the
  off-screen window) verifies Invoke/Toggle/SelectionItem/ValuePattern all
  execute and restore focus.
- Added a post-test assertion that the test process leaves no window
  intersecting the virtual screen.
- MCP native round-trip now asserts foreground/cursor/clipboard invariance when
  no human input is detected, and asserts the message path is used for
  HWND-backed controls.
- Tests: 29/29 (WGC and WPF focus tests are opt-in skips by default); build
  0 warnings / 0 errors; format clean.

## [0.4.0-beta] - 2026-10-24

P4 preview increment: DPI-unaware capture fix, tray per-target list/close,
real VS Code `attach_cdp` verification, and an unsigned portable installer.
Artifact: `artifacts/GhostScreen-0.4.0-beta-win-x64.zip` (SHA256 in the
matching `.sha256` file).

- DPI-unaware targets are captured under an unaware thread context
  (`SetThreadDpiAwarenessContext`) and scaled to physical window size with
  `StretchBlt(HALFTONE)`. The 1024x650 canvas with a black L-shaped band is now
  a filled 1020x630 client image; covered by a test that creates the fixture on
  an unaware thread.
- Tray `Targets` submenu lists each target (title, state, degraded reason) and
  closes one after confirmation. New status endpoint
  `POST /api/close_target?target_id=`; `StatusClient.CloseTargetAsync`.
- Real VS Code 1.142 over `attach_cdp { port: 9223, page_url: "workbench" }`:
  screenshot (115KB PNG), observe 30 elements, detach; VS Code stays alive.
- `scripts/install.ps1`: copies the package to
  `%LOCALAPPDATA%\GhostScreen\app`, creates a Start Menu tray shortcut, and
  supports `-Uninstall` without touching MCP configs/journal/profiles.
- Tests: 28/28 (DPI-unaware scaling + close_target).

## [0.3.1-beta] - 2026-10-24

P4 preview increment: attach to external Chromium/Electron DevTools endpoints,
dead-target safety, and live WGC frame verification. Artifact:
`artifacts/GhostScreen-0.3.1-beta-win-x64.zip` (SHA256 in the matching `.sha256` file).

- New MCP tool `attach_cdp { port, page_url? }`: connects to an already-running
  Chromium/Electron DevTools endpoint (e.g. VS Code with
  `--remote-debugging-port`). Returns `kind=web`, `external=true`, `process=null`;
  all web tools work; `close_target` detaches only and never kills the browser.
  Connection failures return `CDP_CONNECT_FAILED`.
- A browser killed externally now yields `TARGET_CLOSED` instead of an internal
  error; closing that target still removes the journal entry/profile, and a
  target whose window disappeared can still be closed safely.
- WGC visible-window test now takes 8 frames and requires at least 4 distinct
  hashes, proving live frames rather than a frozen surface.
- Tests: 27/27 (21 tools).

## [0.3.0-beta] - 2026-10-24

P4 preview: crash recovery, hung-window refusal, status page, tray, and a soak
harness. Artifact: `artifacts/GhostScreen-0.3.0-beta-win-x64.zip` (SHA256 in
the matching `.sha256` file).

- Crash journal (`targets.json`): restore parked windows after hwnd/pid/class
  checks; kill orphaned headless browsers (pid + start time + name) and delete
  their profiles; sweep profiles older than 24h; move corrupt journals aside;
  `STATE_WRITE_FAILED` aborts the attach/open that could not be recorded.
- Hung windows: `IsHungAppWindow` is checked before `Describe` (which clears the
  flag) and refuses with `TARGET_HUNG` instead of returning a stale DWM frame;
  `attach_window` refuses before moving; two `CAPTURE_TIMEOUT`s degrade a target.
- Status endpoint and page: loopback-only raw TCP with a per-process token;
  target cards, 5s thumbnails (`/api/screenshot`), `/api/status`, `/api/panic`;
  `status.json` discovery and cleanup.
- Tray (`tray/GhostScreen.Tray.exe`): single-instance NotifyIcon polling the
  status endpoint, opening the page, confirming panic, and exiting; it holds no
  targets and shows "not running" after MCP exits.
- `tests/GhostScreen.Soak`: configurable native+web soak with a post-warmup
  baseline. A 3-minute pre-run completed 29 cycles with 0 failures, +6 handles,
  +2MB private memory, and 0 profiles.
- Window DPI awareness is reported as `dpi_awareness` in `list_windows` and
  target DTOs (from `GetWindowDpiAwarenessContext`); `Unaware` targets carry a
  `DPI_UNAWARE` warning. The black-padding capture fix is still pending.
- Verified with the official `@modelcontextprotocol/sdk` 1.32.2 client over
  stdio: initialize, 20 tools, `open_web`, PNG image content, `observe`, and
  `close_all`.
- Tests: 24/24, including DPI awareness reporting.

## [0.2.2-beta] - 2026-10-24

First **v0.2 beta**. G3 (web runtime) scope is complete; code matches
0.2.1-p0 apart from the version string. Artifact:
`artifacts/GhostScreen-0.2.2-beta-win-x64.zip` (SHA256 in the matching `.sha256` file).

- Added `doc/12-Recipes.md`: headless Edge, Edge GUI, VS Code/Electron CDP,
  UWP, local frontend loop, responsive checks, native apps, compatibility
  matrix, security boundaries.
- G3 evidence recorded in `doc/13-0.2.2-beta发布说明.md`: three-viewport
  snapshots, horizontal-overflow detection, console/network diagnostics,
  100-cycle leak stress, process/profile cleanup.
- Known beta limitation: pixel-level comparison between headed and headless
  Edge is deferred to P4; it needs a visible headed instance and may disturb
  the desktop.

## [0.2.1-p0] - 2026-10-24

Third local alpha. Web capture and diagnostics are now complete enough for
responsive visual verification. Artifact:
`artifacts/GhostScreen-0.2.1-p0-win-x64.zip` (SHA256 in the matching `.sha256` file).

- `screenshot` gains web-only `full_page` and `clip {x,y,width,height}`; content
  over 16384px returns `CAPTURE_TOO_LARGE`. Metadata reports `region`.
- Captures restore the caller's viewport and reset any page scale left by
  `captureBeyondViewport`; navigation re-applies the requested viewport.
- New `snapshot` tool: multiple viewports in one call, with `horizontal_overflow`,
  `scroll_width`, and up to five offending elements (selector/bounds/right);
  restores the original viewport.
- New `network` tool: `Network.loadingFailed` and HTTP 4xx/5xx, capped at 200.
- Navigation waits up to 3s for a 300ms network idle window (best effort; long
  polling does not fail navigation).
- New `tests/GhostScreen.WebStress`: 100 open/capture/close cycles, 0 failures,
  managed memory ~0.9MB flat, private memory 32-38MB flat, handles plateau at
  362, temp profiles 0.
- Tests 14/14, now covering full-page, clip, two-viewport snapshot, overflow
  offender detection, network failure capture, and viewport restore.

## [0.2.0-p0] - 2026-10-24

Second local alpha / developer preview. Web targets are now first-class MCP
targets. Artifact: `artifacts/GhostScreen-0.2.0-p0-win-x64.zip` (103.2 MB;
SHA256 in the matching `.sha256` file).

- **Web target**: `open_web` launches a private headless Edge (`--headless=new`,
  `--remote-debugging-port=0`, per-target temp profile) and returns a target id.
  The URL is never placed on the command line; navigation goes through CDP.
- **Unified 18-tool MCP surface**: native windows and web pages share
  `screenshot / observe / click / click_element / scroll / drag / key / set_text`,
  plus web-only `navigate / viewport / console / get_html`.
- CDP capture returns PNG bytes; web actions cover click, drag, synthesized
  scroll, modifier keys, and React-compatible text filling.
- `observe` on web returns ref/role/name/stable CSS selector/bounds/enabled;
  refs expire per observation (`ELEMENT_STALE`).
- Lifecycle: `close_target` runs `Browser.close`, kills the process tree, and
  deletes the temp profile; `close_all`/EOF does the same for every target.
- URL policy: absolute `http`, `https`, `file`, `about` only; strings starting
  with `-` are rejected to prevent flag injection.
- Version is centralized in `ProductInfo` (`0.2.0-p0`); publish script reads it.
- Tests: 14/14, including a real headless-Edge round-trip (screenshot, observe,
  click by ref/coordinate, Chinese `set_text`, Ctrl+S, scroll, drag, console,
  390×844@2 viewport, navigation, close + process exit), and an Edge
  executable allow-list test (only fixed install roots may be launched).
- Known alpha limitations: no full-page/element-clip/network-idle yet; no
  multi-viewport snapshot; 100-cycle leak soak and real MCP-client integration
  still pending.

## [0.1.0-p0] - 2026-10-24

Local alpha / developer preview. Artifact:
`artifacts/GhostScreen-0.1.0-p0-win-x64.zip` (103.1 MB),
SHA256 `5389BC855639EE0BFEEE635B4794F922B7B3D7E51AD3308F9ADE207198AE6F75`.

- Self-contained MCP/CLI executables; 13 MCP tools; client-area screenshots;
  UIA refs/epoch; coordinate click/scroll/drag/key; EOF restore.
- Published MCP stdio smoke: initialize + tools/list (13 tools) passed.
- Known alpha limitations are listed in `doc/09-0.1.0-alpha发布说明.md`.

## [Unreleased]

### Added

- Crash recovery journal (`%LOCALAPPDATA%\GhostScreen\state\targets.json`):
  parked windows are restored on the next start after hwnd/pid/class checks;
  orphaned headless browsers are killed (pid + start time + name match) and
  their profiles deleted; profiles older than 24h are swept. Corrupt journals
  are moved aside instead of blocking startup. Journal write failures surface
  as `STATE_WRITE_FAILED` and abort the attach/open that could not be recorded.
- `WindowRecovery` / `ParkedWindowSnapshot` expose the exact placement, exstyle,
  and visibility needed to restore a window.
- Hung-target degradation: `CaptureEngine` checks `IsHungAppWindow` before any
  window query (Describe clears the flag) and refuses with `TARGET_HUNG` instead
  of returning the DWM surface's stale frame. Two consecutive `CAPTURE_TIMEOUT`s
  also mark a native target `degraded / TARGET_HUNG`; capture/observe/input then
  fail fast instead of spawning more timeout threads. `close` still restores
  the window. `state`/`degraded_reason` appear in target DTOs.
- `attach_window` refuses a hung window before moving it (`WindowHealth.IsHung`),
  so `SetWindowPos` cannot block on a dead message thread.
- Local status endpoint (P4-02 first cut): MCP starts a loopback-only raw
  `TcpListener` HTTP server with a per-process token; `GET /` renders target
  cards (kind/state/degraded reason) with thumbnails, `GET /api/status` returns
  version, pid, uptime, recovery summary and target DTOs,
  `GET /api/screenshot?target_id=` returns one target PNG (native 2s / web 6s
  cap; 409 for hung, 504 on timeout), and `POST /api/panic` closes all targets.
  Thumbnails refresh every 5s (0.2fps) because native captures force a repaint.
  The URL/token/port/pid are written to
  `%LOCALAPPDATA%\GhostScreen\status.json` and removed on exit. Raw TCP avoids
  Http.sys URL ACL/admin requirements.
- `GhostScreen.Tray.exe` (P4-01 first cut): single-instance WinForms NotifyIcon
  that reads `status.json`, polls `/api/status` every 2s, shows version/target
  count, opens the status page, confirms and triggers `/api/panic`, and shows
  "not running" when the MCP process exits. It holds no targets, so no daemon
  was needed. A `StatusClient` in Core provides endpoint discovery and the
  status/panic calls for the tray and tests.
- Tests: 23/23, adding window restore, journal restore with a simulated crash,
  orphan-browser recovery, the timeout-to-degraded state machine, a real
  10-second hung window refused with `TARGET_HUNG`, WGC on a visible window,
  negative-origin/multi-monitor parking math, status endpoint auth/page/panic/
  cleanup, and a headless-Edge render of the status page.
- P0 spike harness: window enumeration, off-screen parking, state snapshots,
  WGC/PrintWindow capture, UIA/window-message input, headless Edge CDP.
- P1-11 modern-app probe: Edge GUI (PrintWindow), VS Code (CDP), Calculator
  (PrintWindow/UIA) compatibility results and evidence.
- `CaptureEngine` (PrintWindow primary, visible-WGC fallback, stable metadata)
  and a one-shot CLI: `gs version/state/list/shot`.
- `TargetManager`/`TargetSession` and a hand-written MCP stdio server with 12
  tools: list/attach/screenshot/observe/click/click_element/scroll/drag/set_text/close.
- UIA observations now carry an epoch and refs; stale refs return `ELEMENT_STALE`.
- Client-area coordinate clicks, wheel scrolling, drags, and message-level keys,
  with child-window hit testing; Chromium/Electron message input is rejected by
  `InputRouter` with `INPUT_UNSUPPORTED`.
- `doc/08-MCP客户端配置.md` with Claude Code / Codex / Cursor / ZCode configs.
- Default client-area crop for PrintWindow captures (`IncludeFrame=false`).
- Deterministic `FixtureApp` target with a magenta marker and animation.
- Zero-dependency unit test runner for geometry, parking math, blank detection,
  and frame hashing.
- CI workflow: build, unit tests, CodeQL, dependency review.
- Apache-2.0 license, contribution/security/conduct docs, issue/PR templates.

### Changed

- Capture strategy based on P0 measurements: **PrintWindow is the default for
  fully off-screen windows** (it forces a live paint); WGC is reserved for
  visible/VDD windows; web targets use headless Edge + CDP.
- Off-screen windows are parked with `WS_EX_NOACTIVATE`; short actions use a
  foreground guard to undo any activation.
- Test launchers use `CreateProcessW` with fixed constants (no shell, no
  caller-supplied command line).
- `PrintWindowCapture.TryCaptureWithTimeout` guards against a hung target on a
  dedicated background thread; per-target worker reuse is still pending.
- `--quiet` invariant harness: waits for idle input, then verifies foreground,
  cursor, clipboard, and last-input across parking/capture/UIA.
- Parking retries `SetWindowPos` after showing a hidden window, because modern
  Notepad resets its position on show.

### Known limitations

- WGC freezes an off-screen window's frame (it keeps the last visible paint).
  A 1px visible sliver does not keep it live.
- PrintWindow compatibility with Chromium/Electron/UWP is not yet verified.
  P1-11 result: Edge GUI full, VS Code only chrome (use CDP), Calculator full.
- UWP apps (Calculator) can steal foreground on launch; focus isolation is
  partial and requires explicit consent until a separate-session solution exists.
- DPI-unaware legacy targets under a PerMonitorV2 caller can produce logical-size
  content with black padding; P4 DPI work is required before claiming support.
- Multi-monitor/mixed-DPI and VDD paths are not tested in P0/P1.
