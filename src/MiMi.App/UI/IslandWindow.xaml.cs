using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using MiMi.App.UI.Mascot;
using MiMi.Core;

namespace MiMi.App.UI;

/// <summary>
/// The Dynamic Island overlay window. A fixed-size transparent canvas whose only
/// hit-testable region is the pill; the pill animates between per-state dimensions.
/// The window never takes keyboard focus (WS_EX_NOACTIVATE + MA_NOACTIVATE).
/// </summary>
public partial class IslandWindow : Window
{
    private readonly ConfigStore _config;
    private readonly Func<IslandContent?> _contentProvider;   // pinned content (compact/resting)
    private readonly Func<string, TimeSpan?> _approvalTimeLeft;
    private readonly MediaViewModel _mediaVm;

    private IslandContent? _pinnedContent;
    private IslandContent? _renderedContent;   // what the template currently shows
    private bool _hoverExpanded;
    private bool _pointerOver;

    private readonly DispatcherTimer _stateTimer;   // shared: enter-delay / leave-grace
    private readonly DispatcherTimer _progressTimer;
    private readonly Random _random = new();

    private Storyboard? _sizeStoryboard;
    private int _geometryGeneration;
    private int _contentGeneration;
    private Panel? _eqHost;
    private Storyboard? _eqStoryboard;
    private Slider? _progressSlider;
    private bool _sliderDragging;
    private INotifyPropertyChanged? _glowContent;

    private const long WsExNoActivate = 0x08000000;
    private const long WsExToolWindow = 0x00000080;
    private const int HotkeyId = 0xB00B; // Ctrl+Alt+M
    private const int WmClipboardUpdate = 0x031D;

    public IntPtr PillHandle { get; private set; }

    /// <summary>Single authority for whether the island is on screen (P3).</summary>
    public VisibilityCoordinator Coordinator { get; } = new();

    /// <summary>Raised when the user's manual preference changes (tray menu reflects it).</summary>
    public event Action<VisibilityPreference>? VisibilityPreferenceChanged;

    /// <summary>Raised when mouse proximity wakes a sleepy MiMi (true) or the cursor
    /// has been away long enough to re-doze (false).</summary>
    public event Action<bool>? OnProximityWake;

    /// <summary>1 Hz heartbeat while visible: Caps Lock / input-language checks.</summary>
    public event Action? KeyboardTick;

    /// <summary>WM_POWERBROADCAST power-status change (plug/unplug, battery level).</summary>
    public event Action? PowerStatusChanged;

    /// <summary>A clipboard listener notification. The app reads text only when the
    /// user has explicitly enabled session-only clipboard history.</summary>
    public event Action? ClipboardChanged;

    /// <summary>True when Ctrl+Alt+M was registered successfully; false if another
    /// application already owns the combination.</summary>
    public bool HotkeyRegistered { get; private set; }

    private bool _hotkeyHooked;

    private double _glowLevel = 0.55; // current in-pill aurora level (0..1)
    private readonly Func<AgentActivityPanelView> _agentPanelProvider;
    private readonly Action? _secondTick;
    private readonly Action<string[]>? _shelfDrop;
    private readonly Action<string>? _shelfRemove;
    private readonly Action? _shelfOpen;
    private readonly Action? _shelfClose;
    private readonly Action? _quickAccessOpen;
    private readonly Func<IslandContent?>? _quickAccessContentProvider;
    private bool _shelfDragActive;
    private bool _brightnessDragging;
    private bool _brightnessDidDrag;
    private MascotExpression _lastMascotExpression = MascotExpression.Calm;
    private DispatcherTimer? _shelfRevertTimer;
    private readonly Core.ProximityWakePolicy _proximityPolicy = new();
    private readonly DispatcherTimer _proximityTimer;
    private bool _proximityAwake;

    public ICommand CloseUtilityCommand { get; }
    public ICommand AddTaskCommand { get; }
    public ICommand OpenQuickAccessCommand { get; }

    public IslandWindow(ConfigStore config,
        Func<IslandContent?> contentProvider,
        Func<string, TimeSpan?> approvalTimeLeft,
        MediaViewModel mediaVm,
        Func<AgentActivityPanelView>? agentPanelProvider = null,
        Action? secondTick = null,
        Action<string[]>? shelfDrop = null,
        Action<string>? shelfRemove = null,
        Action? shelfOpen = null,
        Action? quickAccessOpen = null,
        Action? closeUtility = null,
        Action? shelfClose = null,
        Action? addTask = null,
        Func<IslandContent?>? quickAccessContentProvider = null)
    {
        CloseUtilityCommand = new RelayCommand(() => closeUtility?.Invoke());
        AddTaskCommand = new RelayCommand(() => addTask?.Invoke());
        OpenQuickAccessCommand = new RelayCommand(() => _quickAccessOpen?.Invoke());
        _agentPanelProvider = agentPanelProvider ?? (() => new AgentActivityPanelView([]));
        _secondTick = secondTick;
        _shelfDrop = shelfDrop;
        _shelfRemove = shelfRemove;
        _shelfOpen = shelfOpen;
        _shelfClose = shelfClose;
        _quickAccessOpen = quickAccessOpen;
        _quickAccessContentProvider = quickAccessContentProvider;
        _config = config;
        _contentProvider = contentProvider;
        _approvalTimeLeft = approvalTimeLeft;
        _mediaVm = mediaVm;
        _mediaVm.PropertyChanged += MediaVmChanged;

        InitializeComponent();
        MascotOptions.Changed += RefreshGlow;
        SourceInitialized += (_, _) =>
        {
            PillHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Interop.NativeMethods.SetWindowLongPtrW(PillHandle, Interop.NativeMethods.GWL_EXSTYLE,
                (IntPtr)(Interop.NativeMethods.GetWindowLongPtrW(PillHandle, Interop.NativeMethods.GWL_EXSTYLE).ToInt64()
                    | WsExNoActivate | WsExToolWindow));
            RegisterGlobalHotkey();
            if (!Interop.NativeMethods.AddClipboardFormatListener(PillHandle))
                Logger.Info("Clipboard history listener unavailable on this window.");
        };

        Closed += (_, _) =>
        {
            if (PillHandle != IntPtr.Zero)
                Interop.NativeMethods.RemoveClipboardFormatListener(PillHandle);
            ReleaseGlobalHotkey();
        };

        Coordinator.Changed += RecomputeVisibility;

        // Mouse-proximity wake (Phase 4): throttled cursor sampling runs ONLY while the
        // mascot is sleepy — never while awake, hidden, or suppressed. No hooks.
        _proximityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _proximityTimer.Tick += (_, _) => SampleProximity();

        PositionWindow();
        ApplySettings();
        _config.Changed += _ => Dispatcher.BeginInvoke(ApplySettings);

        // One timer drives both the hover-open delay and the leave-grace period.
        _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _stateTimer.Tick += StateTimerTick;
        MouseEnter += (_, _) =>
        {
            _pointerOver = true;
            _stateTimer.Interval = TimeSpan.FromMilliseconds(140);
            _stateTimer.Start();
            if (_pinnedContent is RestingView { Expression: not MascotExpression.Sleepy }) PlayHello();
        };
        MouseLeave += (_, _) =>
        {
            _pointerOver = false;
            _stateTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(250, _config.Current.CollapseDelayMs));
            _stateTimer.Start();
        };

