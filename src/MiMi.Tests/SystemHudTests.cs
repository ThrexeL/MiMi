using MiMi.App.Interop;
using Xunit;

namespace MiMi.Tests;

/// <summary>Milestone 6: system HUD logic (pure policies — real hardware paths are
/// verified manually per the brief's "supported only" rule).</summary>
public class BatteryNoticePolicyTests
{
    [Fact]
    public void Startup_DoesNotAnnounce_PredatesState()
    {
        var policy = new BatteryNoticePolicy();
        Assert.Equal(BatteryNoticePolicy.NoticeKind.None, policy.Evaluate(charging: true, percent: 80));
        Assert.Equal(BatteryNoticePolicy.NoticeKind.None, policy.Evaluate(charging: false, percent: 60));
    }

    [Fact]
    public void PlugAndUnplug_AnnounceOnceEach()
    {
        var policy = new BatteryNoticePolicy();
        policy.Evaluate(true, 80);
        Assert.Equal(BatteryNoticePolicy.NoticeKind.ChargingDisconnected, policy.Evaluate(false, 79));
        Assert.Equal(BatteryNoticePolicy.NoticeKind.ChargingConnected, policy.Evaluate(true, 80));
        Assert.Equal(BatteryNoticePolicy.NoticeKind.None, policy.Evaluate(true, 81)); // no repeat
    }

    [Fact]
    public void LowBattery_FiresOnce_WhenCrossingDown()
    {
        var policy = new BatteryNoticePolicy();
        policy.Evaluate(false, 30);
        Assert.Equal(BatteryNoticePolicy.NoticeKind.LowBattery, policy.Evaluate(false, 15));
        Assert.Equal(BatteryNoticePolicy.NoticeKind.None, policy.Evaluate(false, 12)); // once
    }

    [Fact]
    public void Recovery_Rearms_LowBattery()
    {
        var policy = new BatteryNoticePolicy();
        policy.Evaluate(false, 30);
        policy.Evaluate(false, 15); // low fires
        policy.Evaluate(true, 50);  // charging: recovers
        policy.Evaluate(false, 60);
        Assert.Equal(BatteryNoticePolicy.NoticeKind.LowBattery, policy.Evaluate(false, 15)); // rearmed
    }

    [Fact]
    public void LowBattery_DoesNotFire_WhileCharging()
    {
        var policy = new BatteryNoticePolicy();
        policy.Evaluate(true, 30);
        Assert.Equal(BatteryNoticePolicy.NoticeKind.None, policy.Evaluate(true, 10));
    }
}

public class KeyboardHudPolicyTests
{
    [Fact]
    public void Startup_DoesNotAnnounce()
    {
        var policy = new KeyboardHudPolicy();
        var (kind, _) = policy.Evaluate(capsOn: true, layout: "ENG · UNITED STATES");
        Assert.Equal(KeyboardHudPolicy.Kind.None, kind);
    }

    [Fact]
    public void CapsToggle_AnnouncesOnAndOff()
    {
        var policy = new KeyboardHudPolicy();
        policy.Evaluate(false, "ENG · US");
        Assert.Equal(KeyboardHudPolicy.Kind.CapsOn, policy.Evaluate(true, "ENG · US").Kind);
        Assert.Equal(KeyboardHudPolicy.Kind.CapsOff, policy.Evaluate(false, "ENG · US").Kind);
    }

    [Fact]
    public void LayoutChange_Announces_WhenNoCapsEvent()
    {
        var policy = new KeyboardHudPolicy();
        policy.Evaluate(false, "ENG · US");
        var (kind, layout) = policy.Evaluate(false, "DEU · GERMANY");
        Assert.Equal(KeyboardHudPolicy.Kind.LayoutChanged, kind);
        Assert.Equal("DEU · GERMANY", layout);
    }

    [Fact]
    public void LayoutChange_WhileCapsToggling_IsSuppressed()
    {
        // A Caps event wins the same tick; the layout label rides along next time.
        var policy = new KeyboardHudPolicy();
        policy.Evaluate(false, "ENG · US");
        Assert.Equal(KeyboardHudPolicy.Kind.CapsOn, policy.Evaluate(true, "DEU · GERMANY").Kind);
    }
}

public class VolumeHudPolicyTests
{
    // Mirror of SystemHudController's coalescing rule (same value within 1.1 s → skip).
    private static bool ShouldPush(int percent, bool muted, int lastPercent, bool lastMuted,
        double secondsSinceLastPush) =>
        !(percent == lastPercent && muted == lastMuted && secondsSinceLastPush < 1.1);

    [Fact]
    public void SameValue_RapidChange_IsAbsorbed()
    {
        Assert.False(ShouldPush(50, false, 50, false, 0.3));
    }

    [Fact]
    public void ChangedValue_Pushes()
    {
        Assert.True(ShouldPush(51, false, 50, false, 0.1));
        Assert.True(ShouldPush(50, true, 50, false, 0.1)); // mute toggle always pushes
    }

    [Fact]
    public void SameValue_AfterExpiry_Pushes()
    {
        Assert.True(ShouldPush(50, false, 50, false, 1.5));
    }
}
