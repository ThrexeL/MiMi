# MiMi

A lightweight Dynamic Island-style overlay for Windows 11. MiMi lives at the top center of
the primary monitor, is always on top, and never steals keyboard focus. It shows your
current media, agent activity, and approvals in a compact pill that expands on hover.

MiMi is inspired by [Nusa](https://rama2555.github.io/nusa/) but is an original
implementation. The stack is C# / .NET 10 / WPF with CsWinRT Windows SDK projections —
no Electron, no WebView, no third-party UI packages. Even the tray icon is a hand-rolled
`Shell_NotifyIconW` P/Invoke.

## Status

Development is phased. What is implemented today:

- **Phase 1 — island and media (implemented)**
  - Always-on-top island window at the top center of the primary monitor: hover expand,
    auto-collapse, fullscreen step-aside via a WinEvent hook, PerMonitorV2 DPI awareness.
  - Resting state (clock + mascot eyes) and compact media state (album art, title, animated
    equalizer while playing).
  - Expanded media panel: transport controls (previous / play-pause / next), seekable
    progress slider, elapsed/remaining time.
  - Real media integration via Windows GSMTC (`GlobalSystemMediaTransportControls`):
    Spotify, browsers, and media players that register Windows media sessions work as-is.
  - System tray icon: Settings, Simulate agent event, Start with Windows, Restart, Exit.
  - Settings window persisted to `%LOCALAPPDATA%\MiMi\settings.json` (scale, animation
    speed, collapse delay, module toggles, bridge toggle, approval TTL, autostart).

- **Phase 2 — agent foundation (implemented)**
  - Normalized, provider-independent `AgentEvent` model.
  - Secure local named-pipe bridge (pipe `mimi-agent-bridge-<username>`, ACL restricted to
    the current user + SYSTEM, shared token at `%LOCALAPPDATA%\MiMi\bridge-token`,
    64 KiB frame limit, control-character stripping, field length clamps).
  - Deduplication by `event_id`; approval store with TTL (expired approvals cannot be
    approved or denied).
  - Island priority system: approval > critical > agent failure > completion > media > idle.
  - Sticky approval cards that survive song changes.
  - Duplex protocol: MiMi pushes `approval_resolved` back over the originating client's pipe.
  - Event simulator script (`tools/Invoke-MiMiEvent.ps1`) and a tray
    "Simulate agent event" demo sequence.

- **Phase 3 — real agent integrations (implemented).** Claude Code and Codex both have real,
  two-way approval integrations through their official PermissionRequest hooks, implemented
  by `MiMi.AgentCli` (see "Agent integrations (Phase 3)" below). Codex additionally has
  optional one-way turn-complete toasts via its `notify` config. Codex app-server
  full-embedding (MiMi driving a Codex session) remains future work (Phase 5 candidate).
  "Open in Agent" still copies approval details to the clipboard; direct session focus is
  not implemented.

- **Phase 4 — extra modules (not started).** Lyrics, Pomodoro, file shelf, to-do, system
  indicators.

- **Phase 5 — personal harness adapter; Codex app-server full embedding (candidate; not
  started).**

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
classic `.sln`.

## Testing real media integration

Media metadata and playback state come exclusively from Windows GSMTC. If an app does not
appear in MiMi, it does not expose a media session to Windows.

1. Start MiMi. It shows the resting state (clock + mascot eyes).
2. Play a song in Spotify, Edge (YouTube), or Windows Media Player. The island switches
   from the clock to a compact media pill with artwork.
3. Hover over the pill to expand the media panel: transport controls, seekable progress
   slider, elapsed/remaining time.
4. Exercise pause, seek, next, and previous.
5. Play in two apps at once (e.g. Spotify + YouTube) to exercise active-session selection.
6. Stop all playback. The island returns to the resting state.

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

`decision` is one of `approved`, `denied`, `expired`. `by` is `user` or `system`.

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
`agent.cancelled`.

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

### Claude Code

```powershell
MiMi.AgentCli.exe install-claude [--timeout 260]
MiMi.AgentCli.exe uninstall-claude
```

`install-claude` adds a `PermissionRequest` hook (no matcher — all tools) to
`~/.claude/settings.json`. Existing hooks and settings are preserved, a timestamped
`.mimi-backup-*` copy is written before every modification, and the install is idempotent.
No trust step is needed — Claude Code picks the hook up automatically.

### Codex

```powershell
MiMi.AgentCli.exe install-codex [--timeout 260]
MiMi.AgentCli.exe uninstall-codex
```

`install-codex` adds the hook to `~/.codex/hooks.json` (same preservation, backup, and
idempotency rules). **Codex requires a one-time trust review: run `/hooks` in the Codex TUI
and approve the MiMi hook** — Codex blocks hooks it has not reviewed.

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

- **Codex app-server embedding.** MiMi does not drive the Codex session; hook decisions
  only fire for approval prompts Codex itself would raise. Full embedding (MiMi as the
  app-server client) remains future work — see `docs/codex-integrations.md`.
- **Claude Code `permission_suggestions` / `updatedInput`.** These stdin fields are
  ignored: MiMi always returns the original tool input unchanged. The emitted decision JSON
  contains only `behavior` (`allow`/`deny`) and `message` — never `updatedInput`,
  `updatedPermissions`, or `interrupt`.

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

## Settings reference

Persisted to `%LOCALAPPDATA%\MiMi\settings.json`. Invalid or corrupt files fall back to
defaults; values are clamped to the ranges below on every save.

| Key | Type | Default | Effect |
|---|---|---|---|
| `version` | int | `1` | Settings schema version |
| `horizontalOffsetDip` | double | `0` | Horizontal offset from top-center, in DIP (clamped -800…800) |
| `topMarginDip` | double | `6` | Distance from the top edge of the work area, in DIP (clamped 0…200) |
| `scale` | double | `1.0` | Island size multiplier (clamped 0.75…1.5) |
| `animationSpeed` | double | `1.0` | Animation speed multiplier (clamped 0.5…2.5) |
| `collapseDelayMs` | int | `900` | Delay before auto-collapse after the pointer leaves (clamped 200…5000) |
| `expandOnHover` | bool | `true` | Expand the island when the pointer hovers over it |
| `mediaModuleEnabled` | bool | `true` | GSMTC media pill and media panel |
| `agentsModuleEnabled` | bool | `true` | Agent event cards and approvals |
| `clockEnabled` | bool | `true` | Clock in the resting state |
| `mascotEnabled` | bool | `true` | Mascot eyes in the resting state |
| `bridgeEnabled` | bool | `true` | Named-pipe agent bridge (inert until a client connects) |
| `approvalTtlMinutes` | int | `5` | Approval card time-to-live (clamped 1…60) |
| `startWithWindows` | bool | `false` | Register MiMi to start with Windows (also via tray) |

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
- **Logs:** `%LOCALAPPDATA%\MiMi\logs\mimi.log`. Bridge connections, rejections, and
  approval decisions are logged there.

## Architecture

| Project | Responsibility |
|---|---|
| `MiMi.Core` | Settings/config store, island state manager (priorities, alerts, pinned state), dedup store, logger |
| `MiMi.Media` | GSMTC media service and media models |
| `MiMi.Agents` | `AgentEvent` model/normalization, approval store, agent event bus |
| `MiMi.Bridge` | Named-pipe receiver, framing/serialization, token and pipe security |
| `MiMi.AgentCli` | Provider hook adapters (Claude Code / Codex PermissionRequest, Codex notify) and their installers |
| `MiMi.App` | WPF island window, presenter, tray icon, settings UI, fullscreen watcher, selftest |
| `MiMi.Tests` | xUnit tests for config, dedup, events, approvals, state, bridge, and hook adapters |
| `docs/` | Research references: Claude Code hooks (`claude-code-hooks.md`), Codex integrations (`codex-integrations.md`) |

Data flow: providers/bridge → `AgentEventBus` → `IslandPresenter` → `IslandStateManager`
→ `IslandWindow`. The bridge normalizes and validates events, the bus decouples producers
from the UI, the state manager decides what the island currently shows (highest priority
wins, transients expire back to the pinned state), and the window renders it.

## Testing checklist

Covered by 52 automated tests (`dotnet test MiMi.slnx -c Debug`) and `MiMi.exe --selftest`:

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
  approval decision push-back to the originating client (also exercised by `--selftest`).
- Hook adapters: Claude Code / Codex PermissionRequest stdin parsing (tool mapping, unknown
  input fields tolerated, wrong event rejected) and the decision-JSON shape — allow/deny
  with a message only, never `updatedInput`; Codex notify payload parsing (turn-complete
  mapping, long messages truncated, unknown types to status).
- Installers: hook entry created, install idempotent, existing user hooks preserved
  untouched, non-object `hooks` keys skipped without writing, uninstall removes only
  MiMi's entries.
- GSMTC availability check (`--selftest` reports the active session count).

Requires manual verification (not automated, not claimed done):

- Actual Spotify/browser playback showing artwork, transport, and seek.
- Hover expand/collapse feel and collapse delay.
- DPI scaling across 100–200%, multi-monitor placement.
- Fullscreen step-aside against real fullscreen apps (games, video players).
- Startup/shutdown memory behavior over longer sessions.