        Pill.SizeChanged += (_, _) => UpdateClip();

        // File shelf drag-in: the whole pill accepts Explorer drops. Dragging toward
        // MiMi auto-opens the shelf panel; the file lands immediately.
        Host.AllowDrop = true;
        Host.DragEnter += (_, e) =>
        {
            // Dragging toward MiMi auto-opens the shelf so the drop target is visible.
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                OnShelfDragSessionStarted();
            }
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            Logger.Info("Shelf: DragEnter (file drop offered).");
        };
        Host.DragLeave += (_, e) =>
        {
            if (_shelfDragActive) OnShelfDragEnded();
        };
        Host.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Host.Drop += (_, e) =>
        {
            _shelfDragActive = false;
            _shelfRevertTimer?.Stop();
            if (e.Data.GetDataPresent(DataFormats.FileDrop)
                && e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            {
                Logger.Info($"Shelf: Drop received ({paths.Length} path(s)).");
                _shelfDrop?.Invoke(paths);
                e.Handled = true;
            }
        };


        // 1 Hz ticker: extrapolated media position + approval countdowns.
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _progressTimer.Tick += (_, _) =>
        {
            _secondTick?.Invoke(); // productivity heartbeat (timer, reminders)
            if (_renderedContent is AgentActivityPanelView activity && IsVisible && !Coordinator.FullscreenSuppressed)
            {
                // Rows AND the live ImpressiveBot line: the panel is fixed-height now, so
                // this only refreshes text and never resizes the pill.
                var panel = _agentPanelProvider();
                activity.Update(panel.Rows, panel.ImpressiveBot);
            }
            if (IsVisible && !Coordinator.FullscreenSuppressed) KeyboardTick?.Invoke();

            if (_renderedContent is MediaExpandedView { Media: { } media } && media.IsPlaying)
                media.RefreshTimeline();
            if (_renderedContent is ApprovalView { Agent: { } card })
                card.TickTimeLeft(_approvalTimeLeft(card.ApprovalId) ?? TimeSpan.Zero);
        };
        _progressTimer.Start();

        // Mascot life signs (blink, dart, glow breathing) live inside MiMiMascotControl.
    }

    // ---- global hotkey (Ctrl+Alt+M) --------------------------------------------

    private void RegisterGlobalHotkey()
    {
        if (_hotkeyHooked || PillHandle == IntPtr.Zero) return;
        _hotkeyHooked = true; // register once per window lifetime, never twice

        HotkeyRegistered = Interop.NativeMethods.RegisterHotKey(PillHandle, HotkeyId,
            Interop.NativeMethods.MOD_CONTROL | Interop.NativeMethods.MOD_ALT | Interop.NativeMethods.MOD_NOREPEAT,
            (uint)'M');
        if (HotkeyRegistered)
        {
            Logger.Info("Hotkey: Ctrl+Alt+M registered.");
        }
        else
        {
            Logger.Warn("Hotkey: Ctrl+Alt+M could not be registered (claimed by another application).");
            HotkeyRegistered = false;
        }

        var source = System.Windows.Interop.HwndSource.FromHwnd(PillHandle);
        source?.AddHook(WndProcHook);
    }

    public void ReleaseGlobalHotkey()
    {
        UnregisterGlobalHotkey();
        if (PillHandle == IntPtr.Zero || !_hotkeyHooked) return;
        var source = System.Windows.Interop.HwndSource.FromHwnd(PillHandle);
        source?.RemoveHook(WndProcHook);
        _hotkeyHooked = false;
    }

    private void UnregisterGlobalHotkey()
    {
        if (PillHandle == IntPtr.Zero || !HotkeyRegistered) return;
        Interop.NativeMethods.UnregisterHotKey(PillHandle, HotkeyId);
        HotkeyRegistered = false;
        Logger.Info("Hotkey: Ctrl+Alt+M unregistered.");
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Ctrl+Alt+M toggles the user's manual visibility preference.
        if (msg == Interop.NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Dispatcher.BeginInvoke(() =>
            {
                Coordinator.TogglePreference();
                VisibilityPreferenceChanged?.Invoke(Coordinator.Preference);
            });
            handled = true;
            return IntPtr.Zero;
        }

        // Mouse interaction never activates the overlay. Typing happens only in an
        // explicitly opened companion editor.
        if (msg == Interop.NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return Interop.NativeMethods.MA_NOACTIVATE;
        }

        // Show Desktop must never minimize MiMi (belt: toolwindows are normally exempt).
        if (msg == Interop.NativeMethods.WM_SYSCOMMAND && wParam == Interop.NativeMethods.SC_MINIMIZE)
        {
            handled = true;
            return IntPtr.Zero;
        }

        // Charger plug/unplug and battery level changes (event-driven, no polling).
        if (msg == Interop.NativeMethods.WM_POWERBROADCAST
            && (uint)wParam == Interop.NativeMethods.PBT_APMPOWERSTATUSCHANGE)
        {
            PowerStatusChanged?.Invoke();
        }
        if (msg == WmClipboardUpdate)
        {
            ClipboardChanged?.Invoke();
        }
        return IntPtr.Zero;
    }

    // ---- visibility (single coordinator) ------------------------------------------

    /// <summary>Manual hide via hotkey/tray. Media, the bridge, and approval expiry
    /// continue; nothing reopens the island until the user toggles it back.</summary>
    public void SetHiddenByUser(bool hidden) =>
        Coordinator.SetPreference(hidden ? VisibilityPreference.HiddenByUser : VisibilityPreference.Visible);

    /// <summary>Fullscreen policy hook (desktop persistence handled by the watcher).</summary>
    public void SetFullscreenSuppressed(bool suppressed) => Coordinator.SetFullscreenSuppressed(suppressed);

    /// <summary>Applies the coordinator's verdict to the actual window. Hide is instant;
    /// restore uses a quick 160 ms fade — polished, not distracting.</summary>
    private readonly FadeSequencer _fadeSequencer = new();
    private bool _windowShown = true; // tracks actual window visibility across fades
    private bool _fadeInFlight;

    private void RecomputeVisibility()
    {
        var visible = Coordinator.EffectiveVisible;
        // Only user-initiated hides fade out; fullscreen/session restrictions stay instant.
        var userInitiatedHide = !visible && Coordinator.Preference == VisibilityPreference.HiddenByUser;
        var plan = VisibilityTransitionPlanner.Plan(_windowShown && IsVisible, Opacity, visible,
            userInitiatedHide, reducedMotion: MascotOptions.ReducedMotion, _fadeInFlight);
        ExecuteFadePlan(plan);
        if (visible) ReassertTopmost();
        SyncProximityTimer();
        Logger.Info($"Visibility: effective={(visible ? "visible" : "hidden")} " +
                    $"(preference={Coordinator.Preference}, fullscreen={Coordinator.FullscreenSuppressed}, " +
                    $"transition={plan.Action}).");
    }

    /// <summary>Executes a transition plan. Every plan begins a new fade generation, so
    /// completion handlers from superseded transitions become no-ops — rapid Ctrl+Alt+M
    /// toggles reverse smoothly from the current rendered opacity with no stale Hide().</summary>
    private void ExecuteFadePlan(FadePlan plan)
    {
        var generation = _fadeSequencer.Begin();
        _fadeInFlight = plan.Action is FadeAction.ShowFadeIn or FadeAction.HideFadeOut;

        switch (plan.Action)
        {
            case FadeAction.None:
                return;

            case FadeAction.ShowFadeIn:
            {
                SetClickThrough(false);
                Opacity = plan.FromOpacity; // continue from the rendered value, no jumps
                if (!_windowShown)
                {
                    Show(); // ShowActivated=False + WS_EX_NOACTIVATE: never steals focus
                    _windowShown = true;
                }
                var fadeIn = new DoubleAnimation(1, FadeDuration(baseMs: 220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                fadeIn.Completed += (_, _) =>
                {
                    if (!_fadeSequencer.IsCurrent(generation)) return; // superseded: do nothing
                    BeginAnimation(OpacityProperty, null);
                    Opacity = 1; // finish exactly at 1.0
                    _fadeInFlight = false;
                };
                BeginAnimation(OpacityProperty, fadeIn);
                break;
            }

            case FadeAction.HideFadeOut:
            {
                // Fade-out must not intercept new mouse input; clicks pass through.
                SetClickThrough(true);
                Opacity = plan.FromOpacity;
                var fadeOut = new DoubleAnimation(0, FadeDuration(baseMs: 200))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                };
                fadeOut.Completed += (_, _) =>
                {
                    if (!_fadeSequencer.IsCurrent(generation)) return; // a newer Show() won: skip Hide()
                    HideNow();
                };
                // HideNow clears the flag too; superseded fades keep it for the replacement.
                BeginAnimation(OpacityProperty, fadeOut);
                break;
            }

            case FadeAction.ShowImmediate:
                SetClickThrough(false);
                Opacity = 0;
                Show();
                _windowShown = true;
                _fadeInFlight = false;
                Opacity = 1;
                break;

            case FadeAction.HideImmediate:
                HideNow();
                break;
        }
    }

    private void HideNow()
    {
        BeginAnimation(OpacityProperty, null);
        Hide(); // stops all island rendering; media/bridge keep running
        _windowShown = false;
        _fadeInFlight = false;
        Opacity = 1; // clean base for the next fade-in
    }

    /// <summary>Fade durations respect the island-wide animation speed setting
    /// (clamped to a responsive 80–400 ms).</summary>
    private TimeSpan FadeDuration(double baseMs) =>
        TimeSpan.FromMilliseconds(Math.Clamp(baseMs / _config.Current.AnimationSpeed, 80, 400));

    /// <summary>Clicks pass through the island while it fades out and while hidden.</summary>
    private void SetClickThrough(bool enabled)
    {
        if (PillHandle == IntPtr.Zero) return;
        var exStyle = Interop.NativeMethods.GetWindowLongPtrW(PillHandle, Interop.NativeMethods.GWL_EXSTYLE).ToInt64();
        var updated = enabled
            ? exStyle | Interop.NativeMethods.WS_EX_TRANSPARENT
            : exStyle & ~Interop.NativeMethods.WS_EX_TRANSPARENT;
        if (updated != exStyle)
            Interop.NativeMethods.SetWindowLongPtrW(PillHandle, Interop.NativeMethods.GWL_EXSTYLE, (IntPtr)updated);
    }

    /// <summary>Shell reshuffles (Win+D, taskbar restarts) can demote topmost. Re-assert
    /// only when actually needed — event-driven, no timers, no flicker.</summary>
    public void ReassertTopmost()
    {
        if (!IsVisible || PillHandle == IntPtr.Zero) return;

        var exStyle = Interop.NativeMethods.GetWindowLongPtrW(PillHandle, Interop.NativeMethods.GWL_EXSTYLE).ToInt64();
        var hadTopmostStyle = (exStyle & Interop.NativeMethods.WS_EX_TOPMOST) != 0;
        var wasIconic = Interop.NativeMethods.IsIconic(PillHandle);

        // The style bit can remain set while another topmost window or shell reshuffle
        // leaves MiMi lower in the actual z-order. Reassert unconditionally on relevant
        // foreground/shell events; SWP_NOACTIVATE keeps this non-disruptive.
        Interop.NativeMethods.SetWindowPos(PillHandle, Interop.NativeMethods.HwndTopmost, 0, 0, 0, 0,
            Interop.NativeMethods.SWP_NOMOVE | Interop.NativeMethods.SWP_NOSIZE
            | Interop.NativeMethods.SWP_NOACTIVATE | Interop.NativeMethods.SWP_NOOWNERZORDER);
        if (wasIconic)
            Interop.NativeMethods.ShowWindow(PillHandle, 4 /* SW_SHOWNOACTIVATE */);
        if (!hadTopmostStyle || wasIconic)
            Logger.Info("Visibility: restored topmost/minimized state without activation.");
    }

    // ---- public surface ------------------------------------------------------

    public void ShowPinned(IslandContent content)
    {
        _pinnedContent = content;
        if (!content.Expandable) _hoverExpanded = false;
        ApplyContent(_hoverExpanded ? Expanded(content) ?? content : content);
    }

    /// <summary>Legacy entry point kept for the watcher: fullscreen now flows through
    /// the visibility coordinator instead of pill-opacity surgery.</summary>
    public void SetSuppressed(bool suppressed)
    {
        if (suppressed && _hoverExpanded) CollapseHover();
        Coordinator.SetFullscreenSuppressed(suppressed);
    }

    // ---- island aurora (inside the pill) ---------------------------------------
    // A separate visual system from the face: warm gold light pooling inside the
    // island, kept off the edges so nothing leaks outside the rounded pill.

    private static double GlowLevelFor(IslandContent content) => content switch
    {
        ApprovalView => 0.9,
        MediaCompactView or MediaExpandedView => 0.7,
        RestingView resting => GlowLevelForExpression(resting.Expression),
        AgentCompactView compact => GlowLevelForExpression(compact.Indicator.Expression),
        AgentExpandedView expanded => GlowLevelForExpression(expanded.Agent.Expression),
        _ => 0.55,
    };

    private static double GlowLevelForExpression(MascotExpression expression) => expression switch
    {
        MascotExpression.Alert => 0.9,
        MascotExpression.Happy => 0.72,
        MascotExpression.Curious => 0.66,
        MascotExpression.Concerned => 0.4,
        MascotExpression.Sleepy => 0.32,
        MascotExpression.Focused => 0.5,
        MascotExpression.Lively => 0.7,
        _ => 0.55,
    };

    private void UpdateGlow(IslandContent content)
    {
        _glowLevel = MascotOptions.GlowEnabled ? GlowLevelFor(content) * Math.Clamp(MascotOptions.GlowIntensity * 2, 0, 1.4) : 0;
        if (MascotOptions.ReducedMotion)
        {
            GlowRoot.BeginAnimation(OpacityProperty, null);
            GlowRoot.Opacity = Math.Min(_glowLevel, 1);
        }
        else
        {
            var glowFade = new DoubleAnimation(Math.Min(_glowLevel, 1), TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            glowFade.Completed += (_, _) =>
            {
                GlowRoot.BeginAnimation(OpacityProperty, null);
                GlowRoot.Opacity = Math.Min(_glowLevel, 1);
            };
            GlowRoot.BeginAnimation(OpacityProperty, glowFade);
        }

        // Geometry is state-aware but FIXED in DIPs: a thin band on the idle pill,
        // a restrained 20 DIP falloff on expanded panels — never proportional to
        // panel height (that was the brown-oval bug).
        if (GlowBand is not null)
        {
            var isIdle = content is RestingView;
            GlowBand.Height = GlowLayout.ClampToBottomRegion(
                GlowLayout.BandHeight(isIdle, Pill.ActualHeight <= 0 ? 38 : Pill.ActualHeight),
                Pill.ActualHeight <= 0 ? 38 : Pill.ActualHeight);
        }
    }

    private void RefreshGlow()
    {
        if (_renderedContent is not null) UpdateGlow(_renderedContent);
    }

    private void OnGlowContentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RestingView.Expression)) return;
        if (sender is IslandContent content)
        {
            UpdateGlow(content);
            SyncProximityTimer();
            if (content is RestingView resting)
            {
                if (_lastMascotExpression == MascotExpression.Sleepy
                    && resting.Expression is not MascotExpression.Sleepy)
                    PlayHello();
                _lastMascotExpression = resting.Expression;
            }
        }
    }

    // ---- content application ---------------------------------------------------

    private IslandContent? Expanded(IslandContent content) => content switch
    {
        MediaCompactView m => new MediaExpandedView(m.Media),
        RestingView => _quickAccessContentProvider?.Invoke(),
        // Hovering the agent pill opens the full multi-session activity panel.
        AgentCompactView => _agentPanelProvider(),
        _ => null,
    };

    private void ApplyContent(IslandContent content)
    {
        if (ReferenceEquals(content, _renderedContent))
        {
            if (content is AgentCompactView && Math.Abs(Pill.ActualWidth - content.TargetWidth) > 2)
                AnimatePillSize(content.TargetWidth, content.TargetHeight, TimeSpan.FromMilliseconds(180), content.TargetCornerRadius);
            return;
        }
        var generation = ++_contentGeneration;
        _renderedContent = content;

        var speed = _config.Current.AnimationSpeed;
        var duration = TimeSpan.FromMilliseconds(240 / speed);
        var swapAt = TimeSpan.FromMilliseconds(70 / speed);

        var fadeOut = new DoubleAnimation(1, 0, swapAt);
        UpdateGlow(content);

        fadeOut.Completed += (_, _) =>
        {
            if (generation != _contentGeneration) return;
            Host.Content = content;
            Host.ApplyTemplate();

            // Glow follows in-place expression changes (sleepy/happy/alert) too, not
            // only content swaps.
            if (_glowContent is not null) _glowContent.PropertyChanged -= OnGlowContentChanged;
            _glowContent = content as INotifyPropertyChanged;
            if (_glowContent is not null) _glowContent.PropertyChanged += OnGlowContentChanged;

            // Measure AFTER the new content is in place (measuring earlier sees the old
            // content and clips cards). Auto-height cards hug their measured content;
            // compact/resting states keep their designed sizes.
            var targetHeight = content.TargetHeight;
            if (content.AutoHeight)
            {
                Host.Measure(new Size(content.TargetWidth, double.PositiveInfinity));
                targetHeight = Math.Max(80, Math.Ceiling(Host.DesiredSize.Height) + 2);
                Logger.Info($"Island: '{content.GetType().Name}' measured {Host.DesiredSize.Height:0.#} → height {targetHeight:0.#}");
            }
            AnimatePillSize(content.TargetWidth, targetHeight, duration - swapAt, content.TargetCornerRadius);

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => HookTemplate(content));
            var fadeIn = new DoubleAnimation(0, 1, duration - swapAt);
            Host.BeginAnimation(OpacityProperty, fadeIn);
        };
        Host.BeginAnimation(OpacityProperty, fadeOut);

        // Self-heal: after a full layout+render pass, re-measure and correct the pill
        // height if it drifted (e.g. late binding changed a row's visibility).
        if (content.AutoHeight)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (!ReferenceEquals(Host.Content, content)) return;
                Host.Measure(new Size(content.TargetWidth, double.PositiveInfinity));
                var corrected = Math.Max(80, Math.Ceiling(Host.DesiredSize.Height) + 2);
                if (Math.Abs(corrected - Pill.ActualHeight) > 2)
                {
                    Logger.Info($"Island: correcting '{content.GetType().Name}' height {Pill.ActualHeight:0.#} → {corrected:0.#}");
                    AnimatePillSize(content.TargetWidth, corrected, TimeSpan.FromMilliseconds(140), content.TargetCornerRadius);
                }
            });
        }
    }

    private void AnimatePillSize(double width, double height, TimeSpan duration, double radius)
    {
        // Fractional layout sizes make a one-pixel stroke land across two device
        // pixels, which reads as a jagged border on layered WPF windows.
        var dpi = VisualTreeHelper.GetDpi(this);
        width = Math.Round(width * dpi.DpiScaleX) / dpi.DpiScaleX;
        height = Math.Round(height * dpi.DpiScaleY) / dpi.DpiScaleY;
        radius = Math.Round(radius * dpi.DpiScaleX) / dpi.DpiScaleX;
        var targetRadius = Math.Min(radius, height / 2);
        if (MascotOptions.ReducedMotion || duration <= TimeSpan.Zero)
        {
            ++_geometryGeneration;
            _sizeStoryboard?.Remove(Pill);
            _sizeStoryboard = null;
            Pill.BeginAnimation(WidthProperty, null);
            Pill.BeginAnimation(HeightProperty, null);
            Pill.BeginAnimation(Border.CornerRadiusProperty, null);
            Pill.Width = width;
            Pill.Height = height;
            Pill.CornerRadius = new CornerRadius(targetRadius);
            UpdateClip();
            return;
        }

        var generation = ++_geometryGeneration;
        _sizeStoryboard?.Remove(Pill);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _sizeStoryboard = new Storyboard
        {
            Children =
            {
                new DoubleAnimation(Pill.ActualWidth, width, duration) { EasingFunction = ease },
                new DoubleAnimation(Pill.ActualHeight, height, duration) { EasingFunction = ease },
                new CornerRadiusAnimation(Pill.CornerRadius, new CornerRadius(targetRadius), duration) { EasingFunction = ease },
            },
        };
        Storyboard.SetTarget(_sizeStoryboard.Children[0], Pill);
        Storyboard.SetTargetProperty(_sizeStoryboard.Children[0], new PropertyPath(WidthProperty));
        Storyboard.SetTarget(_sizeStoryboard.Children[1], Pill);
        Storyboard.SetTargetProperty(_sizeStoryboard.Children[1], new PropertyPath(HeightProperty));
        Storyboard.SetTarget(_sizeStoryboard.Children[2], Pill);
        Storyboard.SetTargetProperty(_sizeStoryboard.Children[2], new PropertyPath(Border.CornerRadiusProperty));
        // Held animation clocks force WPF to compose every frame (idle CPU leak):
        // detach on completion and pin the final values.
        _sizeStoryboard.Completed += (_, _) =>
        {
            if (generation != _geometryGeneration) return;
            _sizeStoryboard.Remove(Pill);
            Pill.BeginAnimation(WidthProperty, null);
            Pill.BeginAnimation(HeightProperty, null);
            Pill.BeginAnimation(Border.CornerRadiusProperty, null);
            Pill.Width = width;
            Pill.Height = height;
            Pill.CornerRadius = new CornerRadius(targetRadius);
            UpdateClip();
        };
        _sizeStoryboard.Begin(Pill, isControllable: false);
    }

    private void SyncProximityTimer()
    {
        var resting = _pinnedContent is RestingView;
        var sleepy = _pinnedContent is RestingView { Expression: MascotExpression.Sleepy }
                     && MascotOptions.SleepyEnabled && MascotOptions.FaceEnabled;
        var shouldRun = IsVisible && !Coordinator.FullscreenSuppressed
                        && resting && (sleepy || _proximityAwake);
        if (shouldRun)
        {
            if (!_proximityTimer.IsEnabled) _proximityTimer.Start();
            return;
        }
        _proximityTimer.Stop();
        if (_proximityAwake)
        {
            _proximityAwake = false;
            _proximityPolicy.Reset();
        }
    }

    /// <summary>Throttled cursor sampling while sleepy. The wake decision comes from
    /// the hysteresis policy; waking goes through the presenter's mood pipeline, so the
    /// face change is the normal expression path (no direct element mutation).</summary>
    private void SampleProximity()
    {
        if (!IsVisible || Coordinator.FullscreenSuppressed
            || _pinnedContent is not RestingView
            || (_pinnedContent is not RestingView { Expression: MascotExpression.Sleepy } && !_proximityAwake))
        {
            _proximityTimer.Stop();
            return;
        }

        if (!Interop.NativeMethods.GetCursorPos(out var cursorPx)) return;
        var rect = GetPillScreenRectPixels();
        var dx = Math.Max(rect.Left - cursorPx.X, 0) + Math.Max(cursorPx.X - rect.Right, 0);
        var dy = Math.Max(rect.Top - cursorPx.Y, 0) + Math.Max(cursorPx.Y - rect.Bottom, 0);
        var distance = Math.Sqrt(dx * dx + dy * dy);

        var decision = _proximityPolicy.Evaluate(distance, DateTimeOffset.UtcNow);
        if (decision is { } wake)
        {
            _proximityAwake = wake;
            OnProximityWake?.Invoke(wake);
        }
    }

    /// <summary>Pill bounds in screen DIPs (the window is a fixed canvas; the pill is
    /// centered horizontally at the top).</summary>
    private Rect GetPillScreenRectPixels()
    {
        var transform = Pill.TransformToAncestor(this);
        var topLeft = PointToScreen(transform.Transform(new Point(0, 0)));
        var bottomRight = PointToScreen(transform.Transform(new Point(Pill.ActualWidth, Pill.ActualHeight)));
        // Extend wake coverage slightly below the pill, in physical pixels.
        return new Rect(topLeft, new Size(Math.Max(1, bottomRight.X - topLeft.X),
            Math.Max(1, bottomRight.Y - topLeft.Y) + 14));
    }

    /// <summary>Diagnostics: lists elements carrying live animation clocks.</summary>
    public System.Collections.Generic.List<string> DumpActiveAnimations()
    {
        var result = new System.Collections.Generic.List<string>();
        DumpActiveAnimationsRecursive(Pill, result);
        if (result.Count == 0) result.Add("(no element carries animation clocks)");
        return result;
    }

    private static void DumpActiveAnimationsRecursive(DependencyObject root,
        System.Collections.Generic.List<string> result)
    {
        if (root is System.Windows.Media.Animation.IAnimatable a && a.HasAnimatedProperties)
        {
            var fe = root as FrameworkElement;
            // Live probe: clear the opacity animation and re-check — distinguishes a
            // stale held clock (clears) from an actively re-applied animation (stays).
            var wasOpacity = fe is not null && fe.Opacity < 1;
            fe?.BeginAnimation(OpacityProperty, null);
            if (fe is not null) fe.Opacity = 1;
            var stillAnimated = a.HasAnimatedProperties;
            result.Add($"{root.GetType().Name}[{fe?.Name ?? "-"}] wasOpacity={wasOpacity} stillAnimated={stillAnimated}");
        }
        var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
            DumpActiveAnimationsRecursive(System.Windows.Media.VisualTreeHelper.GetChild(root, i), result);
    }


    /// <summary>Called while an Explorer drag is over the island: reveal the shelf.
    /// Uses the same shelf-drag session flags so the reveal/revert cannot race.</summary>
    private void OnShelfDragSessionStarted()
    {
        _shelfDragActive = true;
        _shelfRevertTimer?.Stop();
        _shelfOpen?.Invoke();
        UpdateClip();
    }

    /// <summary>Drag left without dropping: revert to the previous state shortly.</summary>
    private void OnShelfDragEnded()
    {
        // Small grace so WPF's spurious leave/enter pairs do not flicker.
        _shelfRevertTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _shelfRevertTimer.Tick += (_, _) =>
        {
            _shelfRevertTimer.Stop();
            _shelfDragActive = false;
            _shelfClose?.Invoke(); // restore only a shelf MiMi auto-opened for this drag
        };
        _shelfRevertTimer.Start();
    }

    private void CurrentLyric_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_mediaVm.CanSeekCurrentLyric) return;
        _mediaVm.SeekToCurrentLyric();
        e.Handled = true;
    }

    private void CompactMedia_Click(object sender, MouseButtonEventArgs e)
    {
        if (_pinnedContent is not MediaCompactView media) return;
        if (_hoverExpanded) CollapseHover();
        else
        {
            _hoverExpanded = true;
            ApplyContent(new MediaExpandedView(media.Media));
        }
        e.Handled = true;
    }

    /// <summary>Explicit agent-options request. Hover may already have opened the
    /// panel; repeated clicks must keep it open rather than toggle it closed.</summary>
    public void OpenAgentOptions()
    {
        if (_pinnedContent is not (AgentCompactView or MediaCompactView { HasAgentBadge: true })) return;
        if (_hoverExpanded) return;
        _hoverExpanded = true;
        ApplyContent(_agentPanelProvider());
    }

    private void CompactAgent_Click(object sender, MouseButtonEventArgs e)
    {
        OpenAgentOptions();
        e.Handled = true;
    }

    private void AgentBadge_Click(object sender, MouseButtonEventArgs e)
    {
        OpenAgentOptions();
        e.Handled = true;
    }

    private void IdleFaceClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _quickAccessOpen?.Invoke();
    }

    private void PlayHello()
    {
        if (FindMascot(Pill) is not MiMiMascotControl mascot) return;
        mascot.PlayHello();
    }

    private static MiMiMascotControl? FindMascot(DependencyObject root)
    {
        if (root is MiMiMascotControl mascot) return mascot;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindMascot(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    private void WireClipboardDragOut()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => WireClipboardDragOutRecursive(Pill));
    }

    private void WireClipboardDragOutRecursive(DependencyObject root)
    {
        if (root is Border { Tag: string text } border
            && root is FrameworkElement { DataContext: ClipboardEntryViewModel })
        {
            border.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (IsInsideButton(e.OriginalSource)) return;
                var data = new DataObject(DataFormats.UnicodeText, text);
                DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);
            };
        }
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            WireClipboardDragOutRecursive(VisualTreeHelper.GetChild(root, i));
    }

    private void UpdateClip()
    {
        if (Pill.ActualWidth <= 0 || Pill.ActualHeight <= 0) return;
        var radius = Math.Min(Pill.CornerRadius.TopLeft, Math.Min(Pill.ActualWidth, Pill.ActualHeight) / 2);
        var halfPixel = 0.5 / VisualTreeHelper.GetDpi(this).DpiScaleX;
        Pill.Clip = new RectangleGeometry(
            new Rect(-halfPixel, -halfPixel, Pill.ActualWidth + halfPixel * 2, Pill.ActualHeight + halfPixel * 2),
            radius + halfPixel, radius + halfPixel);
    }

    // ---- template hookup (elements inside DataTemplates, found by walking the tree) ----

    private void HookTemplate(IslandContent content)
    {
        // AllowDrop stays true in every state: a drop on any panel shelves the file.

        SyncProximityTimer();

        StopEq();
        _eqHost = null;
        _progressSlider = null;

        switch (content)
        {
            case MediaCompactView compact:
                if (FindDescendant(Pill, "Eq") is Panel eq)
                {
                    _eqHost = eq;
                    if (compact.Media.IsPlaying) StartEq();
                }
                break;

            case MediaExpandedView media:
                if (FindDescendant(Pill, "LyricsToggle") is Button lyricsToggle)
                    lyricsToggle.Click += (_, _) => media.Media.ToggleLyrics();
                if (FindDescendant(Pill, "ProgressSlider") is Slider slider)
                {
                    _progressSlider = slider;
                    slider.IsEnabled = !double.IsNaN(media.Media.Progress);
                    slider.Value = double.IsNaN(media.Media.Progress) ? 0 : media.Media.Progress;
                    slider.AddHandler(Thumb.DragStartedEvent,
                        new DragStartedEventHandler((_, _) => _sliderDragging = true));
                    slider.AddHandler(Thumb.DragCompletedEvent,
                        new DragCompletedEventHandler((_, _) =>
                        {
                            _sliderDragging = false;
                            media.Media.SeekToFraction(slider.Value);
                        }));
                    slider.MouseUp += (_, _) => { if (!_sliderDragging) media.Media.SeekToFraction(slider.Value); };
                }
                UpdatePlayGlyph(media.Media.IsPlaying);
                break;

            case BrightnessView brightness:
                if (FindDescendant(Pill, "BrightnessSlider") is Slider brightnessSlider)
                {
                    brightnessSlider.AddHandler(Thumb.DragStartedEvent,
                        new DragStartedEventHandler((_, _) => { _brightnessDragging = true; _brightnessDidDrag = false; }));
                    brightnessSlider.AddHandler(Thumb.DragCompletedEvent,
                        new DragCompletedEventHandler((_, _) =>
                        {
                            _brightnessDragging = false;
                            _brightnessDidDrag = true;
                            _ = brightness.Brightness.ApplyPreviewAsync();
                            Dispatcher.BeginInvoke(() => _brightnessDidDrag = false, DispatcherPriority.Background);
                        }));
                    brightnessSlider.MouseUp += (_, _) =>
                    {
                        if (!_brightnessDragging && !_brightnessDidDrag)
                            _ = brightness.Brightness.ApplyPreviewAsync();
                    };
                }
                break;

            case ClipboardView:
                WireClipboardDragOut();
                break;

            case QuickAccessView:
                // In a hover preview the hub is not pinned, so the normal Back command
                // is a no-op. Collapse the preview explicitly; pinned hubs still use
                // their command to clear the utility pin.
                if (FindDescendant(Pill, "QuickAccessBack") is Button back)
                    back.Click += (_, _) => { if (_hoverExpanded) CollapseHover(); };
                break;

            case RestingView resting:
                // Click the face -> Quick Access hub (island-first interaction).
                if (FindDescendant(Pill, "IdleFaceHost") is FrameworkElement idleHost)
                {
                    idleHost.MouseLeftButtonDown -= IdleFaceClick;
                    idleHost.MouseLeftButtonDown += IdleFaceClick;
                }
                break;

            case ShelfView shelf:
                WireShelfDragOut();
                _ = shelf; // rows wire via WireShelfDragOut
                break;
            }
    }

    private void MediaVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MediaViewModel media) return;

        if (e.PropertyName == nameof(MediaViewModel.IsPlaying))
        {
            if (_renderedContent is MediaCompactView) { if (media.IsPlaying) StartEq(); else StopEq(); }
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => UpdatePlayGlyph(media.IsPlaying));
        }
        else if (e.PropertyName == nameof(MediaViewModel.Progress) && _progressSlider is not null && !_sliderDragging)
        {
            if (!double.IsNaN(media.Progress)) _progressSlider.Value = media.Progress;
        }
    }

    private void UpdatePlayGlyph(bool playing)
    {
        if (FindDescendant(Pill, "PlayPauseGlyph") is TextBlock glyph)
            glyph.Text = playing ? "\uE769" /* pause */ : "\uE768" /* play */;
    }

    private void StartEq()
    {
        if (_eqHost is null || MascotOptions.ReducedMotion || !MascotOptions.AnimationsEnabled) return;
        StopEq();
        var speed = _config.Current.AnimationSpeed;
        _eqStoryboard = new Storyboard();
        foreach (var bar in _eqHost.Children.OfType<Rectangle>())
        {
            // Template-declared transforms arrive frozen; replace with a live one to animate.
            if (bar.RenderTransform is not ScaleTransform || (bar.RenderTransform as ScaleTransform)?.IsFrozen != false)
                bar.RenderTransform = new ScaleTransform(1, 1);
            var anim = new DoubleAnimation
            {
                From = 0.25,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds((520 + 130 * _eqStoryboard.Children.Count) / speed),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(anim, bar);
            Storyboard.SetTargetProperty(anim, new PropertyPath("RenderTransform.ScaleY"));
            _eqStoryboard.Children.Add(anim);
        }
        _eqStoryboard.Begin(_eqHost, isControllable: false);
    }

    private void StopEq()
    {
        if (_eqStoryboard is not null && _eqHost is not null) _eqStoryboard.Remove(_eqHost);
        _eqStoryboard = null;
    }

    // ---- hover expansion ------------------------------------------------------

    private void StateTimerTick(object? sender, EventArgs e)
    {
        _stateTimer.Stop();
        if (_pointerOver)
        {
            TryExpandHover();
        }
        else if (_hoverExpanded)
        {
            CollapseHover();
        }
    }

    private void TryExpandHover()
    {
        if (_hoverExpanded || !_config.Current.ExpandOnHover) return;
        if (_pinnedContent is not { Expandable: true } content) return;

        // While music plays, the compact pill's agent badge (right edge) opens the
        // multi-agent panel; hovering anywhere else opens the media controls.
        if (content is MediaCompactView { HasAgentBadge: true } && PointerOverAgentBadge())
        {
            _hoverExpanded = true;
            ApplyContent(_agentPanelProvider());
            return;
        }

        if (Expanded(content) is not { } expandedView) return;
        _hoverExpanded = true;
        ApplyContent(expandedView);
    }

    private bool PointerOverAgentBadge() =>
        Mouse.GetPosition(Pill).X >= Pill.Width - 44;

    public void CollapseHover()
    {
        if (!_hoverExpanded) return;
        _hoverExpanded = false;
        if (_pinnedContent is not null) ApplyContent(_pinnedContent);
    }



    // ---- layout / DPI ------------------------------------------------------------

    private void ApplySettings()
    {
        var scale = Math.Clamp(_config.Current.Scale, 0.75, 1.5);
        Pill.LayoutTransform = new ScaleTransform(scale, scale);
        ApplyTextScale();
        PositionWindow();
    }

    /// <summary>Text size scales only typography (not the whole island). DynamicResource
    /// lookups update every visible label in place, including DataTemplates.</summary>
    private void ApplyTextScale()
    {
        var scale = _config.Current.TextSize switch
        {
            "small" => 0.94,
            "large" => 1.12,
            _ => 1.0,
        };

        void Set(string key, double baseSize) =>
            Resources[key] = Math.Round(baseSize * scale * 2, MidpointRounding.AwayFromZero) / 2;

        Set("FontMicro", 12);
        Set("FontCaption", 13);
        Set("FontBody", 14);
        Set("FontPrimary", 15);
        Set("FontTitle", 16.5);
        Set("FontMono", 13);
    }

    public void PositionWindow()
    {
        var work = SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var rawLeft = work.Left + (work.Width - Width) / 2 + _config.Current.HorizontalOffsetDip;
        var rawTop = work.Top + _config.Current.TopMarginDip;
        // Keep the layered window itself on whole device pixels; otherwise the
        // rounded edge is resampled by the desktop compositor.
        Left = Math.Round(rawLeft * dpi.DpiScaleX) / dpi.DpiScaleX;
        Top = Math.Round(rawTop * dpi.DpiScaleY) / dpi.DpiScaleY;
    }

    // ---- helpers -------------------------------------------------------------------

    /// <summary>Wires every shelf row for drag-out: holding the row starts a real
    /// OLE drag (FileDrop) that Explorer and most apps accept. Runs on the UI thread
    /// and blocks inside DoDragDrop until the drop finishes (standard WPF pattern).</summary>
    private void WireShelfDragOut()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            WireShelfDragOutRecursive(Pill);
        });
    }

    private void WireShelfDragOutRecursive(DependencyObject root)
    {
        if (root is Border { Tag: string path } border && root is FrameworkElement { DataContext: ShelfRowViewModel })
        {
            border.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 1 || IsInsideButton(e.OriginalSource)) return;
                if (!File.Exists(path) && !Directory.Exists(path)) return;
                var data = new DataObject(DataFormats.FileDrop, new[] { path });
                // References only: never advertise Move, which could let a drop target
                // remove the user's original after MiMi described the shelf as copy-only.
                DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);
            };
        }
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            WireShelfDragOutRecursive(VisualTreeHelper.GetChild(root, i));
    }

    private static bool IsInsideButton(object? source)
    {
        for (var current = source as DependencyObject; current is not null;
             current = current is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
        {
            if (current is ButtonBase) return true;
        }
        return false;
    }

    private static FrameworkElement? FindDescendant(DependencyObject root, string name)
    {
        if (root is FrameworkElement { Name: var n } && n == name) return root as FrameworkElement;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindDescendant(VisualTreeHelper.GetChild(root, i), name);
            if (found is not null) return found;
        }
        return null;
    }
}
