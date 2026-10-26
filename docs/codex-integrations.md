# OpenAI Codex CLI — external notification & approval mechanisms (research)

Researched: 2026-10-27. Primary sources: official Codex docs (developers.openai.com/codex, which
now 308-redirects to learn.chatgpt.com — every page has a Markdown twin by appending `.md`) and the
open source repo [openai/codex](https://github.com/openai/codex) (source files quoted from `main`
and release tags). Latest stable release at research time: **rust-v0.164.0 (2026-10-23)**.

Sources used (all fetched for this document):
- Config reference: https://learn.chatgpt.com/docs/config-file/config-reference.md
- Advanced config (Notifications section): https://learn.chatgpt.com/docs/config-file/config-advanced.md
- Approvals & security: https://learn.chatgpt.com/docs/agent-approvals-security.md
- App Server: https://learn.chatgpt.com/docs/app-server.md  ← the definitive integration doc
- Hooks: https://learn.chatgpt.com/docs/hooks.md
- SDK: https://learn.chatgpt.com/docs/codex-sdk.md ; Non-interactive: https://learn.chatgpt.com/docs/non-interactive-mode.md
- Repo source: `codex-rs/hooks/src/legacy_notify.rs`, `codex-rs/hooks/schema/generated/*`,
  `codex-rs/app-server-protocol/src/protocol/v1.rs` + `v2/*`, `codex-rs/protocol/src/protocol.rs`
  (`ReviewDecision`), historical tags `rust-v0.20.0` / `rust-v0.48.0`.

---

## A. The `notify` config (one-way external notifications)

### Exact TOML syntax (documented)

`~/.codex/config.toml`, top-level key, array of strings = argv (first element is the program):

```toml
notify = ["python3", "/path/to/notify.py"]
```

Config reference entry:

> key: `notify`, type: `array<string>` — "Command invoked for notifications; receives a JSON payload from Codex."

### When it fires — QUOTED from docs (config-advanced, "Notifications")

> "Use `notify` to trigger an external program whenever Codex emits supported events
> (**currently only `agent-turn-complete`**). This is handy for desktop toasts, chat webhooks,
> CI updates, or any side-channel alerting that the built-in TUI notifications don't cover."

Confirmed in source (`codex-rs/hooks/src/legacy_notify.rs`, `main`): `legacy_notify_json()` matches
only `HookEvent::AfterAgent` → serializes `UserNotification::AgentTurnComplete`. **There is NO
approval-request notification type, no tool-event notification, through `notify`.** It is
implemented as a "legacy" hook internally; the source carries
`// TODO: Remove this hook and its environment plumbing when legacy 'notify' support is removed.`
Treat `notify` as deprecated-in-place: works today, slated for eventual removal (code comment; no
public deprecation date — unconfirmed).

### Payload — JSON passed as the LAST argv argument (NOT stdin)

The program is spawned fire-and-forget (stdin/stdout/stderr nulled, environment credential-scrubbed,
exit status ignored) and receives exactly **one extra argument**: the JSON string.

Documented example script (from config-advanced.md, verbatim excerpt):

```python
#!/usr/bin/env python3
import json, subprocess, sys

def main() -> int:
    notification = json.loads(sys.argv[1])
    if notification.get("type") != "agent-turn-complete":
        return 0
    title = f"Codex: {notification.get('last-assistant-message', 'Turn Complete!')}"
    ...
```

> "The script receives a single JSON argument. Common fields include:
> - `type` (currently `agent-turn-complete`)
> - `thread-id` (session identifier)
> - `turn-id` (turn identifier)
> - `cwd` (working directory)
> - `input-messages` (user messages that led to the turn)
> - `last-assistant-message` (last assistant message text)"

### Exact JSON schema (keys are kebab-case)

Verified against source test `legacy_notify_json_matches_historical_wire_shape` (`main`):

```json
{
  "type": "agent-turn-complete",
  "thread-id": "b5f6c1c2-1111-2222-3333-444455556666",
  "turn-id": "12345",
  "cwd": "/Users/example/project",
  "client": "codex-tui",
  "input-messages": ["Rename `foo` to `bar` and update the callsites."],
  "last-assistant-message": "Rename complete and verified `cargo build` succeeds."
}
```

