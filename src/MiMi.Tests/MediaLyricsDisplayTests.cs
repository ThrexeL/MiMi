using MiMi.App.UI;
using MiMi.Lyrics;
using MiMi.Media;
using Xunit;

namespace MiMi.Tests;

public class MediaLyricsDisplayTests
{
    [Theory]
    [InlineData(LyricsState.Unavailable, "No lyrics available")]
    [InlineData(LyricsState.Instrumental, "Instrumental - no lyrics")]
    [InlineData(LyricsState.Loading, "Loading lyrics...")]
    public void MissingLyrics_ReplacePreviousTrackText(LyricsState state, string expected)
    {
        var vm = WithLyrics();
        vm.UpdateLyrics(state, null, 0, true);

        Assert.Equal(expected, vm.LyricCurrent);
        Assert.Empty(vm.LyricPrev);
        Assert.Empty(vm.LyricNext);
        Assert.False(vm.LyricCurrentIsLine);
        Assert.True(vm.LyricsBlockVisible);
    }

    [Theory]
    [InlineData(LyricsState.Plain)]
    [InlineData(LyricsState.Synced)]
    public void EmptyLyrics_DisplayAvailabilityMessage(LyricsState state)
    {
        var vm = WithLyrics();
        vm.UpdateLyrics(state, new LyricsResult { TrackName = "Track", ArtistName = "Artist" }, 0, true);
        Assert.Equal("No lyrics available", vm.LyricCurrent);
        Assert.False(vm.LyricCurrentIsLine);
    }

    [Fact]
    public void ToggleAndClear_UpdateVisibilityBindings()
    {
        var vm = WithLyrics();
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        vm.ToggleLyrics();
        Assert.False(vm.LyricsBlockVisible);
        Assert.True(vm.LyricsBlockHidden);
        Assert.Contains(nameof(vm.LyricsBlockVisible), changes);
        Assert.Contains(nameof(vm.LyricsBlockHidden), changes);
        vm.ToggleLyrics();
        Assert.True(vm.LyricsBlockVisible);
        vm.ClearLyrics();
        Assert.False(vm.LyricsBlockVisible);
        Assert.False(vm.LyricCurrentIsLine);
        Assert.Empty(vm.LyricCurrent);
    }

    [Fact]
    public void DisabledLyrics_HideAvailabilityMessage()
    {
        var vm = WithLyrics();
        vm.UpdateLyrics(LyricsState.Unavailable, null, 0, false);
        Assert.False(vm.LyricsBlockVisible);
        Assert.True(vm.LyricsBlockHidden);
    }

    [Fact]
    public void Settings_RefreshPausedLyricsWithoutNewMediaEvents()
    {
        var vm = WithLyrics();
        Assert.Equal("Current", vm.LyricCurrent);
        vm.ApplyLyricsSettings(10000, true);
        Assert.Equal("Next", vm.LyricCurrent);
        vm.ApplyLyricsSettings(10000, false);
        Assert.False(vm.LyricsBlockVisible);
        vm.ApplyLyricsSettings(0, true);
        Assert.True(vm.LyricsBlockVisible);
        Assert.Equal("Current", vm.LyricCurrent);
    }

    [Fact]
    public void Settings_DoNotOverrideSessionLyricsToggle()
    {
        var vm = WithLyrics();
        vm.ToggleLyrics();
        vm.ApplyLyricsSettings(500, true);
        Assert.False(vm.LyricsBlockVisible);
    }

    [Fact]
    public void CurrentLyric_SeeksToTimestampMinusOffsetAndClampsToDuration()
    {
        var seeks = new List<TimeSpan>();
        var vm = new MediaViewModel(seek: seeks.Add);
        vm.Update(new MediaSnapshot
        {
            Title = "Track",
            Position = TimeSpan.FromSeconds(10),
            Duration = TimeSpan.FromMinutes(3),
        });
        vm.UpdateLyrics(LyricsState.Synced, new LyricsResult
        {
            TrackName = "Track",
            ArtistName = "Artist",
            SyncedLines = new[]
            {
                new LrcLine(TimeSpan.Zero, "Previous"),
                new LrcLine(TimeSpan.FromSeconds(5), "Current"),
                new LrcLine(TimeSpan.FromSeconds(20), "Next"),
            },
        }, 1000, true);

        Assert.True(vm.CanSeekCurrentLyric);
        vm.SeekCurrentLyricCommand.Execute(null);
        Assert.Equal(TimeSpan.FromSeconds(4), Assert.Single(seeks));

        vm.ApplyLyricsSettings(10000, true); // current line becomes the 20 s line
        vm.SeekToCurrentLyric();
        Assert.Equal(TimeSpan.FromSeconds(10), seeks[^1]);
    }

    [Fact]
    public void CurrentLyric_DoesNotSeekWithoutTimedLineDurationOrCallback()
    {
        var vm = WithLyrics();
        Assert.False(vm.CanSeekCurrentLyric);
        vm.SeekToCurrentLyric(); // no-op, must not throw

        var noCallback = new MediaViewModel();
        noCallback.Update(new MediaSnapshot { Title = "Track", Duration = TimeSpan.FromMinutes(3) });
        noCallback.UpdateLyrics(LyricsState.Synced, new LyricsResult
        {
            TrackName = "Track",
            ArtistName = "Artist",
            SyncedLines = new[] { new LrcLine(TimeSpan.FromSeconds(5), "Current") },
        }, 0, true);
        Assert.False(noCallback.CanSeekCurrentLyric);
    }

    private static MediaViewModel WithLyrics()
    {
        var vm = new MediaViewModel();
        vm.Update(new MediaSnapshot { Title = "Track", Position = TimeSpan.FromSeconds(10) });
        vm.UpdateLyrics(LyricsState.Synced, new LyricsResult
        {
            TrackName = "Track",
            ArtistName = "Artist",
            SyncedLines = new[]
            {
                new LrcLine(TimeSpan.Zero, "Previous"),
                new LrcLine(TimeSpan.FromSeconds(5), "Current"),
                new LrcLine(TimeSpan.FromSeconds(20), "Next"),
            },
        }, 0, true);
        return vm;
    }
}
