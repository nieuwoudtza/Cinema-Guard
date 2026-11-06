using CinemaGuard.Core.Phonon;
using CinemaGuard.Core.Subtitles;

namespace CinemaGuard.Core.Intervals;

/// <param name="EstimatedPadBeforeMs">Single words only the subtitles have: around the guessed spot in the line.</param>
/// <param name="WordPadBeforeMs">Single words the speech model timed (or one-word subtitles).</param>
/// <param name="LinePadBeforeMs">Sentence-list words: around the whole subtitle line or the model's sentence.</param>
public sealed record HitCollectorOptions(
    IReadOnlyList<string> WordsFull,
    IReadOnlyList<string> WordsSingle,
    int EstimatedPadBeforeMs,
    int EstimatedPadAfterMs,
    int WordPadBeforeMs,
    int WordPadAfterMs,
    int LinePadBeforeMs = 0,
    int LinePadAfterMs = 0)
{
    public static HitCollectorOptions From(Settings.AppSettings settings) => new(
        settings.WordsFull,
        settings.WordsSingle,
        settings.EstimatedPadBeforeMs,
        settings.EstimatedPadAfterMs,
        settings.WordPadBeforeMs,
        settings.WordPadAfterMs,
        settings.LinePadBeforeMs,
        settings.LinePadAfterMs);
}

