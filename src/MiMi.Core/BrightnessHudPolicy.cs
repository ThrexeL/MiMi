namespace MiMi.Core;

/// <summary>Tracks real backlight readings; startup, invalid values and duplicates stay silent.</summary>
public sealed class BrightnessHudPolicy
{
    private int? _lastPercent;

    public bool Evaluate(int percent)
    {
        if (percent is < 0 or > 100) return false;
        var changed = _lastPercent.HasValue && _lastPercent.Value != percent;
        _lastPercent = percent;
        return changed;
    }
}
