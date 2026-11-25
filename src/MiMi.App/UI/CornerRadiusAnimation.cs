using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MiMi.App.UI;

/// <summary>Small WPF timeline that lets the pill width, height, and capsule radius
/// morph together. WPF does not ship a CornerRadiusAnimation.</summary>
public sealed class CornerRadiusAnimation : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(CornerRadius), typeof(CornerRadiusAnimation), new PropertyMetadata(default(CornerRadius)));

    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To), typeof(CornerRadius), typeof(CornerRadiusAnimation), new PropertyMetadata(default(CornerRadius)));

    public static readonly DependencyProperty EasingFunctionProperty = DependencyProperty.Register(
        nameof(EasingFunction), typeof(IEasingFunction), typeof(CornerRadiusAnimation), new PropertyMetadata(null));

    public CornerRadius From { get => (CornerRadius)GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public CornerRadius To { get => (CornerRadius)GetValue(ToProperty); set => SetValue(ToProperty, value); }
    public IEasingFunction? EasingFunction { get => (IEasingFunction?)GetValue(EasingFunctionProperty); set => SetValue(EasingFunctionProperty, value); }

    public CornerRadiusAnimation() { }

    public CornerRadiusAnimation(CornerRadius from, CornerRadius to, TimeSpan duration)
    {
        From = from;
        To = to;
        Duration = duration;
    }

    public override Type TargetPropertyType => typeof(CornerRadius);

    protected override Freezable CreateInstanceCore() => new CornerRadiusAnimation();

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue,
        AnimationClock animationClock)
    {
        var origin = animationClock.CurrentTime is { } time && time > TimeSpan.Zero
            ? (CornerRadius)defaultOriginValue
            : From;
        var destination = To;
        var progress = animationClock.CurrentProgress ?? 0;
        if (EasingFunction is { } easing) progress = easing.Ease(progress);
        return new CornerRadius(
            origin.TopLeft + (destination.TopLeft - origin.TopLeft) * progress,
            origin.TopRight + (destination.TopRight - origin.TopRight) * progress,
            origin.BottomRight + (destination.BottomRight - origin.BottomRight) * progress,
            origin.BottomLeft + (destination.BottomLeft - origin.BottomLeft) * progress);
    }
}
