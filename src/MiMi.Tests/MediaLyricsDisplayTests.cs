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
