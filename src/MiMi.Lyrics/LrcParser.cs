using System.Globalization;
using System.Text;

namespace MiMi.Lyrics;

/// <summary>
/// Robust LRC parser: handles single and multiple timestamps per line, metadata tags
/// ([ti:]/[ar:]/[al:]/[by:]/[offset:]), unsorted input, CRLF, and fractional formats
/// ([mm:ss.xx] and [mm:ss.xxx]). The [offset:] tag shifts every timestamp as LRC
/// players have always done.
/// </summary>
public static class LrcParser
{
    public static IReadOnlyList<LrcLine> Parse(string? lrcText)
    {
        if (string.IsNullOrWhiteSpace(lrcText)) return Array.Empty<LrcLine>();

        var lines = new List<LrcLine>();
        TimeSpan offset = TimeSpan.Zero;

        foreach (var raw in lrcText.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // Split leading bracket groups from the text payload.
            var cursor = 0;
            var timestamps = new List<TimeSpan>();
            var sawTimestamp = false;

            while (cursor < line.Length && line[cursor] == '[')
            {
                var close = line.IndexOf(']', cursor);
                if (close < 0) break;
                var inner = line.Substring(cursor + 1, close - cursor - 1);

                if (TryParseTimestamp(inner, out var ts))
                {
                    timestamps.Add(ts);
                    sawTimestamp = true;
                }
                else if (inner.StartsWith("offset:", StringComparison.OrdinalIgnoreCase)
                         && TryParseOffset(inner["offset:".Length..], out offset))
                {
                    // offset consumed
                }
                // other metadata tags ([ti:..], [ar:..], [al:..], [by:..]) are ignored:
                // LRCLIB already supplies authoritative track/artist fields.
                cursor = close + 1;
            }

            if (!sawTimestamp) continue; // plain metadata or garbage line

            var text = line[cursor..].Trim();
            foreach (var ts in timestamps)
                lines.Add(new LrcLine(ts - offset, text));
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    /// <summary>Current line index for a playback position: the last line whose time is
    /// at or before the position. -1 before the first line. Stateless, so seeking and
    /// pausing need no special handling and can never desynchronize.</summary>
    public static int CurrentLineIndex(IReadOnlyList<LrcLine> lines, TimeSpan position)
    {
        if (lines.Count == 0) return -1;
        int lo = 0, hi = lines.Count - 1, best = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lines[mid].Time <= position) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best;
    }

    private static bool TryParseTimestamp(string inner, out TimeSpan time)
    {
        // Accept [mm:ss], [mm:ss.xx], [mm:ss.xxx]; minutes may exceed 59.
        time = TimeSpan.Zero;
        var parts = inner.Split(':');
        if (parts is not ({ Length: 2 } or { Length: 3 })) return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
            return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return false;
        var fraction = parts.Length == 3
            && double.TryParse("0." + parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var frac)
            ? frac : 0.0;

        if (seconds < 0 || seconds >= 60 + 0.0001 && parts.Length == 2)
        {
            // [mm:ss.xx] always keeps seconds < 60; tolerate out-of-range generously but
            // reject obviously invalid negatives.
        }
        if (minutes < 0 || seconds < 0) return false;

        time = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds) + TimeSpan.FromSeconds(fraction);
        return true;
    }

    private static bool TryParseOffset(string raw, out TimeSpan offset)
    {
        // LRC offset is milliseconds; positive shifts lyrics earlier.
        offset = TimeSpan.Zero;
        raw = raw.Trim();
        if (raw.Length == 0) return false;
        var sign = 1;
        if (raw[0] == '+') raw = raw[1..];
        else if (raw[0] == '-') { sign = -1; raw = raw[1..]; }
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            return false;
        offset = TimeSpan.FromMilliseconds(sign * ms);
        return true;
    }
}
