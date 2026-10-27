# MiMi 4.0 — Release Candidate State (GLM, 2026-10-28)

Branch: `MiMi-DeepSeek` (DeepSeek's Phase 3.7 + GLM's Phase 4 work; master remains
at the Phase 3.6 checkpoint until the maintainer reviews the diff:
`git log master..MiMi-DeepSeek --oneline` then merge or cherry-pick).
Tests: **211/211 passing**. Release build: green. Selftest: ALL PASSED.

## Milestone status (MiMi 4.0)

| Milestone | Status |
|---|---|
| 0 — Repo review + baseline | DONE - 143/143 verified (DeepSeek 3.7 end) |
| 1 — Visual foundation | DONE `e95c3ad` - face-only idle (no clock), white round eyes, in-pill gold, doze opt-in |
| 2 — Music + synchronized lyrics | DONE `37aeda0`/`9aaf7cd` - LRCLIB client/parser/sync + 3-line UI; live API E2E verified; live GSMTC verification (Bliss -> Synced) |
| 3 — Agents + live notifications | DONE - DeepSeek 3.7 + GLM 3.6 (multi-agent tracking, multi-approval nav, activity timeline) |
| 4 — Productivity | DONE `352515b` - Pomodoro/hydration/to-do backends + panels + tray |
| 5 — File shelf | DONE `40d38c4` - store + panel + drag-in/out wiring; 191/191 |
| 6 — System HUDs | DONE `8896b42`/`15d86d2`/`1808d74` - volume (live-verified), battery, network, audio devices, keyboard; brightness deferred |
| 7 — Notification listener | DONE `c90e04e` - researched: MSIX-only; honest disclosure, not simulated |
| 8 — Visual polish + proximity wake | DONE `0bd5489`/`cf68f10` - glow geometry final, proximity wake, offscreen panel matrix |
| 9 — Release readiness | DONE - build script, measurements, manual checklist |

## Key fixes this phase (genuine defects found by verification)

1. Idle CPU: held animation clocks (every fade/size storyboard left attached)
   forced WPF to compose at 60 fps forever - the classic WPF pitfall. Fixed:
   completion -> detach -> pin final values. Verified via an animation-clock dump.
2. Glow geometry: proportional radial -> fixed-DIP bottom band (8 idle / 20
   expanded); verified across idle, media, expanded toast, shelf screenshots.
3. Mascot ctor deleted by a scripted cleanup -> Content null -> eyes invisible;
   found via a visual-tree probe, restored with timers re-wired.
4. WaterTracker midnight bug: Add compared a full timestamp to a date-only
   field, wiping the daily total on every add (caught by a midnight-crossing test).
5. Missing NullToCollapsed converter -> ~930 retried XamlParseExceptions when a
   HUD appeared; registered; volume HUD then verified live end-to-end.
6. 1 Hz allocation churn: utility VMs/wrappers rebuilt every tick -> cached
   stable wrappers, gated the tick behind "utility active".

## Measurements (this machine, WITH a fullscreen game running behind - contaminated)

| Metric | Value |
|---|---|
| Hidden (Ctrl+Alt+M) | 0% CPU |
| Visible idle (face-only) | ~1.7-3% of one core (varies with desktop activity; clean-desktop number expected lower) |
| Working set / private | ~166 MB / ~111 MB (WPF baseline; settles after GC) |
| XAML errors at idle | 0 |

## Manual acceptance checklist (NOT performed by the agent - for the maintainer)

1. File shelf: tray -> File shelf; drag a file from Explorer onto the panel;
   drag the row back out to Explorer/Notepad; x removes the reference only.
2. Focus timer: tray -> Focus timer; play/pause/stop; leave it running through
   hide/show; completion notice fires.
3. Hydration: tray -> Water tracker; +250 ml; set an interval; reminders respect
   quiet hours (22:00-08:00) and snooze.
4. To-do: tray -> To-do list; add via the box, complete via the checkbox.
5. Sleepy wake: Settings -> enable doze; pause media; wait 2 quiet minutes;
   move the cursor near the island -> wakes; move away -> re-dozes after ~2.5 s.
6. Lyrics: play a known track in Spotify/browser; hover the pill; toggle the
   lyrics glyph; seek and watch the gold line follow.
7. Approvals: real Claude Code/Codex request -> Approve/Deny -> verify the
   provider receives it; timeout -> deny (fail-closed).
8. Win+D, Ctrl+Alt+M rapid toggles, fullscreen app - island persistence and
   suppression per policy.

## Known limitations

- Brightness: WMI-only (laptop backlights); needs the System.Management package -
  deferred rather than faked.
- Windows app notifications: requires MSIX + userNotificationListener - portable
  build shows an honest unavailable disclosure (packaged companion = future work).
- Sleepy + proximity wake needs quiet media (playing audio blocks doze by design).
- Agent session state is in-memory (by design; approvals keep their own TTL while
  the app runs).
- Idle CPU visible-idle ~2-3% of one core on this machine (contaminated by the
  running game; hidden = 0%). Re-measure on a quiet desktop for a clean number.

## Launch

dist\MiMi.exe            (portable single-file build; needs .NET 10 Desktop Runtime)
MiMi.AgentCli.exe install-claude    (or install-codex; then /hooks trust in Codex)
