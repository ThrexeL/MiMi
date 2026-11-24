# MiMi

A lightweight Dynamic Island-style overlay for Windows 11. MiMi lives at the top center of
the primary monitor, is always on top, and never steals keyboard focus. It shows your
current media (with synchronized lyrics), agent activity, approvals, system HUDs, and
utility panels in a compact pill that expands on hover.

MiMi is inspired by [Nusa](https://rama2555.github.io/nusa/) but is an original
implementation. The stack is C# / .NET 10 / WPF with CsWinRT Windows SDK projections —
no Electron, no WebView, no third-party UI packages. Even the tray icon is a hand-rolled
`Shell_NotifyIconW` P/Invoke.

## MiMi 4.0 — what's new

- **Face-only idle.** The resting state is a slim pill with the mascot face only — the
  clock (and its setting) are gone.
- **Round white mascot eyes with an expression system.** Eight expressions derived from
  what the island is doing: Calm, Lively (music), Alert (approval required), Focused
  (agent working), Concerned (failure), Happy (turn completed), Curious (agent started),
  and Sleepy.
- **Gold glow as a bottom-edge band.** Fixed-DIP geometry (8 DIP on idle pills, 20 DIP on
  expanded panels), clipped to the island's bottom edge; opacity is state-driven and
  decoupled from geometry — no more proportional "brown oval".
- **Mouse-proximity wake.** With the opt-in sleepy mode enabled, MiMi dozes after
  sustained inactivity and wakes when the cursor comes near (hysteresis so it cannot
  flicker).
- **Ctrl+Alt+M** global toggle with smooth, reversible fades — rapid toggles are safe
  (superseded transitions become no-ops).
- **Utility panels via the tray:** focus timer (Pomodoro), water tracker, to-do list, and
  the file shelf.
- **File shelf with real Explorer drag-in/out.** Drop files from Explorer onto the open
  shelf panel; hold a row to drag it back out (references only — originals are never
  copied or deleted).
- **System HUDs:** volume/mute, charging and low battery, network, audio device changes,
  Caps Lock, and input language — all real system state, event-driven where the OS
  allows.
- **Synchronized lyrics via LRCLIB**, displayed as a 3-line window on the expanded media
  panel.
- **Multi-agent tracking with multi-approval navigation**: per-session lifecycle states,
  a hover activity panel, and `‹ ›` navigation across pending approval cards.
- **Refresh 2026-11-24:** morphing pill geometry, clickable compact states, six-tile Quick
  Access, fixed-height activity panel, dismissible shelf with count badges, larger type,
  and a non-activating task editor.
- **New controls:** interactive volume/mute panel and an experimental embedded Codex
  conversation window with read-only sandbox and one-shot approvals. See
  `docs/ui-refresh-and-codex.md`.

## UI and usability refresh

- **Categorized settings:** General, Appearance, Notifications, Integrations, and System pages in a resizable window, with independently scrolling content and persistent save feedback. Changes save automatically; failures are displayed instead of reporting success. Escape closes settings, and keyboard focus is visible.
- **Complete everyday controls:** hover expansion, lyrics and timing offset, all system HUD toggles, typography, mascot, glow, notifications, and provider status are available in settings. Keyboard and pointer slider changes persist without an Apply button.
- **Island polish:** clearer section headings, higher-contrast secondary text and seek track, consistent action buttons, labeled Quick Access tiles, useful empty states, and descriptive icon tooltips/automation names. The idle face, non-activating overlay, and fixed gold glow geometry are unchanged.
- **Lyrics availability:** unavailable tracks show **No lyrics available**, instrumental tracks are labeled, and fetching shows **Loading lyrics...**. Switching tracks clears the previous lyrics.
- **Laptop brightness HUD:** real WMI backlight-change events on supported laptops, with its own settings toggle. No external-monitor control, polling, or simulated readings.
- **Regression coverage:** settings persistence and layout, island template layouts, Unicode text/icons, lyrics availability, and brightness policy are automated. Offscreen renders help review layouts; live hardware, DPI, and accessibility checks remain manual.

## Status

Development is phased. State as of the 4.0 release candidate:

| Phase | Scope | Status |
|---|---|---|
| 1 — island and media | Island window, resting state, GSMTC media pill/panel, tray, settings | implemented |
| 2 — agent foundation | Named-pipe bridge, `AgentEvent` model, approvals, island priorities | implemented |
| 3 — agent integrations | Claude Code / Codex approval hooks via `MiMi.AgentCli`, Codex notify | implemented |
| 3.6 — activity & multi-agent UI | Lifecycle hooks, per-session tracking, multi-approval navigation | implemented |
| 4 — extra modules | Lyrics, Pomodoro, hydration, to-do, file shelf | **implemented in 4.0** |
| 6 — system HUDs | Volume, battery, network, audio devices, Caps Lock, input language, supported laptop brightness | implemented |
| 5 — personal harness adapter; Codex app-server full embedding | Thursday activity and approval adapters implemented; experimental read-only Codex conversation client implemented | partial / experimental |
| 7 — Windows notification listener | | **researched; intentionally unavailable in the portable build** |
| 8 — mascot and UI polish | Expressions, categorized settings, island styling, offscreen template/layout review | implemented; interactive/DPI acceptance remains manual |
| 9 — release prep | Portable build script, automated tests, selftest, documented acceptance checklist | implemented; release signing/distribution and manual acceptance remain maintainer tasks |

Notes on the intentionally-unsupported features:

- **Windows notification listener (Phase 7).** Reading other apps' notifications requires
  MSIX packaging with the `userNotificationListener` capability plus explicit user consent
  via `RequestAccessAsync` (revocable in Windows Settings). The portable unpackaged build
  cannot access it. This is documented — including an honest disclosure in Settings —
  rather than simulated. A packaged companion app is the supported path if the feature is
  wanted later.
- **External-monitor brightness control.** Not implemented. The brightness HUD observes
  supported laptop backlights through WMI and `System.Management`; desktops without that
  interface stay silent. MiMi does not change brightness or provide a fake slider.

## Build and run

Prerequisites: Windows 11 x64 and the .NET SDK 10. The machine was provisioned with a
user-local SDK at `%LOCALAPPDATA%\Microsoft\dotnet` (via `dotnet-install.ps1`), so make
sure that directory is on your `PATH`.

```powershell
dotnet build MiMi.slnx -c Release
dotnet run --project src/MiMi.App -c Release

# or run the built executable directly:
.\src\MiMi.App\bin\Release\net10.0-windows10.0.26100.0\MiMi.exe

# headless pipeline check (prints [PASS]/[FAIL] lines, exit code 0/1):
MiMi.exe --selftest

# unit tests:
dotnet test MiMi.slnx -c Debug
```

Note: the solution file is `MiMi.slnx` — the new .NET 10 XML solution format, not the
classic `.sln`. For a distributable build, see "Portable release" below.

## Testing real media integration

Media metadata and playback state come exclusively from Windows GSMTC. If an app does not
appear in MiMi, it does not expose a media session to Windows.

1. Start MiMi. It shows the resting state (face-only mascot, no clock).
2. Play a song in Spotify, Edge (YouTube), or Windows Media Player. The island switches
   from the face to a compact media pill with artwork.
3. Hover over the pill to expand the media panel: transport controls, seekable progress
   slider, elapsed/remaining time — and the lyrics window when synced lyrics are found.
4. Exercise pause, seek, next, and previous.
5. Play in two apps at once (e.g. Spotify + YouTube) to exercise active-session selection.
6. Stop all playback. The island returns to the resting face.

## Utility panels

All four panels are toggled from the tray menu (**Focus timer / Water tracker / To-do
list / File shelf**, checkmarked when pinned). A panel you open yourself pins at
*UserPanel* priority — only approvals and agent failures displace it — while a merely
running timer pins at *Utility* so agent status keeps priority. The island's existing
1 Hz heartbeat drives all of them; there are no extra timer loops.

- **Focus timer (Pomodoro).** Presets 25 / 5 / 15 minutes (focus / short break / long
  break); a long break follows every 4 completed focus sessions. Timing is deadline-based
  (stopwatch + target), so the countdown stays accurate across hide/collapse/sleep.
  Play-pause, stop, and skip are supported.
- **Water tracker.** Daily goal (default 1800 ml) with quick-add buttons and a bounded
  history. Reminders repeat at a configurable interval (0 disables them) and fire only
  between 08:00 and 22:00 local time; a reminder can be snoozed. Daily totals reset at
  midnight. Persisted to `water.json`.
- **To-do list.** Add, complete, reopen, and delete items; due reminders fire once.
  Persisted to `todos.json` with corruption recovery.
- **File shelf.** Holds up to 12 references (oldest evicted), duplicate-safe. References
  only: MiMi never copies, moves, or deletes originals. Drag-in is accepted only while
  the shelf panel is open (the idle face never intercepts Explorer drops); drag-out is a
  real OLE `FileDrop` drag via `DoDragDrop`, so rows can be dropped into Explorer or any
  accepting app. Files whose original has disappeared render grayed with a "missing"
  hint. Persisted to `shelf.json`.

## System HUDs

All HUD information is real system state; unsupported hardware renders nothing rather
than a placeholder. Each HUD can be toggled in settings (`volumeHudEnabled`,
`batteryHudEnabled`, `networkHudEnabled`, `capsLockHudEnabled`, `headphoneHudEnabled`).

- **Volume / mute.** Core Audio `IAudioEndpointVolumeCallback` — event-driven, no
  polling. Hardware volume keys produce a HUD with the percentage and a gold progress
  bar; rapid key presses coalesce into one toast. Mute has its own state.
- **Battery / charging.** `GetSystemPowerStatus` + `WM_POWERBROADCAST` through the island
  wndproc. Notices fire only on real transitions (charger connected / disconnected,
  low-battery crossing below 20%, re-armed on recovery). Desktops without a battery
  render nothing.
- **Network.** `NetworkChange.NetworkAvailabilityChanged` / interface-changed events.
  Truthful Wi-Fi / Ethernet labels from the interface type; local connectivity only —
  MiMi never claims internet access and never pings.
- **Audio device changes.** `IMMNotificationClient` on the default render device — e.g.
  headphones/Bluetooth/speakers switching, shown with the friendly name.
- **Caps Lock and input language.** `GetKeyState` / `GetKeyboardLayout` sampled on the
  existing 1 Hz island tick — no keyboard hooks are installed and no keystroke content is
  ever read.
- **Brightness.** `WmiMonitorBrightnessEvent` on supported active laptop backlights,
  with a real percentage/progress HUD and no startup toast. Event setup/teardown runs off
  the UI thread; duplicate values are suppressed and bursts are bounded. Unsupported or
  denied WMI access stays silent. Toggle with `brightnessHudEnabled`; this does not
  control brightness or monitor external displays.

## Synchronized lyrics

- **Source:** LRCLIB (`https://lrclib.net`). The exact-match `/api/get` endpoint is tried
  first, then `/api/search` with a 15-second duration window — a different recording with
  the same title is rejected.
- **Parsing:** LRC parser supporting multi-timestamp lines, metadata tags, and the
  `[offset:]` tag. Line lookup is stateless and seek-proof.
- **Display:** the expanded media panel shows a 3-line window — previous line dim,
  current line gold, next line dim — replacing the title block when synced lyrics are
  available. Instrumental gaps render as a gentle `♪` instead of an empty block.
- **Toggle:** a lyrics glyph on the expanded player turns lyrics on/off per session.
- **Settings:** `lyricsEnabled` (default on) and `lyricsOffsetMs` (clamped
  -10000…10000) for fine sync adjustment.
- **Robustness:** bounded cache (negative results cached too), per-track deduplication,
  1 fetch/second pacing, obsolete-fetch cancellation, and HTTP 429 `Retry-After`
  cooldown. All failures degrade to an `Unavailable` state; confirmed-instrumental
  tracks show an `Instrumental` state.

## Agent bridge API contract

The bridge is a local, single-user IPC surface for agent adapters. Everything arriving
over it is **display-only**.

### Endpoint and security

| Aspect | Value |
|---|---|
| Transport | Windows named pipe, duplex, byte mode |
| Pipe name | `mimi-agent-bridge-<username>` (per-user namespace) |
| Pipe ACL | Current user + SYSTEM only; other users cannot connect even before the token check |
| Authentication | First frame must carry the shared token from `%LOCALAPPDATA%\MiMi\bridge-token` (64 hex chars, user-private file) |
| Handshake deadline | 5 seconds for the hello frame |
| Frame limit | 64 KiB payload per frame |
| Concurrency | Up to 8 simultaneous clients |

### Framing

Every frame is a 4-byte little-endian `Int32` payload length followed by that many UTF-8
JSON bytes. Any language that can write to a named pipe can speak this protocol; it is not
C#-specific.

### Frame types

Client → MiMi — hello (first frame, mandatory):

```json
{ "kind": "hello", "token": "<64 hex chars>", "client": "claude-code-hooks" }
```

Client → MiMi — event:

```json
{ "kind": "event", "event": { "event_id": "9f2c…", "provider": "claude-code",
  "session_id": "sess-1", "project": "mimi", "type": "agent.status",
  "title": "Refactoring", "message": "Step 3 of 5",
  "timestamp": "2026-10-27T12:00:00.0000000+00:00", "priority": "Normal",
  "metadata": { "tool": "bash" } } }
```

MiMi → client — ack (one per hello/event frame):

```json
{ "kind": "ack", "ok": true }
{ "kind": "ack", "ok": false, "error": "unknown or missing event type 'agent.hax'" }
{ "kind": "ack", "ok": true, "error": "duplicate" }
```

MiMi → client — approval resolution push (only after a genuine local user decision, or
system expiry; sent over the pipe of the client that originated the approval):

```json
{ "kind": "approval_resolved", "approval_request_id": "apr-1",
  "decision": "approved", "by": "user", "at": "2026-10-27T12:00:05.0000000+00:00" }
```

`decision` is one of `approved`, `denied`, `expired`, `dismissed`. `by` is `user`,
`system`, or `provider-disconnected` (a pending approval is dismissed when its requesting
connection drops, so the card cannot linger for an agent that can no longer receive it).

### AgentEvent schema

| Field | Type | Constraints |
|---|---|---|
| `event_id` | string | Max 80 chars; empty becomes a generated GUID (N format). Dedup key. |
| `provider` | string | Required, max 40 chars |
| `session_id` | string | Max 80 chars |
| `project` | string | Max 80 chars |
| `type` | string | Required; must be one of the event types below |
| `title` | string | Max 120 chars; empty becomes a type-derived default |
| `message` | string | Max 500 chars |
| `timestamp` | string (ISO 8601) | Advisory only; MiMi trusts its own clock for ordering/expiry |
| `priority` | string | `Critical`, `High`, `Normal`, `Low` (default `Normal`) |
| `approval_request_id` | string | Optional; **required** for `agent.approval_requested` and `agent.approval_resolved`; max 80 chars |
| `metadata` | object | Max 16 entries; keys max 40 chars, values max 200 chars. `action_kind` and `command` populate approval cards. |

Event types: `agent.started`, `agent.status`, `agent.tool_started`,
`agent.approval_requested`, `agent.approval_resolved`, `agent.completed`, `agent.failed`,
`agent.cancelled`, plus the Phase 3.6 lifecycle additions:
`agent.prompt_submitted`, `agent.tool_finished`, `agent.tool_failed`,
`agent.turn_finished`, `agent.interrupted`, `agent.session_ended`,
`agent.subagent_started`, `agent.subagent_stopped`.

### Validation rules

- Unknown or missing event types are rejected (ack `ok:false`).
- Missing provider is rejected.
- `agent.approval_requested` / `agent.approval_resolved` without `approval_request_id`
  are rejected.
- Control characters are stripped from every field; over-length fields are clamped
  (not rejected).
- Frames larger than 64 KiB are rejected; malformed JSON is rejected.
- Events with a previously seen `event_id` are acked as `ok:true, error:"duplicate"` and
  not re-displayed.

### Security rules

- Display-only: MiMi never executes any payload content — titles, messages, commands, and
  metadata are rendered as text and nothing else.
- No auto-approve: approvals are resolved only by a genuine local user decision (or TTL
  expiry); a resolved approval resolves exactly once, so responses are never replayed.
- Delivery is not acceptance: the `approval_resolved` push only means MiMi delivered the
  frame; MiMi never claims the agent acted on it.

### Testing with the simulator

```powershell
# A simple status event (shows a transient card on the island):
.\tools\Invoke-MiMiEvent.ps1 -EventType agent.status -Provider claude-code `
    -Project "MiMi" -Title "Refactoring" -Message "Step 3 of 5"

# An approval request (shows a sticky Approve/Deny card):
.\tools\Invoke-MiMiEvent.ps1 -EventType agent.approval_requested -Provider codex `
    -Project "web" -Title "Run test suite" -Message "npm test" `
    -Metadata @{ action_kind = "Execute Command"; command = "npm test" }

# Approval request + listen for the approval_resolved push after you click Approve/Deny:
.\tools\Invoke-MiMiEvent.ps1 -EventType agent.approval_requested -Provider claude-code `
    -Title "Edit app.py" -Message "About to modify 2 files" -Listen
```

Exit code 0 means accepted (including duplicates), 1 means rejected or transport failure.
Run with `-Verbose` to see handshake details. The same protocol works from C# or any other
language — the framing is four bytes plus JSON.

## Agent integrations (Phase 3)

### How it works

A provider hook fires → `MiMi.AgentCli` reads the hook's stdin JSON → it sends an approval
event over the secure named-pipe bridge (above) → the island shows a sticky Approve/Deny
card → the decision flows back over the same pipe → the CLI prints the provider-native
decision JSON on stdout. Every failure path — timeout, MiMi offline, expired request, parse
error, crash — DENIES: the integration is fail-closed, and MiMi never auto-approves.

Both installers write the **PermissionRequest hook and the Phase 3.6 lifecycle activity
hooks together** — one install gets you approvals and activity tracking. Codex
additionally requires the one-time `/hooks` trust review (below).

### Claude Code

```powershell
MiMi.AgentCli.exe install-claude [--timeout 260]
MiMi.AgentCli.exe uninstall-claude
```

`install-claude` adds a `PermissionRequest` hook (no matcher — all tools) plus the
lifecycle activity hooks (`SessionStart`, `UserPromptSubmit`, `PreToolUse`, `PostToolUse`,
`PostToolUseFailure`, `Stop`, `SessionEnd`, `SubagentStart`, `SubagentStop`) to
`~/.claude/settings.json`. Existing hooks and settings are preserved, a timestamped
`.mimi-backup-*` copy is written before every modification, and the install is idempotent.
No trust step is needed — Claude Code picks the hooks up automatically (start a new
session if a running one does not reload settings).

### Codex

```powershell
MiMi.AgentCli.exe install-codex [--timeout 260]
MiMi.AgentCli.exe uninstall-codex
```

`install-codex` adds the `PermissionRequest` hook plus the lifecycle activity hooks
(`SessionStart`, `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `Stop`, `Interrupt`,
`SessionEnd`, `SubagentStart`, `SubagentStop`) to `~/.codex/hooks.json` (same
preservation, backup, and idempotency rules). **Codex requires a one-time trust review:
run `/hooks` in the Codex TUI and approve the MiMi hook** — Codex blocks hooks it has not
reviewed. Then start a new Codex session.

### Optional Codex turn-complete toasts

```powershell
MiMi.AgentCli.exe install-codex-notify
MiMi.AgentCli.exe uninstall-codex-notify
```

Adds `notify = ["<MiMi.AgentCli.exe>", "codex-notify"]` to `~/.codex/config.toml`. This is
one-way only: `agent-turn-complete` payloads become activity cards on the island; no
approvals flow through `notify`. If the config already sets `notify` for another program,
the install is skipped and the existing entry left untouched.

### Manual hook configuration

The exact command line the installers write (use your own absolute path to the built exe;
by default `src\MiMi.AgentCli\bin\Release\net10.0-windows\MiMi.AgentCli.exe`):

```
<path to MiMi.AgentCli.exe> claude-permission    # Claude Code hook
<path to MiMi.AgentCli.exe> codex-permission     # Codex hook
```

To add it by hand, use this shape in `~/.claude/settings.json` (Claude Code) or
`~/.codex/hooks.json` (Codex, with `codex-permission`):

```json
{
  "hooks": {
    "PermissionRequest": [
      {
        "hooks": [
          { "type": "command", "command": "<path to MiMi.AgentCli.exe> claude-permission", "timeout": 260 }
        ]
      }
    ]
  }
}
```

The hook command also accepts `--wait N` (default 240 seconds): how long the CLI waits for
the island decision before denying on its own. The installer's default provider-side
`timeout` (260 s) is deliberately longer than the CLI wait so the CLI, not the provider's
hook timeout, makes the final call.

### Behavior on failure

| Situation | Outcome |
|---|---|
| CLI wait elapses with no island decision (`--wait`, default 240 s) | **deny** — the message explains the timeout |
| MiMi not running / bridge disabled | **deny** — message says to start MiMi or remove the hook |
| Approval expired in MiMi (TTL passed unanswered) | **deny** |
| Empty stdin or unparseable hook JSON | **deny** |
| Unexpected CLI crash | **deny** (top-level fail-closed handler) |
| Provider-level hook timeout (config `timeout` < CLI wait) | no decision emitted; the provider falls back to its normal interactive prompt — never auto-approve |

### Security model

- Decisions are correlated by `approval_request_id`, generated per request. Concurrent
  agent sessions are supported: responses are routed per-connection and filtered by id, so
  two simultaneous hooks never receive each other's decisions.
- MiMi never executes payload content; the command shown on the approval card is
  display-only.
- A decision is reported back only after the user clicks Approve/Deny (or the approval TTL
  expires). MiMi never auto-approves.

### What is NOT supported

- **Full Codex app-server embedding.** MiMi now includes an experimental, read-only
  conversation client for `codex-cli 0.160.x`. It supports text turns, streamed items,
  interruption, and one-shot command approvals. Thread resume/history, model pickers,
  session-wide grants, MCP elicitation, and file-change approvals are not supported;
  hooks remain the default integration.
- **Claude Code `permission_suggestions` / `updatedInput`.** These stdin fields are
  ignored: MiMi always returns the original tool input unchanged. The emitted decision JSON
  contains only `behavior` (`allow`/`deny`) and `message` — never `updatedInput`,
  `updatedPermissions`, or `interrupt`.
- **Windows notification listener.** Researched and intentionally unavailable in the
  portable build — see "Status" above.

### Testing it

- **Claude Code:** run Claude Code and ask it to do something requiring permission (e.g.
  "run npm run build" under default permissions). The island shows the approval card;
  Approve lets Claude continue, Deny blocks the tool call with the message.
- **Codex:** same in the Codex TUI, after approving the MiMi hook via `/hooks`.
- **No-op parse check:** `MiMi.AgentCli claude-permission --dry-run < input.json` prints
  the event it would send (to stderr) and denies, without touching the bridge.
  `codex-permission` supports `--dry-run` the same way.
- **Simulator:** `tools\Invoke-MiMiEvent.ps1` exercises the same bridge/approval pipeline
  without a provider (examples above).

## ImpressiveBot live status

ImpressiveBot's status mirror must report **runtime state**, not notification delivery.
The coordinated sender in `ImpressiveBot/app/server/mimi-runtime-sender.ts` snapshots
confirmed live turns every 20 seconds, including quiet thinking and tool execution.
Quiet-turn preferences and notification routing do not suppress these updates. Updates
are serialized and coalesced, with bounded pending state; completion/stop comes from
runtime lifecycle events. No prompt, transcript, or tool output is needed.

The sender invokes `MiMi.AgentCli.exe impressivebot-status --require-ack`. This opt-in
flag returns nonzero if the bridge does not acknowledge delivery, allowing the sender
to retry its latest state. Without the flag, the legacy command remains non-failing.
Payloads retain the existing `bot`, `session`, and `state` contract (`working`, `waiting`,
`finished`, `failed`, `stopped`), adding optional monotonic numeric `sequence` and UTC ISO
`timestamp`. Ordering is session-wide, not reset per turn. Old, replayed, expired, and
excessively future-dated updates cannot overwrite or freshen newer activity.

MiMi keeps its five-minute silence safety lease: a disconnected source becomes
**status unknown**, never falsely finished or healthy idle. The compact count includes
working and waiting sessions; the detailed status reports working and waiting counts
separately. Received fresh status proves the link works even if a custom CLI path is
not visible to MiMi. Approval decisions remain exclusively in ImpressiveBot.

**Deployment requires both sides.** Replace/restart MiMi and its companion CLI, and
deploy/restart the updated ImpressiveBot server. Updating MiMi alone cannot manufacture
missing runtime updates from an old installed ImpressiveBot bundle. Source changes and
isolated tests do not update an already-running installed app.

## Agent activity and multi-agent UI (Phase 3.6)

### How activity events reach MiMi

```
Claude Code / Codex lifecycle hook
  → MiMi.AgentCli activity --provider <claude-code|codex>   (fire-and-forget, never blocks)
  → authenticated named-pipe bridge (same token, ACL, dedup, normalization as approvals)
  → AgentEventBus
  → AgentSessionManager   (per-session state, staleness, bounded history)
  → IslandPresenter       (compact indicator, badge, panel rows, approval queue)
  → IslandWindow
```

Lifecycle hooks are **not** approval hooks: they are fast, best-effort telemetry. If the
bridge is down they exit 0 silently and never affect the agent. Approvals still travel only
through the synchronous `claude-permission` / `codex-permission` path, which fails closed.

### Session identity and state

A session is identified by `(provider, session_id)` — never by project name or provider
label, because several sessions (including two from the same provider) can share a project.
`project` is display metadata only.

`AgentSessionManager` maps provider events to one of these truthful states:

| State | Meaning | Set by |
|---|---|---|
| Started | session opened, no task yet | `SessionStart` / `agent.started` |
| Working | a task was submitted or a tool finished | `UserPromptSubmit`, `PostToolUse`, `agent.prompt_submitted`, `agent.tool_finished` |
| Executing tool | a tool call is in progress | `PreToolUse`, `SubagentStart`, `agent.tool_started` |
| Awaiting approval | a permission request is pending | `agent.approval_requested` |
| Turn finished | the provider ended the turn; **the session is still alive** | `Stop`, `agent.turn_finished` |
| Interrupted | the user/provider interrupted the turn | `Interrupt`, `agent.interrupted` |
| Error | a tool or the turn failed | `PostToolUseFailure`, `agent.failed` |
| Ended | the session terminated | `SessionEnd`, `agent.session_ended` |
| Unknown | no confirmed event for 90 s (silence is not proof of completion) | staleness sweeper |

Silence never becomes "finished", and MiMi does not claim the model is generating tokens —
hook telemetry cannot see that. `Turn finished` and `Ended` are deliberately distinct.
Out-of-order `sequence` metadata cannot rewind a session, late events cannot resurrect an
ended one, and ended history is capped at 20 sessions.

### Compact indicator

- One working session: `Claude Code · Executing tool` (provider + state).
- Two or more: `3 agents active`, with a pending-approval count when applicable.
- While music is playing, the media pill keeps its place and shows a small agent-count
  badge. Hover the **badge** to open the agent activity panel; hover anywhere else on the
  pill to open the media controls.
- Lifecycle updates change the indicator in place — they do not re-trigger island
  animations. Only turn completion/failure/interruption produces a short toast.

### Multi-agent activity panel

Hovering the agent pill (or the badge on the media pill) opens a panel with one row per
session: provider, project, state, last action/tool, age, and pending approvals. Finished
rows stay visible for 2 minutes, then age out; live sessions that have gone silent show
`Status unknown`.

### Multi-approval navigation

Approval cards are sticky and outrank media. When several approvals are pending, the newest
is shown first with `1 of 2` / `2 of 2` and `‹ ›` arrow buttons. Each decision resolves only
its own `approval_request_id` and is pushed back over the connection that requested it;
concurrent Claude Code and Codex hooks never receive each other's answers. If the requesting
hook times out or disconnects before a decision, its pending card is dismissed (and the hook
itself still denies, fail-closed).

### Installing the lifecycle hooks

Both `install-claude` and `install-codex` write the lifecycle hooks alongside the
PermissionRequest hook in a single idempotent pass, so a fresh install needs exactly one
run of each installer. Users who installed MiMi before Phase 3.6 should re-run them
(existing hooks are preserved):

```powershell
MiMi.AgentCli.exe install-claude
MiMi.AgentCli.exe install-codex
```

Activity hooks carry no timeout override, so they can never gate or delay the provider.
`uninstall-claude` / `uninstall-codex` remove only MiMi-signed commands.

### Verification performed

- 211/211 automated tests (see "Testing checklist").
- Bridge integration test: approval is registered before subscribers see it, and a pending
  approval is dismissed when its requesting connection drops.
- Live bridge + UI verification using provider-shaped hook JSON through the real
  `MiMi.AgentCli activity` and `*-permission` commands (three concurrent sessions across two
  providers; approval card priority; ‹ › navigation; Approve/Deny routing verified from each
  hook's stdout; 8 s timeout produced a deny; hidden mode stayed hidden on new events;
  media badge and both hover panels inspected in screenshots).
- **Not verified with a live Claude Code or Codex session in this pass.** The hook payloads
  were provider-shaped simulations driven through the real adapter and bridge, not actual
  paid provider sessions. Treat real-provider end-to-end as still to be confirmed.

### Known limitations

- No token-level/"thinking" telemetry; states reflect hook moments only.
- A session that goes silent flips to `Status unknown` after five minutes; MiMi cannot know
  whether the agent is still reasoning unless the provider sends another confirmed update.
- Finished session rows are hidden after 2 minutes; ended-session history is capped at 20.
- Only one approval card is visible at a time; the others are reachable with the arrows.
- Codex lifecycle hooks require the one-time `/hooks` trust review.
- "Open in Agent" copies identifying text to the clipboard; it does not focus the provider.
- The activity panel refreshes in place every second while visible, with approvals first and at most three recent inactive rows.

## Thursday local harness

Thursday reports detached chat/agent runs through `MiMi.AgentCli.exe relay`, a
activity adapter that accepts one normalized `AgentEvent` JSON object per
stdin line. It shows thinking/progress stages, tool activity, waiting for input,
completion, errors and interruptions alongside Codex CLI and Claude Code.
Hover over the island for per-chat details. Thursday permission requests use the
separate `thursday-permission` adapter, kept connected while the island displays
the complete sealed action with **Allow for this task** and **Deny** controls.
Decisions return through Thursday's authenticated continuation route and retain
its exact-action checks. An offline island grants nothing; the Thursday card
remains usable. Session-wide approval is available in Thursday itself.

Build the solution and restart both apps. Thursday automatically finds the newest
CLI build in the sibling `MiMi` project, or an executable on PATH. For a separate
installation, set `THURSDAY_MIMI_CLI` to its `MiMi.AgentCli.exe` path. Set
`THURSDAY_MIMI_ENABLED=0` to opt out. Agents and Bridge must be enabled in MiMi.
No provider hooks are needed. The relay authenticates over the existing local
named pipe and retries on later updates if MiMi is unavailable. Activity sends
progress labels and tool names, without prompts or tool output. Permission cards
also include the sealed action input; oversized actions remain in Thursday.

The activity panel groups approvals, active work and up to three recent results,
with a bounded scrolling area and live updates while open. Confirmed terminal
states retain their result instead of becoming "Status unknown". Silent live
sessions still become unknown after five minutes, and heartbeat frames refresh
Thursday activity without repeating timeline entries.

## Settings reference

Persisted to `%LOCALAPPDATA%\MiMi\settings.json`. Invalid or corrupt files fall back to
defaults; values are clamped to the ranges below on every save.

| Key | Type | Default | Effect |
|---|---|---|---|
| `version` | int | `1` | Settings schema version |
| `horizontalOffsetDip` | double | `0` | Horizontal offset from top-center, in DIP (clamped -800…800) |
| `topMarginDip` | double | `6` | Distance from the top edge of the work area, in DIP (clamped 0…200) |
| `scale` | double | `1.0` | Island size multiplier (clamped 0.75…1.5) |
| `textSize` | string | `"default"` | Island typography: `"small"`, `"default"`, `"large"` |
| `animationSpeed` | double | `1.0` | Animation speed multiplier (clamped 0.5…2.5) |
| `collapseDelayMs` | int | `900` | Delay before auto-collapse after the pointer leaves (clamped 200…5000) |
| `expandOnHover` | bool | `true` | Expand the island when the pointer hovers over it |
| `mediaModuleEnabled` | bool | `true` | GSMTC media pill and media panel |
| `agentsModuleEnabled` | bool | `true` | Agent event cards and approvals |
| `mascotEnabled` | bool | `true` | Mascot face in the resting state |
| `mascotAnimationsEnabled` | bool | `true` | Mascot idle animations (blinking, breathing) |
| `mascotReactionsEnabled` | bool | `true` | Brief mascot reactions to agent events |
| `mascotSleepyEnabled` | bool | `false` | Opt-in sleepy mode: doze after inactivity, wake on mouse proximity |
| `glowEnabled` | bool | `true` | Gold bottom-edge glow band |
| `glowIntensity` | double | `0.6` | Glow intensity (clamped 0…1) |
| `animationIntensity` | double | `1.0` | Animation amplitude (clamped 0.5…2) |
| `lyricsEnabled` | bool | `true` | Synchronized lyrics on the expanded media panel |
| `lyricsOffsetMs` | int | `0` | Lyrics sync offset in ms (clamped -10000…10000) |
| `volumeHudEnabled` | bool | `true` | Volume/mute HUD |
| `batteryHudEnabled` | bool | `true` | Charging/low-battery HUD |
| `brightnessHudEnabled` | bool | `true` | Brightness-change HUD on WMI-supported laptop backlights |
| `networkHudEnabled` | bool | `true` | Network HUD |
| `capsLockHudEnabled` | bool | `true` | Caps Lock / input language HUD |
| `headphoneHudEnabled` | bool | `true` | Audio device change HUD |
| `agentNotificationsEnabled` | bool | `true` | Agent notification toasts (Phase 3.7) |
| `completionNotificationsEnabled` | bool | `true` | Turn-completion notifications |
| `approvalNotificationsEnabled` | bool | `true` | Approval-arrival notifications |
| `windowsNotificationsWhenHidden` | bool | `false` | Windows toast notifications while the island is hidden (opt-in) |
| `notificationDurationMs` | int | `3000` | Notification display time (clamped 1500…10000) |
| `notificationSoundEnabled` | bool | `false` | Notification sound (opt-in) |
| `bridgeEnabled` | bool | `true` | Named-pipe agent bridge (inert until a client connects) |
| `approvalTtlMinutes` | int | `5` | Approval card time-to-live (clamped 1…60) |
| `startWithWindows` | bool | `false` | Register MiMi to start with Windows (also via tray) |
| `desktopPersistVisible` | bool | `true` | Stay visible on the desktop |
| `fullscreenSuppress` | bool | `true` | Step aside for fullscreen apps |

The clock has no setting in 4.0: the idle state is face-only by design.

## Troubleshooting

- **No media is shown.** The player has no GSMTC media session (some apps only expose one
  for certain playback modes), or `mediaModuleEnabled` is off in settings. Test with
  `MiMi.exe --selftest`, which reports how many active media sessions Windows sees.
- **Bridge events are not arriving.** Check that the bridge is enabled in settings
  (`bridgeEnabled`), that the client targets the right pipe name
  (`mimi-agent-bridge-<username>`), and that the token matches. A stale token (e.g. after
  reinstalling) can be fixed by deleting `%LOCALAPPDATA%\MiMi\bridge-token` and restarting
  MiMi, which generates a fresh one.
- **Approval UI shows "expired".** The approval TTL passed (default 5 minutes,
  `approvalTtlMinutes`). Expired approvals cannot be approved or denied; send a new
  `agent.approval_requested` event.
- **Agent activity never appears.** Check that `agentsModuleEnabled` and `bridgeEnabled`
  are on, then re-run `MiMi.AgentCli.exe install-claude` / `install-codex` (builds before
  Phase 3.6 did not install lifecycle hooks), start a new provider session, and for Codex
  approve the hook in `/hooks`. The log shows `Bridge: client 'claude-code-activity'
  connected` for each event; if that line is absent the provider is not invoking the hook.
- **An approval card stays on screen after the agent gave up.** It should be dismissed
  automatically when the requesting hook disconnects. If it persists, check the log for
  `dismissed: requesting client ... disconnected`; otherwise the TTL will clear it.
- **Ctrl+Alt+M does nothing.** Another application may own the hotkey; the Settings window
  states when registration failed.
- **The mascot never dozes.** Sleepy mode is opt-in (`mascotSleepyEnabled`), and media
  playback blocks doze by design — quiet is required.
- **Lyrics never appear.** The track needs a synced entry on LRCLIB; instrumental and
  not-found tracks show their own states instead. Check `lyricsEnabled` and network access.
- **No battery HUD.** Desktop machines without a battery correctly render nothing.
- **Logs:** `%LOCALAPPDATA%\MiMi\logs\mimi.log`. Bridge connections, rejections, approval
  cards/decisions, dismissals, and visibility transitions are logged there.

## Architecture

| Project / component | Responsibility |
|---|---|
| `MiMi.Core` | Settings/config store, island state manager (priorities, alerts, pinned state), dedup store, logger, visibility coordination (`VisibilityCoordinator`, `VisibilityTransitionPlanner` with `FadePlan`/`FadeSequencer`), `ProximityWakePolicy`, `PomodoroTimer`, `WaterTracker`, `TodoStore`, `FileShelfStore` |
| `MiMi.Media` | GSMTC media service and media models |
| `MiMi.Lyrics` | `LrcParser`, `LyricsMatchPicker`, `LrcLibClient` (LRCLIB), bounded lyrics cache, `LyricsService` |
| `MiMi.Agents` | `AgentEvent` model/normalization, approval store, agent event bus, `AgentSessionManager`, `MascotMood` policy, `ProviderHookCatalog`/`ProviderIntegrationInspector`, `AgentNotificationCoordinator` |
| `MiMi.Bridge` | Named-pipe receiver, framing/serialization, token and pipe security, per-connection approval correlation |
| `MiMi.AgentCli` | Provider hook adapters (Claude Code / Codex PermissionRequest and `activity` lifecycle hooks, Codex notify) and their installers |
| `MiMi.App` | WPF island window, presenter, tray icon, settings UI, fullscreen watcher, selftest; mascot (`UI/Mascot`: `MiMiMascotControl`, `MascotExpression`), `GlowLayout`, `SystemHudController` with the Core Audio / power / network / keyboard interop monitors |
| `MiMi.Tests` | xUnit tests covering core policies, integration contracts, settings persistence, and offscreen island layouts |
| `docs/` | Research references: Claude Code hooks (`claude-code-hooks.md`), Codex integrations (`codex-integrations.md`) |

Data flow: providers/bridge → `AgentEventBus` → `IslandPresenter` → `IslandStateManager`
→ `IslandWindow`. The bridge normalizes and validates events, the bus decouples producers
from the UI, the state manager decides what the island currently shows (highest priority
wins, transients expire back to the pinned state), and the window renders it. Visibility
(hover expand, hotkey toggle, fades) is planned by `VisibilityCoordinator` +
`VisibilityTransitionPlanner`; the mascot mood pipeline maps island state to
`MascotExpression`; `SystemHudController` pushes monitor snapshots as HUD toasts.

## Testing checklist

Automated coverage (`dotnet test src/MiMi.Tests/MiMi.Tests.csproj -c Release`)
and the separate `MiMi.exe --selftest` pipeline check:

- Settings round-trip and corrupt-file fallback to defaults; value clamping.
- Dedup store: duplicate ids rejected, capacity eviction, empty id accepted.
- Event normalization: valid events pass; missing provider, unknown types, and approvals
  without an id are rejected; long fields clamped; control characters stripped.
- Approval store: single resolve (double resolve fails), expiry prevents decisions,
  sweeper expires stale requests, duplicate registration ignored.
- Island state: priority ordering including approval-over-media, duplicate pushes don't
  re-fire, transient alerts expire and the pinned state returns.
- Bridge: pipe frame round-trip, oversized frame rejection, invalid token rejection,
  malformed event rejection with error ack, hello/event/ack handshake end-to-end,
  approval decision push-back to the originating client (also exercised by `--selftest`),
  approval registered before subscribers are notified, pending approval dismissed when its
  requesting connection disconnects.
- Phase 3.6 session tracking: full lifecycle, prompt/tool states, two sessions from one
  provider, mixed providers with identical session ids, approval correlation, staleness →
  `Unknown`, ended sessions ignore late events, out-of-order sequences cannot rewind,
  bounded ended history, finished-row aging, active-count excludes ended/stale sessions.
- Activity hooks and installers: `PreToolUse`/`PostToolUse`/`Stop`/`SessionEnd`/
  `SubagentStart`/`SubagentStop`/`Interrupt` mapping, permission events not double-reported,
  provider-aware approval text, all Claude/Codex activity hooks installed idempotently with
  no timeout gate, uninstall preserves user hooks.
- Hook adapters: Claude Code / Codex PermissionRequest stdin parsing (tool mapping, unknown
  input fields tolerated, wrong event rejected) and the decision-JSON shape — allow/deny
  with a message only, never `updatedInput`; Codex notify payload parsing (turn-complete
  mapping, long messages truncated, unknown types to status).
- **Lyrics** (new in 4.0): LRC parser (multi-timestamp, metadata, `[offset:]`), line
  indexing, exact-vs-search matching with the 15 s duration window, cache behavior,
  instrumental/unavailable degradation.
- **Agent sessions & notifications** (new in 4.0): timeline tests, notification
  coordinator policy, provider hook catalog/inspector.
- **File shelf** (new in 4.0): add/validate/duplicates, capacity eviction, persistence and
  corruption recovery, missing-original detection.
- **Productivity** (new in 4.0): Pomodoro phase flow and deadline math, water tracker
  (midnight rollover, quiet hours, snooze, goal), to-do store (due reminders, persistence).
- **System HUD policies** (new in 4.0): battery transition/low-battery policy, network
  labels, volume coalescing, keyboard HUD sampling.
- **Mascot mood & proximity wake** (new in 4.0): mood→expression mapping;
  `ProximityWakePolicy` hysteresis (wake/re-doze/min-awake).
- **Visibility & glow** (new in 4.0): `VisibilityCoordinator` preferences, `FadePlan`
  transitions, `FadeSequencer` superseded-generation handling, `GlowLayout` band geometry.
- GSMTC availability check (`--selftest` reports the active session count).

Requires manual verification (not automated, not claimed done):

- Actual Spotify/browser playback showing artwork, transport, seek, and live lyrics sync.
- Real Explorer drag-in/drag-out against the file shelf (cannot be scripted).
- Tray panel interaction and panel screenshots (user's visual pass).
- Sleepy mode + cursor wake over a sustained quiet period (media blocks doze by design).
- Hover expand/collapse feel and collapse delay.
- DPI scaling across 100–200%, multi-monitor placement.
- Fullscreen step-aside against real fullscreen apps (games, video players).
- Startup/shutdown memory behavior over longer sessions.

### Remaining optional integrations

These are separate features, not enabled or represented as complete by the UI refresh:

- **Codex app-server embedding:** the hook-based integration is implemented. A full embedded
  client still needs a version-pinned protocol, process/session ownership, transcript UI,
  sandbox/model configuration, and end-to-end approval/cancellation verification. See
  `docs/codex-integrations.md`; hooks must remain the default until that client is validated.
- **Windows notification ingestion:** still requires an MSIX package identity, declared
  `userNotificationListener` capability, explicit/revocable user consent, and packaged
  deployment testing. The portable build cannot provide this. Notification bodies can be
  sensitive; an opt-in privacy/filtering policy is needed before exposing them on the island.
- **Acceptance:** physical brightness events, assistive technology, multi-monitor DPI,
  long-running performance, real drag/drop, and fullscreen behavior require a live pass.
  Offscreen render tests do not certify these behaviors.

To save offscreen layout review images without opening windows:

```powershell
$env:MIMI_QA_DIR = Join-Path $PWD 'artifacts\\ui-review'
dotnet test src/MiMi.Tests/MiMi.Tests.csproj -c Release --filter FullyQualifiedName~IslandVisualLayoutTests
Remove-Item Env:MIMI_QA_DIR
```

## Portable release

`build-release.ps1` produces a portable build in `dist/`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build-release.ps1 -Verify

# Publish separately while the normal dist build is running:
powershell -NoProfile -ExecutionPolicy Bypass -File build-release.ps1 -Verify -OutputDirectory artifacts\release
```

`-Verify` runs the Release test suite before publishing and the published app's headless
selftest afterward. Failures stop the script. Published files include `SHA256SUMS.txt`
for the app, companion CLI, and README. Close MiMi before overwriting its executable,
or select a separate output directory.

- **Output:** `dist\MiMi.exe` (single file, win-x64), `dist\MiMi.AgentCli.exe` next to it
  (hook installers + adapters resolve this path), plus `README.md` and `SHA256SUMS.txt`.
- **Framework-dependent by default:** the target machine needs the
  [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download). Pass `-SelfContained`
  to embed the runtime (~180 MB) so it runs with nothing installed.
- **No installer, no admin:** launch `dist\MiMi.exe` directly.
- **Settings and data live in `%LOCALAPPDATA%\MiMi`** (settings.json, shelf.json,
  todos.json, water.json, bridge-token, logs) and survive upgrades — replacing the exe
  does not touch user data.
