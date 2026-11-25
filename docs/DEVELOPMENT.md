# MiMi maintainer guide

**Current state: 2026-11-26.** This is the first document to read before changing MiMi.
`HANDOFF.md` and `PROJECT_STATE.md` remain useful historical context but contain old
branch, test-count, and sizing notes. Their current-status banners point here.

## Snapshot

| Item | Current value |
|---|---|
| Product | Windows 11 Dynamic Island overlay, C# / .NET 10 / WPF, no Electron or WebView |
| Working folder | `C:\Users\basit\MiMi` |
| Solution | `MiMi.slnx` |
| Main active branch | `fix/mimi-agent-gold` |
| Verified HEAD before cleanup | `28a25aa` |
| Test suite | 424 passed, 0 failed, 0 skipped |
| Debug / Release builds | 0 warnings, 0 errors |
| Release self-test | `SELFTEST: ALL PASSED` |

## Prerequisites and commands

The machine-wide `dotnet` has no SDK. Use the user-local SDK. The running app locks its
output, so stop it before building and relaunch it afterwards.

```powershell
cd C:\Users\basit\MiMi
$env:DOTNET_ROOT = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_NOLOGO = '1'

Get-Process -Name MiMi -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build MiMi.slnx -c Debug --nologo
dotnet test src/MiMi.Tests/MiMi.Tests.csproj -c Debug --nologo
dotnet build MiMi.slnx -c Release --nologo
& 'src\MiMi.App\bin\Release\net10.0-windows10.0.26100.0\MiMi.exe' --selftest
```

Portable release build:

```powershell
powershell -NoProfile -File build-release.ps1
```

Target frameworks: `net10.0-windows` for plain libraries; `net10.0-windows10.0.26100.0`
for `MiMi.App`, `MiMi.Media`, and `MiMi.Tests` because GSMTC WinRT projections require it.
The trailing `.0` is part of the target framework, not a package version.

## Source map

- `src/MiMi.Core` — settings, config store, state priority, visibility transitions,
  productivity stores, file shelf references, drag/pin policies, and the approval TTL model.
- `src/MiMi.Agents` — normalized agent events, approval store, lifecycle sessions,
  notifications, and provider diagnostics.
- `src/MiMi.Bridge` — authenticated named-pipe bridge; approval responses correlate by
  `approval_request_id` and return only to the originating stream.
- `src/MiMi.App` — WPF shell, island UI, mascot, HUDs, tray, embedded Codex client, and AI:
  - `AI/AiProviderService.cs` detects Codex, Claude, OpenCode, Grok, and Custom AI on PATH
    or in per-user settings.
  - `AI/AiApiClient.cs` sends text-only OpenAI-compatible chat-completions requests.
  - `AI/AiApiKeyProtector.cs` encrypts API keys with current-user DPAPI. Plaintext keys
    are never written to `settings.json`, logged, or shown after saving.
  - `UI/ApiConversationWindow.xaml(.cs)` provides a local chat surface for API providers;
    it has no file, shell, tool, or approval capability.
  - `Interop/MiMiIcon.cs` is the shared seven-resolution icon source for the executable,
    taskbar windows, and tray; windows deliberately select the 64 px frame.
- `src/MiMi.Tests` — 424 regression tests, including WPF layout probes and local fake HTTP
  transport tests. No test requires a paid API call or live provider credential.
- `tools/generate-mimi-icon.py` — regenerates `src/MiMi.App/Assets/mimi.ico` using only the
  Python standard library. Keep runtime artwork and the executable icon in sync.

## Non-negotiable behavior

1. Approvals fail closed: no auto-approve; expiry, timeout, disconnect, parse failure, or
   bridge failure denies. Never auto-approve on startup or restart.
2. The approval card must show enough of the real action for an informed decision. Commands
   in cards are display-only and are never executed or opened by MiMi.
