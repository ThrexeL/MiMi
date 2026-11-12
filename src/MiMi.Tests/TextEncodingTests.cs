using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using MiMi.App.UI;
using MiMi.Lyrics;
using MiMi.Media;
using Xunit;

namespace MiMi.Tests;

public class TextEncodingTests
{
    [Fact]
    public void InstrumentalGap_DisplaysSingleMusicalNote()
    {
        var vm = new MediaViewModel();
        vm.Update(new MediaSnapshot { Title = "Neverender", Position = TimeSpan.FromSeconds(10) });
        vm.UpdateLyrics(LyricsState.Synced, new LyricsResult
        {
            TrackName = "Neverender",
            ArtistName = "Justice",
            SyncedLines = new[] { new LrcLine(TimeSpan.Zero, "") },
        }, 0, true);

        Assert.Equal("\u266A", vm.LyricCurrent);
        Assert.False(vm.LyricCurrentIsLine);
    }

    [Fact]
    public void MediaMetadata_PreservesInternationalCharacters()
    {
        const string title = "\u00C2me \u2014 \u6771\u4EAC \U0001F3B5";
        const string artist = "Bj\u00F6rk";
        var vm = new MediaViewModel();
        vm.Update(new MediaSnapshot { Title = title, Artist = artist });

        Assert.Equal(title, vm.Title);
        Assert.Equal(artist, vm.Subtitle);
    }

    [Theory]
    [InlineData(true, "\uE769")]
    [InlineData(false, "\uE768")]
    public void PlaybackGlyph_IsSingleSegoeIcon(bool running, string expected)
    {
        var converter = new RunningToGlyphConverter();
        Assert.Equal(expected, converter.Convert(running, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void SourceFiles_AreValidUtf8WithoutKnownEncodingCorruption()
    {
        // Escaped signatures keep this regression check from matching its own source.
        string[] corruptSequences =
        {
            "\u00C2\u00B7", "\u00E2\u20AC\u201D", "\u00E2\u20AC\u00A6",
            "\u00E2\u2020\u2019", "\u00C3\u2014", "\u00E2\u20AC\u00B9",
            "\u00E2\u20AC\u00BA", "\u00E2\u2122\u00AA",
            "\u00EE\u009D\u00A9", "\u00EE\u009D\u00A8", "\uFFFD",
        };
        var sourceRoot = Path.GetFullPath(Path.Combine(TestDirectory(), ".."));
        var utf8 = new UTF8Encoding(false, true);
        var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p) is ".cs" or ".xaml")
            .Where(p => !Path.GetRelativePath(sourceRoot, p).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"))
            .ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var text = utf8.GetString(File.ReadAllBytes(file));
            foreach (var sequence in corruptSequences)
                Assert.False(text.Contains(sequence, StringComparison.Ordinal),
                    $"Encoding corruption in {file}: {string.Join(" ", sequence.Select(c => $"U+{(int)c:X4}"))}");
        }
    }

    private static string TestDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
