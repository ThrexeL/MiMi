using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MiMi.App.UI.Mascot;

/// <summary>
/// MiMi's face: two tall, softly gold-glowing eyes (Nusa-inspired), balanced spacing,
/// and a whisper of a curved smile in relaxed states. The face self-manages its life
/// signs (blink, occasional glance) and runs them only while visible and loaded — a
/// hidden mascot costs nothing. The wider gold aurora is NOT part of this control; it
/// lives inside the island pill (see IslandWindow's internal glow layer).
///
/// Expression grammar (subtle deltas, never cartoonish):
///   Calm      tall glowing eyes, slow blink, faint smile
///   Lively    eyes slightly eased, gentle float, smile a touch brighter
///   Alert     eyes enlarge, bloom brightens, smile hidden — attention without motion
///   Focused   eyes slightly narrowed, smile hidden
///   Concerned  eyes tilt faintly inward, bloom dims, smile hidden
///   Sleepy    eyes half-lidded, bloom dims to an ember
/// </summary>
public partial class MiMiMascotControl : UserControl
{
    public static readonly DependencyProperty ExpressionProperty = DependencyProperty.Register(
        nameof(Expression), typeof(MascotExpression), typeof(MiMiMascotControl),
        new PropertyMetadata(MascotExpression.Calm, OnExpressionChanged));

    public MascotExpression Expression
    {
        get => (MascotExpression)GetValue(ExpressionProperty);
        set => SetValue(ExpressionProperty, value);
    }

    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _dartTimer;
    private Storyboard? _bobStoryboard;
    private Storyboard? _sleepZStoryboard;
    private readonly Random _random = new();
    private MascotExpression _lastExpression = MascotExpression.Calm;
    private bool _templateReady;

    // Per-eye pose per expression (scaleX, scaleY, tilt degrees). The base shape is a
    // tall oval; asymmetry only happens mid-blink and is always restored.
    private static (double Sx, double Sy, double Tilt) Pose(MascotExpression e) => e switch
    {
        MascotExpression.Calm => (1.0, 1.0, 0),
        MascotExpression.Lively => (0.96, 0.96, 0),
        MascotExpression.Alert => (1.16, 1.14, 0),
        MascotExpression.Focused => (0.88, 0.88, 0),
        MascotExpression.Concerned => (1.0, 0.92, -4),
        MascotExpression.Curious => (1.08, 1.08, 0),
        // Happy: a brief squint, never a crescent-shaped cartoon eye.
        MascotExpression.Happy => (1.0, 0.5, 0),
        // Sleepy: softly half-lidded, still wide enough to read as eyes.
        MascotExpression.Sleepy => (1.0, 0.34, 0),
        _ => (1, 1, 0),
    };

