# Portable AI assistants — 2026-11-25

## What changed

- Quick Access now has a single **AI** tile instead of a Codex-only tile.
- The AI picker shows three providers:
  - **Codex** — MiMi's embedded read-only conversation with one-shot approvals.
  - **Claude** — Claude Code opened in its own foreground console window.
  - **Custom AI** — any user-configured executable, script, or PATH command.
- Codex and Claude are discovered on `PATH`; MiMi contains no hard-coded user paths.
- Custom name, command, arguments, and default provider are stored in per-user MiMi settings.
- Legacy Quick Access hidden-page value `codex` migrates to `ai`.
- Settings, Codex, and the AI picker are taskbar-visible and are brought to the foreground when opened.
- All nine Quick Access tiles now use the same spacing, sizing, colored surface, border, and typography.

## Configuration

Open **Settings → Integrations → AI assistants**:

1. Choose the default assistant.
2. Optionally set a custom display name.
3. Browse to a custom executable/script or enter a PATH command.
4. Add optional arguments.
5. Refresh availability.

The AI picker disables providers that are not currently available and explains what to fix.

## Safety

- MiMi never stores or logs custom command arguments as secrets.
- External AIs run with the current user's permissions in the selected MiMi workspace.
- Codex keeps its existing read-only sandbox, one-shot approvals, and fail-closed approval path.
- Custom commands are explicitly user-configured; MiMi does not download or execute remote content.

## Verification

- Full test suite: **417 passed, 0 failed, 0 skipped**.
- Debug and Release builds: **0 warnings, 0 errors**.
- `MiMi.exe --selftest`: **ALL PASSED**.
- Live desktop check at approximately **18:08 IST**: Settings and the AI picker were the
  foreground windows, Codex opened in front, and all three windows reported taskbar visibility.
- Codex was detected at the user-local PATH location; Claude and Custom correctly reported
  unavailable until configured.
- Regression coverage includes PATH/absolute command detection, custom-provider settings,
  legacy hidden-page migration, taskbar visibility, offscreen AI picker loading, and uniform
  Quick Access tile styling.

Visual evidence:

- `C:\Users\basit\MiMi\artifacts\ai-quick-refresh\island-pill-quick-access.png`
- `C:\Users\basit\MiMi\artifacts\ai-quick-refresh\live-quick-ai.png`
- `C:\Users\basit\MiMi\artifacts\ai-quick-refresh\live-ai-picker.png`
- `C:\Users\basit\MiMi\artifacts\ai-quick-refresh\live-codex.png`
- `C:\Users\basit\MiMi\artifacts\ai-quick-refresh\live-settings-ai.png`
