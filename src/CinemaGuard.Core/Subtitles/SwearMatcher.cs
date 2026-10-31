using System.Text.RegularExpressions;

namespace CinemaGuard.Core.Subtitles;

public sealed record WordMatch(int Index, int Length, string Entry)
{
    public int End => Index + Length;
}

/// <summary>
/// Whole-word matching for the filter lists, plus detection of words the source already masked
/// (f***, s**t, [bleep]). Whole-word means the character before and after the entry is not a letter,
/// so FUCK'S matches and SCUNTHORPE never matches CUNT.
/// </summary>
public static partial class SwearMatcher
{
    private static readonly Regex MaskedTokenRegex = new(
        @"(?<![A-Za-z])[A-Za-z][A-Za-z']*(?:[*#@]{2,}[A-Za-z'*#@]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BleepMarkerRegex = new(
        @"\[(?:bleep|censored|expletive)\]|\((?:bleep|censored|expletive)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public const string MaskedLabel = "MASKED";

    public static List<WordMatch> Find(string? text, IReadOnlyCollection<string> entries)
    {
        var matches = new List<WordMatch>();
        if (string.IsNullOrEmpty(text) || entries.Count == 0) return matches;

        var upper = text.ToUpperInvariant();

        foreach (var rawEntry in entries)
        {
            var entry = rawEntry.Trim().ToUpperInvariant();
            if (entry.Length == 0) continue;

            var searchFrom = 0;
            while (searchFrom <= upper.Length - entry.Length)
            {
                var found = upper.IndexOf(entry, searchFrom, StringComparison.Ordinal);
                if (found < 0) break;

                var beforeOk = found == 0 || !char.IsLetter(upper[found - 1]);
                var after = found + entry.Length;
                var afterOk = after >= upper.Length || !char.IsLetter(upper[after]);

                if (beforeOk && afterOk) matches.Add(new WordMatch(found, entry.Length, entry));

                searchFrom = found + 1;
            }
        }

        matches.Sort((a, b) => a.Index.CompareTo(b.Index));
        return matches;
    }

    /// <summary>Words the source text already censored, which need no list entry.</summary>
    public static List<WordMatch> FindMasked(string? text)
    {
        var matches = new List<WordMatch>();
        if (string.IsNullOrEmpty(text)) return matches;

        foreach (Match match in MaskedTokenRegex.Matches(text))
            matches.Add(new WordMatch(match.Index, match.Length, MaskedLabel));

        foreach (Match match in BleepMarkerRegex.Matches(text))
            matches.Add(new WordMatch(match.Index, match.Length, MaskedLabel));

        matches.Sort((a, b) => a.Index.CompareTo(b.Index));
        return matches;
    }

    public static bool HasMaskedWord(string? text) => FindMasked(text).Count > 0;

    /// <summary>Word-by-word normalisation used for transcripts: punctuation off, apostrophes kept.</summary>
    public static string NormaliseWord(string word)
    {
        if (string.IsNullOrEmpty(word)) return string.Empty;

        var value = SubtitleFormatter.NormaliseSpace(word).Trim().ToUpperInvariant();
        var start = 0;
        var end = value.Length - 1;

        while (start <= end && !char.IsLetterOrDigit(value[start]) && value[start] != '\'') start++;
        while (end >= start && !char.IsLetterOrDigit(value[end]) && value[end] != '\'') end--;

        return start > end ? string.Empty : value[start..(end + 1)];
    }
}
