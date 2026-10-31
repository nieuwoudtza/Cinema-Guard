using System.Globalization;
using System.Text.RegularExpressions;
using CinemaGuard.Core.Tools;

namespace CinemaGuard.Core.Subtitles;

/// <summary>
/// SRT / WebVTT / ASS reading that does not assume a fixed timestamp width.
/// Fixes the old parser: the last cue was dropped, and any line that was not exactly
/// 29 characters (trailing spaces, X1:/Y1: coordinates, a dot instead of a comma,
/// a one-digit hour) broke cue detection.
/// </summary>
public static partial class SubtitleReader
{
    private static readonly Regex TimingRegex = new(
        @"^\s*(?:(?<sh>\d{1,3}):)?(?<sm>\d{1,2}):(?<ss>\d{2})[.,](?<sf>\d{1,3})\s*-{1,3}>\s*(?:(?<eh>\d{1,3}):)?(?<em>\d{1,2}):(?<es>\d{2})[.,](?<ef>\d{1,3})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AssOverrideRegex = new(@"\{[^}]*\}", RegexOptions.Compiled); 

    public static SubtitleDocument ReadFile(string path)
    {
        var bytes = File.ReadAllBytes(PathGuard.Extended(path));
        var text = SubtitleEncoding.Decode(bytes);
        var format = GuessFormat(path, text);
        return new SubtitleDocument(format, Parse(text, format), text);
    }

    public static SubtitleFormat GuessFormat(string path, string text)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".vtt") return SubtitleFormat.Vtt;
        if (extension is ".ass" or ".ssa") return SubtitleFormat.Ass;
        if (text.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Dialogue:", StringComparison.OrdinalIgnoreCase)) return SubtitleFormat.Ass;
        if (text.TrimStart().StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)) return SubtitleFormat.Vtt;
        return SubtitleFormat.Srt;
    }

    public static List<Cue> Parse(string text, SubtitleFormat format) => format switch
    {
        SubtitleFormat.Ass => ParseAss(text),
        SubtitleFormat.Vtt => ParseTimed(StripVttBlocks(text)),
        _ => ParseTimed(text),
    };

    public static List<Cue> ParseSrt(string text) => ParseTimed(text);

    private static List<Cue> ParseTimed(string text)
    {
        var lines = SplitLines(text);
        var cues = new List<Cue>();
        var index = 0;

        while (index < lines.Count)
        {
            if (!TryParseTiming(lines[index], out var start, out var end))
            {
                index++;
                continue;
            }

            index++;
            var body = new List<string>();

            while (index < lines.Count)
            {
                if (TryParseTiming(lines[index], out _, out _)) break;

                if (lines[index].Trim().Length == 0)
                {
                    index++;
                    break;
                }

                // No blank line between cues: the next cue's index sits on its own line.
                if (IsIndexLine(lines[index]) && index + 1 < lines.Count && TryParseTiming(lines[index + 1], out _, out _))
                    break;

                body.Add(lines[index]);
                index++;
            }

            cues.Add(new Cue
            {
                Start = start,
                End = end > start ? end : start + TimeSpan.FromSeconds(1),
                Text = string.Join("\n", body).Trim(),
            });
        }

        cues.Sort((a, b) => a.Start.CompareTo(b.Start));
        return cues;
    }

    private static List<Cue> ParseAss(string text)
    {
        var cues = new List<Cue>();
        var lines = SplitLines(text);
        var inEvents = false;
        List<string>? fields = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (line.StartsWith('['))
            {
                inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inEvents) continue;

            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                fields = line[7..].Split(',').Select(f => f.Trim()).ToList();
                continue;
            }

            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;

            var fieldCount = fields?.Count ?? 10;
            var parts = SplitLimited(line[9..], ',', fieldCount);
            if (parts.Count < fieldCount) continue;

            var startIndex = FieldIndex(fields, "Start", 1);
            var endIndex = FieldIndex(fields, "End", 2);
            var textIndex = FieldIndex(fields, "Text", fieldCount - 1);

            if (!TryParseAssTime(parts[startIndex], out var start) || !TryParseAssTime(parts[endIndex], out var end))
                continue;

            var content = parts[textIndex].Replace("\\N", "\n").Replace("\\n", "\n");
            content = AssOverrideRegex.Replace(content, string.Empty);

            cues.Add(new Cue
            {
                Start = start,
                End = end > start ? end : start + TimeSpan.FromSeconds(1),
                Text = content.Trim(),
            });
        }

        cues.Sort((a, b) => a.Start.CompareTo(b.Start));
        return cues;
    }

    private static int FieldIndex(List<string>? fields, string name, int fallback)
    {
        if (fields is null) return fallback;
        var index = fields.FindIndex(f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : fallback;
    }

    private static string StripVttBlocks(string text)
    {
        var lines = SplitLines(text);
        var output = new List<string>();
        var skipping = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (skipping)
            {
                if (trimmed.Length == 0) skipping = false;
                continue;
            }

            if (trimmed.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)) continue;

            if (trimmed.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("STYLE", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("REGION", StringComparison.OrdinalIgnoreCase))
            {
                skipping = true;
                continue;
            }

            output.Add(line);
        }

        return string.Join("\n", output);
    }

    public static bool TryParseTiming(string line, out TimeSpan start, out TimeSpan end)
    {
        start = TimeSpan.Zero;
        end = TimeSpan.Zero;

        var match = TimingRegex.Match(line);
        if (!match.Success) return false;

        start = BuildTime(match, "sh", "sm", "ss", "sf");
        end = BuildTime(match, "eh", "em", "es", "ef");
        return true;
    }

    private static TimeSpan BuildTime(Match match, string hoursGroup, string minutesGroup, string secondsGroup, string fractionGroup)
    {
        var hours = match.Groups[hoursGroup].Success
            ? int.Parse(match.Groups[hoursGroup].Value, CultureInfo.InvariantCulture)
            : 0;
        var minutes = int.Parse(match.Groups[minutesGroup].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[secondsGroup].Value, CultureInfo.InvariantCulture);

        var fractionText = match.Groups[fractionGroup].Success ? match.Groups[fractionGroup].Value : "0";
        var milliseconds = int.Parse(fractionText.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);

        return new TimeSpan(0, hours, minutes, seconds, milliseconds);
    }

    private static bool TryParseAssTime(string value, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        var parts = value.Trim().Split(':');
        if (parts.Length != 3) return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)) return false;

        var secondParts = parts[2].Split(['.', ','], 2);
        if (!int.TryParse(secondParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)) return false;

        var milliseconds = 0;
        if (secondParts.Length == 2)
        {
            var fraction = secondParts[1].PadRight(3, '0');
            milliseconds = int.Parse(fraction[..3], CultureInfo.InvariantCulture);
        }

        time = new TimeSpan(0, hours, minutes, seconds, milliseconds);
        return true;
    }

    private static bool IsIndexLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length is > 0 and <= 6 && trimmed.All(char.IsAsciiDigit);
    }

    private static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    private static List<string> SplitLimited(string value, char separator, int count)
    {
        var parts = new List<string>(count);
        var start = 0;

        for (var i = 0; i < value.Length && parts.Count < count - 1; i++)
        {
            if (value[i] != separator) continue;
            parts.Add(value[start..i]);
            start = i + 1;
        }

        parts.Add(value[start..]);
        return parts;
    }
}
