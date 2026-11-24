using MiMi.Core;
using Xunit;

namespace MiMi.Tests;

public class SettingsTests
{
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var settings = AppSettings.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "none.json"));
        Assert.Equal(1.0, settings.Scale);
        Assert.True(settings.MediaModuleEnabled);
        Assert.True(settings.BridgeEnabled);
    }

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mimi-test-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new AppSettings { Scale = 1.25, AnimationSpeed = 1.5, StartWithWindows = true };
            settings.Save(path);
            var loaded = AppSettings.Load(path);
            Assert.Equal(1.25, loaded.Scale);
            Assert.Equal(1.5, loaded.AnimationSpeed);
            Assert.True(loaded.StartWithWindows);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mimi-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not valid json !!");
            var settings = AppSettings.Load(path);
            Assert.Equal(1.0, settings.Scale);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Update_FailedSaveLeavesCurrentSettingsUnchanged()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mimi-config-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ConfigStore(path);
            store.Update(s => s.Scale = 1.2);
            Directory.CreateDirectory(path + ".tmp");
            Assert.True(Record.Exception(() => store.Update(s => s.Scale = 1.4))
                is IOException or UnauthorizedAccessException);
            Assert.Equal(1.2, store.Current.Scale);

            Directory.Delete(path + ".tmp");
            store.Update(s => s.AnimationSpeed = 1.5);
            Assert.Equal(1.2, AppSettings.Load(path).Scale);
        }
        finally
        {
            if (Directory.Exists(path + ".tmp")) Directory.Delete(path + ".tmp");
            File.Delete(path);
        }
    }

    [Fact]
    public void Clamp_RejectsNonsense()
    {
        var settings = new AppSettings { Scale = 99, AnimationSpeed = 0.001, ApprovalTtlMinutes = -5 };
        settings.Clamp();
        Assert.Equal(1.5, settings.Scale);
        Assert.Equal(0.5, settings.AnimationSpeed);
        Assert.Equal(1, settings.ApprovalTtlMinutes);
    }
}

public class DedupTests
{
    [Fact]
    public void SameId_SecondRecord_Fails()
    {
        var store = new DedupStore();
        Assert.True(store.TryRecord("e1"));
        Assert.False(store.TryRecord("e1"));
    }

    [Fact]
    public void Capacity_EvictsOldest()
    {
        var store = new DedupStore(capacity: 4);
        for (var i = 0; i < 6; i++) store.TryRecord($"e{i}");
        Assert.True(store.TryRecord("e0"));  // evicted, can be recorded again
        Assert.False(store.TryRecord("e5")); // still tracked
    }

    [Fact]
    public void Forget_AllowsRejectedDeliveryToBeRetriedWithoutStaleEviction()
    {
        var store = new DedupStore(capacity: 2);
        Assert.True(store.TryRecord("retry"));
        store.Forget("retry");
        Assert.True(store.TryRecord("retry"));
        Assert.True(store.TryRecord("newer"));
        Assert.False(store.TryRecord("retry")); // stale queue entry did not evict the retry
    }

    [Fact]
    public void EmptyId_AlwaysAccepted()
    {
        var store = new DedupStore();
        Assert.True(store.TryRecord(""));
        Assert.True(store.TryRecord(""));
    }
}