Notes:
- `client` is optional/omitted when unset (present in current `main`; older builds may not send it).
- Historical docs from 2025 showed a similar example with snake_case keys (`input_messages`,
  `last_assistant_message`); the shipped serializer used kebab-case. Parse defensively (accept both
  spellings) if you must support old versions. (Old-docs-example discrepancy: marked unconfirmed.)
- On Windows the program is spawned directly from argv (no shell), so use an absolute path to an
  .exe/script host (e.g. `["C:\\...\\python.exe", "C:\\...\\mimi_notify.py"]`). Shell interpolation
  or `commandWindows` overrides are hooks-only features, not `notify`. (Windows spawn path verified
  in source `command_from_argv`/`background_command`; exact PATHEXT resolution behavior unverified.)

### `tui.notifications` — the TUI-internal alternative (NOT usable by an external program)

From config-advanced.md:

> - `notify` runs an external program (good for webhooks, desktop notifiers, CI hooks).
> - `tui.notifications` is built in to the TUI and can optionally filter by event type (for example,
>   `agent-turn-complete` and **`approval-requested`**).
> - `tui.notification_method` controls how the TUI emits terminal notifications (`auto`, `osc9`, or `bel`).
> - `tui.notification_condition` controls whether TUI notifications fire only when the terminal is
>   `unfocused` or `always`.

So an "approval-requested" event type DOES exist — but only as an in-terminal OSC9/BEL notification
emitted by the TUI itself. It cannot reach an external program.

---

## B. Approval policies and what triggers the interactive prompt

CLI: `--ask-for-approval <policy>` (shorthand `-a`), config `approval_policy`. The four 2025-era
values were `untrusted`, `on-failure`, `on-request`, `never`. **Current state (Oct 2026 docs):**

> Config reference: "Controls when Codex pauses for approval before executing commands. You can also
> use `approval_policy = { granular = { ... } }` to allow or auto-reject specific prompt categories
> while keeping other prompts interactive. **`untrusted` is unsupported, and `on-failure` is
> deprecated**; use `on-request` for interactive runs or `never` for non-interactive runs."

> Approvals doc: "Codex and ChatGPT Work no longer support `approval_policy = "untrusted"`.
> The retired setting can prevent either client from starting."

Current documented values:
- `on-request` — interactive: commands allowed by the sandbox run without approval; out-of-sandbox
  actions (writes outside workspace, network access, untrusted commands) prompt.
- `never` — never asks (non-interactive/CI).
- `{ granular = { sandbox_approval, rules, mcp_elicitations, request_permissions, skill_approval } }`
  — per-category interactive/auto-reject toggles (config reference).
- `untrusted` — retired; `on-failure` — deprecated. (`trust_level = "untrusted"` on a
  `[projects."path"]` entry is the supported stricter-command-approval replacement.)

What triggers a prompt (approvals doc): "Codex asks for approval to edit files outside the workspace
or to run commands that require network access"; "Codex can also elicit approval for app (connector)
tool calls that advertise side effects... Destructive app/MCP tool calls always require approval..."
The interactive prompts in the TUI are: **exec command approval**, **patch/file-change approval**,
MCP tool-call approval, and managed-network approval.

`codex exec` (non-interactive): runs read-only by default; `--full-auto` is **removed** ("Update
scripts to use `--sandbox workspace-write`, or use `--approve-for-me` to route approval requests
through automatic review"). `codex exec --json` turns stdout into a one-way JSONL event stream
(`thread.started`, `turn.started`, `item.*`, `turn.completed`...) — **observation only, no decision
channel**.

App-server v2 wire values (source `app-server-protocol/src/protocol/v2/shared.rs`, serde
`kebab-case` + explicit rename): `UnlessTrusted`→`"untrusted"`, `OnRequest`, `Never`, `Granular`.
BUT the official app-server doc examples show `"approvalPolicy": "never"` and
`"approvalPolicy": "unlessTrusted"` and `allowedApprovalPolicies: ["onRequest", "unlessTrusted"]`.
**The exact wire casing differs between doc examples and the `main` enum — unconfirmed; generate the
schema for your installed version (`codex app-server generate-json-schema`) instead of hardcoding.**

---

## C. `codex app-server` — the official interactive client protocol

**What it is** (app-server.md, verbatim):

> "Codex app-server is the interface Codex uses to power rich clients (for example, the Codex VS Code
> extension). Use it when you want a deep integration inside your own product: authentication,
> conversation history, **approvals**, and streamed agent events."

