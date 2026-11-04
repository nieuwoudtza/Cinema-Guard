using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CinemaGuard.Core.Phonon;
using CinemaGuard.Core.Subtitles;

namespace CinemaGuard.Core.Intervals;

/// <summary>Where a subtitle word was spoken, on the transcript's timeline.</summary>
/// <param name="GapStart">End of the matched word before it.</param>
/// <param name="GapEnd">Start of the matched word after it.</param>
/// <param name="Exact">
/// Nothing else unmatched lies between those neighbours (or the word matched itself), so the span is the
/// word; otherwise it is the word's share of a gap holding several words.
/// </param>
public readonly record struct AlignedSpan(double Start, double End, double GapStart, double GapEnd, bool Exact);

/// <summary>The subtitle file matched to the transcript word by word; see <see cref="WordAligner"/>.</summary>
public sealed class WordAlignment
{
    // A gap is only trusted while it is short: at most this many unmatched words, spoken at a plausible rate.
    // Tested on five films: tighter limits leave more words partly muted, looser ones miss more entirely.
    private const int MaxGapUnits = 12;
    private const double SecondsPerUnit = 1.0;
    private const double SlackSeconds = 1.0;

    private readonly IReadOnlyList<WordAligner.SubtitleWord>[] _cueWords;
    private readonly string[] _keys;
    private readonly int[] _match;
    private readonly bool[] _supported;
    private readonly double[] _starts;
    private readonly double[] _ends;

    internal WordAlignment(
        IReadOnlyList<WordAligner.SubtitleWord>[] cueWords,
        string[] keys,
        int[] match,
        bool[] supported,
        double[] starts,
        double[] ends)
    {
        _cueWords = cueWords;
        _keys = keys;
        _match = match;
        _supported = supported;
        _starts = starts;
        _ends = ends;
        MatchedWords = supported.Count(s => s);
    }

    public int SubtitleWords => _keys.Length;
    public int MatchedWords { get; }
    public double MatchedShare => SubtitleWords == 0 ? 0 : (double)MatchedWords / SubtitleWords;

    /// <summary>
    /// When the characters [<paramref name="from"/>, <paramref name="to"/>) of cue <paramref name="cue"/>'s text
    /// (<see cref="SubtitleFormatter.FormatCue"/> with masks kept) were spoken; null when the matched words
    /// around them are too far apart to say.
    /// </summary>
    public AlignedSpan? Locate(int cue, int from, int to)
    {
        if (cue < 0 || cue >= _cueWords.Length) return null;

        int first = -1, last = -1;
        foreach (var word in _cueWords[cue])
        {
            if (word.Index >= to || word.Index + word.Length <= from) continue;
            if (first < 0) first = word.FirstUnit;
            last = word.FirstUnit + word.UnitCount - 1;
        }

        if (first < 0) return null;

        var heard = true;
        for (var unit = first; unit <= last; unit++) heard &= _supported[unit];

        if (heard)
        {
            var start = Enumerable.Range(first, last - first + 1).Min(u => _starts[_match[u]]);
            var end = Enumerable.Range(first, last - first + 1).Max(u => _ends[_match[u]]);
            return new AlignedSpan(start, end, start, end, Exact: true);
        }

        var previous = first - 1;
        while (previous >= 0 && !_supported[previous]) previous--;

        var next = last + 1;
        while (next < _keys.Length && !_supported[next]) next++;

        var gapUnits = next - previous - 1;
        if (previous < 0 || next >= _keys.Length || gapUnits > MaxGapUnits) return null;

        var gapStart = _ends[_match[previous]];
        var gapEnd = _starts[_match[next]];
        if (gapEnd < gapStart) gapStart = gapEnd = (gapStart + gapEnd) / 2;
        if (gapEnd - gapStart > SecondsPerUnit * gapUnits + SlackSeconds) return null;

        // The gap shared out by syllables, as the words in it are spoken one after another.
        double before = 0, own = 0, total = 0;
        for (var unit = previous + 1; unit < next; unit++)
        {
            var weight = Syllables(_keys[unit]);
            total += weight;
            if (unit < first) before += weight;
            else if (unit <= last) own += weight;
        }

        var span = gapEnd - gapStart;
        var wordStart = gapStart + span * before / total;
        return new AlignedSpan(wordStart, wordStart + span * own / total, gapStart, gapEnd, Exact: gapUnits == last - first + 1);
    }

    private static int Syllables(string key)
    {
        var word = key.EndsWith('E') ? key[..^1] : key;
        var groups = 0;
        var inVowels = false;

        foreach (var character in word)
        {
            var vowel = character is 'A' or 'E' or 'I' or 'O' or 'U' or 'Y';
            if (vowel && !inVowels) groups++;
            inVowels = vowel;
        }

        return Math.Max(1, groups);
    }
}

