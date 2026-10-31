using CinemaGuard.Core.Subtitles;

namespace CinemaGuard.Core.Phonon;

/// <summary>
/// Direct port of subgen.py's cue builder: Phonon already emits subtitle-shaped segments
/// (2 lines x 42 characters, 1-7 s, cut on pauses); this adds the reading-speed guard,
/// the line balancing and the ordering rules.
/// </summary>
public static class CueBuilder
{
    public const int CueLineChars = 42;
    public const int CueLines = 2;
    public const double MinPieceSeconds = 0.8;
    public const double MinCueSeconds = 0.8;
    public const double MaxCueSeconds = 7.0;

    private sealed class Piece
    {
        public double Start;
        public double End;
        public string Text = string.Empty;
        public List<PhononWord> Words = [];
    }

    public static List<Cue> Build(PhononResult result, double maxCps)
    {
        var segments = result.Segments ?? [];
        var words = result.Words ?? [];
        var attached = AttachWords(segments, words);
        var cues = new List<Piece>();

        for (var i = 0; i < segments.Count; i++)
        {
            var piece = new Piece
            {
                Start = segments[i].Start,
                End = segments[i].End,
                Text = segments[i].Text ?? string.Empty,
                Words = attached[i],
            };

            cues.AddRange(SplitDense(piece, maxCps));
        }

        // Keep them ordered and non-overlapping after splitting.
        for (var i = 0; i < cues.Count; i++)
        {
            if (i > 0 && cues[i].Start < cues[i - 1].End) cues[i].Start = cues[i - 1].End;
            if (cues[i].End <= cues[i].Start) cues[i].End = cues[i].Start + 0.04;
        }

        // A cue still too dense is held longer into the silence before the next one.
        for (var i = 0; i < cues.Count; i++)
        {
            if (Cps(cues[i].Text, cues[i].Start, cues[i].End) <= maxCps) continue;

            var want = cues[i].Start + cues[i].Text.Length / maxCps;
            var hard = i + 1 < cues.Count ? cues[i + 1].Start - 0.04 : want;
            cues[i].End = Math.Max(cues[i].End, Math.Min(Math.Min(want, hard), cues[i].Start + MaxCueSeconds));
        }

        // Never flash a cue, never park one on screen for too long.
        for (var i = 0; i < cues.Count; i++)
        {
            if (cues[i].End - cues[i].Start > MaxCueSeconds) cues[i].End = cues[i].Start + MaxCueSeconds;

            if (cues[i].End - cues[i].Start < MinCueSeconds)
            {
                var hard = i + 1 < cues.Count ? cues[i + 1].Start - 0.04 : double.PositiveInfinity;
                cues[i].End = Math.Max(cues[i].End, Math.Min(cues[i].Start + MinCueSeconds, hard));
            }
        }

        return cues
            .Where(p => !string.IsNullOrWhiteSpace(p.Text))
            .Select(p => new Cue
            {
                StartSeconds = p.Start,
                EndSeconds = p.End,
                Text = Layout(p.Text),
            })
            .ToList();
    }

    private static List<List<PhononWord>> AttachWords(List<PhononSegment> segments, List<PhononWord> words)
    {
        var output = new List<List<PhononWord>>(segments.Count);
        var index = 0;

        foreach (var segment in segments)
        {
            var selection = new List<PhononWord>();

            while (index < words.Count)
            {
                var word = words[index];

                if (word.End <= segment.Start - 0.05)
                {
                    index++;
                }
                else if (word.Start >= segment.Start - 0.05 && word.End <= segment.End + 0.05)
                {
                    selection.Add(word);
                    index++;
                }
                else
                {
                    break;
                }
            }

            var joined = string.Join(" ", selection.Select(w => w.Text));
            output.Add(string.Equals(joined, segment.Text, StringComparison.Ordinal) ? selection : []);
        }

        return output;
    }

    private static List<Piece> SplitDense(Piece cue, double maxCps)
    {
        var baseCps = Cps(cue.Text, cue.Start, cue.End);
        if (cue.Words.Count < 2 || baseCps <= maxCps) return [cue];

        (double Over, int LengthDiff, int Index)? best = null;

        for (var k = 1; k < cue.Words.Count; k++)
        {
            var leftWords = cue.Words.Take(k).ToList();
            var rightWords = cue.Words.Skip(k).ToList();

            var leftText = string.Join(" ", leftWords.Select(w => w.Text));
            var rightText = string.Join(" ", rightWords.Select(w => w.Text));

            if (Wrap(leftText).Count > CueLines || Wrap(rightText).Count > CueLines) continue;

            var leftStart = leftWords[0].Start;
            var leftEnd = leftWords[^1].End;
            var rightStart = rightWords[0].Start;
            var rightEnd = rightWords[^1].End;

            if (leftEnd - leftStart < MinPieceSeconds || rightEnd - rightStart < MinPieceSeconds) continue;

            var over = Math.Round(
                Math.Max(0, Cps(leftText, leftStart, leftEnd) - maxCps) +
                Math.Max(0, Cps(rightText, rightStart, rightEnd) - maxCps), 3);

            // Cutting a fast run at a pause can make both halves denser than the whole.
            if (over >= baseCps - maxCps - 1e-9) continue;

            var lengthDiff = Math.Abs(leftText.Length - rightText.Length);

            if (best is null ||
                over < best.Value.Over - 1e-9 ||
                (Math.Abs(over - best.Value.Over) < 1e-9 && lengthDiff < best.Value.LengthDiff))
            {
                best = (over, lengthDiff, k);
            }
        }

        if (best is null) return [cue];

        var split = best.Value.Index;
        var left = cue.Words.Take(split).ToList();
        var right = cue.Words.Skip(split).ToList();

        var first = new Piece
        {
            Start = left[0].Start,
            End = Math.Min(left[^1].End, right[0].Start),
            Text = string.Join(" ", left.Select(w => w.Text)),
            Words = left,
        };

        var second = new Piece
        {
            Start = right[0].Start,
            End = right[^1].End,
            Text = string.Join(" ", right.Select(w => w.Text)),
            Words = right,
        };

        return SplitDense(first, maxCps).Concat(SplitDense(second, maxCps)).ToList();
    }

    public static List<string> Wrap(string text, int width = CueLineChars)
    {
        var lines = new List<string>();
        var current = string.Empty;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length == 0)
            {
                current = word;
            }
            else if (current.Length + 1 + word.Length <= width)
            {
                current += " " + word;
            }
            else
            {
                lines.Add(current);
                current = word;
            }
        }

        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    /// <summary>One line if it fits, otherwise two balanced lines, preferring a break after a comma.</summary>
    public static string Layout(string text)
    {
        if (text.Length <= CueLineChars) return text;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        (int Score, string Value)? best = null;

        for (var i = 1; i < words.Length; i++)
        {
            var first = string.Join(" ", words.Take(i));
            var second = string.Join(" ", words.Skip(i));

            if (first.Length > CueLineChars || second.Length > CueLineChars) continue;

            var last = first.TrimEnd();
            var bonus = last.Length > 0 && ",;:".Contains(last[^1]) ? 0 : 4;
            var score = Math.Abs(first.Length - second.Length) + bonus;

            if (best is null || score < best.Value.Score) best = (score, first + "\n" + second);
        }

        if (best is not null) return best.Value.Value;

        // No balanced split fits: keep every word (the old port truncated here, losing dialogue).
        return string.Join("\n", Wrap(text));
    }

    private static double Cps(string text, double start, double end) =>
        text.Length / Math.Max(0.001, end - start);
}
