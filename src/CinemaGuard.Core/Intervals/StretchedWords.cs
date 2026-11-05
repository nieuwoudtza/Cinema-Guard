using CinemaGuard.Core.Phonon;
using CinemaGuard.Core.Subtitles;

namespace CinemaGuard.Core.Intervals;

/// <summary>A transcript word <see cref="StretchedWords"/> cut back, for the log.</summary>
public sealed record TrimmedWord(HitSource Source, string Text, double Start, double End, double TrimmedEnd, string Reason);

/// <summary>
/// Phonon sometimes stretches the last word before a pause up to the next word it hears: "god." ran 13.8 s into
/// the gap before the next line, and the mute with it. A word longer than it could be spoken is cut back to the end
/// of the caption on screen when it started. With no caption around it, only a word that runs right up to the next
/// word (the mark of the stretch) is cut, to that longest plausible length. A word that ends within its caption
/// keeps its length, however long: a drawn-out shout is real.
/// </summary>
public static class StretchedWords
{
    private const double BaseSeconds = 1.0;
    private const double SecondsPerSyllable = 0.35;

    /// <summary>Subtitles are often shown a little before or after the line is spoken.</summary>
    private const double CaptionLeewaySeconds = 0.3;

    /// <summary>A word cut back to a caption that ended just before it started keeps this much.</summary>
    private const double ShortestSeconds = 0.3;

    private const double TouchingSeconds = 0.01;

    /// <summary>Longer than this, a word is checked: 1.35 s for GOD, 1.7 s for FUCKING, 2.4 s for MOTHERFUCKER.</summary>
    public static double LongestPlausible(string text)
    {
        var key = new string(text.Where(char.IsLetter).Select(char.ToUpperInvariant).ToArray());
        return BaseSeconds + SecondsPerSyllable * WordAlignment.Syllables(key);
    }

    /// <param name="fit">
    /// Puts the captions on the transcript's timeline. Captions only count when it was accepted: subtitles that
    /// could not be shown to line up with the speech say nothing about where a word ends.
    /// </param>
    /// <returns>The transcript itself when nothing was cut; otherwise a copy with the cut words.</returns>
    public static (PhononResult Transcript, List<TrimmedWord> Trimmed) Trim(
        PhononResult transcript, HitSource source, IReadOnlyList<Cue>? cues, SyncFit fit)
    {
        var trimmed = new List<TrimmedWord>();
        List<PhononWord>? words = null;

        var captions = cues is { Count: > 0 } && fit.Accepted
            ? cues.Select(c => (Start: fit.Map(c.Start.TotalSeconds), End: fit.Map(c.End.TotalSeconds))).ToList()
            : [];

        for (var index = 0; index < transcript.Words.Count; index++)
        {
            var word = transcript.Words[index];
            var limit = LongestPlausible(word.Text);
            if (word.End - word.Start <= limit) continue;

            // Of the captions on screen as the word starts, the one shown longest gives the word the most room.
            var captionEnd = captions
                .Where(c => c.Start - CaptionLeewaySeconds <= word.Start && word.Start <= c.End + CaptionLeewaySeconds)
                .Select(c => (double?)c.End)
                .Max();

            double end;
            string reason;

            if (captionEnd is { } shown)
            {
                if (word.End <= shown + CaptionLeewaySeconds) continue;
                end = Math.Max(shown, word.Start + ShortestSeconds);
                reason = "its caption ends there";
            }
            else
            {
                var next = index + 1 < transcript.Words.Count ? transcript.Words[index + 1] : null;
                if (next is null || next.Start - word.End > TouchingSeconds) continue;
                end = word.Start + limit;
                reason = "no caption; it ran up to the next word";
            }

            words ??= [.. transcript.Words];
            words[index] = new PhononWord { Start = word.Start, End = end, Text = word.Text };
            trimmed.Add(new TrimmedWord(source, word.Text, word.Start, word.End, end, reason));
        }

        if (words is null) return (transcript, trimmed);

        return (new PhononResult
        {
            Text = transcript.Text,
            Segments = transcript.Segments,
            Words = words,
            DurationSeconds = transcript.DurationSeconds,
            Truncated = transcript.Truncated,
            Extra = transcript.Extra,
        }, trimmed);
    }
}