/// <param name="Trimmed">Transcript words cut back before they were used; see <see cref="StretchedWords"/>.</param>
/// <param name="CaptionFit">The fit that placed the captions for <see cref="StretchedWords"/>, even when <paramref name="Fit"/> is not used.</param>
public sealed record HitSet(
    List<Hit> Hits,
    List<TimeRange> Ranges,
    SyncFit Fit,
    WordAlignment? Alignment = null,
    IReadOnlyList<TrimmedWord>? Trimmed = null,
    SyncFit? CaptionFit = null)
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

    // The shortest and longest a subtitle word's own share of its line may be, when no padding widens it.
    private const double MinimumWordSeconds = 0.2;
    private const double MaximumWordSeconds = 1.0;

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

        // Fitted even when the subtitle hits are not moved by it: it tells whether the captions can show where a
        // stretched word really ends. Read from word starts, which a stretch leaves alone.
        var captionFit = english is not null && subtitleCues is { Count: > 0 }
            ? SubtitleAligner.Fit(subtitleCues, english)
            : SyncFit.Identity;

        var trimmed = new List<TrimmedWord>();

        if (english is not null)
        {
            (english, var cut) = StretchedWords.Trim(english, HitSource.English, subtitleCues, captionFit);
            trimmed.AddRange(cut);
            transcriptHits.AddRange(FromTranscript(english, HitSource.English, options));
        }

        if (multilingual is not null)
        {
            (multilingual, var cut) = StretchedWords.Trim(multilingual, HitSource.Multilingual, subtitleCues, captionFit);
            trimmed.AddRange(cut);
            transcriptHits.AddRange(FromTranscript(multilingual, HitSource.Multilingual, options));
        }

        hits.AddRange(transcriptHits);

        var fit = SyncFit.Identity;
        WordAlignment? alignment = null;

        if (alignSubtitles && english is not null && subtitleCues is { Count: > 0 })
        {
            fit = captionFit;
            alignment = WordAligner.Align(subtitleCues, english);
        }

        if (subtitleCues is { Count: > 0 })
            hits.AddRange(FromSubtitles(subtitleCues, fit, options, transcriptHits, alignment));

        if (legacyOneWordCues is { Count: > 0 } && english is null)
            hits.AddRange(FromLegacyOneWord(legacyOneWordCues, options));

        var ranges = BuildRanges(hits, duration, audioOffset);
        return new HitSet(hits, ranges, fit, alignment, trimmed, captionFit);
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
        var linePadBefore = options.LinePadBeforeMs / 1000.0;
        var linePadAfter = options.LinePadAfterMs / 1000.0;

        foreach (var entry in full)
        {
            foreach (var (start, end) in FindPhrase(words.Select(w => w.Normalised).ToList(), entry))
            {
                var wordStart = words[start].Word.Start;
                var wordEnd = words[end].Word.End;
                var segment = FindSegment(transcript.Segments, wordStart, wordEnd);
                var (from, to) = segment is not null ? (segment.Start, segment.End) : (wordStart, wordEnd);

                hits.Add(new Hit(from - linePadBefore, to + linePadAfter, entry, source, WholeCue: true));
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

    /// <param name="alignment">
    /// The word-by-word match to the transcript: a single word it can place is timed from the transcript's
    /// words around it; one it cannot is estimated from its place in the cue, through <paramref name="fit"/>.
    /// </param>
    public static List<Hit> FromSubtitles(
        IReadOnlyList<Cue> cues,
        SyncFit fit,
        HitCollectorOptions options,
        IReadOnlyList<Hit> transcriptHits,
        WordAlignment? alignment = null)
    {
        var hits = new List<Hit>();
        var full = options.WordsFull.ToHashSet(StringComparer.Ordinal);
        var single = options.WordsSingle.Where(w => !full.Contains(w)).ToList();

        var linePadBefore = options.LinePadBeforeMs / 1000.0;
        var linePadAfter = options.LinePadAfterMs / 1000.0;

        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            var cueDuration = cue.End.TotalSeconds - cue.Start.TotalSeconds;
            if (cueDuration <= 0) continue;

            // Masks kept, so a censored f*** is found in the same text the word alignment read.
            var formatted = SubtitleFormatter.FormatCue(cue.Text, keepMasks: true);
            if (formatted.Length == 0) continue;

            // The whole line, widened: subtitles are often shown a little before or after the line is spoken.
            foreach (var match in SwearMatcher.Find(formatted, full))
            {
                var start = fit.Map(cue.Start.TotalSeconds) - linePadBefore;
                var end = fit.Map(cue.End.TotalSeconds) + linePadAfter;
                hits.Add(new Hit(start, end, match.Entry, HitSource.Subtitle, WholeCue: true));
            }

            foreach (var match in SwearMatcher.Find(formatted, single).Concat(SwearMatcher.FindMasked(formatted)))
                AddWordHit(hits, transcriptHits, fit, alignment, options, index, cue, formatted, match);
        }

        return hits;
    }

    public static List<Hit> FromLegacyOneWord(IReadOnlyList<Cue> cues, HitCollectorOptions options)
    {
        var hits = new List<Hit>();
        var padBefore = options.WordPadBeforeMs / 1000.0;
        var padAfter = options.WordPadAfterMs / 1000.0;
        var linePadBefore = options.LinePadBeforeMs / 1000.0;
        var linePadAfter = options.LinePadAfterMs / 1000.0;

        var full = options.WordsFull.ToHashSet(StringComparer.Ordinal);
        var all = options.WordsFull.Concat(options.WordsSingle).Distinct().ToList();

        foreach (var cue in cues)
        {
            var text = cue.Text.Trim();
            if (text.Length == 0) continue;

            foreach (var match in SwearMatcher.Find(text, all))
            {
                if (full.Contains(match.Entry))
                    hits.Add(new Hit(cue.Start.TotalSeconds - linePadBefore, cue.End.TotalSeconds + linePadAfter, match.Entry,
                        HitSource.LegacyOneWord, WholeCue: true));
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

    private static void AddWordHit(
        List<Hit> hits,
        IReadOnlyList<Hit> transcriptHits,
        SyncFit fit,
        WordAlignment? alignment,
        HitCollectorOptions options,
        int cueIndex,
        Cue cue,
        string formatted,
        WordMatch match)
    {
        var wordPadBefore = options.WordPadBeforeMs / 1000.0;
        var wordPadAfter = options.WordPadAfterMs / 1000.0;
        var estimatedPadBefore = options.EstimatedPadBeforeMs / 1000.0;
        var estimatedPadAfter = options.EstimatedPadAfterMs / 1000.0;

        double spoken, from, to;

        if (alignment?.Locate(cueIndex, match.Index, match.End) is { } span)
        {
            // Already on the transcript's timeline. Alone between its neighbours it is timed like a word the
            // model heard; a share of a longer gap gets the wide padding, but never past those neighbours.
            spoken = span.Start;
            from = span.Exact ? span.Start - wordPadBefore : Math.Max(span.Start - estimatedPadBefore, span.GapStart - wordPadBefore);
            to = span.Exact ? span.End + wordPadAfter : Math.Min(span.End + estimatedPadAfter, span.GapEnd + wordPadAfter);
        }
        else
        {
            var (start, end) = EstimatedSpan(formatted, match, cue);
            spoken = fit.Map(start);
            from = spoken - estimatedPadBefore;
            to = fit.Map(end) + estimatedPadAfter;
        }

        // With no padding a word placed at a single moment (its middle, or a gap the neighbours closed)
        // would mute nothing: it gets its share of the line, around that moment. A word the model timed keeps its own length.
        if (to - from < MinimumWordSeconds)
        {
            var least = Math.Clamp(match.Length / (double)formatted.Length * (cue.End.TotalSeconds - cue.Start.TotalSeconds),
                MinimumWordSeconds, MaximumWordSeconds);

            var middle = (from + to) / 2;
            from = middle - least / 2;
            to = middle + least / 2;
        }

        // Phonon already timed this word precisely; the subtitle adds nothing. Compared on the transcript's
        // timeline: drifted subtitles are seconds off by the end of a film, and the raw time would match a
        // different word.
        var label = match.Entry;
        var duplicate = transcriptHits.Any(h => Math.Abs(h.Start - spoken) <= SnapWindowSeconds &&
                                                (string.Equals(h.Label, label, StringComparison.Ordinal) || label == SwearMatcher.MaskedLabel));
        if (duplicate) return;

        hits.Add(new Hit(from, to, label, HitSource.Subtitle, WholeCue: false));
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
