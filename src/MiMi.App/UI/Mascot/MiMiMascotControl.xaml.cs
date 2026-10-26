using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MiMi.App.UI.Mascot;

/// <summary>
/// MiMi's face: two soft capsule eyes, a warm gold glow pooled beneath them, and a
/// whisper of a mouth. The face self-manages its life signs (blink, occasional eye
/// dart, glow breathing) and runs them only while it is visible and loaded — a hidden
/// mascot costs nothing. All motion is gradient/transform work; no bitmap effects.
///
/// Expression grammar (subtle deltas, never cartoonish):
///   Calm      relaxed capsules, slow blink, gentle glow breathing, faint mouth
///   Lively    glow breathes deeper and quicker, slight eye bob with the beat
///   Alert     eyes open wider, glow brighter and steady, mouth hidden
///   Focused   eyes slightly narrowed, glow steady and dim, mouth hidden
///   Concerned eyes tilt faintly inward, glow dims, mouth hidden
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

    // Per-eye pose parameters per expression (scaleX, scaleY, tilt degrees).
    private static (double Sx, double Sy, double Tilt) Pose(MascotExpression e) => e switch
    {
        MascotExpression.Calm => (1.0, 1.0, 0),
        MascotExpression.Lively => (0.92, 0.94, 0),
        MascotExpression.Alert => (1.12, 1.18, 0),
        MascotExpression.Focused => (0.85, 0.92, 0),
        MascotExpression.Concerned => (0.95, 0.95, -4),
        _ => (1, 1, 0),
    };

    private static double GlowLevel(MascotExpression e) => e switch
    {
        MascotExpression.Calm => 0.9,
        MascotExpression.Lively => 1.0,
        MascotExpression.Alert => 1.25,
        MascotExpression.Focused => 0.6,
        MascotExpression.Concerned => 0.45,
        _ => 0.9,
    };

    private static bool MouthVisible(MascotExpression e) =>
        e is MascotExpression.Calm or MascotExpression.Lively;

    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _dartTimer;
    private Storyboard? _glowPulse;
    private Storyboard? _bobStoryboard;
    private readonly Random _random = new();
    private bool _templateReady;

    public MiMiMascotControl()
    {
        InitializeComponent();
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

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _blinkTimer.Tick += (_, _) => Blink();

        _dartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
        _dartTimer.Tick += (_, _) => Dart();

        MascotOptions.Changed += OnOptionsChanged;
        Unloaded += (_, _) => MascotOptions.Changed -= OnOptionsChanged;
    }

    private static void OnExpressionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MiMiMascotControl)d).ApplyExpression((MascotExpression)e.NewValue, animated: true);

    private void OnOptionsChanged()
    {
        if (!_templateReady) return;
        // Re-apply from scratch: intensities and the enable flag may all have changed.
        ApplyExpression(Expression, animated: false);
        if (MascotOptions.FaceEnabled && IsVisible && IsLoaded) StartLife(); else StopLife();
    }

    // ---- expression ------------------------------------------------------------

    private void ApplyExpression(MascotExpression expression, bool animated)
    {
        if (!_templateReady) return;

        var intensity = MascotOptions.AnimationIntensity;
        var glowOn = MascotOptions.GlowEnabled && MascotOptions.FaceEnabled;
        var level = glowOn ? GlowLevel(expression) * MascotOptions.GlowIntensity * 2 : 0.0; // 0..~2.5 base scale
        var (sx, sy, tilt) = Pose(expression);

        var poseDuration = TimeSpan.FromMilliseconds(animated ? 220 : 0);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

        AnimateScale(EyeLBlink, sx, sy, poseDuration, ease, animated);
        AnimateScale(EyeRBlink, sx, sy, poseDuration, ease, animated);
        // Concerned: tilt the eyes toward each other, faintly.
        AnimateAngle(EyeLTilt, tilt, poseDuration, ease, animated);
        AnimateAngle(EyeRTilt, -tilt, poseDuration, ease, animated);

        // Mouth: fade in/out rather than popping.
        AnimateScalar(Mouth, OpacityProperty, MouthVisible(expression) ? 0.32 : 0.0, poseDuration, ease, animated);

        // Glow: static level + optional breathing pulse (Lively breathes deeper/quicker,
        // Calm breathes very gently, attention states stay steady for composure).
        StopGlowPulse();
        if (!glowOn)
        {
            SetGlow(0);
            return;
        }

        var breathing = expression switch
        {
            MascotExpression.Lively => (Base: level * 0.8, Depth: 0.30 * intensity, Seconds: 2.4),
            MascotExpression.Calm => (Base: level * 0.8, Depth: 0.10 * intensity, Seconds: 6.0),
            _ => (Base: level * 0.8, Depth: 0.0, Seconds: 6.0),
        };
        SetGlow(breathing.Base);

        if (breathing.Depth > 0.01)
        {
            _glowPulse = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            foreach (var target in new UIElement[] { GlowWide, GlowCore })
            {
                var pulse = new DoubleAnimation
                {
                    From = Math.Max(0, breathing.Base - breathing.Depth / 2),
                    To = Math.Min(1, breathing.Base + breathing.Depth / 2),
                    Duration = TimeSpan.FromSeconds(breathing.Seconds),
                    AutoReverse = true,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                Storyboard.SetTarget(pulse, target);
                Storyboard.SetTargetProperty(pulse, new PropertyPath(OpacityProperty));
                _glowPulse.Children.Add(pulse);
            }
            _glowPulse.Begin(this, isControllable: false);
        }

        // Eye bob only in Lively: a slow ±1 px float, barely there.
        StopBob();
        if (expression == MascotExpression.Lively)
        {
            _bobStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
            foreach (var t in new[] { EyeLMove, EyeRMove })
            {
                var bob = new DoubleAnimation
                {
                    From = 0,
                    To = -1.2,
                    Duration = TimeSpan.FromSeconds(1.6 / intensity),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                Storyboard.SetTarget(bob, t);
                Storyboard.SetTargetProperty(bob, new PropertyPath(TranslateTransform.YProperty));
                _bobStoryboard.Children.Add(bob);
            }
            _bobStoryboard.Begin(this, isControllable: false);
        }
    }

    private void SetGlow(double level)
    {
        // Level 0..~1.6 maps onto both gradient layers' peak alphas.
        GlowWide.Opacity = Math.Clamp(level, 0, 1);
        GlowCore.Opacity = Math.Clamp(level * 0.85, 0, 1);
    }

    private void StopGlowPulse()
    {
        if (_glowPulse is null) return;
        _glowPulse.Remove(this);
        _glowPulse = null;
        // Remove leaves the last animated value behind; clear to the base level.
        GlowWide.BeginAnimation(OpacityProperty, null);
        GlowCore.BeginAnimation(OpacityProperty, null);
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

    private static void AnimateScalar(UIElement target, DependencyProperty dp, double to, TimeSpan d,
        IEasingFunction ease, bool animate)
    {
        if (!animate) { target.BeginAnimation(dp, null); target.SetValue(dp, to); return; }
        target.BeginAnimation(dp, new DoubleAnimation(to, d) { EasingFunction = ease });
    }

    // ---- life signs ------------------------------------------------------------

    private void StartLife()
    {
        if (!MascotOptions.FaceEnabled || !IsVisible || !_templateReady) return;
        if (!_blinkTimer.IsEnabled)
        {
            _blinkTimer.Interval = NextBlinkDelay();
            _blinkTimer.Start();
        }
        if (!_dartTimer.IsEnabled)
        {
            _dartTimer.Interval = TimeSpan.FromSeconds(7 + _random.NextDouble() * 8 / MascotOptions.AnimationIntensity);
            _dartTimer.Start();
        }
    }

    private void StopLife()
    {
        _blinkTimer.Stop();
        _dartTimer.Stop();
        StopGlowPulse();
        StopBob();
    }

    private TimeSpan NextBlinkDelay() =>
        TimeSpan.FromSeconds((2.5 + _random.NextDouble() * 5.5) / MascotOptions.AnimationIntensity);

    /// <summary>A blink: quick close-open. Occasionally a double blink (very human).</summary>
    private void Blink()
    {
        var close = TimeSpan.FromMilliseconds(70);
        var open = TimeSpan.FromMilliseconds(110);
        foreach (var eye in new[] { EyeLBlink, EyeRBlink })
        {
            eye.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(eye.ScaleY, 0.08, close) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        }
        // Reopen after the close; schedule the next blink after fully open.
        _blinkTimer.Stop();
        var reopen = new DispatcherTimer { Interval = close };
        reopen.Tick += (_, _) =>
        {
            reopen.Stop();
            foreach (var eye in new[] { EyeLBlink, EyeRBlink })
            {
                eye.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(0.08, 1, open) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
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
}
