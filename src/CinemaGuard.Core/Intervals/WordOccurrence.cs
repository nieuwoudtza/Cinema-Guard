using CinemaGuard.Core.Phonon;
using CinemaGuard.Core.Subtitles;

namespace CinemaGuard.Core.Intervals;

/// <summary>
/// One moment a filtered word is heard, for the review before censoring. The hits the English pass, the
/// multilingual pass and the subtitles found at that moment are one occurrence, so leaving it in drops
/// all of them (one left behind would still mute the word).
/// </summary>
public sealed class WordOccurrence
{
    public required double Start { get; init; }
    public required double End { get; init; }
    public required IReadOnlyList<Hit> Hits { get; init; }

    /// <summary>A sentence-list word (the whole line is muted) rather than a single word.</summary>
    public bool WholeCue => Hits[0].WholeCue;

    /// <summary>"FUCK", or "FUCK / FUCKING" when the passes heard the same moment as different words.</summary>
    public string Label => string.Join(" / ", Hits.Select(hit => hit.Label).Distinct(StringComparer.Ordinal));

    public IReadOnlyList<HitSource> Sources => Hits.Select(hit => hit.Source).Distinct().ToList();

    /// <summary>The subtitle line shown at this moment; null when there is none.</summary>
    public string? SubtitleText { get; set; }

    /// <summary>The words the speech model heard around it; null without a transcript.</summary>
    public string? HeardText { get; set; }

    /// <summary>Muted when the film is censored; the review unticks it to leave the word in.</summary>
    public bool Mute { get; set; } = true;

    public bool Overlaps(Hit hit) => hit.WholeCue == WholeCue && hit.Start < End && Start < hit.End;
}

public static class WordOccurrences
{
    private const int ContextWords = 4;

    /// <summary>
    /// The hits as occurrences in time order: overlapping hits are one moment. Single words and whole
    /// lines are grouped apart, so a sworn word inside a muted sentence is still its own row.
    /// </summary>
    public static List<WordOccurrence> Group(IEnumerable<Hit> hits)
    {
        var occurrences = new List<WordOccurrence>();

        foreach (var kind in hits.GroupBy(hit => hit.WholeCue))
        {
            List<Hit>? current = null;
            var end = double.NegativeInfinity;

            foreach (var hit in kind.OrderBy(hit => hit.Start).ThenBy(hit => hit.End))
            {
                if (current is not null && hit.Start < end)
                {
                    current.Add(hit);
                    end = Math.Max(end, hit.End);
                    continue;
                }

                if (current is not null) occurrences.Add(Create(current, end));
                current = [hit];
                end = hit.End;
            }

            if (current is not null) occurrences.Add(Create(current, end));
        }

        return occurrences.OrderBy(occurrence => occurrence.Start).ToList();

        static WordOccurrence Create(List<Hit> hits, double end) => new() { Start = hits[0].Start, End = end, Hits = hits };
    }

    /// <summary>True when the review left this hit's moment in. Also matches hits found later, such as a surround channel's own.</summary>
    public static bool IsLeftIn(Hit hit, IEnumerable<WordOccurrence> occurrences) =>
        occurrences.Any(occurrence => !occurrence.Mute && occurrence.Overlaps(hit));

    /// <summary>The hits without the moments the review left in, and the mute ranges rebuilt from them.</summary>
    public static HitSet LeaveIn(HitSet set, IReadOnlyList<WordOccurrence> occurrences, double duration, double audioOffset)
    {
        var leftIn = occurrences.Where(occurrence => !occurrence.Mute).ToList();
        if (leftIn.Count == 0) return set;

        var hits = set.Hits.Where(hit => !IsLeftIn(hit, leftIn)).ToList();
        return set with { Hits = hits, Ranges = HitCollector.BuildRanges(hits, duration, audioOffset) };
    }

