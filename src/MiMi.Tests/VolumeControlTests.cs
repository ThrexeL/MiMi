using MiMi.App.UI;
using Xunit;

namespace MiMi.Tests;

public class VolumeControlTests
{
    [Fact]
    public void Volume_ClampsWritesAndRefreshesSnapshot()
    {
        var level = 0.4f;
        var muted = false;
        var vm = new VolumeViewModel(() => (level, muted, true), value =>
        {
            level = Math.Clamp(value, 0f, 1f);
            return true;
        }, () => muted = !muted);

        vm.Level = 65;
        Assert.Equal(65, vm.Level, 0);
        Assert.Equal(0.65f, level, 0.001f);

        vm.Level = 200;
        Assert.Equal(100, vm.Level, 0);
        Assert.Equal(1f, level, 0.001f);

        vm.MuteCommand.Execute(null);
        Assert.True(vm.Muted);
    }

    [Fact]
    public void Volume_UnavailableEndpointDoesNotAcceptWrites()
    {
        var writes = 0;
        var vm = new VolumeViewModel(() => (0f, false, false), _ => { writes++; return false; }, () => { });
        vm.Level = 50;
        Assert.Equal(0, writes);
        Assert.False(vm.IsAvailable);
        Assert.Equal("Audio unavailable", vm.StatusLabel);
    }

    [Fact]
    public void Volume_NonFiniteOrFailedWriteRestoresSnapshot()
    {
        var level = 0.4f;
        var succeed = false;
        var vm = new VolumeViewModel(() => (level, false, true), value =>
        {
            if (!succeed) return false;
            level = value;
            return true;
        }, () => { });

        vm.Level = double.NaN;
        Assert.Equal(40, vm.Level, 0);
        vm.Level = 70;
        Assert.Equal(40, vm.Level, 0);
        Assert.Equal(0.4f, level, 0.001f);
    }
}
