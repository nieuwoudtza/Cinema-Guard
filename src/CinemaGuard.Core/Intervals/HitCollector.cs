using CinemaGuard.Core.Phonon;
using CinemaGuard.Core.Subtitles;

namespace CinemaGuard.Core.Intervals;

public sealed record HitCollectorOptions(
    IReadOnlyList<string> WordsFull,
    IReadOnlyList<string> WordsSingle,
    int EstimatedPadBeforeMs,
    int EstimatedPadAfterMs,
    int WordPadBeforeMs,
    int WordPadAfterMs);

public sealed record HitSet(List<Hit> Hits, List<TimeRange> Ranges, SyncFit Fit)
{
    public int HitCount => Hits.Count;

    public IReadOnlyList<(string Label, int Count)> Summary =>
        Hits.GroupBy(h => h.Label, StringComparer.Ordinal)
            .Select(g => (Label: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Label, StringComparer.Ordinal)
            .ToList();
}

/// <summary>
/// One definition of "the film has filtered words": the union of hits from the English transcript,
/// the multilingual transcript, the subtitle file (drift-corrected), and legacy word-level subtitles.
/// A word one decoder missed is still caught by the subtitle, and vice versa.
/// </summary>
public static class HitCollector
{
    private const double SnapWindowSeconds = 2.0;
    private const double MergeGapSeconds = 0.05;
    private const double MinimumRangeSeconds = 0.02;

    public static HitSet Collect(
        HitCollectorOptions options,
        double duration,
        double audioOffset,
        PhononResult? english,
        PhononResult? multilingual,
        IReadOnlyList<Cue>? subtitleCues,
        IReadOnlyList<Cue>? legacyOneWordCues,
        bool alignSubtitles)
    {
        var hits = new List<Hit>();
        var transcriptHits = new List<Hit>();

        if (english is not null) transcriptHits.AddRange(FromTranscript(english, HitSource.English, options));
        if (multilingual is not null) transcriptHits.AddRange(FromTranscript(multilingual, HitSource.Multilingual, options));

        hits.AddRange(transcriptHits);

        var fit = SyncFit.Identity;
        if (alignSubtitles && english is not null && subtitleCues is { Count: > 0 })
            fit = SubtitleAligner.Fit(subtitleCues, english);

        if (subtitleCues is { Count: > 0 })
            hits.AddRange(FromSubtitles(subtitleCues, fit, options, transcriptHits));

        if (legacyOneWordCues is { Count: > 0 } && english is null)
            hits.AddRange(FromLegacyOneWord(legacyOneWordCues, options));

        var ranges = BuildRanges(hits, duration, audioOffset);
        return new HitSet(hits, ranges, fit);
    }

    public static List<Hit> FromTranscript(PhononResult transcript, HitSource source, HitCollectorOptions options)
    {
        var hits = new List<Hit>();
        if (transcript.Words.Count == 0) return hits;

        var words = new List<(PhononWord Word, string Normalised)>(transcript.Words.Count);
        foreach (var word in transcript.Words)
        {
            var normalised = SwearMatcher.NormaliseWord(word.Text);
            if (normalised.Length > 0) words.Add((word, normalised));
        }

        var full = options.WordsFull.ToHashSet(StringComparer.Ordinal);
        var single = options.WordsSingle.Where(w => !full.Contains(w)).ToList();

        var padBefore = options.WordPadBeforeMs / 1000.0;
        var padAfter = options.WordPadAfterMs / 1000.0;

        foreach (var entry in full)
        {
            foreach (var (start, end) in FindPhrase(words.Select(w => w.Normalised).ToList(), entry))
            {
                var wordStart = words[start].Word.Start;
                var wordEnd = words[end].Word.End;
                var segment = FindSegment(transcript.Segments, wordStart, wordEnd);

                hits.Add(segment is not null
                    ? new Hit(segment.Start, segment.End, entry, source, WholeCue: true)
                    : new Hit(wordStart, wordEnd, entry, source, WholeCue: true));
            }
        }

        foreach (var entry in single)
        {
            // A phrase (HOLY SHIT) mutes from its first word to its last, not the first word alone.
            foreach (var (start, end) in FindPhrase(words.Select(w => w.Normalised).ToList(), entry))
                hits.Add(new Hit(words[start].Word.Start - padBefore, words[end].Word.End + padAfter, entry, source, WholeCue: false));
        }

        // Words the source already masked (rare in a transcript, common in subtitles).
        foreach (var word in transcript.Words)
        {
            if (!SwearMatcher.HasMaskedWord(word.Text)) continue;
            hits.Add(new Hit(word.Start - padBefore, word.End + padAfter, SwearMatcher.MaskedLabel, HitSource.Masked, WholeCue: false));
        }

        return hits;
    }

