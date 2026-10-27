using MiMi.Lyrics;
using Xunit;

namespace MiMi.Tests;

public class LrcParserTests
{
    [Fact]
    public void Parses_BasicTimestamps()
    {
        var lines = LrcParser.Parse("[00:37.66]Waiting in a car\n[00:42.39]Waiting for a ride in the dark");
        Assert.Equal(2, lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(37.66), lines[0].Time);
        Assert.Equal("Waiting in a car", lines[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(42.39), lines[1].Time);
    }

    [Fact]
    public void Handles_MultipleTimestamps_PerLine()
    {
        var lines = LrcParser.Parse("[00:10.00][01:10.00]Chorus repeat");
        Assert.Equal(2, lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(70), lines[1].Time);
        Assert.All(lines, l => Assert.Equal("Chorus repeat", l.Text));
    }

    [Fact]
    public void Ignores_MetadataTags_ButHonorsOffset()
    {
        var lrc = "[ti:Song]\n[ar:Artist]\n[offset:+500]\n[00:10.00]Line one\n[00:20.00]Line two";
        var lines = LrcParser.Parse(lrc);
        Assert.Equal(2, lines.Count);
        // Positive offset shifts timestamps earlier by 500 ms.
        Assert.Equal(TimeSpan.FromSeconds(9.5), lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(19.5), lines[1].Time);
    }

    [Fact]
    public void NegativeOffset_ShiftsLater()
    {
        var lines = LrcParser.Parse("[offset:-1000]\n[00:05.00]X");
        Assert.Equal(TimeSpan.FromSeconds(6), lines[0].Time);
    }

    [Fact]
    public void UnsortedInput_IsSortedByTime()
    {
        var lines = LrcParser.Parse("[00:20.00]B\n[00:10.00]A\n[00:30.00]C");
        Assert.Equal(new[] { "A", "B", "C" }, lines.Select(l => l.Text));
    }

    [Fact]
    public void Handles_CRLF_And_ThreeDigitFractions()
    {
        var lines = LrcParser.Parse("[00:05.123]First\r\n[00:07.5]Second");
        Assert.Equal(TimeSpan.FromSeconds(5.123), lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(7.5), lines[1].Time);
    }

    [Fact]
    public void Garbage_AndEmpty_Input_ReturnEmpty()
    {
        Assert.Empty(LrcParser.Parse(null));
        Assert.Empty(LrcParser.Parse(""));
        Assert.Empty(LrcParser.Parse("not lrc at all\n[ti:only metadata]"));
    }
}

public class LrcSynchronizerTests
{
    private static readonly IReadOnlyList<LrcLine> Lines = LrcParser.Parse(
        "[00:10.00]First\n[00:20.00]Second\n[00:30.00]Third");

    [Fact]
    public void BeforeFirstLine_ReturnsMinusOne()
    {
        Assert.Equal(-1, LrcParser.CurrentLineIndex(Lines, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void BetweenLines_ReturnsTheEarlierLine()
    {
        Assert.Equal(0, LrcParser.CurrentLineIndex(Lines, TimeSpan.FromSeconds(19.99)));
    }

    [Fact]
    public void ExactHit_ReturnsThatLine()
    {
        Assert.Equal(1, LrcParser.CurrentLineIndex(Lines, TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void AfterLast_ReturnsLastIndex()
    {
        Assert.Equal(2, LrcParser.CurrentLineIndex(Lines, TimeSpan.FromSeconds(90)));
    }

    [Fact]
    public void Seek_Backwards_WorksImmediately_Stateless()
    {
        Assert.Equal(2, LrcParser.CurrentLineIndex(Lines, TimeSpan.FromSeconds(45)));
        Assert.Equal(0, LrcParser.CurrentLineIndex(Lines, TimeSpan.FromSeconds(11))); // jumped back
    }

    [Fact]
    public void EmptyLines_ReturnMinusOne()
    {
        Assert.Equal(-1, LrcParser.CurrentLineIndex(Array.Empty<LrcLine>(), TimeSpan.Zero));
    }
}

public class LyricsMatchPickerTests
{
    private static LyricsRecord Rec(long id, string track, string artist, double duration,
        bool instrumental = false, string? synced = "[00:01.00]x") =>
        new(id, track, artist, "Album", duration, instrumental, "plain", synced);

    private static readonly LyricsQuery Query = new("Midnight City", "M83", "Dreaming", 244);

    [Fact]
    public void Prefers_ExactTrackAndArtist_ClosestDuration()
    {
        var candidates = new List<LyricsRecord>
        {
            Rec(1, "midnight city", "M83", 260),
            Rec(2, "Midnight City", "M83.", 244), // trailing dot like real LRCLIB data
            Rec(3, "midnight city (remix)", "M83", 244),
        };
        var pick = LyricsMatchPicker.Pick(candidates, Query);
        Assert.Equal(2, pick!.Id);
    }

    [Fact]
    public void FallsBack_ToTrackOnlyMatch()
    {
        var candidates = new List<LyricsRecord> { Rec(7, "Midnight City", "Some Cover Band", 243) };
        var pick = LyricsMatchPicker.Pick(candidates, Query);
        Assert.Equal(7, pick!.Id);
    }

    [Fact]
    public void Rejects_WrongDuration_WhenNothingClosest()
    {
        // Only candidates 40 s off: a different recording with the same title.
        var candidates = new List<LyricsRecord> { Rec(9, "Midnight City", "M83", 284) };
        var pick = LyricsMatchPicker.Pick(candidates, Query);
        Assert.Null(pick);
    }

    [Fact]
    public void EmptyCandidates_ReturnNull()
    {
        Assert.Null(LyricsMatchPicker.Pick([], Query));
    }
}

public class LyricsCacheTests
{
    private static LyricsQuery Q(string track = "T") => new(track, "A", "", 100);

    [Fact]
    public void Miss_ThenHit_AfterPut()
    {
        var cache = new LyricsCache();
        Assert.False(cache.TryGet(Q(), out var miss));
        Assert.Null(miss);

        cache.Put(Q(), new LyricsResult { TrackName = "T", ArtistName = "A", SyncedLines = Array.Empty<LrcLine>() });
        Assert.True(cache.TryGet(Q(), out var hit));
        Assert.NotNull(hit);
    }

    [Fact]
    public void Caches_NegativeResults_Too()
    {
        var cache = new LyricsCache();
        cache.Put(Q("unknown-track"), null); // sentinel: "we looked, nothing exists"
        Assert.True(cache.TryGet(Q("unknown-track"), out var hit));
        Assert.NotNull(hit);
        Assert.Equal(-1, hit!.RecordId);
    }

    [Fact]
    public void Capacity_IsBounded()
    {
        var cache = new LyricsCache();
        for (var i = 0; i < 100; i++) cache.Put(Q("track-" + i), null);
        Assert.True(cache.Count <= LyricsCache.Capacity);
    }
}
