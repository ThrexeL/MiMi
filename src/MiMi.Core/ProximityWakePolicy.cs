namespace MiMi.Core;

/// <summary>
/// Hysteresis policy for mouse-proximity wake: MiMi wakes when the cursor comes
/// within <see cref="WakeDistanceDip"/> of the island and returns to sleep only after
/// the cursor has stayed beyond <see cref="ReSleepDistanceDip"/> for
/// <see cref="ReSleepDelay"/> — and never before a minimum awake time — so moving
/// the mouse around cannot cause sleep/wake flicker.
///
/// Pure and testable; the window samples the cursor (throttled, only while sleepy)
/// and feeds distances here. All distances are DIPs.
/// </summary>
public sealed class ProximityWakePolicy
{
    public double WakeDistanceDip { get; set; } = 45;
    public double ReSleepDistanceDip { get; set; } = 85;
    public TimeSpan ReSleepDelay { get; set; } = TimeSpan.FromSeconds(2.5);
    public TimeSpan MinAwakeTime { get; set; } = TimeSpan.FromSeconds(2);

    public bool Awake { get; private set; }

    private DateTimeOffset _awakeSince = DateTimeOffset.MinValue;
    private DateTimeOffset _farSince = DateTimeOffset.MinValue;

    /// <summary>Resets to the sleeping state (used when MiMi hides or wakes is disabled).</summary>
    public void Reset()
    {
        Awake = false;
        _awakeSince = DateTimeOffset.MinValue;
        _farSince = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Feeds one cursor distance sample. Returns true for "wake now", false for
    /// "sleep now", null for "no change".
    /// </summary>
    public bool? Evaluate(double distanceDip, DateTimeOffset now)
    {
        if (!Awake)
        {
            if (distanceDip <= WakeDistanceDip)
            {
                Awake = true;
                _awakeSince = now;
                _farSince = DateTimeOffset.MinValue;
                return true;
            }
            return null;
        }

        // Awake: near cursor keeps us awake; far cursor must persist for the delay,
        // and the minimum awake time prevents flicker right after waking.
        if (distanceDip <= ReSleepDistanceDip)
        {
            _farSince = DateTimeOffset.MinValue;
            return null;
        }

        if (_farSince == DateTimeOffset.MinValue) _farSince = now;
        if (now - _farSince >= ReSleepDelay && now - _awakeSince >= MinAwakeTime)
        {
            Awake = false;
            _farSince = DateTimeOffset.MinValue;
            return false;
        }
        return null;
    }
}