/// <summary>
/// Matches the subtitle file to the transcript word by word. Four-word phrases that occur once on both sides
/// anchor the match; a longest increasing chain keeps the anchors in film order, so repeated dialogue cannot
/// reverse it; Needleman–Wunsch matches the words between anchors; a small Levenshtein allowance absorbs
/// spelling. It follows the subtitles through cuts and local jumps a single straight line cannot. A subtitle
/// word the model did not hear (a masked f***, or "film" for "fucking") is then timed between the matched
/// words either side of it. The matching is ported from Petrus Vermaak's SRT Mapper (v10.22).
/// </summary>
public static class WordAligner
{
    private const int AnchorSize = 4;
    private const long MaxCells = 750_000;
    private const double MatchConfidence = 0.88;
    private const double MinimumShare = 0.25;

    private static readonly Regex WordRegex = new(@"[\p{L}\p{N}'*#@]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly char[] MaskCharacters = ['*', '#', '@'];

    private static readonly Dictionary<string, string> Contractions = new(StringComparer.Ordinal)
    {
        ["I'M"] = "I AM", ["YOU'RE"] = "YOU ARE", ["WE'RE"] = "WE ARE", ["THEY'RE"] = "THEY ARE",
        ["HE'S"] = "HE IS", ["SHE'S"] = "SHE IS", ["IT'S"] = "IT IS", ["THAT'S"] = "THAT IS",
        ["I'VE"] = "I HAVE", ["YOU'VE"] = "YOU HAVE", ["WE'VE"] = "WE HAVE", ["THEY'VE"] = "THEY HAVE",
        ["I'LL"] = "I WILL", ["YOU'LL"] = "YOU WILL", ["HE'LL"] = "HE WILL", ["SHE'LL"] = "SHE WILL",
        ["WE'LL"] = "WE WILL", ["THEY'LL"] = "THEY WILL", ["DON'T"] = "DO NOT", ["DOESN'T"] = "DOES NOT",
        ["DIDN'T"] = "DID NOT", ["CAN'T"] = "CAN NOT", ["CANNOT"] = "CAN NOT", ["WON'T"] = "WILL NOT",
        ["ISN'T"] = "IS NOT", ["AREN'T"] = "ARE NOT", ["WASN'T"] = "WAS NOT", ["WEREN'T"] = "WERE NOT",
        ["HAVEN'T"] = "HAVE NOT", ["HASN'T"] = "HAS NOT", ["HADN'T"] = "HAD NOT",
        ["COULDN'T"] = "COULD NOT", ["WOULDN'T"] = "WOULD NOT", ["SHOULDN'T"] = "SHOULD NOT",
        ["GONNA"] = "GOING TO", ["WANNA"] = "WANT TO", ["GOTTA"] = "GOT TO", ["KINDA"] = "KIND OF",
    };

    /// <summary>A word of a cue's text: its characters, and the units (words after expanding contractions) it became.</summary>
    internal sealed record SubtitleWord(int Index, int Length, int FirstUnit, int UnitCount);

    /// <summary>Null when too little of the subtitle file matches: another language, or another film.</summary>
    public static WordAlignment? Align(IReadOnlyList<Cue> cues, PhononResult transcript)
    {
        var aKeys = new List<string>();
        var cueWords = new IReadOnlyList<SubtitleWord>[cues.Count];

        for (var cue = 0; cue < cues.Count; cue++)
        {
            var words = new List<SubtitleWord>();
            var text = SubtitleFormatter.FormatCue(cues[cue].Text, keepMasks: true);

            foreach (Match token in WordRegex.Matches(text))
            {
                var units = Units(token.Value);
                if (units.Count == 0) continue;

                words.Add(new SubtitleWord(token.Index, token.Length, aKeys.Count, units.Count));
                aKeys.AddRange(units);
            }

            cueWords[cue] = words;
        }

        var bKeys = new List<string>();
        var starts = new List<double>();
        var ends = new List<double>();

        foreach (var word in transcript.Words)
        {
            var units = Units(word.Text);
            var share = (word.End - word.Start) / Math.Max(1, units.Count);

            for (var unit = 0; unit < units.Count; unit++)
            {
                bKeys.Add(units[unit]);
                starts.Add(word.Start + share * unit);
                ends.Add(word.Start + share * (unit + 1));
            }
        }

        if (aKeys.Count == 0 || bKeys.Count == 0) return null;

        var a = aKeys.ToArray();
        var b = bKeys.ToArray();
        var match = Enumerable.Repeat(-1, a.Length).ToArray();
        var supported = new bool[a.Length];

        MatchAll(a, b, match, supported);
        Support(a, b, match, supported);

        var matched = supported.Count(s => s);
        if (matched < Math.Min(4, a.Length) || (double)matched / a.Length < MinimumShare) return null;

        return new WordAlignment(cueWords, a, match, supported, starts.ToArray(), ends.ToArray());
    }

    /// <summary>Comparison keys: accents and punctuation off, contractions spelled out. A masked word is one empty key, which matches nothing.</summary>
    private static List<string> Units(string word)
    {
        if (word.IndexOfAny(MaskCharacters) >= 0) return [string.Empty];

        var text = RemoveAccents(word).ToUpperInvariant().Replace('’', '\'').Replace('ʼ', '\'').Trim().Trim('\'');
        if (Contractions.TryGetValue(text, out var spelled)) text = spelled;

        return text.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => new string(part.Where(char.IsLetterOrDigit).ToArray()))
            .Where(key => key.Length > 0)
            .ToList();
    }

