# Medium reliability fixes — 2026-11-24

Status: **applied, not built or tested**. This change set addresses the Medium-severity
findings from the static review of commit `280924b6ccca9526bc97ad1e008f2797829442ac`.

## Approval boundary and bridge resilience

- Approval resolutions are accepted only from the pipe connection that created the
  request, with matching provider and session identity.
- Approval decisions are returned only to that originating connection instead of being
  broadcast to every authenticated client.
- The bridge rate-limits event frames per connection, bounds pending and retained
  approvals, and bounds tracked agent sessions.
- Agent events entering the WPF dispatcher use a bounded queue that preserves approval
  requests first; session refreshes are coalesced.
- Accept-loop failures release their client slot unless ownership was transferred to a
  client handler.
- Capacity-rejected approvals can be retried with the same event id after capacity is
  available.
- Approval TTLs start at MiMi receipt time rather than trusting a sender timestamp.

Relevant files: `src/MiMi.Bridge/NamedPipeAgentBridge.cs`,
`src/MiMi.Agents/AgentEvents.cs`, `src/MiMi.Agents/AgentSessionManager.cs`,
`src/MiMi.Core/DedupStore.cs`, `src/MiMi.App/App.xaml.cs`.

## Startup, Windows integration, and lifecycle

- Shelf, to-do, and water loaders tolerate recoverable I/O, permission, JSON, and schema
  failures without preventing startup.
- Water totals are loaded from `water.json` at startup.
- Bridge-token failures disable only the bridge instead of terminating the overlay.
- Fullscreen state is evaluated immediately after startup.
- Topmost recovery now reasserts actual z-order rather than checking only the style bit.
- Restart releases the old global hotkey before launching the replacement process.
- Settings and tray autostart changes write the Run key, report failure, and roll back
  inconsistent registry/configuration changes.
- Hidden mascot blink/dart callbacks can no longer restart life timers while hidden.
- Overlapping GSMTC refreshes use a generation check so older snapshots cannot publish
  over newer ones.

Relevant files: `src/MiMi.Core/FileShelfStore.cs`, `src/MiMi.Core/TodoStore.cs`,
`src/MiMi.Core/WaterTracker.cs`, `src/MiMi.App/App.xaml.cs`,
`src/MiMi.App/Interop/FullscreenWatcher.cs`, `src/MiMi.App/UI/IslandWindow.xaml.cs`,
`src/MiMi.App/UI/SettingsWindow.xaml.cs`,
`src/MiMi.App/UI/Mascot/MiMiMascotControl.xaml.cs`,
`src/MiMi.Media/GsmtcMediaService.cs`.

## UI/UX and interaction fixes

- Volume and mute share one stable HUD identity, so rapid changes update one HUD.
- Drag-away closes only the shelf MiMi auto-opened; an already pinned shelf stays open.
- Shelf remove buttons no longer initiate a drag.
- Shelf drag-out offers Copy only and revalidates the referenced path before dragging.
- The to-do panel has an inline add box and Add button; Enter also adds a task.
- To-do empty-state notification refreshes when rows change.
- Resolving an earlier approval preserves the currently selected queued approval.
- Explicit user panels outrank agent completions and HUDs, while approvals, critical
  system events, and failures still take precedence.

Relevant files: `src/MiMi.App/SystemHudController.cs`,
`src/MiMi.App/UI/IslandPresenter.cs`, `src/MiMi.App/UI/IslandWindow.xaml`,
`src/MiMi.App/UI/IslandWindow.xaml.cs`, `src/MiMi.App/UI/ViewModels.cs`,
`src/MiMi.Core/IslandStateManager.cs`.

## Thursday and ImpressiveBot compatibility

The Thursday permission adapter and ImpressiveBot relay remain display/decision-bound
to their existing contracts:

- Thursday approvals use the same originating pipe connection and receive the same
  `approval_resolved` response as before.
- ImpressiveBot status events remain display-only and cannot create approvals.
- Thursday and ImpressiveBot live summary text continues through the unchanged session
  indicator paths; dispatcher coalescing only reduces redundant refreshes.
- Existing Thursday and ImpressiveBot integration tests were left in place and remain
  the acceptance checks for these paths.

## Verification still required

No build, test suite, self-test, application launch, or desktop interaction was run for
this change set. Before release, run:

```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:Path = "$env:DOTNET_ROOT;$env:Path"
dotnet build MiMi.slnx -c Debug
dotnet build MiMi.slnx -c Release
dotnet test src/MiMi.Tests/MiMi.Tests.csproj -c Debug --nologo
./src/MiMi.App/bin/Release/net10.0-windows10.0.26100.0/MiMi.exe --selftest
```

Manual acceptance should include Thursday approval round-trip, ImpressiveBot live status,
shelf drag-away/remove/copy, to-do add, volume/mute coalescing, restart hotkey recovery,
autostart, startup under fullscreen, and water persistence across restart.