    public static List<Hit> FromSubtitles(
        IReadOnlyList<Cue> cues,
        SyncFit fit,
        HitCollectorOptions options,
        IReadOnlyList<Hit> transcriptHits)
    {
        var hits = new List<Hit>();
        var full = options.WordsFull.ToHashSet(StringComparer.Ordinal);
        var single = options.WordsSingle.Where(w => !full.Contains(w)).ToList();

        var padBefore = options.EstimatedPadBeforeMs / 1000.0;
        var padAfter = options.EstimatedPadAfterMs / 1000.0;

        foreach (var cue in cues)
        {
            var cueDuration = cue.End.TotalSeconds - cue.Start.TotalSeconds;
            if (cueDuration <= 0) continue;

            var formatted = SubtitleFormatter.FormatCue(cue.Text);
            if (formatted.Length == 0) continue;

            foreach (var match in SwearMatcher.Find(formatted, full))
            {
                var start = fit.Map(cue.Start.TotalSeconds);
                var end = fit.Map(cue.End.TotalSeconds);
                hits.Add(new Hit(start, end, match.Entry, HitSource.Subtitle, WholeCue: true));
            }

            foreach (var match in SwearMatcher.Find(formatted, single))
            {
                var (from, to) = EstimatedSpan(formatted, match, cue);
                AddEstimatedHit(hits, transcriptHits, fit, from, to, padBefore, padAfter, match.Entry);
            }

            foreach (var masked in SwearMatcher.FindMasked(cue.Text))
            {
                var raw = cue.Text.Replace("\r\n", " ").Replace('\n', ' ');
                if (raw.Length == 0) continue;
                var fraction = (masked.Index + masked.Length / 2.0) / raw.Length;
                var estimate = cue.Start.TotalSeconds + fraction * cueDuration;
                AddEstimatedHit(hits, transcriptHits, fit, estimate, estimate, padBefore, padAfter, SwearMatcher.MaskedLabel);
            }
        }

        return hits;
    }

    public static List<Hit> FromLegacyOneWord(IReadOnlyList<Cue> cues, HitCollectorOptions options)
    {
        var hits = new List<Hit>();
        var padBefore = options.WordPadBeforeMs / 1000.0;
        var padAfter = options.WordPadAfterMs / 1000.0;

        var full = options.WordsFull.ToHashSet(StringComparer.Ordinal);
        var all = options.WordsFull.Concat(options.WordsSingle).Distinct().ToList();

        foreach (var cue in cues)
        {
            var text = cue.Text.Trim();
            if (text.Length == 0) continue;

            foreach (var match in SwearMatcher.Find(text, all))
            {
                if (full.Contains(match.Entry))
                    hits.Add(new Hit(cue.Start.TotalSeconds, cue.End.TotalSeconds, match.Entry, HitSource.LegacyOneWord, WholeCue: true));
                else
                    hits.Add(new Hit(cue.Start.TotalSeconds - padBefore, cue.End.TotalSeconds + padAfter, match.Entry,
                        HitSource.LegacyOneWord, WholeCue: false));
            }
        }

        return hits;
    }

    public static List<TimeRange> BuildRanges(IEnumerable<Hit> hits, double duration, double audioOffset)
    {
        var ranges = hits
            .Select(h => new TimeRange(h.Start, h.End))
            .Where(r => r.End > r.Start)
            .Select(r => new TimeRange(
                Math.Max(0, r.Start),
                duration > 0 ? Math.Min(duration, r.End) : r.End))
            .Where(r => r.End > r.Start)
            .OrderBy(r => r.Start)
            .ToList();

        var merged = new List<TimeRange>(ranges.Count);

        foreach (var range in ranges)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End + MergeGapSeconds)
            {
                if (range.End > merged[^1].End) merged[^1] = new TimeRange(merged[^1].Start, range.End);
            }
            else
            {
                merged.Add(range);
            }
        }

        if (Math.Abs(audioOffset) > 1e-6)
        {
            merged = merged
                .Select(r => new TimeRange(Math.Max(0, r.Start + audioOffset), r.End + audioOffset))
                .Where(r => r.End > r.Start)
                .ToList();
        }

        return merged.Where(r => r.Duration >= MinimumRangeSeconds).ToList();
    }

    private static void AddEstimatedHit(
        List<Hit> hits,
        IReadOnlyList<Hit> transcriptHits,
        SyncFit fit,
        double from,
        double to,
        double padBefore,
        double padAfter,
        string label)
    {
        // Phonon already timed this word precisely; the subtitle estimate adds nothing.
        var duplicate = transcriptHits.Any(h => Math.Abs(h.Start - from) <= SnapWindowSeconds &&
                                                (string.Equals(h.Label, label, StringComparison.Ordinal) || label == SwearMatcher.MaskedLabel));
        if (duplicate) return;

        hits.Add(new Hit(fit.Map(from) - padBefore, fit.Map(to) + padAfter, label, HitSource.Subtitle, WholeCue: false));
    }

    /// <summary>
    /// Where in the cue the match is spoken, by its share of the text. A word is a single moment (its
    /// middle) padded either side; a phrase runs from where its first word starts to where its last ends.
    /// </summary>
    private static (double From, double To) EstimatedSpan(string formatted, WordMatch match, Cue cue)
    {
        var duration = cue.End.TotalSeconds - cue.Start.TotalSeconds;
        double At(double index) => cue.Start.TotalSeconds + index / formatted.Length * duration;

        if (!match.Entry.Contains(' '))
        {
            var middle = At(match.Index + match.Length / 2.0);
            return (middle, middle);
        }

        return (At(match.Index), At(match.End));
    }

    private static PhononSegment? FindSegment(IReadOnlyList<PhononSegment> segments, double wordStart, double wordEnd)
    {
        foreach (var segment in segments)
        {
            if (segment.Start <= wordStart && segment.End >= wordEnd) return segment;
            if (segment.Start > wordEnd) break;
        }

        return segments.LastOrDefault(s => s.Start <= wordStart && s.End > wordStart);
    }

    /// <summary>All (firstWordIndex, lastWordIndex) runs that equal the entry (which may be a phrase).</summary>
    private static List<(int Start, int End)> FindPhrase(IReadOnlyList<string> words, string entry)
    {
        var results = new List<(int, int)>();
        var parts = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return results;

        for (var i = 0; i + parts.Length <= words.Count; i++)
        {
            var matched = true;

            for (var j = 0; j < parts.Length; j++)
            {
                if (!string.Equals(words[i + j], parts[j], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched) results.Add((i, i + parts.Length - 1));
        }

        return results;
    }
}
