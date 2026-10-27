using System.ComponentModel;
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
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _progressTimer;
    private readonly Random _random = new();

    private Storyboard? _sizeStoryboard;
    private Panel? _eqHost;
    private Storyboard? _eqStoryboard;
    private Slider? _progressSlider;
    private bool _sliderDragging;
    private INotifyPropertyChanged? _glowContent;

    private const long WsExNoActivate = 0x08000000;
    private const long WsExToolWindow = 0x00000080;
    private const int HotkeyId = 0xB00B; // Ctrl+Alt+M

    public IntPtr PillHandle { get; private set; }

    /// <summary>Single authority for whether the island is on screen (P3).</summary>
    public VisibilityCoordinator Coordinator { get; } = new();

    /// <summary>Raised when the user's manual preference changes (tray menu reflects it).</summary>
    public event Action<VisibilityPreference>? VisibilityPreferenceChanged;

    /// <summary>True when Ctrl+Alt+M was registered successfully; false if another
    /// application already owns the combination.</summary>
    public bool HotkeyRegistered { get; private set; }

    private bool _hotkeyHooked;

    private double _glowLevel = 0.55; // current island-bottom glow level (0..1)
    private readonly Func<AgentActivityPanelView> _agentPanelProvider;

    public IslandWindow(ConfigStore config,
        Func<IslandContent?> contentProvider,
        Func<string, TimeSpan?> approvalTimeLeft,
        MediaViewModel mediaVm,
        Func<AgentActivityPanelView>? agentPanelProvider = null)
    {
        _agentPanelProvider = agentPanelProvider ?? (() => new AgentActivityPanelView([]));
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
        };

        Closed += (_, _) => UnregisterGlobalHotkey();

        Coordinator.Changed += RecomputeVisibility;

        PositionWindow();
        ApplySettings();
        _config.Changed += _ => Dispatcher.BeginInvoke(ApplySettings);

        // One timer drives both the hover-open delay and the leave-grace period.
        _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _stateTimer.Tick += StateTimerTick;
        MouseEnter += (_, _) => { _pointerOver = true; _stateTimer.Interval = TimeSpan.FromMilliseconds(140); _stateTimer.Start(); };
        MouseLeave += (_, _) =>
        {
            _pointerOver = false;
            _stateTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(250, _config.Current.CollapseDelayMs));
            _stateTimer.Start();
        };

        Pill.SizeChanged += (_, _) => UpdateClip();

        // Clock (resting pill only) — 30 s cadence is plenty for a minute display.
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clockTimer.Tick += (_, _) =>
        {
            if (_pinnedContent is RestingView resting) resting.Clock = DateTime.Now.ToString("HH:mm");
        };
        _clockTimer.Start();

        // 1 Hz ticker: extrapolated media position + approval countdowns.
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _progressTimer.Tick += (_, _) =>
        {
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

        // Show Desktop must never minimize MiMi (belt: toolwindows are normally exempt).
        if (msg == Interop.NativeMethods.WM_SYSCOMMAND && wParam == Interop.NativeMethods.SC_MINIMIZE)
        {
            handled = true;
            return IntPtr.Zero;
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
            userInitiatedHide, reducedMotion: false, _fadeInFlight);
        ExecuteFadePlan(plan);
        if (visible) ReassertTopmost();
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
        if ((exStyle & Interop.NativeMethods.WS_EX_TOPMOST) == 0)
        {
            Interop.NativeMethods.SetWindowPos(PillHandle, Interop.NativeMethods.HwndTopmost, 0, 0, 0, 0,
                Interop.NativeMethods.SWP_NOMOVE | Interop.NativeMethods.SWP_NOSIZE
                | Interop.NativeMethods.SWP_NOACTIVATE | Interop.NativeMethods.SWP_NOOWNERZORDER);
            Logger.Info("Visibility: re-asserted topmost after shell z-order change.");
        }
        else if (Interop.NativeMethods.IsIconic(PillHandle))
        {
            Interop.NativeMethods.ShowWindow(PillHandle, 4 /* SW_SHOWNOACTIVATE */);
            Logger.Info("Visibility: restored minimized window without activation.");
        }
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

    // ---- island-bottom glow -----------------------------------------------------
    // A separate visual system from the face: warm light leaking from beneath the
    // floating island, bound live to the pill's animated bounds in XAML.

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
        GlowRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(Math.Min(_glowLevel, 1), TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private void RefreshGlow()
    {
        if (_renderedContent is not null) UpdateGlow(_renderedContent);
    }

    private void OnGlowContentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RestingView.Expression)) return;
        if (sender is IslandContent content) UpdateGlow(content);
    }

    // ---- content application ---------------------------------------------------

    private IslandContent? Expanded(IslandContent content) => content switch
    {
        MediaCompactView m => new MediaExpandedView(m.Media),
        // Hovering the agent pill opens the full multi-session activity panel.
        AgentCompactView => _agentPanelProvider(),
        _ => null,
    };

    private void ApplyContent(IslandContent content)
    {
        if (ReferenceEquals(content, _renderedContent)) return;
        _renderedContent = content;

        var speed = _config.Current.AnimationSpeed;
        var duration = TimeSpan.FromMilliseconds(240 / speed);
        var swapAt = TimeSpan.FromMilliseconds(70 / speed);

        var fadeOut = new DoubleAnimation(1, 0, swapAt);
        UpdateGlow(content);

        fadeOut.Completed += (_, _) =>
        {
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
            AnimatePillSize(content.TargetWidth, targetHeight, duration - swapAt);

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
                    AnimatePillSize(content.TargetWidth, corrected, TimeSpan.FromMilliseconds(140));
                }
            });
        }
    }

    private void AnimatePillSize(double width, double height, TimeSpan duration)
    {
        _sizeStoryboard?.Remove(Pill);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _sizeStoryboard = new Storyboard
        {
            Children =
            {
                new DoubleAnimation(Pill.ActualWidth, width, duration) { EasingFunction = ease },
                new DoubleAnimation(Pill.ActualHeight, height, duration) { EasingFunction = ease },
            },
        };
        Storyboard.SetTarget(_sizeStoryboard.Children[0], Pill);
        Storyboard.SetTargetProperty(_sizeStoryboard.Children[0], new PropertyPath(WidthProperty));
        Storyboard.SetTarget(_sizeStoryboard.Children[1], Pill);
        Storyboard.SetTargetProperty(_sizeStoryboard.Children[1], new PropertyPath(HeightProperty));
        _sizeStoryboard.Begin(Pill, isControllable: false);
    }

    private void UpdateClip()
    {
        if (Pill.ActualWidth <= 0 || Pill.ActualHeight <= 0) return;
        var radius = Math.Min(Pill.CornerRadius.TopLeft, Math.Min(Pill.ActualWidth, Pill.ActualHeight) / 2);
        Pill.Clip = new RectangleGeometry(
            new Rect(0, 0, Pill.ActualWidth, Pill.ActualHeight), radius, radius);
    }

    // ---- template hookup (elements inside DataTemplates, found by walking the tree) ----

    private void HookTemplate(IslandContent content)
    {
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
        if (_eqHost is null) return;
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
        PositionWindow();
    }

    public void PositionWindow()
    {
        var work = SystemParameters.WorkArea;
        // The canvas is fixed; center it in the primary work area, just below the top edge.
        Left = work.Left + (work.Width - Width) / 2 + _config.Current.HorizontalOffsetDip;
        Top = work.Top + _config.Current.TopMarginDip;
    }

    // ---- helpers -------------------------------------------------------------------

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