The command is `codex app-server` (not `experimental`; launched standalone by your process).
It was first present in a tagged release by **rust-v0.44.0 (2025-10-03)** (absent at v0.40.0);
protocol **v2** is the current documented surface.

**Transport** (verbatim):

> "Like MCP, `codex app-server` supports bidirectional communication using JSON-RPC 2.0 messages
> (with the `"jsonrpc":"2.0"` header omitted on the wire).
> - `stdio` (`--listen stdio://`, default): newline-delimited JSON (JSONL).
> - `websocket` (`--listen ws://IP:PORT`, experimental and unsupported): one JSON-RPC message per
>   WebSocket text frame.
> - Unix socket (`--listen unix://`) ... `off` (`--listen off`): don't expose a local transport."

Per-version machine schemas: `codex app-server generate-ts --out ./schemas` and
`codex app-server generate-json-schema --out ./schemas`.

**Handshake**: client → `initialize` (params `{ clientInfo: { name, title, version }, capabilities:
{ experimentalApi?, optOutNotificationMethods? } }`), then notification `initialized`. Requests
before `initialize` are rejected. `experimentalApi: true` gates experimental methods/fields; the
stable approval flow does **not** require it.

**Minimum viable session** (from the doc's own Node.js example, abridged):

```json
{"method":"initialize","id":0,"params":{"clientInfo":{"name":"mimi","title":"MiMi","version":"0.1.0"}}}
{"method":"initialized","params":{}}
{"method":"thread/start","id":1,"params":{"cwd":"C:\\repo","approvalPolicy":"on-request","sandbox":"read-only"}}
{"method":"turn/start","id":2,"params":{"threadId":"thr_123","input":[{"type":"text","text":"..."}]}}
```

**Approval requests are server-initiated JSON-RPC REQUESTS the client must answer.** Quoted method
names and payload fields (app-server.md, "Approvals" — current v2 names):

> "Depending on a user's Codex settings, command execution and file changes may require approval.
> The app-server sends a server-initiated JSON-RPC request to the client, and the client responds
> with a decision payload.
> - Command execution decisions: `accept`, `acceptForSession`, `decline`, `cancel`, or
>   `{ "acceptWithExecpolicyAmendment": { "execpolicy_amendment": ["cmd", "..."] } }`.
> - File change decisions: `accept`, `acceptForSession`, `decline`, `cancel`."

- **`item/commandExecution/requestApproval`** — params include `itemId`, `threadId`, `turnId`,
  optional `reason`, `command`, `cwd`, `commandActions`, `proposedExecpolicyAmendment`,
  `networkApprovalContext`, `availableDecisions` (+ experimental `additionalPermissions`).
  Order: `item/started` (pending commandExecution item) → requestApproval → client decision →
  `serverRequest/resolved` → `item/completed` (`status: completed | failed | declined`).
- **`item/fileChange/requestApproval`** — params: `itemId`, `threadId`, `turnId`, optional `reason`,
  `grantRoot`. Same lifecycle; fileChange decisions as listed above.
- **`item/permissions/requestApproval`** — for the built-in `request_permissions` tool; client
  responds with `{ permissions: <granted subset>, scope: "turn" | "session" }`.
- **`tool/requestUserInput`** (experimental) and **`mcpServer/elicitation/request`** — MCP/app tool
  approval and form/URL elicitation (`action: "accept" | "decline" | "cancel"`).
- `thread/status/changed` reports `{ "status": { "type": "active", "activeFlags": ["waitingOnApproval"] } }`.
- `turn/interrupt` cancels an in-flight turn (ends `status: "interrupted"`).

**Legacy v1 names** (the ones referenced in the task): at tag `rust-v0.48.0` the server→client
request enum serialized as method names **`execCommandApproval`** and **`applyPatchApproval`** with
params (camelCase):

```json
{ "method": "execCommandApproval", "id": 7,
  "params": { "conversationId": "...", "callId": "...", "command": ["bash","-lc","ls"],
              "cwd": "/repo", "reason": null, "parsedCmd": [] } }
```

and response `{ "decision": <ReviewDecision> }` with snake_case values `approved`,
`approved_for_session`, `denied`, `abort`, `approved_execpolicy_amendment`, ... (source:
`codex-rs/protocol/src/protocol.rs` `ReviewDecision`). The v1 types still exist on `main`
(`app-server-protocol/src/protocol/v1.rs`, now with an extra optional `approvalId`), but the current
dispatch is v2-only as far as I could verify (no v1 method strings found in `app-server/src`).
**Whether current builds still accept v1 method names: unconfirmed — do not build against v1.**

### Honest assessment of effort for a minimal MiMi app-server client

- Mechanics: spawn `codex app-server`, read stdout line-by-line (JSONL), write one JSON object per
  line to stdin, correlate `id`s, and answer server requests (requests carry an `id`; reply with
  `{"id": <same>, "result": <decision>}`).
- MVP surface is small: `initialize`, `initialized`, `thread/start`, `turn/start`, plus answering
  `item/commandExecution/requestApproval` + `item/fileChange/requestApproval`, mapping to
  `accept`/`acceptForSession`/`decline`/`cancel`, plus `turn/interrupt` for a stop button, and
  mapping `item/*` notifications to a transcript UI. Realistic scale: **~800–1,500 LOC of C#** plus
  a config screen (model, cwd, sandbox, approvalPolicy).
- Documentation quality: officially documented page with worked examples and per-version generated
  JSON Schema — good. But the surface **moves fast** (v1→v2 in ~a year; methods still being added/
  deprecated per release notes in `codex-rs/app-server/README.md`), some fields are
  `experimentalApi`-gated, and the doc itself marks WebSocket as "experimental and unsupported".
  Stdio JSONL is the stable path. Pin the Codex version, generate schemas per version, and probe
  capabilities at `initialize`. On Windows this works (server reports `platformOs: "windows"`; there
  are `windowsSandbox/*` methods).

---

## D. Other official mechanisms

### 1. Lifecycle hooks (NEW, documented at learn.chatgpt.com/docs/hooks) — the hidden gem

Codex now has a Claude-Code-style hooks framework: scripts (or MCP tool calls) run at lifecycle
points, configured in `~/.codex/hooks.json`, inline `[[hooks.*]]` tables in `config.toml`,
`<repo>/.codex/hooks.json`, plugin bundles, or enterprise `requirements.toml`. Feature flag
`[features] hooks = true` (on by default).

Events: `SessionStart`, `SessionEnd`, `UserPromptSubmit`, `PreToolUse`, **`PermissionRequest`**,
`PostToolUse`, `PreCompact`, `PostCompact`, `SubagentStart`, `SubagentStop`, `Stop`, `Interrupt`.

Each command hook receives **one JSON object on stdin** (shared fields: `session_id`,
`transcript_path`, `cwd`, `hook_event_name`, `model`, plus event-specific fields like `turn_id`,
`tool_name`, `tool_input`) and returns JSON on stdout. Exit 0 = success.

**`PermissionRequest` fires exactly when Codex is about to show an approval prompt and can return a
real allow/deny decision** (hooks.md, verbatim):

> "`PermissionRequest` runs when Codex is about to ask for approval, such as a shell escalation or
> managed-network approval. It can allow the request, deny the request, or decline to decide and let
> the normal approval prompt continue. It doesn't run for commands that don't need approval."

> "To approve the request, return:
> ```json
> {"hookSpecificOutput": {"hookEventName": "PermissionRequest", "decision": {"behavior": "allow"}}}
> ```
> To deny the request, return:
> ```json
> {"hookSpecificOutput": {"hookEventName": "PermissionRequest",
>   "decision": {"behavior": "deny", "message": "Blocked by repository policy."}}}
> ```
> If multiple matching hooks return decisions, any `deny` wins. Otherwise, an `allow` lets the
> request proceed without surfacing the approval prompt. If no matching hook decides, Codex uses the
> normal approval flow."

- `matcher` is a regex on tool name: `Bash`, `apply_patch` (aliases `Edit`/`Write`), MCP names
  (`mcp__server__tool`). Input includes `tool_input.command` and `tool_input.description`
  ("Human-readable approval reason, when Codex has one").
- `PreToolUse` can also deny/rewrite tool calls (`permissionDecision: "deny"|"allow"`,
  `updatedInput`), and `Stop` can force continuation.
- Exact wire schemas per version: `codex-rs/hooks/schema/generated/permission-request.command.{input,output}.schema.json`.
- Caveats: hooks must be **reviewed and trusted** (`/hooks` UI; per-definition hash) unless they come
  from managed config — or `--dangerously-bypass-hook-trust` for one-off automation. Codex blocks
  synchronously up to `timeout` (default 600 s), so a shim that calls MiMi over localhost HTTP/named
  pipe and waits for the user's click is feasible. `async: true` hooks cannot return decisions.
- This gives a third-party GUI REAL interactive approvals **without wrapping the session** — but
  MiMi would not be driving the conversation; the user still runs `codex` (TUI or exec) and MiMi
  surfaces the approval prompts. (Windows: `command_windows`/`commandWindows` override is supported.)

### 2. Removed/other

- **`codex mcp-server` / `codex-mcp-server` binary: REMOVED.** SDK doc: "The `codex mcp-server`
  command and standalone `codex-mcp-server` binary have been removed. Use the Codex app server for
  existing integrations." (Codex can still *consume* MCP servers as tools — not a notification
  channel.)