    private static string RemoveAccents(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark) builder.Append(character);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Anchors split the film into gaps, each matched the same way with anchors unique inside it; a gap with
    /// none tries shorter phrases, and one small enough is matched word by word.
    /// </summary>
    private static void MatchAll(string[] a, string[] b, int[] match, bool[] supported)
    {
        var work = new Stack<(int AFrom, int ATo, int BFrom, int BTo, int Size)>();
        work.Push((0, a.Length, 0, b.Length, AnchorSize));

        while (work.Count > 0)
        {
            var (aFrom, aTo, bFrom, bTo, size) = work.Pop();
            if (aTo <= aFrom || bTo <= bFrom) continue;

            var anchors = Anchors(a, aFrom, aTo, b, bFrom, bTo, size);

            if (anchors.Count == 0)
            {
                var cells = (long)(aTo - aFrom) * (bTo - bFrom);
                if (size > 2 && cells > MaxCells) work.Push((aFrom, aTo, bFrom, bTo, size - 1));
                else if (cells <= MaxCells) MatchGap(a, aFrom, aTo, b, bFrom, bTo, match);

                // A huge gap without one shared phrase stays unmatched: the full matrix would be too large.
                continue;
            }

            int aAt = aFrom, bAt = bFrom;
            foreach (var (anchorA, anchorB, anchorSize) in anchors)
            {
                work.Push((aAt, anchorA, bAt, anchorB, size));

                for (var k = 0; k < anchorSize; k++)
                {
                    match[anchorA + k] = anchorB + k;
                    supported[anchorA + k] = true;
                }

                aAt = anchorA + anchorSize;
                bAt = anchorB + anchorSize;
            }

            work.Push((aAt, aTo, bAt, bTo, size));
        }
    }

    private static List<(int A, int B, int Size)> Anchors(string[] a, int aFrom, int aTo, string[] b, int bFrom, int bTo, int size)
    {
        var aIndex = Fingerprints(a, aFrom, aTo, size);
        var bIndex = Fingerprints(b, bFrom, bTo, size);

        var candidates = new List<(int A, int B)>();
        foreach (var (key, i) in aIndex)
        {
            if (i >= 0 && bIndex.TryGetValue(key, out var j) && j >= 0) candidates.Add((i, j));
        }

        candidates.Sort((x, y) => x.A.CompareTo(y.A));

        // Longest chain increasing on both sides: a phrase repeated later in the film cannot pull the match backwards.
        var tails = new List<int>();
        var previous = new int[candidates.Count];

        for (var i = 0; i < candidates.Count; i++)
        {
            int low = 0, high = tails.Count;
            while (low < high)
            {
                var mid = (low + high) >> 1;
                if (candidates[tails[mid]].B < candidates[i].B) low = mid + 1;
                else high = mid;
            }

            previous[i] = low > 0 ? tails[low - 1] : -1;
            if (low == tails.Count) tails.Add(i);
            else tails[low] = i;
        }

        var chain = new List<(int A, int B)>();
        for (var i = tails.Count > 0 ? tails[^1] : -1; i >= 0; i = previous[i]) chain.Add(candidates[i]);
        chain.Reverse();

        // Each anchor grows over the equal words either side of it, up to its neighbours.
        var anchors = new List<(int A, int B, int Size)>();
        int aEnd = aFrom, bEnd = bFrom;

        foreach (var (candidateA, candidateB) in chain)
        {
            if (candidateA < aEnd || candidateB < bEnd) continue;

            int anchorA = candidateA, anchorB = candidateB, anchorSize = size;
            while (anchorA > aEnd && anchorB > bEnd && Same(a[anchorA - 1], b[anchorB - 1]))
            {
                anchorA--;
                anchorB--;
                anchorSize++;
            }

            while (anchorA + anchorSize < aTo && anchorB + anchorSize < bTo && Same(a[anchorA + anchorSize], b[anchorB + anchorSize]))
                anchorSize++;

            aEnd = anchorA + anchorSize;
            bEnd = anchorB + anchorSize;
            anchors.Add((anchorA, anchorB, anchorSize));
        }

        return anchors;
    }

