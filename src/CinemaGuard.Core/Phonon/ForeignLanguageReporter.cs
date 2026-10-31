namespace CinemaGuard.Core.Phonon;

public sealed record ForeignSpan(
    double Start,
    double End,
    int Segments,
    string LanguageCode,
    string LanguageName,
    string Text,
    string EnglishText)
{
    public TimeSpan StartTime => TimeSpan.FromSeconds(Start);
    public TimeSpan EndTime => TimeSpan.FromSeconds(End);

    public string Range => $"{StartTime:hh\\:mm\\:ss} - {EndTime:hh\\:mm\\:ss}";
}

/// <summary>
/// Compares the multilingual transcript with the English one and reports the stretches that are not
/// English ("65:58, 4 cues, Spanish, verbatim available"). It never changes the output by itself.
/// </summary>
public static class ForeignLanguageReporter
{
    private const double MergeGapSeconds = 10;
    private const int MinimumTokens = 4;

    public static List<ForeignSpan> Analyse(PhononResult english, PhononResult multilingual)
    {
        var spans = new List<ForeignSpan>();
        if (multilingual.Segments.Count == 0) return spans;

        var current = new List<PhononSegment>();
        LanguageGuess? currentLanguage = null;

        void Flush()
        {
            if (current.Count == 0 || currentLanguage is null) return;

            var start = current[0].Start;
            var end = current[^1].End;
            var text = string.Join(" ", current.Select(s => s.Text)).Trim();

            spans.Add(new ForeignSpan(
                start,
                end,
                current.Count,
                currentLanguage.Code,
                currentLanguage.Name,
                text,
                EnglishText(english, start, end)));

            current = [];
            currentLanguage = null;
        }

        foreach (var segment in multilingual.Segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Text)) continue;

            var guess = LanguageIdentifier.Identify(segment.Text);
            var tokens = LanguageIdentifier.Tokenize(segment.Text);

            if (!guess.IsKnown || guess.IsEnglish || tokens.Count < MinimumTokens)
            {
                // Unknown or English: a short filler line does not end a foreign stretch on its own.
                if (current.Count > 0 && segment.Start - current[^1].End > MergeGapSeconds) Flush();
                continue;
            }

            if (current.Count > 0)
            {
                var gap = segment.Start - current[^1].End;
                var sameLanguage = currentLanguage?.Code == guess.Code;

                if (!sameLanguage || gap > MergeGapSeconds)
                {
                    Flush();
                }
            }

            current.Add(segment);
            currentLanguage ??= guess;

            // Keep the language that started the stretch; mixed lines inside it are still the same scene.
        }

        Flush();
        return spans;
    }

    private static string EnglishText(PhononResult english, double start, double end) =>
        string.Join(" ", english.Segments
            .Where(s => s.End > start && s.Start < end)
            .Select(s => s.Text))
            .Trim();
}
