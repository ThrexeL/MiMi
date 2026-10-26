using MiMi.Core;
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
