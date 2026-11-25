# Nusa-inspired island refresh — 2026-11-25

## Reference and intent

Basit asked for MiMi to keep its existing capabilities while adopting Nusa's cute,
living-notch feel and useful everyday features.

Reference inspected on 2026-11-25:

- Public page: <https://rama2555.github.io/nusa/>
- Public page source: <https://raw.githubusercontent.com/rama2555/nusa/main/index.html>
- Attached reference image: `C:\Users\basit\.impressivebot\attachments\53bf2f79-bb31-4c3f-82ae-da55b407ab2f.png`

This is an independent MiMi implementation. No Nusa application code or assets were copied.

## Delivered

### Look and interaction

- Deep near-black surface with a warm gold bottom aurora.
- Taller blue-white glowing eyes and a short eye-wave when the pointer returns or MiMi wakes.
- Hover-open Quick Access preview; moving away collapses it, while clicking the face pins it.
- Larger rounded cards, colored feature tiles, live greeting/clock/date, and a Settings gear.
- Reduced-motion and mascot/glow settings remain respected.

### Everyday features

- **Local Widgets:** week strip, open/due tasks, next task, focus sessions, water goal,
  battery/charging, local network type, moon phase, and local/UTC clocks.
- **Clipboard history:** opt-in, text-only, bounded to 20 entries / 4,000 characters each,
  kept in memory until MiMi exits, never written to disk or logs, with copy/remove/clear and drag-out.
- **Brightness panel:** real `WmiMonitorBrightnessMethods.WmiSetBrightness` control for a supported
  active laptop backlight. Unsupported desktops/displays show an honest unavailable state.
- **To-do due times:** optional date and 24-hour time in the task editor; due reminders can be
  snoozed for ten minutes and re-arm once.
- **Click-to-seek lyrics:** the current timed lyric seeks to its timestamp minus the configured offset.
- **Quick Access:** Focus, Water, To-do, Shelf, Volume, Clipboard, Widgets, Brightness, Codex,
  Settings, and Back.

Existing approval contracts, display-only bridge payloads, reference-only file shelf,
non-activating overlay, and Codex sandbox behavior were left intact.

## Privacy and platform limits

- Clipboard capture defaults to off and is session-only. MiMi never logs clipboard text.
- Widgets are local. No weather, account, licensing, or cloud API was added.
- Windows notification reading still requires MSIX packaging and remains unavailable in the portable build.
- Prayer times, Smart OCR, translation, weather, Google Calendar, and Nusa Pro services were not added;
  they need location/consent, credentials, packaging, or a larger dedicated implementation.
- External-monitor brightness control is not implemented.

## Follow-up polish — 2026-11-25 14:19 IST

- Switched the primary aurora, eye bloom, active-agent accents, and lyric highlight from
  blue/violet to warm gold at Basit's request.
- Made the entire compact agent card a transparent hit target. A click now opens agent
  activity/options even when hover has already expanded the card; it no longer collapses it.
- Increased the compact agent width cap from 380 to 640 DIPs, removed text trimming, and
  allowed wrapping so statuses such as “Executing tool” remain complete.
- Added warm cheek accents for Happy/Lively/Curious, a completion sparkle, and a raised/tilted
  eye for Curious. Tool-running agents now use Lively; starting/waiting agents use Curious.
- Snapped pill geometry and window placement to whole device pixels and preserved the full
  one-pixel border stroke to reduce layered-window edge aliasing.

## Verification

Checked on 2026-11-25 at approximately 01:27 IST:

- Debug build: **0 warnings, 0 errors**.
- Release build: **0 warnings, 0 errors**.
- Full test suite: **405 passed, 0 failed, 0 skipped**.
- `MiMi.exe --selftest`: **ALL PASSED**.
- Offscreen panel renders reviewed for clipping and spacing.
- Live desktop checks: agent activity → Quick Access → Widgets opened successfully through
  background accessibility actions; the rendered face, aurora, tiles, and widgets were visible.
- Follow-up live check at 14:19 IST: the compact “4 agents active” card opened the full Agent
  activity panel on click, and Quick Access rendered the gold aurora without clipped status text.
- Runtime brightness probe on this desktop: **WMI brightness not supported**, reported honestly as
  unavailable. Physical laptop brightness acceptance remains manual.

Visual evidence:

- `C:\Users\basit\MiMi\artifacts\nusa-refresh\island-pill-idle.png`
- `C:\Users\basit\MiMi\artifacts\nusa-refresh\island-pill-quick-access.png`
- `C:\Users\basit\MiMi\artifacts\nusa-refresh\island-pill-widgets.png`
- `C:\Users\basit\MiMi\artifacts\nusa-refresh\live-quick-access.png`
- `C:\Users\basit\MiMi\artifacts\nusa-refresh\live-widgets.png`
- `C:\Users\basit\MiMi\artifacts\agent-gold-refresh\live-agent-compact.png`
- `C:\Users\basit\MiMi\artifacts\agent-gold-refresh\live-agent-options.png`
- `C:\Users\basit\MiMi\artifacts\agent-gold-refresh\live-quick-gold.png`
