using System.Text;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Tools;

namespace CinemaGuard.Core.Subtitles;

/// <summary>Writes SRT files: UTF-8 with BOM, CRLF, hours never truncated.</summary>
public static class SrtWriter
{
    public static void Write(string path, IEnumerable<Cue> cues)
    {
        var builder = new StringBuilder();
        var index = 1;

        foreach (var cue in cues)
        {
            builder.Append(index++).Append("\r\n");
            builder.Append(FormatTime(cue.Start)).Append(" --> ").Append(FormatTime(cue.End)).Append("\r\n");

            foreach (var line in cue.Text.Replace("\r\n", "\n").Split('\n'))
                builder.Append(line).Append("\r\n");

            builder.Append("\r\n");
        }

        File.WriteAllText(PathGuard.Extended(path), builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static string FormatTime(TimeSpan time)
    {
        var total = (long)Math.Round(time.TotalMilliseconds);
        if (total < 0) total = 0;

        var hours = total / 3_600_000;
        var minutes = total % 3_600_000 / 60_000;
        var seconds = total % 60_000 / 1_000;
        var milliseconds = total % 1_000;

        return $"{hours:00}:{minutes:00}:{seconds:00},{milliseconds:000}";
    }
}

public sealed record CensoredSubtitleResult(
    string? FullPath,
    string? CensoredPath,
    int TotalCues,
    int CensoredCues,
    int MaskedWords);

/// <summary>
/// Writes the masked subtitle files. Full keeps every cue with the words masked. Censored also keeps
/// every cue, but a cue with no hit gets a non-breaking space instead of its text: VLC, Plex and some
/// other players need the full cue timeline and misbehave when the non-censored cues are missing. The
/// old app used U+00A0 for the same reason, and FFmpeg keeps it in the embedded track. Masking works on
/// a copy, so the cue objects used for matching and language detection are never modified.
/// </summary>
public static class CensoredSubtitleWriter
{
    public static string FullPath(string basePathWithoutExtension, AppSettings settings) =>
        basePathWithoutExtension + "_Full" + AppSettings.SanitiseAffix(settings.CustomAffix) + ".srt";

    public static string CensoredPath(string basePathWithoutExtension, AppSettings settings) =>
        basePathWithoutExtension + "_Censored" + AppSettings.SanitiseAffix(settings.CustomAffix) + ".srt";

    /// <param name="spared">
    /// Per cue, the matches left unmasked (words the review before censoring left in at that line); null, or a
    /// null entry, masks everything.
    /// </param>
    public static CensoredSubtitleResult Write(
        IReadOnlyList<Cue> cues,
        string basePathWithoutExtension,
        AppSettings settings,
        SubtitleOutputType outputType,
        IReadOnlyList<Func<string, bool>?>? spared = null)
    {
        if (outputType == SubtitleOutputType.None)
            return new CensoredSubtitleResult(null, null, cues.Count, 0, 0);

        var entries = settings.WordsFull.Concat(settings.WordsSingle).ToList();

        var full = new List<Cue>(cues.Count);
        var censored = new List<Cue>(cues.Count);
        var maskedWords = 0;
        var censoredCues = 0;

        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            var spare = spared is not null && index < spared.Count ? spared[index] : null;
            var masked = Mask(cue.Text, entries, settings.MaskCharacter(), out var count, spare);
            maskedWords += count;

            var fullCue = cue.Clone();
            fullCue.Text = masked;
            full.Add(fullCue);

            var censoredCue = cue.Clone();
            if (count > 0)
            {
                censoredCue.Text = masked;
                censoredCues++;
            }
            else
            {
                // The old app's workaround: a real cue holding a non-breaking space (U+00A0), not an
                // ASCII space. FFmpeg treats an ordinary blank line as an empty cue and drops it;
                // the non-breaking space keeps the cue in the embedded track, which VLC and Plex need.
                censoredCue.Text = "\u00A0";
            }

            censored.Add(censoredCue);
        }

        string? fullPath = null;
        string? censoredPath = null;

        if (outputType is SubtitleOutputType.Full or SubtitleOutputType.Dual)
        {
            fullPath = FullPath(basePathWithoutExtension, settings);
            SrtWriter.Write(fullPath, full);
        }

        if (outputType is SubtitleOutputType.Censored or SubtitleOutputType.Dual)
        {
            if (censored.Count > 0)
            {
                censoredPath = CensoredPath(basePathWithoutExtension, settings);
                SrtWriter.Write(censoredPath, censored);
            }
            else
            {
                // No source cues at all: never leave an empty file that FFmpeg cannot open.
                var empty = CensoredPath(basePathWithoutExtension, settings);
                if (File.Exists(empty)) File.Delete(empty);
            }
        }

        return new CensoredSubtitleResult(fullPath, censoredPath, cues.Count, censoredCues, maskedWords);
    }

    /// <summary>Mask every occurrence; overlapping entries are handled by a character mask, not by re-scanning.</summary>
    /// <param name="spare">Matches (by entry) to leave as they are, such as a word the review left in; not counted.</param>
    public static string Mask(string text, IReadOnlyList<string> entries, char censorCharacter, out int matchCount, Func<string, bool>? spare = null)
    {
        matchCount = 0;
        if (string.IsNullOrEmpty(text) || entries.Count == 0) return text;

        var matches = SwearMatcher.Find(text, entries);
        if (spare is not null) matches = matches.Where(match => !spare(match.Entry)).ToList();
        matchCount = matches.Count;
        if (matches.Count == 0) return text;

        var mask = new bool[text.Length];
        var labels = new HashSet<string>(StringComparer.Ordinal);

        // Longest first so "MOTHERFUCKING" wins over "FUCK".
        foreach (var match in matches.OrderByDescending(m => m.Length))
        {
            var end = Math.Min(match.End, text.Length);
            if (end <= match.Index) continue;

            for (var i = match.Index; i < end; i++) mask[i] = true;
            labels.Add(match.Entry);
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++) builder.Append(mask[i] ? censorCharacter : text[i]);
        return builder.ToString();
    }
}