3. Approval cards auto-height. Measure after `Host.Content` and `Host.ApplyTemplate()`, then
   re-measure after the template and bindings load. The 236 DIP fixed height clipped action
   buttons; `IslandWindow.MeasureContentHeight` is the regression guard.
4. The file shelf holds references only. Removing a shelf entry never touches the original.
5. No keyboard interception. WPF objects are touched only on the Dispatcher thread.
6. External AI CLIs and custom commands run with the user's normal permissions; they are not
   the embedded Codex read-only sandbox. API chats are text-only.
7. Preserve the visual baseline: round white eyes, gold bottom glow, dark surface, smooth
   reversible transitions, and no focus stealing.

## Verification practice

- Build both configurations, run the whole test suite, and run the Release self-test
  before claiming a change is verified.
- Set `MIMI_QA_DIR` to an ignored folder such as `artifacts/mimi-final-qa` to render WPF
  panels offscreen. Always inspect screenshots; a passing layout unit test alone is not a
  visual claim.
- Current evidence locations:
  - `artifacts/mimi-final-qa/approval-buttons-fit.png` — approval actions inside the pill.
  - `artifacts/mimi-final-qa/api-settings-panel.png` — API settings panel.
  - `artifacts/mimi-taskbar-icon-crop.png` — live taskbar icon.
- Never log prompts, API keys, command arguments used as secrets, or sensitive tool output.
- Live external provider calls require the user's own key or signed-in session. Do not probe
  deployed services or spend paid quota for verification.

## Repository hygiene

Tracked content is source, tests, release scripts, and maintained documentation under
`docs/`. Generated output and local notes stay out of GitHub via `.gitignore`:

- `bin/`, `obj/`, `dist/`, `TestResults/`, `artifacts/`, and `coverage/` are generated.
- `HANDOFF.md` and `PROJECT_STATE.md` are local-only running notes.
- `FIXES_SUMMARY.md`, `FIX_ISSUES.md`, and `WORKSPACE_EXAMINATION.md` are superseded
  2026-10-30 notes, preserved on disk with a historical banner and ignored by Git.
- `scratch-relay/` is a local harness for testing `ImpressiveBotRelay.Parse`; its `bin/`
  and `obj/` were already ignored and the whole directory is now ignored.
- `.env`, `.env.*` (except `.env.example`), `*.pfx`, `*.p12`, and `*.snk` are ignored so
  local credentials and signing keys cannot be committed accidentally.

Check before committing:

```powershell
git status --short --branch
git diff --check
git check-ignore -v artifacts dist TestResults HANDOFF.md scratch-relay
```

Commit only verified states and do not push without explicit approval. Preserve unrelated
untracked files; the stale notes and scratch harness are intentionally retained locally.

## Security review tooling

Use the enabled **`security-audit`** skill. It is Cloudflare's official workflow: its
companion notes cite pinned commit
`c1c8a8c1471069fb0e188eeaff69b8e8db6564a8`, and its full installed package (all companion
guides, `report-schema.json`, and the two zero-dependency validators) is under the bot's
skills folder in `cloudflare-security-audit/skills/security-audit`.

On 2026-11-26 an additional native installation named `cloudflare-security-audit-official`
was compared against it. The workflow bodies were identical, so the duplicate was removed
and only `security-audit` remains enabled. A full six-phase audit runs only on an explicit
audit or pen-test request; ordinary security questions use guidance mode. Source inspection
is read-only, and target-controlled execution needs a real sandbox.

## Remaining manual acceptance

These still need a human at the machine; do not claim them from unit tests:

1. Real Explorer drag-in and drag-out on the file shelf.
2. Pomodoro, water, and to-do flows across hide/show and reminders.
3. Sleepy proximity wake after quiet time with no media.
4. Lyrics against a live playing track.
5. Real Codex/Claude approval end-to-end, including timeout deny.
6. Win+D, Ctrl+Alt+M rapid toggles, and fullscreen suppression.
7. A live API chat with the user's own key and chosen model.
8. Claude and Grok CLI launch paths on a PC where those commands are installed.
