using System.IO;
using System.Windows;
using System.Windows.Threading;
using MiMi.Agents;
using MiMi.App.Interop;
using MiMi.App.UI;
using MiMi.App.UI.Mascot;
using MiMi.Bridge;
using MiMi.Core;
using MiMi.Lyrics;
using MiMi.Media;
using Wmc = Windows.Media.Control;

namespace MiMi.App;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    private ConfigStore _config = null!;
    private ApprovalStore _approvals = null!;
    private AgentEventBus _bus = null!;
    private IslandStateManager _stateManager = null!;
    private GsmtcMediaService? _media;
    private LyricsService _lyrics = new();
    private NamedPipeAgentBridge? _bridge;
    private IslandPresenter _presenter = null!;
    private IslandWindow _island = null!;
    private FullscreenWatcher _fullscreen = null!;
    private SystemHudController _hudController = null!;
    private TrayIcon _tray = null!;
    private SettingsWindow? _settingsWindow;
    private ProviderIntegrationInspector _providerInspector = null!;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunSelfTestAsync();
            return;
        }

        // Single instance (selftest is exempt so CI can run it alongside a live app).
        _singleInstance = new Mutex(initiallyOwned: true, "Local\\MiMi.SingleInstance", out var isFirst);
        _ownsMutex = isFirst;
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("UI thread exception (handled)", args.Exception);
            if (args.Exception.InnerException is { } inner)
                Logger.Error("  inner", inner);
            args.Handled = true; // the overlay must survive; details land in the log
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _config = new ConfigStore();
        _providerInspector = new ProviderIntegrationInspector();
        MascotOptions.Apply(_config.Current);
        _config.Changed += settings => MascotOptions.Apply(settings);
        _approvals = new ApprovalStore(TimeSpan.FromMinutes(_config.Current.ApprovalTtlMinutes));
        _bus = new AgentEventBus();
        _stateManager = new IslandStateManager();

        var mediaVm = new MediaViewModel(
            playPause: () => _ = _media?.SendCommandAsync(MediaCommand.PlayPause),
            next: () => _ = _media?.SendCommandAsync(MediaCommand.Next),
            previous: () => _ = _media?.SendCommandAsync(MediaCommand.Previous),
            seek: pos => _ = _media?.SeekAsync(pos));

        var water = new WaterTracker();
        var todos = new TodoStore();
        var shelf = new FileShelfStore();
        _presenter = new IslandPresenter(_config, _stateManager, _approvals, mediaVm, Dispatcher, water, todos, shelf);

        _island = new IslandWindow(
            _config,
            () => _presenter.PinnedContent,
            approvalId => _approvals.Get(approvalId) is { } r && r.IsPending
                ? r.TimeToLive - (DateTimeOffset.UtcNow - r.CreatedUtc)
                : TimeSpan.Zero,
            mediaVm,
            () => _presenter.BuildAgentPanel(),
            secondTick: () => _presenter.OnSecondTick(),
            shelfDrop: paths => Dispatcher.BeginInvoke(() => _presenter.AddToShelf(paths)),
            shelfRemove: path => Dispatcher.BeginInvoke(() => _presenter.RemoveFromShelf(path)),
            shelfOpen: () => _presenter.SetUtilityPin("shelf", true),
            quickAccessOpen: () => _presenter.OpenQuickAccess(),
            closeUtility: () => _presenter.CloseUtilityPanel());
        _island.Show();

        _stateManager.EffectiveContentChanged += content =>
        {
            if (content is UI.IslandContent islandContent)
                Dispatcher.BeginInvoke(() => _island.ShowPinned(islandContent));
        };
        _presenter.RefreshPinned();

        // Fullscreen step-aside, event-driven via WinEvent hook. Desktop persistence and
        // the fullscreen policy are user settings read live on every foreground change.
        _fullscreen = new FullscreenWatcher(
            suppressed => Dispatcher.BeginInvoke(() =>
            {
                var policy = _config.Current.FullscreenSuppress;
                _island.SetSuppressed(suppressed && policy);
            }),
            desktopPersistEnabled: () => _config.Current.DesktopPersistVisible);
        _fullscreen.SetSelfWindow(_island.PillHandle);
        _fullscreen.ForegroundChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (_config.Current.DesktopPersistVisible) _island.ReassertTopmost();
        });

        // Media: event-driven via GSMTC.
        if (_config.Current.MediaModuleEnabled)
        {
            _media = new GsmtcMediaService();
            _media.Changed += snapshot => Dispatcher.BeginInvoke(() =>
            {
                mediaVm.Update(snapshot);
                _presenter.OnMediaChanged(snapshot);

                // Lyrics follow the playing track (Phase 4 / Milestone 2).
                if (snapshot is null)
                {
                    _lyrics.OnTrackChanged(new LyricsQuery("", "", "", 0));
                    mediaVm.UpdateLyrics(LyricsState.Idle, null, _config.Current.LyricsOffsetMs, _config.Current.LyricsEnabled);
                }
                else
                {
                    _lyrics.OnTrackChanged(new LyricsQuery(
                        snapshot.Title, snapshot.Artist, snapshot.Album,
                        snapshot.Duration.TotalSeconds));
                    mediaVm.UpdateLyrics(_lyrics.State, _lyrics.Current, _config.Current.LyricsOffsetMs, _config.Current.LyricsEnabled);
                }
            });
            _lyrics.Changed += () => Dispatcher.BeginInvoke(() =>
            {
                Logger.Info($"Lyrics: state={_lyrics.State} track='{_lyrics.Current?.TrackName ?? "(none)"}' synced={_lyrics.Current?.HasSynced}");
                mediaVm.UpdateLyrics(_lyrics.State, _lyrics.Current, _config.Current.LyricsOffsetMs, _config.Current.LyricsEnabled);
            });
            _ = StartMediaAsync();
        }

        // Bridge: secure local named pipe receiver (Phase 2 foundation).
        if (_config.Current.BridgeEnabled)
        {
            var token = BridgeProtocol.EnsureToken(ConfigStore.DefaultDirectory);
            _bridge = new NamedPipeAgentBridge(_bus, _approvals, token);
            _bridge.Start();
        }

        _bus.Subscribe(ev => Dispatcher.BeginInvoke(() => _presenter.OnAgentEvent(ev)));

        _approvals.Resolved += request => Dispatcher.BeginInvoke(() =>
        {
            _presenter.OnApprovalResolved(request);
            if (request.Decision is ApprovalDecision.Approved or ApprovalDecision.Denied)
                Logger.Info($"Approval '{request.ApprovalRequestId}' {request.Decision} by {request.ResolvedBy}.");
        });

        // Tray icon.
        _tray = new TrayIcon();
        _tray.SetAutoStartIndicator(AutoStart.IsEnabled());
        _tray.SetVisibilityIndicator(true);
        _tray.SetUtilityIndicators(false, false, false);
        _tray.SetShelfIndicator(shelf.Count > 0, false);
        _tray.UtilityIndicatorsRequested += () => Dispatcher.BeginInvoke(() =>
        {
            _tray.SetUtilityIndicators(
                _presenter.UtilityPins.Contains("pomodoro"),
                _presenter.UtilityPins.Contains("water"),
                _presenter.UtilityPins.Contains("todo"));
            _tray.SetShelfIndicator(_presenter.Shelf.Count > 0,
                _presenter.UtilityPins.Contains("shelf"));
        });
        _hudController = new SystemHudController(_config, _presenter, Dispatcher);
        _tray.CommandSelected += OnTrayCommand;
        _island.KeyboardTick += () => _hudController.OnKeyboardTick();
        _island.PowerStatusChanged += () => _hudController.OnPowerStatusChanged();
        _island.OnProximityWake += wake => Dispatcher.BeginInvoke(() =>
        {
            Logger.Info($"Proximity: {(wake ? "wake" : "re-doze")}");
            _presenter.SetProximityAwake(wake);
        });
        _island.VisibilityPreferenceChanged += preference =>
            Dispatcher.BeginInvoke(() =>
            {
                var visible = preference == VisibilityPreference.Visible;
                _tray.SetVisibilityIndicator(visible);
                // Notification policy follows the user's explicit choice only; fullscreen
                // suppression is transient and must not enable or disable toasts.
                _presenter.Notifications.SetHidden(!visible);
                // Nusa-style hello: bringing MiMi back counts as activity, so a dozing
                // face wakes instead of greeting the user asleep.
                if (visible) _presenter.NoteUserActivity();
            });
        try { _tray.Initialize(); }
        catch (Exception ex) { Logger.Error("Tray icon failed to initialize", ex); }

        _presenter.WindowsNoticeRequested += OnWindowsAgentNotice;

        SystemParameters.StaticPropertyChanged += OnSystemParamsChanged;
        _island.Closed += (_, _) => Shutdown();

        Logger.Info("MiMi started.");
    }

    private async Task StartMediaAsync()
    {
        try { await _media!.StartAsync(); }
        catch (Exception ex) { Logger.Error("Media service failed to start (GSMTC unavailable?)", ex); }
    }

    private void OnWindowsAgentNotice(AgentNotice notice)
    {
        if (_tray is null) return;
        var kind = notice.Kind switch
        {
            AgentNoticeKind.Error => TrayNotificationKind.Error,
            AgentNoticeKind.ApprovalRequired => TrayNotificationKind.Warning,
            _ => TrayNotificationKind.Info,
        };
        _tray.ShowBalloon(notice.Title, notice.Body, kind, notice.AllowSound, () => Dispatcher.BeginInvoke(() =>
        {
            // An explicit click is the only path that can bring MiMi back; passive
            // delivery never overrides the user's hidden preference.
            _presenter.NoteUserActivity();
            _island.SetHiddenByUser(false);
            if (notice.ApprovalRequestId is { Length: > 0 } id) _presenter.ShowApproval(id);
        }));
    }

    private void OnSystemParamsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.WorkArea))
            Dispatcher.BeginInvoke(() => _island.PositionWindow());
        else if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation))
            Dispatcher.BeginInvoke(MascotOptions.RefreshReducedMotion);
    }

    private void OnTrayCommand(TrayIcon _, TrayMenuCommand command)
    {
        Dispatcher.BeginInvoke(() =>
        {
            switch (command)
            {
                case TrayMenuCommand.ToggleVisibility:
                    var hiding = _island.Coordinator.Preference == VisibilityPreference.Visible;
                    if (!hiding) _presenter.NoteUserActivity();
                    _island.SetHiddenByUser(hiding);
                    break;
                case TrayMenuCommand.TogglePomodoro:
                    _presenter.ToggleUtility("pomodoro");
                    break;
                case TrayMenuCommand.ToggleWater:
                    _presenter.ToggleUtility("water");
                    break;
                case TrayMenuCommand.ToggleTodo:
                    _presenter.ToggleUtility("todo");
                    break;
                case TrayMenuCommand.ToggleShelf:
                    _presenter.ToggleUtility("shelf");
                    break;
                case TrayMenuCommand.Settings: ShowSettings(); break;
                case TrayMenuCommand.Simulate: _presenter.SimulateAgentSequence(); break;
                case TrayMenuCommand.ToggleAutoStart:
                    var enable = !AutoStart.IsEnabled();
                    AutoStart.Set(enable);
                    _config.Update(s => s.StartWithWindows = enable);
                    _tray.SetAutoStartIndicator(enable);
                    break;
                case TrayMenuCommand.Restart:
                    // Release the single-instance mutex immediately so the new instance can acquire it successfully
                    if (_ownsMutex)
                    {
                        try { _singleInstance?.ReleaseMutex(); } catch { }
                        _ownsMutex = false;
                    }
                    try { _singleInstance?.Dispose(); } catch { }
                    _singleInstance = null;

                    System.Diagnostics.Process.Start(Environment.ProcessPath ?? "");
                    Shutdown();
                    break;
                case TrayMenuCommand.Exit: Shutdown(); break;
            }
        });
    }

    private void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_config, enable => _tray.SetAutoStartIndicator(enable),
            () => _island.HotkeyRegistered, _providerInspector, _presenter);
        _settingsWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("MiMi exiting.");
        try { _tray?.Dispose(); } catch { }
        try { _fullscreen?.Dispose(); } catch { }
        try { _hudController?.Dispose(); } catch { }
        try { _stateManager?.Dispose(); } catch { }
        try { _approvals?.Dispose(); } catch { }
        try { _bridge?.DisposeAsync().AsTask().Wait(2000); } catch { }
        try { _media?.DisposeAsync().AsTask().Wait(2000); } catch { }
        if (_ownsMutex) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    // ---- headless self test ----------------------------------------------------

    private async Task RunSelfTestAsync()
    {
        NativeMethods.AttachConsole(unchecked((uint)-2)); // ATTACH_PARENT_PROCESS
        var results = new List<(string Name, bool Ok, string Detail)>();

        void Check(string name, bool ok, string detail = "")
        {
            results.Add((name, ok, detail));
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name} {detail}");
        }

        try
        {
            // 1. Config round-trip.
            var path = Path.Combine(Path.GetTempPath(), $"mimi-selftest-{Guid.NewGuid():N}.json");
            try
            {
                new AppSettings { Scale = 1.1 }.Save(path);
                var loaded = AppSettings.Load(path);
                Check("config.save/load", Math.Abs(loaded.Scale - 1.1) < 1e-9, path);
            }
            finally { try { File.Delete(path); } catch { } }

            // 2. Bridge token.
            var token = BridgeProtocol.EnsureToken(ConfigStore.DefaultDirectory);
            Check("bridge.token", token.Length == 64, "token file in %LOCALAPPDATA%\\MiMi");

            // 3. Bridge end-to-end: client sends event, receives ack + approval response.
            var bus = new AgentEventBus();
            using var approvals = new ApprovalStore();
            var bridge = new NamedPipeAgentBridge(bus, approvals, token,
                pipeName: $"mimi-selftest-{Guid.NewGuid():N}");
            bridge.Start();

            var gotEvent = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            bus.Subscribe(ev => gotEvent.TrySetResult(ev));

            await using var client = new System.IO.Pipes.NamedPipeClientStream(".", bridge.PipeName,
                System.IO.Pipes.PipeDirection.InOut);
            await client.ConnectAsync(5000);
            await BridgeProtocol.WriteFrameAsync(client, new BridgeProtocol { Kind = "hello", Token = token, Client = "selftest" }, default);
            var helloAck = BridgeProtocol.Deserialize((await BridgeProtocol.ReadFrameAsync(client, default))!);
            Check("bridge.hello", helloAck is { Ok: true });

            var approval = AgentEventFactory.Create("selftest", "sess", AgentEventTypes.ApprovalRequested,
                title: "Self-test approval", approvalRequestId: "st-1",
                metadata: new Dictionary<string, string> { ["command"] = "echo hello", ["action_kind"] = "Execute Command" });
            await BridgeProtocol.WriteFrameAsync(client, new BridgeProtocol { Kind = "event", Event = approval }, default);
            var ack = BridgeProtocol.Deserialize((await BridgeProtocol.ReadFrameAsync(client, default))!);
            Check("bridge.event.ack", ack is { Ok: true });
            var published = await gotEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check("bus.publish", published.ApprovalRequestId == "st-1");

            approvals.TryDecide("st-1", ApprovalDecision.Approved, "user");
            var response = BridgeProtocol.Deserialize((await BridgeProtocol.ReadFrameAsync(client, default))!);
            Check("bridge.approval_response", response is { Kind: "approval_resolved", Decision: "approved" });

            await bridge.DisposeAsync();

            // 4. GSMTC availability (may legitimately have zero sessions).
            try
            {
                var manager = await Wmc.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var sessions = manager.GetSessions();
                Check("media.gsmtc", true, $"{sessions.Count} active media session(s)");
            }
            catch (Exception ex)
            {
                Check("media.gsmtc", false, ex.Message);
            }

            // 5. State manager priorities.
            using var sm = new IslandStateManager();
            object? effective = null;
            sm.EffectiveContentChanged += c => effective = c;
            sm.SetPinned(IslandPriority.Media, "MEDIA");
            sm.Push(new IslandAlert { Id = "ap", Priority = IslandPriority.Approval, Content = "APPROVAL" });
            Check("state.priority", (string)effective! == "APPROVAL");

            var allPassed = results.All(r => r.Ok);
            Console.WriteLine(allPassed ? "SELFTEST: ALL PASSED" : "SELFTEST: FAILURES PRESENT");
            Environment.ExitCode = allPassed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SELFTEST EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            Logger.Error("selftest failed", ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            Shutdown();
        }
    }
}
