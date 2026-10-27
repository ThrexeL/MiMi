using MiMi.Core;
using MiMi.App.UI;
using Xunit;

namespace MiMi.Tests;

/// <summary>
/// Visibility coordinator rules (spec §P3):
///   EffectiveVisible = Preference == Visible && !FullscreenSuppressed && SystemAllowsOverlay
/// The coordinator is the ONLY authority so systems can't fight each other.
/// </summary>
public class VisibilityCoordinatorTests
{
    [Fact]
    public void Default_IsVisible()
    {
        var c = new VisibilityCoordinator();
        Assert.True(c.EffectiveVisible);
        Assert.Equal(VisibilityPreference.Visible, c.Preference);
    }

    [Fact]
    public void HideByUser_WinsOverEverything_AndSurvivesShellEvents()
    {
        var c = new VisibilityCoordinator();
        c.SetPreference(VisibilityPreference.HiddenByUser);
        Assert.False(c.EffectiveVisible);

        // Win+D / taskbar Show Desktop / fullscreen exit must not resurrect it.
        c.SetFullscreenSuppressed(false);
        c.SystemAllowsOverlay = true;
        Assert.False(c.EffectiveVisible);

        // Only the user's toggle brings it back.
        c.SetPreference(VisibilityPreference.Visible);
        Assert.True(c.EffectiveVisible);
    }

    [Fact]
    public void FullscreenSuppresses_ButUserHideTakesPrecedence()
    {
        var c = new VisibilityCoordinator();
        c.SetFullscreenSuppressed(true);
        Assert.False(c.EffectiveVisible);
        c.SetFullscreenSuppressed(false);
        Assert.True(c.EffectiveVisible);

        c.SetPreference(VisibilityPreference.HiddenByUser);
        c.SetFullscreenSuppressed(true);
        c.SetFullscreenSuppressed(false);
        Assert.False(c.EffectiveVisible); // still hidden by user
    }

    [Fact]
    public void Changed_FiresOnlyOnActualTransitions()
    {
        var c = new VisibilityCoordinator();
        var fired = 0;
        c.Changed += () => fired++;

        c.SetFullscreenSuppressed(true);
        c.SetFullscreenSuppressed(true);   // no-op: no event
        c.SetFullscreenSuppressed(false);
        c.TogglePreference();
        c.SetPreference(VisibilityPreference.HiddenByUser); // no-op: already hidden

        Assert.Equal(3, fired);
    }

    [Fact]
    public void SecureDesktopFlag_BlocksOverlay()
    {
        var c = new VisibilityCoordinator { SystemAllowsOverlay = false };
        Assert.False(c.EffectiveVisible);
        c.SystemAllowsOverlay = true;
        Assert.True(c.EffectiveVisible);
    }

    [Fact]
    public void NewSettings_DefaultsMatchSpec()
    {
        var s = new AppSettings();
        Assert.True(s.DesktopPersistVisible);
        Assert.True(s.FullscreenSuppress);
        Assert.True(s.MascotEnabled);
        Assert.True(s.GlowEnabled);
    }

    [Fact]
    public void NewSettings_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mimi-vis-{Guid.NewGuid():N}.json");
        try
        {
            new AppSettings { DesktopPersistVisible = false, FullscreenSuppress = false }.Save(path);
            var loaded = AppSettings.Load(path);
            Assert.False(loaded.DesktopPersistVisible);
            Assert.False(loaded.FullscreenSuppress);
        }
        finally { File.Delete(path); }
    }
}

public class VisibilityTransitionPlannerTests
{
    [Fact]
    public void Show_WhileHidden_FadesIn_FromZero()
    {
        var plan = VisibilityTransitionPlanner.Plan(currentlyShown: false, 0, desiredVisible: true,
            hideWasRequestedByUser: false, reducedMotion: false, transitionInFlight: false);
        Assert.Equal(FadeAction.ShowFadeIn, plan.Action);
        Assert.Equal(0, plan.FromOpacity);
    }