    /// <summary>
    /// Fills in what was said at each occurrence: the subtitle line on screen (placed by <paramref name="fit"/>)
    /// and a few words either side from the transcript, so a misheard word can be told from a real one.
    /// </summary>
    public static void Describe(IEnumerable<WordOccurrence> occurrences, PhononResult? transcript, IReadOnlyList<Cue>? cues, SyncFit fit)
    {
        var words = transcript?.Words ?? [];

        foreach (var occurrence in occurrences)
        {
            occurrence.SubtitleText = CueAt(occurrence, cues, fit);
            occurrence.HeardText = WordsAround(occurrence, words);
        }
    }

    /// <summary>
    /// For each subtitle line, which matches the masking spares: those of a word the review left in at that
    /// line's moment. Null for a line with none. A match that is also a word still muted in the same line
    /// stays masked, as it cannot be told which of the two it is.
    /// </summary>
    /// <param name="fit">Places the lines on the hits' timeline, as for <see cref="Describe"/>.</param>
    public static Func<string, bool>?[] SparedInCues(IReadOnlyList<WordOccurrence> occurrences, IReadOnlyList<Cue> cues, SyncFit fit)
    {
        var spared = new Func<string, bool>?[cues.Count];
        if (occurrences.All(occurrence => occurrence.Mute)) return spared;

        var leftIn = new HashSet<string>?[cues.Count];
        var muted = new HashSet<string>?[cues.Count];

        foreach (var occurrence in occurrences)
        {
            var index = CueIndexAt(occurrence, cues, fit);
            if (index < 0) continue;

            var words = occurrence.Mute ? muted[index] ??= [] : leftIn[index] ??= [];
            foreach (var hit in occurrence.Hits) words.UnionWith(EntryWords(hit.Label));
        }

        for (var index = 0; index < cues.Count; index++)
        {
            if (leftIn[index] is not { } kept) continue;

            var still = muted[index] ?? [];
            spared[index] = entry =>
            {
                var words = EntryWords(entry);
                return words.Any(kept.Contains) && !words.Any(still.Contains);
            };
        }

        return spared;
    }

    /// <summary>"OH MY GOD" as OH, MY, GOD: a line's match and the word the review left in agree when they share a word.</summary>
    private static string[] EntryWords(string entry) =>
        entry.ToUpperInvariant().Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries);

    private static string? CueAt(WordOccurrence occurrence, IReadOnlyList<Cue>? cues, SyncFit fit)
    {
        if (cues is not { Count: > 0 }) return null;

        var index = CueIndexAt(occurrence, cues, fit);
        var text = index < 0 ? string.Empty : SubtitleFormatter.FormatCue(cues[index].Text, keepMasks: true);
        return text.Length == 0 ? null : text;
    }

    /// <summary>The line on screen at the occurrence: the one overlapping it most; -1 when none does.</summary>
    private static int CueIndexAt(WordOccurrence occurrence, IReadOnlyList<Cue> cues, SyncFit fit)
    {
        var best = -1;
        var bestOverlap = 0.0;

        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            var overlap = Math.Min(occurrence.End, fit.Map(cue.End.TotalSeconds)) - Math.Max(occurrence.Start, fit.Map(cue.Start.TotalSeconds));
            if (overlap <= bestOverlap) continue;

            best = index;
            bestOverlap = overlap;
        }

        return best;
    }

    private static string? WordsAround(WordOccurrence occurrence, IReadOnlyList<PhononWord> words)
    {
        var first = -1;
        var last = -1;

        for (var index = 0; index < words.Count; index++)
        {
            if (words[index].End <= occurrence.Start || words[index].Start >= occurrence.End) continue;
            if (first < 0) first = index;
            last = index;
        }

        if (first < 0) return null;

        var from = Math.Max(0, first - ContextWords);
        var to = Math.Min(words.Count - 1, last + ContextWords);
        var text = string.Join(" ", words.Skip(from).Take(to - from + 1).Select(word => word.Text.Trim()));

        return (from > 0 ? "… " : string.Empty) + text + (to < words.Count - 1 ? " …" : string.Empty);
    }
}
