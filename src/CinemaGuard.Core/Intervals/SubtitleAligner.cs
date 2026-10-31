using CinemaGuard.Core.Phonon;
using CinemaGuard.Core.Subtitles;

namespace CinemaGuard.Core.Intervals;

/// <summary>
/// Fits the subtitle file onto the transcript. A downloaded or ripped SRT is often timed for another
/// frame rate (24.000 against 23.976 is common, which is 6 seconds late by the end of a film).
/// Anchors are cue words that appear exactly once within a window around their estimated time;
/// a robust least-squares line through them gives the mapping.
/// </summary>
public sealed record SyncFit(bool Accepted, double Slope, double Intercept, int Anchors, double MedianResidual)
{
    public static SyncFit Identity { get; } = new(false, 1, 0, 0, 0);

    public double Map(double seconds) => Accepted ? Slope * seconds + Intercept : seconds;
}

public static class SubtitleAligner
{
    private const double SearchWindowSeconds = 15;
    private const double OutlierResidual = 1.0;
    private const double AcceptMedianResidual = 0.4;
    private const int MinimumAnchors = 30;
    private const double SlopeTolerance = 0.05;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "THE", "AND", "YOU", "THAT", "THIS", "WHAT", "WITH", "HAVE", "THERE", "THEY", "THEIR",
        "ABOUT", "WHICH", "WHEN", "WHERE", "WOULD", "COULD", "SHOULD", "BECAUSE", "BEEN", "FROM",
        "JUST", "KNOW", "LIKE", "WELL", "RIGHT", "GET", "GOT", "GOING", "WANT", "YEAH", "OKAY",
        "WELL", "DON'T", "DIDN'T", "DOESN'T", "ISN'T", "AREN'T", "CAN'T", "WON'T",
    };

    public static SyncFit Fit(IReadOnlyList<Cue> cues, PhononResult transcript)
    {
        if (cues.Count == 0 || transcript.Words.Count == 0) return SyncFit.Identity;

        var index = BuildWordIndex(transcript);
        var anchors = new List<(double Subtitle, double Transcript)>();

        foreach (var cue in cues)
        {
            var duration = cue.End.TotalSeconds - cue.Start.TotalSeconds;
            if (duration <= 0.2) continue;

            var formatted = SubtitleFormatter.FormatCue(cue.Text);
            if (formatted.Length < 8) continue;

            var searchFrom = 0;

            foreach (var rawToken in formatted.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var token = rawToken.Trim('-', '\'', '"', '.', ',', '?', '!').ToUpperInvariant();
                if (token.Length < 5 || StopWords.Contains(token)) continue;

                var position = formatted.IndexOf(rawToken, searchFrom, StringComparison.Ordinal);
                if (position < 0) position = searchFrom;
                searchFrom = Math.Min(formatted.Length, position + rawToken.Length);

                if (!index.TryGetValue(token, out var times)) continue;

                var estimate = cue.Start.TotalSeconds + (position + rawToken.Length / 2.0) / formatted.Length * duration;
                var matches = times.Where(t => Math.Abs(t - estimate) <= SearchWindowSeconds).ToList();

                // Exactly one plausible word: this anchor is unambiguous.
                if (matches.Count == 1) anchors.Add((estimate, matches[0]));

                if (anchors.Count >= 6000) break;
            }

            if (anchors.Count >= 6000) break;
        }

        if (anchors.Count < MinimumAnchors) return SyncFit.Identity;

        var (slope, intercept) = LeastSquares(anchors);

        for (var pass = 0; pass < 5; pass++)
        {
            var kept = anchors.Where(a => Math.Abs(slope * a.Subtitle + intercept - a.Transcript) <= OutlierResidual).ToList();
            if (kept.Count < MinimumAnchors) return SyncFit.Identity;

            var changed = kept.Count != anchors.Count;
            anchors = kept;
            (slope, intercept) = LeastSquares(anchors);
            if (!changed) break;
        }

        var residuals = anchors.Select(a => Math.Abs(slope * a.Subtitle + intercept - a.Transcript)).OrderBy(r => r).ToList();
        var median = residuals[residuals.Count / 2];

        var accepted = anchors.Count >= MinimumAnchors &&
                       median <= AcceptMedianResidual &&
                       slope >= 1 - SlopeTolerance &&
                       slope <= 1 + SlopeTolerance;

        return accepted
            ? new SyncFit(true, slope, intercept, anchors.Count, median)
            : SyncFit.Identity with { Anchors = anchors.Count, MedianResidual = median };
    }

    private static Dictionary<string, List<double>> BuildWordIndex(PhononResult transcript)
    {
        var index = new Dictionary<string, List<double>>(StringComparer.Ordinal);

        foreach (var word in transcript.Words)
        {
            var normalised = SwearMatcher.NormaliseWord(word.Text);
            if (normalised.Length < 5) continue;

            if (!index.TryGetValue(normalised, out var list))
            {
                list = [];
                index[normalised] = list;
            }

            list.Add(word.Start);
        }

        foreach (var list in index.Values) list.Sort();
        return index;
    }

    private static (double Slope, double Intercept) LeastSquares(List<(double Subtitle, double Transcript)> points)
    {
        var count = points.Count;
        var meanX = points.Average(p => p.Subtitle);
        var meanY = points.Average(p => p.Transcript);

        double numerator = 0, denominator = 0;
        foreach (var (x, y) in points)
        {
            numerator += (x - meanX) * (y - meanY);
            denominator += (x - meanX) * (x - meanX);
        }

        var slope = denominator <= 1e-9 ? 1.0 : numerator / denominator;
        return (slope, meanY - slope * meanX);
    }
}