    [Fact]
    public void Show_WhileMidFadeOut_ContinuesFromRenderedOpacity()
    {
        // Half-faded-out (opacity 0.4), user toggles show: reverse from 0.4, no jumps.
        var plan = VisibilityTransitionPlanner.Plan(currentlyShown: true, 0.4, desiredVisible: true,
            hideWasRequestedByUser: false, reducedMotion: false, transitionInFlight: true);
        Assert.Equal(FadeAction.ShowFadeIn, plan.Action);
        Assert.Equal(0.4, plan.FromOpacity);
    }

    [Fact]
    public void UserHide_WhileVisible_FadesOut()
    {
        var plan = VisibilityTransitionPlanner.Plan(currentlyShown: true, 1, desiredVisible: false,
            hideWasRequestedByUser: true, reducedMotion: false, transitionInFlight: false);
        Assert.Equal(FadeAction.HideFadeOut, plan.Action);
        Assert.Equal(1, plan.FromOpacity);
    }

    [Fact]
    public void FullscreenHide_IsImmediate_NotAnimated()
    {
        var plan = VisibilityTransitionPlanner.Plan(currentlyShown: true, 1, desiredVisible: false,
            hideWasRequestedByUser: false, reducedMotion: false, transitionInFlight: false);
        Assert.Equal(FadeAction.HideImmediate, plan.Action);
    }

    [Fact]
    public void ReducedMotion_CollapsesToImmediateTransitions()
    {
        var show = VisibilityTransitionPlanner.Plan(false, 0, true, false, reducedMotion: true, transitionInFlight: false);
        var hide = VisibilityTransitionPlanner.Plan(true, 1, false, true, reducedMotion: true, transitionInFlight: false);
        Assert.Equal(FadeAction.ShowImmediate, show.Action);
        Assert.Equal(FadeAction.HideImmediate, hide.Action);
    }

    [Fact]
    public void MatchingState_IsNoOp()
    {
        var shown = VisibilityTransitionPlanner.Plan(true, 1, true, false, false, transitionInFlight: false);
        var hidden = VisibilityTransitionPlanner.Plan(false, 0, false, false, false, transitionInFlight: false);
        Assert.Equal(FadeAction.None, shown.Action);
        Assert.Equal(FadeAction.None, hidden.Action);
    }

    [Fact]
    public void Show_WhileFadeOutStillInFlight_ReplacesTransition()
    {
        // Window still shown but a fade-out is running toward hidden; the new decision
        // is visible — the planner must replace the transition, not no-op.
        var plan = VisibilityTransitionPlanner.Plan(currentlyShown: true, 0.5, desiredVisible: true,
            hideWasRequestedByUser: false, reducedMotion: false, transitionInFlight: true);
        Assert.Equal(FadeAction.ShowFadeIn, plan.Action);
        Assert.Equal(0.5, plan.FromOpacity);
    }

    [Fact]
    public void HideFadeOut_WhileMidFadeIn_ContinuesFromRenderedOpacity()
    {
        var plan = VisibilityTransitionPlanner.Plan(true, 0.6, false, true, false, transitionInFlight: true);
        Assert.Equal(FadeAction.HideFadeOut, plan.Action);
        Assert.Equal(0.6, plan.FromOpacity);
    }
}

public class FadeSequencerTests
{
    [Fact]
    public void StaleCompletion_DoesNothing()
    {
        var seq = new FadeSequencer();
        var hideGen = seq.Begin();   // hide starts, schedules a delayed Hide()

        var showGen = seq.Begin();   // user toggles show before hide completed
        Assert.True(seq.IsCurrent(showGen));

        // The stale hide completion must be skipped — a newer Show() won.
        Assert.False(seq.IsCurrent(hideGen));

        // And the newest generation stays current until another transition begins.
        Assert.True(seq.IsCurrent(showGen));
    }