- **Codex SDK** (`@openai/codex-sdk` TypeScript, `sdk/python`): programmatic threads/turns; the docs
  route approval-handling integrations to app-server. A wrapper, not a separate protocol.
- **OpenTelemetry** (`[otel]` config): security/audit event export — monitoring only, no decisions.
- `notify` and `tui.notifications`: covered in A.

---

## E. Version notes

- Check installed version: `codex --version` (prints e.g. `codex-cli 0.164.0`; standard CLI flag).
  Releases are tagged `rust-vX.Y.Z` at github.com/openai/codex/releases; CHANGELOG.md now defers to
  the releases page.
- **`notify`**: present since at least **rust-v0.20.0 (2025-08-09)** (same one-JSON-argument shape,
  verified in tag source) through current `main`. Implemented as `legacy_notify`; source TODO says
  removal is planned (no date — unconfirmed).
- **`codex app-server`**: first present in a tagged release by **rust-v0.44.0 (2025-10-03)** (not in
  v0.40.0); exact introduction minor version between 0.41–0.44 unconfirmed. Protocol v2 is the
  current documented surface; v1 methods (`execCommandApproval`, `applyPatchApproval`) are the
  2025-era names. Use `codex app-server generate-json-schema` for the exact contract of the
  installed version.
- **Approval policies**: `untrusted` retired (can prevent startup), `on-failure` deprecated,
  `on-request`/`never`/granular current. `--full-auto` removed from `codex exec`.
