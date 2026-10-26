using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
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
    private readonly DispatcherTimer _blinkTimer;
    private readonly Random _random = new();

    private Storyboard? _sizeStoryboard;
    private Panel? _eqHost;
    private Storyboard? _eqStoryboard;
    private Slider? _progressSlider;
    private bool _sliderDragging;

    private const long WsExNoActivate = 0x08000000;
    private const long WsExToolWindow = 0x00000080;

    public IntPtr PillHandle { get; private set; }

    public IslandWindow(ConfigStore config,
        Func<IslandContent?> contentProvider,
        Func<string, TimeSpan?> approvalTimeLeft,
        MediaViewModel mediaVm)
    {
        _config = config;
        _contentProvider = contentProvider;
        _approvalTimeLeft = approvalTimeLeft;
        _mediaVm = mediaVm;
        _mediaVm.PropertyChanged += MediaVmChanged;

        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            PillHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Interop.NativeMethods.SetWindowLongPtrW(PillHandle, Interop.NativeMethods.GWL_EXSTYLE,
                (IntPtr)(Interop.NativeMethods.GetWindowLongPtrW(PillHandle, Interop.NativeMethods.GWL_EXSTYLE).ToInt64()
                    | WsExNoActivate | WsExToolWindow));
        };

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

        // Mascot blink at a natural, random cadence.
        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _blinkTimer.Tick += (_, _) =>
        {
            if (_renderedContent is RestingView) BlinkEyes();
            _blinkTimer.Interval = TimeSpan.FromSeconds(3 + _random.NextDouble() * 5);
        };
        _blinkTimer.Start();
    }

    // ---- public surface ------------------------------------------------------

    public void ShowPinned(IslandContent content)
    {
        _pinnedContent = content;
        if (!content.Expandable) _hoverExpanded = false;
        ApplyContent(_hoverExpanded ? Expanded(content) ?? content : content);
    }

    public void SetSuppressed(bool suppressed)
    {
        var fade = new DoubleAnimation(suppressed ? 0.0 : 1.0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Pill.BeginAnimation(OpacityProperty, fade);
        if (suppressed && _hoverExpanded) CollapseHover();
    }

    // ---- content application ---------------------------------------------------

    private static IslandContent? Expanded(IslandContent content) => content switch
    {
        MediaCompactView m => new MediaExpandedView(m.Media),
        AgentCompactView a => new AgentExpandedView(a.Agent),
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
        fadeOut.Completed += (_, _) =>
        {
            Host.Content = content;
            Host.ApplyTemplate();

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

            case RestingView:
                if (_random.NextDouble() < 0.8) BlinkEyes();
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

    private void BlinkEyes()
    {
        var duration = TimeSpan.FromMilliseconds(140);
        foreach (var name in new[] { "EyeL", "EyeR" })
        {
            if (FindDescendant(Pill, name) is FrameworkElement eye)
            {
                // Template-declared transforms arrive frozen; replace with a live one.
                if (eye.RenderTransform is not ScaleTransform st || st.IsFrozen)
                    eye.RenderTransform = st = new ScaleTransform(1, 1);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.06, duration)
                {
                    AutoReverse = true,
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
                });
            }
        }
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
        if (Expanded(content) is not { } expandedView) return;
        _hoverExpanded = true;
        ApplyContent(expandedView);
    }

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