    [Fact]
    public void RapidToggles_EachSupersedesThePrevious()
    {
        var seq = new FadeSequencer();
        var gens = Enumerable.Range(0, 10).Select(_ => seq.Begin()).ToList();
        Assert.All(gens.Take(9), g => Assert.False(seq.IsCurrent(g)));
        Assert.True(seq.IsCurrent(gens[^1]));
    }
}

public class ProximityWakePolicyTests
{
    private static DateTimeOffset T(int ms) => DateTimeOffset.UtcNow.AddMilliseconds(ms);

    [Fact]
    public void Wakes_WithinThreshold()
    {
        var policy = new ProximityWakePolicy();
        Assert.Null(policy.Evaluate(200, T(0)));   // far: nothing
        Assert.True(policy.Evaluate(30, T(100)));  // near: wake
        Assert.True(policy.Awake);
    }

    [Fact]
    public void StaysAwake_WhileCursorNear()
    {
        var policy = new ProximityWakePolicy();
        policy.Evaluate(30, T(0));
        Assert.Null(policy.Evaluate(10, T(500)));
        Assert.Null(policy.Evaluate(60, T(1000))); // within re-sleep distance: no flicker
        Assert.True(policy.Awake);
    }

    [Fact]
    public void ReSleeps_AfterCursorLeaves_ForTheDelay()
    {
        var policy = new ProximityWakePolicy();
        policy.Evaluate(30, T(0));                                  // wake
        Assert.Null(policy.Evaluate(200, T(100)));                  // left: far timer starts
        Assert.Null(policy.Evaluate(200, T(1500)));                  // delay not elapsed
        Assert.False(policy.Evaluate(200, T(2600)));                 // 2.5 s away + 2 s awake: sleep
        Assert.False(policy.Awake);
        Assert.True(policy.Evaluate(30, T(3000)));                   // can wake again
    }

    [Fact]
    public void MinimumAwakeTime_PreventsInstantReSleep()
    {
        var policy = new ProximityWakePolicy { MinAwakeTime = TimeSpan.FromSeconds(2) };
        policy.Evaluate(30, T(0));
        Assert.Null(policy.Evaluate(200, T(2600)));  // far past the delay, but awake < 2 s
        Assert.True(policy.Awake);
        Assert.False(policy.Evaluate(200, T(5300))); // now past min awake: sleep
    }

    [Fact]
    public void Reset_ReturnsToSleeping()
    {
        var policy = new ProximityWakePolicy();
        policy.Evaluate(30, T(0));
        policy.Reset();
        Assert.False(policy.Awake);
    }
}

public class GlowLayoutTests
{
    [Fact]
    public void IdleBand_IsThin()
    {
        // 38-tall idle pill: bottom 10-20% -> clamped to at most 8 DIP.
        Assert.InRange(GlowLayout.BandHeight(isIdle: true, pillHeight: 38), 7.5, 7.7);
        Assert.Equal(8.0, GlowLayout.BandHeight(isIdle: true, pillHeight: 60));
    }

    [Fact]
    public void ExpandedBand_IsFixedDips_NotProportional()
    {
        // The brown-oval regression: band height must NOT scale with panel height.
        Assert.Equal(GlowLayout.PanelBandHeight, GlowLayout.BandHeight(isIdle: false, pillHeight: 190));
        Assert.Equal(GlowLayout.PanelBandHeight, GlowLayout.BandHeight(isIdle: false, pillHeight: 60));
    }

    [Fact]
    public void Band_IsClampedToTheLowerRegion()
    {
        Assert.Equal(3, GlowLayout.ClampToBottomRegion(2, 38));        // never thinner than 3
        Assert.Equal(8, GlowLayout.ClampToBottomRegion(8, 38));
        Assert.Equal(17.1, GlowLayout.ClampToBottomRegion(50, 38));    // never above 45% height
    }
}
