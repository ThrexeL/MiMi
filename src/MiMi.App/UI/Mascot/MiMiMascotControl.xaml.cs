using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MiMi.App.UI.Mascot;

/// <summary>
/// MiMi's face: two perfectly round warm-white eyes, balanced spacing, and a whisper
/// of a curved smile in relaxed states. The face self-manages its life signs (blink,
/// occasional glance) and runs them only while visible and loaded — a hidden mascot
/// costs nothing. The gold ambient glow is NOT part of this control; it belongs to the
/// island's bottom edge (see IslandWindow's glow layer).
///
/// Expression grammar (subtle deltas, never cartoonish):
///   Calm      round eyes, slow blink, faint smile
///   Lively    eyes slightly eased, gentle float, smile a touch brighter
///   Alert     eyes enlarge, smile hidden — attention without motion
///   Focused   eyes slightly narrowed, smile hidden
///   Concerned  eyes tilt faintly inward, smile hidden
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

    // Per-eye pose per expression (scaleX, scaleY, tilt degrees). Circles stay circles:
    // asymmetry only mid-blink, and always restored.
    private static (double Sx, double Sy, double Tilt) Pose(MascotExpression e) => e switch
    {
        MascotExpression.Calm => (1.0, 1.0, 0),
        MascotExpression.Lively => (0.94, 0.94, 0),
        MascotExpression.Alert => (1.2, 1.2, 0),
        MascotExpression.Focused => (0.85, 0.9, 0),
        MascotExpression.Concerned => (1.0, 0.92, -4),
        _ => (1, 1, 0),
    };

    private static bool SmileVisible(MascotExpression e) =>
        e is MascotExpression.Calm or MascotExpression.Lively;

    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _dartTimer;
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
        ApplyExpression(Expression, animated: false);
        if (MascotOptions.FaceEnabled && IsVisible && IsLoaded) StartLife(); else StopLife();
    }

    // ---- expression ------------------------------------------------------------

    private void ApplyExpression(MascotExpression expression, bool animated)
    {
        if (!_templateReady) return;

        var (sx, sy, tilt) = Pose(expression);
        var poseDuration = TimeSpan.FromMilliseconds(animated ? 220 : 0);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

        AnimateScale(EyeLBlink, sx, sy, poseDuration, ease, animated);
        AnimateScale(EyeRBlink, sx, sy, poseDuration, ease, animated);
        // Concerned: tilt the eyes toward each other, faintly.
        AnimateAngle(EyeLTilt, tilt, poseDuration, ease, animated);
        AnimateAngle(EyeRTilt, -tilt, poseDuration, ease, animated);

        // Smile: fade rather than pop.
        var smileOpacity = SmileVisible(expression)
            ? (expression == MascotExpression.Lively ? 0.62 : 0.5)
            : 0.0;
        if (!animated) { Mouth.BeginAnimation(OpacityProperty, null); Mouth.Opacity = smileOpacity; }
        else Mouth.BeginAnimation(OpacityProperty, new DoubleAnimation(smileOpacity, poseDuration) { EasingFunction = ease });

        // Lively: a barely-there float, like nodding along to the music.
        StopBob();
        if (expression == MascotExpression.Lively)
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
        StopBob();
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
            foreach (var eye in new[] { EyeLBlink, EyeRBlink })
            {
                eye.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(0.12, 1, open) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
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