    public MiMiMascotControl()
    {
        InitializeComponent();

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _blinkTimer.Tick += (_, _) => Blink();
        _dartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
        _dartTimer.Tick += (_, _) => Dart();

        Loaded += (_, _) =>
        {
            _templateReady = true;
            PlayWake();
            ApplyExpression(Expression, animated: false);
            StartLife();
        };
        Unloaded += (_, _) => StopLife();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) StartLife(); else StopLife();
        };

        MascotOptions.Changed += OnOptionsChanged;
        Unloaded += (_, _) => MascotOptions.Changed -= OnOptionsChanged;
    }

    private static void OnExpressionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MiMiMascotControl)d).ApplyExpression((MascotExpression)e.NewValue, animated: true);

    private void OnOptionsChanged()
    {
        if (!_templateReady) return;
        ApplyExpression(Expression, animated: false);
        if (MascotOptions.FaceEnabled && IsVisible && IsLoaded) StartLife(); else StopLife();
    }

    // ---- expression ------------------------------------------------------------

    private void ApplyExpression(MascotExpression expression, bool animated)
    {
        if (!_templateReady) return;

        var wasSleepy = _lastExpression == MascotExpression.Sleepy;
        _lastExpression = expression;
        if (MascotOptions.ReducedMotion) animated = false;

        var (sx, sy, tilt) = Pose(expression);
        var poseDuration = TimeSpan.FromMilliseconds(animated ? 220 : 0);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

        AnimateScale(EyeLBlink, sx, sy, poseDuration, ease, animated);
        AnimateScale(EyeRBlink, sx, sy, poseDuration, ease, animated);
        // Concerned: tilt the eyes toward each other, faintly.
        AnimateAngle(EyeLTilt, tilt, poseDuration, ease, animated);
        AnimateAngle(EyeRTilt, -tilt, poseDuration, ease, animated);

        // Dozing gets its own punctuation: two small z's drifting where the clock was.
        UpdateSleepZs(expression);

        // Lively: a barely-there float, like nodding along to the music.
        StopBob();
        if (expression == MascotExpression.Lively && !MascotOptions.ReducedMotion)
        {
            _bobStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
            foreach (var t in new[] { EyeLMove, EyeRMove })
            {
                var bob = new DoubleAnimation
                {
                    From = 0,
                    To = -1.1,
                    Duration = TimeSpan.FromSeconds(1.6 / MascotOptions.AnimationIntensity),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                Storyboard.SetTarget(bob, t);
                Storyboard.SetTargetProperty(bob, new PropertyPath(TranslateTransform.YProperty));
                _bobStoryboard.Children.Add(bob);
            }
            _bobStoryboard.Begin(this, isControllable: false);
        }

        // Happy: one small bounce, under 400 ms, then perfectly still.
        if (animated && expression == MascotExpression.Happy && !MascotOptions.ReducedMotion)
            PlayHappyBounce();

        // Waking from a doze is a gentle "oh, hello" rather than a snap.
        if (animated && wasSleepy && expression != MascotExpression.Sleepy && !MascotOptions.ReducedMotion)
            PlayWake();

        // Sleepy faces breathe very slowly; blinking stops so they look genuinely asleep.
        if (expression == MascotExpression.Sleepy) _blinkTimer.Stop();
        if (expression is MascotExpression.Alert or MascotExpression.Concerned) _dartTimer.Stop();
        if (expression != MascotExpression.Sleepy && IsVisible && IsLoaded
            && MascotOptions.AnimationsEnabled && !MascotOptions.ReducedMotion)
            StartLife();
    }

    private void StopBob()
    {
        if (_bobStoryboard is null) return;
        _bobStoryboard.Remove(this);
        _bobStoryboard = null;
        EyeLMove.BeginAnimation(TranslateTransform.YProperty, null);
        EyeRMove.BeginAnimation(TranslateTransform.YProperty, null);
        EyeLMove.Y = 0;
        EyeRMove.Y = 0;
    }

    private static void AnimateScale(ScaleTransform t, double sx, double sy, TimeSpan d, IEasingFunction ease, bool animate)
    {
        if (!animate) { t.ScaleX = sx; t.ScaleY = sy; return; }
        t.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(sx, d) { EasingFunction = ease });
        t.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(sy, d) { EasingFunction = ease });
    }

    private static void AnimateAngle(RotateTransform t, double degrees, TimeSpan d, IEasingFunction ease, bool animate)
    {
        if (!animate) { t.Angle = degrees; return; }
        t.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(degrees, d) { EasingFunction = ease });
    }

    private static void AnimateOpacity(UIElement element, double opacity, TimeSpan d, IEasingFunction ease, bool animate)
    {
        if (!animate) { element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = opacity; return; }
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(opacity, d) { EasingFunction = ease });
    }

    // ---- life signs ------------------------------------------------------------

    private void StartLife()
    {
        if (!MascotOptions.FaceEnabled) return;
        if (!IsVisible || !_templateReady) return;
        // Sleepy has no blink/dart, but it does keep its drifting z's.
        if (Expression == MascotExpression.Sleepy) { UpdateSleepZs(Expression); return; }
        if (MascotOptions.ReducedMotion) return;
        if (!MascotOptions.AnimationsEnabled) return;
        if (!_blinkTimer.IsEnabled)
        {
            _blinkTimer.Interval = NextBlinkDelay();
            _blinkTimer.Start();
        }
        // Alert/concerned faces stay attentive and still; no darting eyes while the
        // user is deciding or reading an error.
        if (Expression is not (MascotExpression.Alert or MascotExpression.Concerned)
            && !_dartTimer.IsEnabled)
        {
            _dartTimer.Interval = TimeSpan.FromSeconds(7 + _random.NextDouble() * 8 / MascotOptions.AnimationIntensity);
            _dartTimer.Start();
        }
    }

    private void StopLife()
    {
        _blinkTimer.Stop();
        _dartTimer.Stop();
        StopBob();
        StopSleepZs();
    }

    /// <summary>Two small z's that rise and fade above the dozing face. With motion off
    /// they stay as quiet static marks, so the sleepy state never loses its cue.</summary>
    private void UpdateSleepZs(MascotExpression expression)
    {
        if (expression != MascotExpression.Sleepy) { StopSleepZs(); return; }
        if (!MascotOptions.AnimationsEnabled || MascotOptions.ReducedMotion)
        {
            StopSleepZs(reveal: true);
            return;
        }
        if (_sleepZStoryboard is not null) return;
        // Three gentle drift loops, then the z's freeze in place: a sleepy cue that
        // never becomes a permanent 60 fps rendering loop at idle.
        _sleepZStoryboard = new Storyboard { RepeatBehavior = new RepeatBehavior(3) };
        AddZ(_sleepZStoryboard, SleepZ1, SleepZ1Move, delay: 0, peak: 0.55, seconds: 2.8);
        AddZ(_sleepZStoryboard, SleepZ2, SleepZ2Move, delay: 0.9, peak: 0.4, seconds: 3.1);
        _sleepZStoryboard.Completed += (_, _) =>
        {
            // Freeze as static marks (still visible), then stop all animations.
            SleepZ1.BeginAnimation(OpacityProperty, null);
            SleepZ2.BeginAnimation(OpacityProperty, null);
            SleepZ1Move.BeginAnimation(TranslateTransform.YProperty, null);
            SleepZ2Move.BeginAnimation(TranslateTransform.YProperty, null);
            SleepZ1.Opacity = 0.5;
            SleepZ2.Opacity = 0.32;
            _sleepZStoryboard.Remove(this);
            _sleepZStoryboard = null;
        };
        _sleepZStoryboard.Begin(this, isControllable: false);
    }

    private static void AddZ(Storyboard board, TextBlock z, TranslateTransform move,
        double delay, double peak, double seconds)
    {
        var fade = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = TimeSpan.FromSeconds(delay),
            Duration = TimeSpan.FromSeconds(seconds),
        };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(peak, KeyTime.FromPercent(0.3)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(peak, KeyTime.FromPercent(0.6)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        Storyboard.SetTarget(fade, z);
        Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));
        board.Children.Add(fade);

        var rise = new DoubleAnimation(1.4, -3.2, TimeSpan.FromSeconds(seconds))
        {
            BeginTime = TimeSpan.FromSeconds(delay),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(rise, move);
        Storyboard.SetTargetProperty(rise, new PropertyPath(TranslateTransform.YProperty));
        board.Children.Add(rise);
    }

    private void StopSleepZs(bool reveal = false)
    {
        if (_sleepZStoryboard is not null)
        {
            _sleepZStoryboard.Remove(this);
            _sleepZStoryboard = null;
        }
        SleepZ1.BeginAnimation(OpacityProperty, null);
        SleepZ2.BeginAnimation(OpacityProperty, null);
        SleepZ1Move.BeginAnimation(TranslateTransform.YProperty, null);
        SleepZ2Move.BeginAnimation(TranslateTransform.YProperty, null);
        SleepZ1.Opacity = reveal ? 0.5 : 0;
        SleepZ2.Opacity = reveal ? 0.32 : 0;
        SleepZ1Move.Y = 0;
        SleepZ2Move.Y = 0;
    }

    private TimeSpan NextBlinkDelay() =>
        TimeSpan.FromSeconds((2.5 + _random.NextDouble() * 5.5) / MascotOptions.AnimationIntensity);

    /// <summary>A blink: quick close-open. Occasionally a double blink (very human).
    /// The circles flatten only mid-blink and always return to perfect circles.</summary>
    private void Blink()
    {
        var close = TimeSpan.FromMilliseconds(70);
        var open = TimeSpan.FromMilliseconds(110);
        foreach (var eye in new[] { EyeLBlink, EyeRBlink })
        {
            eye.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(eye.ScaleY, 0.12, close) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        }
        _blinkTimer.Stop();
        var reopen = new DispatcherTimer { Interval = close };
        reopen.Tick += (_, _) =>
        {
            reopen.Stop();
            if (Expression == MascotExpression.Sleepy)
            {
                ApplyExpression(MascotExpression.Sleepy, animated: false);
                return;
            }
            var poseY = Pose(Expression).Sy;
            foreach (var eye in new[] { EyeLBlink, EyeRBlink })
            {
                eye.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(0.12, poseY, open) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            }
            _blinkTimer.Interval = _random.NextDouble() < 0.2
                ? TimeSpan.FromMilliseconds(260) // double blink soon
                : NextBlinkDelay();
            _blinkTimer.Start();
        };
        reopen.Start();
    }

    /// <summary>Rare tiny glance to one side, then back. Keeps MiMi feeling aware.</summary>
    private void Dart()
    {
        var direction = _random.NextDouble() < 0.5 ? -1.4 : 1.4;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        foreach (var eye in new[] { EyeLMove, EyeRMove })
        {
            eye.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, direction, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        }
        var back = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        back.Tick += (_, _) =>
        {
            back.Stop();
            foreach (var eye in new[] { EyeLMove, EyeRMove })
            {
                eye.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(direction, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
            }
        };
        back.Start();
    }

    /// <summary>Wake-up: a soft pop as the face appears — MiMi "notices" you.</summary>
    private void PlayWake()
    {
        WakeScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 },
        });
        WakeScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 },
        });
    }

    /// <summary>Celebration: one tiny bounce (≈360 ms total), then still.</summary>
    private void PlayHappyBounce()
    {
        var bounce = new DoubleAnimation(1.0, 1.05, TimeSpan.FromMilliseconds(160))
        {
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
        };
        WakeScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        WakeScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
    }
}