- **Hooks**: current-generation feature (documented 2026); availability window in older releases
  unconfirmed.

---

## RECOMMENDATION for MiMi (WPF, Windows)

**Verdict on the core question:** `notify` is strictly one-way and **cannot** carry approval
requests — only `agent-turn-complete`. Real interactive approvals require either (a) MiMi acting as
the **app-server client** (officially documented, the same mechanism the VS Code extension uses), or
(b) a **`PermissionRequest` hook shim** (officially documented; MiMi receives real approval requests
and returns allow/deny while the user keeps driving Codex from the terminal).

- **Phase 3 (activity notifications, low effort — do first):** set `notify = ["<abs path to a small
  shim exe/script>"]` in `~/.codex/config.toml`; shim receives the agent-turn-complete JSON as
  argv[1] and forwards it to MiMi (named pipe / localhost HTTP). One-way only; expect eventual
  `notify` removal (source TODO) — keep the shim trivial to migrate to hooks (`Stop` hook) later.
- **Phase 4 (real approvals without embedding a session — moderate):** ship a `PermissionRequest`
  (+ optionally `PreToolUse`) hook via `~/.codex/hooks.json`; the shim reads the stdin JSON, posts it
  to MiMi, MiMi shows the Allow/Deny dialog, shim returns
  `{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}` or
  `{"decision":{"behavior":"deny","message":...}}`. Caveats: one-time hook trust review in `/hooks`,
  synchronous timeout budgeting, and decisions only fire for prompts Codex would have raised.
- **Phase 5 (full interactive embedding — the "real" answer):** spawn `codex app-server` from MiMi
  and implement the JSONL JSON-RPC client: initialize handshake → `thread/start` → `turn/start` →
  answer `item/commandExecution/requestApproval` / `item/fileChange/requestApproval` with
  `accept`/`acceptForSession`/`decline`/`cancel` → render `item/*` events. ~800–1,500 LOC C#; MiMi
  becomes the whole UI (no terminal involved), matching the officially supported integration path.
  Mitigate protocol churn by pinning the Codex version and generating schemas with
  `codex app-server generate-json-schema`.

Phases 3 and 4 are composable and independent of Phase 5; none of the three wraps or proxies the
Codex session itself.