    private static bool Same(string x, string y) => x.Length > 0 && string.Equals(x, y, StringComparison.Ordinal);

    /// <summary>Every run of <paramref name="size"/> keys in the range: where it starts, or -1 when it occurs more than once.</summary>
    private static Dictionary<string, int> Fingerprints(string[] keys, int from, int to, int size)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = from; i + size <= to; i++)
        {
            var masked = false;
            for (var k = 0; k < size && !masked; k++) masked = keys[i + k].Length == 0;
            if (masked) continue;

            var key = string.Join('\u0001', keys, i, size);
            index[key] = index.ContainsKey(key) ? -1 : i;
        }

        return index;
    }

    /// <summary>Needleman–Wunsch over a gap between anchors; only close words count as matches, and only as unsupported ones.</summary>
    private static void MatchGap(string[] a, int aFrom, int aTo, string[] b, int bFrom, int bTo, int[] match)
    {
        const double gap = -0.9;
        var n = aTo - aFrom;
        var m = bTo - bFrom;
        var width = m + 1;
        var score = new double[(n + 1) * width];
        var move = new byte[(n + 1) * width];

        for (var i = 1; i <= n; i++) { score[i * width] = i * gap; move[i * width] = 1; }
        for (var j = 1; j <= m; j++) { score[j] = j * gap; move[j] = 2; }

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var confidence = Confidence(a[aFrom + i - 1], b[bFrom + j - 1]);
                var diagonal = score[(i - 1) * width + j - 1] + (confidence == 1 ? 3.4 : confidence >= MatchConfidence ? 2.1 : -1.8);
                var up = score[(i - 1) * width + j] + gap;
                var left = score[i * width + j - 1] + gap;

                if (diagonal >= up && diagonal >= left) { score[i * width + j] = diagonal; move[i * width + j] = 0; }
                else if (up >= left) { score[i * width + j] = up; move[i * width + j] = 1; }
                else { score[i * width + j] = left; move[i * width + j] = 2; }
            }
        }

        for (int i = n, j = m; i > 0 || j > 0;)
        {
            var step = move[i * width + j];

            if (step == 0 && i > 0 && j > 0)
            {
                if (Confidence(a[aFrom + i - 1], b[bFrom + j - 1]) >= MatchConfidence) match[aFrom + i - 1] = bFrom + j - 1;
                i--;
                j--;
            }
            else if (step == 1 && i > 0) i--;
            else j--;
        }
    }

    /// <summary>1 for equal words; close spellings of longer words score a little less; short words must be equal.</summary>
    private static double Confidence(string x, string y)
    {
        if (x.Length == 0 || y.Length == 0) return 0;
        if (string.Equals(x, y, StringComparison.Ordinal)) return 1;
        if (Math.Min(x.Length, y.Length) < 4) return 0;

        var longest = Math.Max(x.Length, y.Length);
        var distance = Levenshtein(x, y);
        return distance <= Math.Max(1, longest / 5) ? Math.Min(0.97, 1 - (double)distance / longest + 0.05) : 0;
    }

    private static int Levenshtein(string x, string y)
    {
        var previous = new int[y.Length + 1];
        var current = new int[y.Length + 1];
        for (var j = 0; j <= y.Length; j++) previous[j] = j;

        for (var i = 1; i <= x.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= y.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (x[i - 1] == y[j - 1] ? 0 : 1));

            (previous, current) = (current, previous);
        }

        return previous[y.Length];
    }

    /// <summary>
    /// A run of three or more words matched in step on both sides is trusted when it touches an anchor or holds
    /// a three-word phrase found once in each file; a lone close match between anchors is not.
    /// </summary>
    private static void Support(string[] a, string[] b, int[] match, bool[] supported)
    {
        var uniqueA = Fingerprints(a, 0, a.Length, 3);
        var uniqueB = Fingerprints(b, 0, b.Length, 3);
        var run = new List<int>();

        void Finish()
        {
            if (run.Count >= 3)
            {
                var trusted = run.Any(i => supported[i]);

                for (var k = 0; !trusted && k + 2 < run.Count; k++)
                {
                    var i = run[k];
                    if (a[i].Length == 0 || a[i + 1].Length == 0 || a[i + 2].Length == 0) continue;

                    var key = string.Join('\u0001', a, i, 3);
                    trusted = uniqueA.TryGetValue(key, out var inA) && inA == i &&
                              uniqueB.TryGetValue(key, out var inB) && inB == match[i];
                }

                if (trusted) foreach (var i in run) supported[i] = true;
            }

            run.Clear();
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (match[i] < 0) continue;
            if (run.Count > 0 && (i != run[^1] + 1 || match[i] != match[run[^1]] + 1)) Finish();
            run.Add(i);
        }

        Finish();
    }
}
