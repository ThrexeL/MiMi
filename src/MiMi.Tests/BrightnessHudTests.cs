using System.Text.Json;
using MiMi.Core;
using Xunit;

namespace MiMi.Tests;

public class BrightnessHudTests
{
    [Fact]
    public void StartupAndDuplicateReadingsStaySilent()
    {
        var policy = new BrightnessHudPolicy();
        Assert.False(policy.Evaluate(50));
        Assert.False(policy.Evaluate(50));
        Assert.True(policy.Evaluate(51));
        Assert.False(policy.Evaluate(51));
        Assert.True(policy.Evaluate(50));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(255)]
    public void InvalidReadingsDoNotSeedOrChangeBaseline(int invalid)
    {
        var policy = new BrightnessHudPolicy();
        Assert.False(policy.Evaluate(invalid));
        Assert.False(policy.Evaluate(50));
        Assert.False(policy.Evaluate(invalid));
        Assert.False(policy.Evaluate(50));
        Assert.True(policy.Evaluate(60));
    }

    [Fact]
    public void ZeroAndFullBrightnessAreRealValues()
    {
        var policy = new BrightnessHudPolicy();
        Assert.False(policy.Evaluate(0));
        Assert.True(policy.Evaluate(100));
        Assert.True(policy.Evaluate(0));
    }

    [Fact]
    public void BrightnessPreferenceIsIndependentAndBackwardCompatible()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"volumeHudEnabled\":false}", AppSettings.JsonOpts)!;
        Assert.True(settings.BrightnessHudEnabled);
        Assert.False(settings.VolumeHudEnabled);
        settings.BrightnessHudEnabled = false;
        settings.VolumeHudEnabled = true;
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, AppSettings.JsonOpts), AppSettings.JsonOpts)!;
        Assert.False(restored.BrightnessHudEnabled);
        Assert.True(restored.VolumeHudEnabled);
    }
}
