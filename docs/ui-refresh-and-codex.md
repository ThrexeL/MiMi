# Island refresh and embedded Codex — 2026-11-24

## UI refresh

- The pill now morphs width, height, and corner radius together with reduced-motion support.
- Compact media, agent, and idle states are clickable as well as hoverable.
- Quick Access is reachable from idle, media, and agent panels.
- Quick Access now has six tiles: Focus, Water, To-do, Shelf, Volume, and Codex.
- Typography and primary hit targets are larger; utility panels show their live sublabels.
- The activity panel keeps a fixed 320 DIP height and scrolls internally.
- A non-empty shelf can be dismissed again; shelf count badges remain visible when closed.
- Shelf rows scroll, use a single “Missing” label, and preserve copy-only drag-out.
- Approval cards are wider, with larger navigation targets and a clearer copy-session label.
- The island never activates for mouse input. Task entry uses a separate editor window.

## Volume controls

- New Volume panel with master-volume slider and mute/unmute.
- Uses the existing event-driven Core Audio endpoint; no polling.
- Rebinds when the default render device changes.
- Unavailable audio endpoints disable writes and show an honest unavailable state.

## Embedded Codex conversation (experimental)

- Open from Quick Access, the tray menu, or `MiMi.exe --codex`.
- Uses the installed native `codex.exe` app-server over stdio JSONL.
- Pinned to the generated `codex-cli 0.160.x` protocol.
- Global Codex hooks and transcript persistence are disabled for the embedded process,
  avoiding a competing approval path and keeping conversation content in memory only.
- Read-only sandbox, explicit `on-request` approvals, one-shot accept/decline only.
- Conversation content stays in memory, is bounded, and is never logged.
- Assistant/tool items stream into a separate normal WPF window.
- The island shows Codex lifecycle and approval cards through the existing session/approval pipeline.
- File-change approvals are automatically declined in this first slice because the stable payload does not expose enough affected-path detail.
- Unknown server requests fail closed and disconnect the embedded session.
- Closing MiMi interrupts the turn, denies outstanding approvals, and terminates the child process tree.

## Verification

- Debug and Release builds: 0 warnings, 0 errors.
- Full suite: 389 passed, 0 failed, 0 skipped.
- Release self-test: ALL PASSED.
- Desktop smoke: island and Codex conversation window launch; Codex connects and streams a text turn.
- Live Codex response verified at 22:40 IST: `MIMI_OK — café ✓` rendered correctly.
- Final Codex response verified after lifecycle hardening: `MIMI_FINAL`.
- Closing the Codex window terminated its child `codex.exe` (PID 18860) while MiMi remained running.
- Live UI verified at 22:43 IST: Quick Access showed all six tiles; Volume showed the real 90% endpoint level.
- Evidence images: `artifacts/verification/codex-final.png`, `quick-access-final.png`,
  `volume-final.png`, `activity-final.png`, and `island-final.png` (git-ignored).

Live acceptance still recommended: command approval accept/decline, interruption, process-tree cleanup,
volume changes across device swaps, shelf dismissal/count badges, DPI/text-scale layouts, and reduced motion.
