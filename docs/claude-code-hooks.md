# Claude Code Hooks — PermissionRequest Integration Reference

Researched 2026-10-27 from official sources. All quoted text is verbatim from the docs unless marked UNCONFIRMED.

Sources:
- Hooks reference: https://code.claude.com/docs/en/hooks (fetched as raw markdown at `hooks.md`)
- Hooks guide: https://code.claude.com/docs/en/hooks-guide
- Settings precedence: https://code.claude.com/docs/en/settings
- CLI reference: https://code.claude.com/docs/en/cli-reference
- Local cross-check: `C:\Users\basit\.zcode\cli\plugins\cache\zcode-plugins-official\zcode-guide\0.3.0\skills\diagnosing-hooks\SKILL.md` (documents **ZCode's** hook system, a different product — see the last section)

---

## A. Hook events — complete list

**`PermissionRequest` officially exists.** From the hooks reference event table:

| Event | When it fires (verbatim from docs) |
| :- | :- |
| `SessionStart` | When a session begins or resumes |
| `Setup` | When you start Claude Code with `--init-only`, or with `--init` or `--maintenance` in `-p` mode |
| `UserPromptSubmit` | When a prompt is submitted, before Claude processes it |
| `UserPromptExpansion` | When a user-typed command expands into a prompt, before it reaches Claude |
| `PreToolUse` | Before a tool call executes. Can block it |
| **`PermissionRequest`** | **When a tool call needs a permission decision** |
| `PermissionDenied` | When auto mode denies a tool call, including denials without a classifier verdict |
| `PostToolUse` | After a tool call succeeds |
| `PostToolUseFailure` | After a tool call fails |
| `PostToolBatch` | After a full batch of parallel tool calls resolves, before the next model call |
| `Notification` | When Claude Code sends a notification |
| `MessageDisplay` | While assistant message text is displayed |
| `SubagentStart` | When a subagent is spawned |
| `SubagentStop` | When a subagent finishes |
| `TaskCreated` | When a task is being created via `TaskCreate` |
| `TaskCompleted` | When a task is being marked as completed |
| `Stop` | When Claude finishes responding |
| `StopFailure` | When the turn ends due to an API error |
| `TeammateIdle` | When an agent-team teammate is about to go idle |
| `InstructionsLoaded` | When a CLAUDE.md or `.claude/rules/*.md` file is loaded into context |
| `ConfigChange` | When a configuration file changes during a session |
| `CwdChanged` | When the working directory changes |
| `DirectoryAdded` | When a working directory is added mid-session |
| `FileChanged` | When a watched file changes on disk |
| `WorktreeCreate` | When a worktree is being created |
| `WorktreeRemove` | When a worktree is being removed |
| `PreCompact` | Before context compaction |
| `PostCompact` | After context compaction completes |
| `PreModelSwitch` | Before Claude Code applies a model switch |
| `PostModelSwitch` | After the session's model changes |
| `Elicitation` | When an MCP server requests user input during a tool call |
| `ElicitationResult` | After a user responds to an MCP elicitation |
| `SessionEnd` | When a session terminates |

### PermissionRequest semantics (verbatim quotes)

> "Runs when Claude Code is about to ask you for permission to use a tool. In sessions that can't show a prompt, such as background subagents in non-interactive mode, Claude Code still runs these hooks, and if no hook returns a decision, it denies the tool call. For a call that reaches a `--permission-prompt-tool` or the Agent SDK's `canUseTool` callback, the hooks run alongside your host, and whichever decides first applies."

> "PreToolUse hooks run before every tool call, whether or not it needs permission. PermissionRequest hooks run only when Claude Code is about to ask you for permission, or when it would otherwise auto-deny a call that can't prompt."

> "Claude Code doesn't run PermissionRequest hooks for a sandboxed command's network request." (use the `permission_prompt` Notification type for that)

> "Matches on tool name, same values as PreToolUse." Supported hook types: "`PermissionRequest` supports `command`, `http`, `mcp_tool`, and `prompt` hooks but not `agent` hooks."

---

## B. Stdin JSON schema

### Common fields (all events, verbatim table)

| Field | Description |
| :- | :- |
| `session_id` | Current session identifier |
| `prompt_id` | UUID identifying the user prompt currently being processed. Absent until the first user input |
| `transcript_path` | Path to conversation JSON. "The transcript file is written asynchronously and may lag the in-memory conversation" |
| `cwd` | Current working directory when the hook is invoked |
| `scratchpad_dir` | Session scratchpad directory. "Requires Claude Code v2.1.257 or later". Absent when unavailable |
| `permission_mode` | `"default"`, `"plan"`, `"acceptEdits"`, `"auto"`, `"dontAsk"`, or `"bypassPermissions"`. "The mode labeled Manual arrives as `"default"`, never as `"manual"`" |
| `effort` | Object with a `level` field (`"low"`, `"medium"`, `"high"`, `"xhigh"`, `"max"`); also available as `$CLAUDE_EFFORT` |
| `hook_event_name` | Name of the event that fired |
| `agent_id`, `agent_type` | Which subagent/teammate the hook fired in (subagent contexts) |

### PermissionRequest-specific fields (verbatim)

> "PermissionRequest hooks receive `tool_name` and `tool_input` fields like PreToolUse hooks, but **without `tool_use_id`**. For an MCP tool, they also receive the `mcp_server` object. An optional `permission_suggestions` array contains the permission updates Claude Code suggests for this request, such as adding an allow rule or changing the permission mode."

### Full official input example (verbatim from the reference)

```json
{
  "session_id": "abc123",
  "transcript_path": "/Users/.../.claude/projects/.../00893aaf-19fa-41d2-8238-13269b9b3ca0.jsonl",
  "cwd": "/Users/...",
  "permission_mode": "default",
  "hook_event_name": "PermissionRequest",
  "tool_name": "Bash",
  "tool_input": {
    "command": "rm -rf node_modules",
    "description": "Remove node_modules directory"
  },
  "permission_suggestions": [
    {
      "type": "addRules",
      "rules": [{ "toolName": "Bash", "ruleContent": "rm -rf node_modules" }],
      "behavior": "allow",
      "destination": "localSettings"
    }
  ]
}
```

Note: unlike the PreToolUse example (which also carries `prompt_id`, `scratchpad_dir`, `effort`, `tool_use_id`), the PermissionRequest example omits `tool_use_id`. Fields are additive per version; treat `prompt_id`/`scratchpad_dir`/`effort` as possibly-present optional fields (UNCONFIRMED whether they always appear on PermissionRequest; the reference shows them on PreToolUse).

---

## C. Output: the decision JSON

Decision control fields (verbatim from the reference's `PermissionRequest decision control` table):

> "`PermissionRequest` hooks can allow or deny permission requests. In addition to the JSON output fields available to all hooks, your hook script can return a `decision` object with these event-specific fields:"

| Field | Description (verbatim) |
| :- | :- |
| `behavior` | "`"allow"` grants the permission, `"deny"` denies it. Deny and ask rules are still evaluated, so a hook returning `"allow"` doesn't override a matching deny rule" |
| `updatedInput` | "For `"allow"` only: modifies the tool's input parameters before execution. **Replaces the entire input object, so include unchanged fields alongside modified ones.** The modified input is re-evaluated against deny and ask rules" |
| `updatedPermissions` | "For `"allow"` only: array of permission update entries to apply, such as adding an allow rule or changing the session permission mode" |
| `message` | "For `"deny"` only: tells Claude why the permission was denied" |
| `interrupt` | "For `"deny"` only: if `true`, stops Claude" |

**Is `updatedInput` required for allow?** No. The official guide's auto-approve example returns `"behavior": "allow"` with no `updatedInput` and that is sufficient. `updatedInput` is optional and only used when you want to rewrite the tool's arguments.

### ALLOW — official example (verbatim, hooks reference)

```json
{
  "hookSpecificOutput": {
    "hookEventName": "PermissionRequest",
    "decision": {
      "behavior": "allow",
      "updatedInput": {
        "command": "npm run lint"
      }
    }
  }
}
```

### ALLOW without modifying input — official example (verbatim, hooks guide, `ExitPlanMode` auto-approve)

```json
{
  "hookSpecificOutput": {
    "hookEventName": "PermissionRequest",
    "decision": {
      "behavior": "allow"
    }
  }
}
```

### ALLOW with a permission-mode change — official example (verbatim, hooks guide)

```json
{
  "hookSpecificOutput": {
    "hookEventName": "PermissionRequest",
    "decision": {
      "behavior": "allow",
      "updatedPermissions": [
        { "type": "setMode", "mode": "acceptEdits", "destination": "session" }
      ]
    }
  }
}
```

`updatedPermissions` entries use `type`: `addRules` | `replaceRules` | `removeRules` | `setMode` | `addDirectories` | `removeDirectories`, each with a `destination` of `session` (in-memory only) | `localSettings` (`.claude/settings.local.json`) | `projectSettings` (`.claude/settings.json`) | `userSettings` (`~/.claude/settings.json`). A hook can echo back one of the `permission_suggestions` it received.

### DENY — composed from the documented field table

The reference documents the deny fields (`behavior: "deny"`, `message`, `interrupt`) in the table above but shows no single full deny JSON for this event. Composed strictly from those documented fields (structure verified field-by-field; the exact combination below is UNCONFIRMED as a copy-paste example):

```json
{
  "hookSpecificOutput": {
    "hookEventName": "PermissionRequest",
    "decision": {
      "behavior": "deny",
      "message": "Blocked by policy: rm -rf commands are not allowed"
    }
  }
}
```

- `message` "tells Claude why the permission was denied" (Claude sees it and can adjust).
- Add `"interrupt": true` to additionally stop Claude entirely.

### How decisions surface

- Allow: "The transcript shows **'Allowed by PermissionRequest hook'** where the dialog would have appeared." (hooks guide)
- Exit code semantics for this event (verbatim): "Exit code 2 isn't honored for this event and the permission flow proceeds unchanged. Deny through the decision object instead." Also: "A hook that exits 2 without a `decision` object leaves the permission flow unchanged, and its stderr is discarded. To grant or deny the request, return the `decision` object."
- Exit 0 with no JSON decision: the hook "has no decision to report, so the tool call continues through the normal permission flow" (the interactive prompt still appears). "The hook can deny the call, but staying silent doesn't approve it." (that quote is about PreToolUse; the same exit-0-means-no-decision rule is the general JSON-output rule).
- Non-interactive (`claude -p`): PermissionRequest hooks still run outside `dontAsk` mode, and "a call that no hook decides and nothing else can answer is denied" (hooks guide, Limitations).
- `onFailure: "block"` (requires Claude Code v2.1.295+): "a failure does what exit code 2 does on that event, **except on `PermissionRequest`, where it denies the request**." So a failed/timed-out PermissionRequest hook with `onFailure: "block"` DENIES.
- Windows caveat (verbatim note): "On Windows without Git Bash, a shell-form `PreToolUse` or `PermissionRequest` command hook that fails with `onFailure` unset can instead block the tool call."

---

## D. Settings configuration

### Structure

Three nesting levels: **hook event** → **matcher group** → **hook handlers array**. Handler fields for `type: "command"` (verbatim, `Common fields` table): `type` (required: `"command"`, `"http"`, `"mcp_tool`, `"prompt"`, `"agent"`), `if` (permission-rule filter, only on tool events), `timeout` (seconds), `statusMessage`, `once` (skill frontmatter only), `async` / `asyncRewake` (command hooks only), `onFailure` (`"continue"` default / `"block"`, v2.1.295+), plus `command` and optional `args`.

Matcher rules for tool events: `"*"`, `""`, or omitted = match all; names containing only letters/digits/`_`/`-`/spaces/`,`/`|` = exact string or `|`/`,`-separated list (`"Bash"`, `"Edit|Write"`); anything else = unanchored JavaScript regex (`"mcp__.*"`). Matchers are case-sensitive. Matching hooks of one event "run in parallel".

### Config for intercepting ALL tools (user-global)

`~/.claude/settings.json` scope per the official table: "**All your projects**". To match every tool, omit the `matcher` (or use `"*"`):

```json
{
  "hooks": {
    "PermissionRequest": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "${CLAUDE_PROJECT_DIR}/.claude/hooks/permission-gate.py",
            "timeout": 60
          }
        ]
      }
    ]
  }
}
```

(Structure follows the official `PostToolUse` lint-check example, verbatim pattern:)

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write",
        "hooks": [
          {
            "type": "command",
            "command": "/path/to/lint-check.sh"
          }
        ]
      }
    ]
  }
}
```

The guide's own PermissionRequest example (auto-approve `ExitPlanMode`) is registered in `~/.claude/settings.json` exactly like:

```json
{
  "hooks": {
    "PermissionRequest": [
      {
        "matcher": "ExitPlanMode",
        "hooks": [
          {
            "type": "command",
            "command": "echo '{\"hookSpecificOutput\": {\"hookEventName\": \"PermissionRequest\", \"decision\": {\"behavior\": \"allow\"}}}'"
          }
        ]
      }
    ]
  }
}
```

Warning from the guide (verbatim): "Keep the matcher as narrow as possible. Matching on `.*` or leaving the matcher empty would auto-approve every tool permission prompt, including file writes and shell commands." (This is guidance for auto-approve scripts; a policy gate that denies does not need a narrow matcher.)

### Hook locations and scope (verbatim table)

| Location | Scope |
| :- | :- |
| `~/.claude/settings.json` | All your projects (this is the USER-global file) |
| `.claude/settings.json` | Single project, shareable/committable |
| `.claude/settings.local.json` | Single project, gitignored |
| Managed policy settings | Organization-wide |
| Plugin `hooks/hooks.json` | When plugin is enabled |
| Skill / subagent frontmatter | Session / subagent scoped |

### Settings precedence (from settings.md, highest first, verbatim)

"Settings precedence, highest first: managed settings, command line, project local, shared project, user."

1. Managed settings (`managed-settings.json`, MDM, server-managed)
2. Command line (`--settings <file-or-json>`)
3. Project local (`.claude/settings.local.json`)
4. Shared project (`.claude/settings.json`)
5. User (`~/.claude/settings.json`)

For hooks specifically: "Hook entries **merge across settings levels** rather than replacing each other: user, project, and local settings add their own hooks without removing managed ones, and the `disableAllHooks` setting can't disable managed hooks from outside managed settings." "If you define the same handler in more than one settings file, it runs once."

---

## E. Timeout semantics

- Unit: **seconds** (verbatim): "`timeout` | no | Seconds before canceling."
- Defaults (verbatim): "Defaults: 600 for `command`, `http`, and `mcp_tool`; 30 for `prompt`; 60 for `agent`." (600 s = 10 minutes, confirmed in the guide: "`command`, `http`, `mcp_tool`: 10 minutes.")
- Event-specific default reductions: lowered to 30 s on `UserPromptSubmit`, `PreModelSwitch`, `PostModelSwitch`; to 10 s on `MessageDisplay`; `SessionEnd` hooks share a 1.5-second budget (raisable to 60 s via per-hook `timeout` or `CLAUDE_CODE_SESSIONEND_HOOKS_TIMEOUT_MS`). None of these reductions apply to PermissionRequest; its command-hook default is 600 s.
- On timeout (verbatim): "Claude Code cancels a `command`, `http`, or `mcp_tool` hook that reaches its `timeout`, **discarding the hook's output, so on most events a timed-out hook renders no decision**."
- Consequence for PermissionRequest: a timed-out hook yields no decision, so the normal permission flow proceeds (the prompt is shown / the non-interactive auto-deny applies). The tool is NOT auto-approved and NOT auto-denied by the timeout itself.
- Fail-closed option: set `"onFailure": "block"` — then a timed-out or failed hook "denies the request" on PermissionRequest (v2.1.295+).
- Not enforced on `async: true` hooks ("Claude Code doesn't enforce it on a command hook you run with `async: true`") — but async hooks can't return decisions at all, so async is useless for permission gating.
- Maximum sane value: no maximum is documented; the default maximum-ish value used by Claude Code itself is 600 s. UNCONFIRMED whether arbitrary larger values are accepted; keep PermissionRequest hooks to a few seconds in practice since the user is waiting on the permission dialog.

---

## F. Blocking behavior, stdout/stderr, environment variables

### Synchronous execution (verbatim)

> "By default, hooks block Claude's execution until they complete."

> "When an event fires, its matching hooks run in parallel."

Hooks are per-event; for tool events, `PermissionRequest` runs per tool-call that needs a permission decision, and the agentic loop waits for it (or for the permission dialog). `"async": true` (command hooks only) runs in the background instead, but then "response fields like `decision`, `permissionDecision`, and `continue` have no effect" — so permission hooks must be synchronous.

### stdout / stderr

- stdout is parsed as JSON **only if** it is a single JSON object with nothing around it but whitespace; any other stdout is treated as plain text / parse failure. "Print nothing but the JSON object to stdout."
- "For most events, Claude Code writes stdout to the debug log and doesn't show it in the transcript." (Exceptions where plain-text stdout becomes context: `UserPromptSubmit`, `UserPromptExpansion`, `SessionStart`, `PostModelSwitch` — not PermissionRequest.)
- Exit 0: "Claude never sees stderr from a hook that exits 0."
- Exit 2 on PermissionRequest: not honored; "its stderr is discarded."
- Other exit codes: non-blocking error; transcript shows `<hook name> hook error ... Failed with non-blocking status code: <first line of stderr>`. Warning (verbatim): "Exit code 1 without valid JSON output is a non-blocking error ... If your hook is meant to enforce a policy, use `exit 2`." (On PermissionRequest specifically, policy enforcement must use the deny decision, not exit 2.)
- String caps: `additionalContext`, `systemMessage`, plain stdout are capped at 10,000 chars (overflow saved to a file with a 2,000-char preview).

### Environment (verbatim highlights)

- `${CLAUDE_PROJECT_DIR}`: "the project root where the session started" — usable both as a path placeholder in the `command` string and "exported as the environment variables `CLAUDE_PROJECT_DIR`, `CLAUDE_PLUGIN_ROOT`, and `CLAUDE_PLUGIN_DATA` on the spawned process." In worktrees it "stays put"; the `cwd` input field follows the worktree/`cd`.
- `${CLAUDE_PLUGIN_ROOT}` / `${CLAUDE_PLUGIN_DATA}`: plugin install dir / plugin persistent data dir.
- `$CLAUDE_CODE_REMOTE` = `"true"` in remote web environments; `$CLAUDE_CODE_BRIDGE_SESSION_ID` during Remote Control (v2.1.199+); `$CLAUDE_EFFORT` = effort level. "There is no `$CLAUDE_MODEL` environment variable."
- "A hook process inherits the parent environment, apart from the `OTEL_*` exporter variables that Claude Code removes from every subprocess it spawns" (and, with `CLAUDE_CODE_SUBPROCESS_ENV_SCRUB=1`, additional scrubbing).
- Hooks run "in the current directory with Claude Code's environment"; command hooks run without a controlling terminal (no `/dev/tty`; use the `terminalSequence` output field for terminal effects). Shell form runs through `sh -c` (Git Bash on Windows) unless `shell: "powershell"`; exec form (`args` set) spawns directly with no shell.

---

## G. Version notes

- **Minimum version for `PermissionRequest`: no minimum is documented** in the current official reference. The event is part of the stable hooks system and is not flagged with any "requires vX" note, unlike newer additions (e.g. `onFailure` v2.1.295, `scratchpad_dir` v2.1.257, `mcp_server` v2.1.274, `classifierContext` v2.1.236). UNCONFIRMED: exact version that introduced it (predates the current docs' version annotation practice); treat it as available in any current install.
- Check installed version: `claude --version` (CLI reference, verbatim: "`--version`, `-v` | Output the version number | `claude -v`").
- Version-gated features relevant to permission hooks: `onFailure: "block"` requires **v2.1.295+**; `mcp_server` input field requires **v2.1.274+**; `scratchpad_dir` requires **v2.1.257+**.
- Update: `claude update`; install a pinned version: `claude install [version|stable|latest]`.

---

## Cross-check: local ZCode skill file vs. official Claude Code docs

The local file `C:\Users\basit\.zcode\cli\plugins\cache\zcode-plugins-official\zcode-guide\0.3.0\skills\diagnosing-hooks\SKILL.md` documents **ZCode's** hook runner (this CLI environment), not Claude Code's. It agrees that a `PermissionRequest` event exists, but the systems differ materially:

| Aspect | Official Claude Code | ZCode (local skill) |
| :- | :- | :- |
| Events | 30+ (see section A) | Exactly 7: `SessionStart`, `UserPromptSubmit`, `PreToolUse`, `PermissionRequest`, `PostToolUse`, `PostToolUseFailure`, `Stop` |
| `PermissionRequest` deny via exit 2 | NOT honored — "Deny through the `decision` object instead" | Honored: "`2` blocks (a deny for `PreToolUse`/`PermissionRequest`)" |
| Config location | `~/.claude/settings.json`, `.claude/settings.json`, `.claude/settings.local.json` | `~/.zcode/cli/config.json` under `hooks.events.<Event>` or plugin `hooks/hooks.json` |
| Enablement | Always on (`disableAllHooks` to opt out) | Config-file hooks disabled by default; need `hooks.enabled: true` (plugin hooks auto-enable the runner) |
| Handler types | `command`, `http`, `mcp_tool`, `prompt`, `agent` | `command` (shell string) and `process` (argv, no shell) |
| Timeout units | `timeout` in seconds | `timeout` in seconds for `command`; `timeoutMs` in milliseconds for `process` |
| Matcher | Case-sensitive; exact list or unanchored JS regex | Case-sensitive regex always |
| Output schema | Loose JSON output w/ universal fields (`continue`, `systemMessage`, ...) | Strict schema — "any extra key fails validation" |

**Do not mix the two systems.** A hook intended for Claude Code goes in `~/.claude/settings.json` and must decide via `hookSpecificOutput.decision`, never exit 2. A hook intended for this ZCode environment goes in `~/.zcode/cli/config.json` with `hooks.enabled: true` (or a plugin's `hooks/hooks.json`), and there exit 2 works as a deny. The stdin input (session_id / transcript_path / cwd / hook_event_name / tool_name / tool_input) and the allow-decision JSON shape (`hookSpecificOutput.hookEventName: "PermissionRequest"` + `decision.behavior: "allow"`) are the same convention in both.

---

## Quick recipe: deny-by-default gate for all tools (Claude Code)

`~/.claude/settings.json`:

```json
{
  "hooks": {
    "PermissionRequest": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "python ${CLAUDE_PROJECT_DIR}/.claude/hooks/gate.py",
            "timeout": 30
          }
        ]
      }
    ]
  }
}
```

`gate.py` reads the section-B stdin JSON from stdin and prints, per section C:
- Allow: `{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}` (optionally with `updatedInput` — it replaces the whole input object — or `updatedPermissions`)
- Deny: `{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"deny","message":"why"}}}` (+ `"interrupt": true` to also stop Claude)
- Silence (exit 0, no output): normal permission flow (interactive prompt) proceeds
- Fail closed: add `"onFailure": "block"` to the handler so a crash/timeout denies the request (v2.1.295+)
- Never rely on exit 2 for this event; it is ignored
