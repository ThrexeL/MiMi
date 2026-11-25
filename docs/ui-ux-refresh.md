# MiMi UI/UX refresh — 2026-11-25

## Scope

Basit requested a focused UI/UX and feature pass: reliable idle dismissal, better Agent
activity layout and color consistency, a modern Settings window, and additional quality-of-life
features using Hazel's best judgement.

## Interaction fixes

- Hover state now follows the **pill's actual bounds**, not the full transparent overlay canvas.
  Moving away from the pill starts the normal leave grace even while still inside the window.
- Hover-expanded panels have a configurable inactivity cap, **30 seconds by default**. Pointer
  movement resets it; `0` disables only the cap, leaving normal pointer-leave dismissal intact.
- Agent activity has an explicit close button and uses the same dismissal path as other hover panels.
- Repeated agent clicks keep the activity panel open instead of toggling it closed.

## Agent activity redesign

- Fixed 560 × 360 DIP panel with a stable header, live summary counts, and scrolling session list.
- Uniform card spacing, padding, typography, avatars, status columns, age, and pending badges.
- Full wrapping for status/detail/timeline text instead of ellipsis truncation.
- Consistent gold avatar treatment; status color remains meaningful and confined to the live summary.
- Every session with an ID exposes a safe **Copy session reference** action.
- Empty state explains how activity appears.

## Settings redesign

- Replaced the old left-tab form with a dark/gold card-based layout and top navigation.
- Added a live mascot preview with Calm, Happy, Curious, and Sleepy expressions.
- Added Quick Access page visibility controls; hidden pages are removed and the grid reflows.
- Added the hover idle-timeout control.
- Added an activity demo, local log shortcut, version display, and restart action.
- Preserved every existing setting, immediate-save behavior, slider flush behavior, provider
  diagnostics, accessibility names, and failure feedback.

## Additional behavior

- `MiMi.exe --settings` opens Settings directly.
- Settings can restart MiMi after module changes using the same safe hotkey/mutex release path as
  the tray restart action.

## Safety and contracts

Approval fail-closed behavior, display-only bridge payloads, reference-only file shelf,
non-activating island behavior, clipboard privacy, and Codex sandbox behavior are unchanged.

## Verification

- Full test suite: **411 passed, 0 failed, 0 skipped**.
- Debug and Release builds: **0 warnings, 0 errors**.
- `MiMi.exe --selftest`: **ALL PASSED**.
- Offscreen renders reviewed for all five Settings categories and the new Agent activity cards.
- Live desktop check: the redesigned Agent activity panel rendered with consistent cards, complete
  status text, visible copy/close controls, and a stable scroll area.
- Live Settings check: all content stayed inside the client area, the category panes used the full
  width, the scrollbar worked, and the Done footer remained visible. The initial overflow found in
  the live window was fixed by constraining the tab content height and fitting category widths.

Visual evidence:

- `C:\Users\basit\MiMi\artifacts\ui-ux-refresh\settings-general.png`
- `C:\Users\basit\MiMi\artifacts\ui-ux-refresh\settings-appearance.png`
- `C:\Users\basit\MiMi\artifacts\ui-ux-refresh\settings-notifications.png`
- `C:\Users\basit\MiMi\artifacts\ui-ux-refresh\settings-integrations.png`
- `C:\Users\basit\MiMi\artifacts\ui-ux-refresh\settings-system.png`
- `C:\Users\basit\MiMi\artifacts\ui-ux-refresh\island-agent-activity-cards.png`
- `C:\Users\basit\MiMi\artifacts\ui-ux-refresh\live-quick-access.png`
- `C:\Users\basit\MiMi\artifacts\ui-ux-refresh\live-settings-recovered.png`
- `C:\Users\basit\MiMi\artifacts\ui-ux-refresh\live-settings-appearance-fitted.png`
