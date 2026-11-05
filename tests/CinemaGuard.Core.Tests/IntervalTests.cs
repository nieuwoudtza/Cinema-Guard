using CinemaGuard.Core.Intervals;
using CinemaGuard.Core.Phonon;
using CinemaGuard.Core.Subtitles;

namespace CinemaGuard.Core.Tests;

public class HitCollectorTests
{
    private static HitCollectorOptions Options(string[]? full = null, string[]? single = null) =>
        new(full ?? [], single ?? [], 500, 500, 150, 250);

    private static PhononResult Transcript(double start, double end, string text, params (double Start, double End, string Text)[] words) =>
        new()
        {
            Segments = [new PhononSegment { Start = start, End = end, Text = text }],
            Words = words.Select(w => new PhononWord { Start = w.Start, End = w.End, Text = w.Text }).ToList(),
        };

    [Fact]
    public void SingleWordUsesTheRealWordTiming()
    {
        var transcript = Transcript(9, 12, "fuck", (10, 10.5, "fuck"));

        var hit = Assert.Single(HitCollector.FromTranscript(transcript, HitSource.English, Options(single: ["FUCK"])));

        Assert.Equal(9.85, hit.Start, 3);
        Assert.Equal(10.75, hit.End, 3);
        Assert.False(hit.WholeCue);
    }

    [Fact]
    public void AWordOnlyPhraseMutesEveryWordOfIt()
    {
        var transcript = Transcript(9, 14, "Oh, holy shit! Run.", (9.2, 9.4, "Oh,"), (10, 10.3, "holy"), (10.4, 10.8, "shit!"), (11.5, 11.8, "Run."));

        var hit = Assert.Single(HitCollector.FromTranscript(transcript, HitSource.English, Options(single: ["HOLY SHIT"])));

        // From "holy" (10.0 - 0.15 pad) to the end of "shit" (10.8 + 0.25 pad); not "holy" alone.
        Assert.Equal(9.85, hit.Start, 3);
        Assert.Equal(11.05, hit.End, 3);
        Assert.Equal("HOLY SHIT", hit.Label);
        Assert.False(hit.WholeCue);
    }

    [Fact]
    public void ASubtitlePhraseIsEstimatedFromItsFirstWordToItsLast()
    {
        // "HOLY SHIT" fills the whole text, so the estimate spans the whole cue, padded 0.5 s each side.
        var cue = new Cue { StartSeconds = 10, EndSeconds = 12, Text = "Holy... shit!" };

        var hit = Assert.Single(HitCollector.FromSubtitles([cue], SyncFit.Identity, Options(single: ["HOLY SHIT"]), []));

        Assert.True(hit.Start <= 9.5 + 0.001, $"started at {hit.Start}");
        Assert.True(hit.End >= 11.8, $"ended at {hit.End}");
        Assert.True(hit.End - hit.Start > 2, "a phrase covers more than a single padded word");
    }

    [Fact]
    public void FullWordsMuteTheirWholeSegment()
    {
        var transcript = Transcript(9, 12, "jesus christ", (9.5, 9.8, "jesus"), (10, 10.4, "christ"));

        var hits = HitCollector.FromTranscript(transcript, HitSource.English, Options(full: ["JESUS", "CHRIST"]));

        Assert.Equal(2, hits.Count);
        Assert.All(hits, hit => Assert.Equal(9, hit.Start, 3));
        Assert.All(hits, hit => Assert.Equal(12, hit.End, 3));
        Assert.All(hits, hit => Assert.True(hit.WholeCue));
    }

    [Fact]
    public void AWholeSubtitleLineGetsTheSentencePadding()
    {
        // The subtitle shows the line early: the extra after-padding still covers the spoken word.
        var cue = new Cue { StartSeconds = 10, EndSeconds = 11, Text = "Jesus." };
        var options = Options(full: ["JESUS"]) with { LinePadBeforeMs = 250, LinePadAfterMs = 1000 };

        var hit = Assert.Single(HitCollector.FromSubtitles([cue], SyncFit.Identity, options, []));

        Assert.Equal(9.75, hit.Start, 3);
        Assert.Equal(12, hit.End, 3);
        Assert.True(hit.WholeCue);
    }

    [Fact]
    public void TheSentencePaddingAlsoWidensTheModelsSentence()
    {
        var transcript = Transcript(9, 12, "jesus christ", (9.5, 9.8, "jesus"));
        var options = Options(full: ["JESUS"]) with { LinePadBeforeMs = 500, LinePadAfterMs = 500 };

        var hit = Assert.Single(HitCollector.FromTranscript(transcript, HitSource.English, options));

        Assert.Equal(8.5, hit.Start, 3);
        Assert.Equal(12.5, hit.End, 3);
    }

    [Fact]
    public void RangesMergeClampAndShift()
    {
        var hits = new List<Hit>
        {
            new(-1, 0.5, "A", HitSource.English, false),
            new(1, 2, "B", HitSource.English, false),
            new(1.5, 2.5, "C", HitSource.English, false),
        };

        var ranges = HitCollector.BuildRanges(hits, duration: 20, audioOffset: 1.5);

        Assert.Equal(2, ranges.Count);
        Assert.Equal(1.5, ranges[0].Start, 3);
        Assert.Equal(2.0, ranges[0].End, 3);
        Assert.Equal(2.5, ranges[1].Start, 3);
        Assert.Equal(4.0, ranges[1].End, 3);
    }

    [Fact]
    public void DropsRangesShorterThanTwentyMilliseconds()
    {
        var ranges = HitCollector.BuildRanges([new Hit(3, 3.01, "A", HitSource.English, false)], 20, 0);

        Assert.Empty(ranges);
    }

    [Fact]
    public void SubtitleWordIsEstimatedThenSnappedToTheTranscript()
    {
        var cue = new Cue { StartSeconds = 10, EndSeconds = 12, Text = "FUCK THIS" };

        var estimated = HitCollector.FromSubtitles([cue], SyncFit.Identity, Options(single: ["FUCK"]), []);
        Assert.Single(estimated);
        Assert.Equal(9.944, estimated[0].Start, 3);

        var transcriptHit = new Hit(10.5, 10.8, "FUCK", HitSource.English, false);
        var snapped = HitCollector.FromSubtitles([cue], SyncFit.Identity, Options(single: ["FUCK"]), [transcriptHit]);

        // Within two seconds of a precise Phonon hit: the estimate is redundant.
        Assert.Empty(snapped);
    }

    [Fact]
    public void ADriftedSubtitleWordIsComparedWhereTheFitPutsIt()
    {
        // The subtitles run 5 s early: the line at 10-12 s is spoken at 15-17 s.
        var fit = new SyncFit(true, 1.0, 5.0, 100, 0.1);
        var cue = new Cue { StartSeconds = 10, EndSeconds = 12, Text = "FUCK THIS" };

        // A different FUCK the model heard at 10.5 s is not this word: the estimate stays, at 15 s.
        var other = new Hit(10.5, 10.8, "FUCK", HitSource.English, false);
        var kept = Assert.Single(HitCollector.FromSubtitles([cue], fit, Options(single: ["FUCK"]), [other]));
        Assert.Equal(14.944, kept.Start, 3);

        // The model's own hit at 15.5 s is the same word: the estimate is redundant.
        var same = new Hit(15.5, 15.8, "FUCK", HitSource.English, false);
        Assert.Empty(HitCollector.FromSubtitles([cue], fit, Options(single: ["FUCK"]), [same]));
    }

    [Fact]
    public void AlignsSubtitlesTimedForAnotherFrameRate()
    {
        var cues = new List<Cue>();
        var words = new List<PhononWord>();

        for (var i = 0; i < 60; i++)
        {
            var start = i * 6.0;
            var token = $"WORD{i % 20:D2}";
            cues.Add(new Cue { StartSeconds = start, EndSeconds = start + 2, Text = $"{token} TEST" });

            // "WORDnn TEST" is 11 characters; the aligner places the word half way through it.
            var estimate = start + 6 / 2.0 / 11 * 2;
            var transcriptTime = estimate * 1.001 + 0.3;
            words.Add(new PhononWord { Start = transcriptTime, End = transcriptTime + 0.4, Text = token });
        }

        var transcript = new PhononResult { Words = words };

        var fit = SubtitleAligner.Fit(cues, transcript);

        Assert.True(fit.Accepted);
        Assert.Equal(1.001, fit.Slope, 3);
        Assert.Equal(0.3, fit.Intercept, 2);
        Assert.InRange(fit.Anchors, 55, 60);
        Assert.Equal(1.001 * 100 + 0.3, fit.Map(100), 2);
    }

    [Fact]
    public void RejectsAlignmentWhenThereIsNothingToAlign()
    {
        var fit = SubtitleAligner.Fit([new Cue { StartSeconds = 0, EndSeconds = 2, Text = "HELLO THERE" }], new PhononResult());

        Assert.False(fit.Accepted);
        Assert.Equal(1, fit.Slope, 3);
        Assert.Equal(42, fit.Map(42), 3);
    }

    [Fact]
    public void CollectsHitsFromTranscriptAndSubtitleTogether()
    {
        var transcript = Transcript(9, 12, "fuck", (10, 10.5, "fuck"));
        var cue = new Cue { StartSeconds = 1, EndSeconds = 2, Text = "HELL DAMN" };

        var set = HitCollector.Collect(
            Options(full: ["DAMN"], single: ["FUCK"]),
            120,
            0,
            transcript,
            null,
            [cue],
            null,
            alignSubtitles: false);

        Assert.Equal(2, set.Hits.Count);
        Assert.Equal(2, set.Ranges.Count);
        Assert.Contains(set.Summary, entry => entry.Label == "FUCK");
        Assert.Contains(set.Summary, entry => entry.Label == "DAMN");
    }

    [Fact]
    public void MaskedWordsInSubtitlesStillMute()
    {
        var cue = new Cue { StartSeconds = 5, EndSeconds = 6, Text = "what the f***" };

        var set = HitCollector.Collect(Options(), 60, 0, null, null, [cue], null, alignSubtitles: false);

        var hit = Assert.Single(set.Hits);
        Assert.Equal(SwearMatcher.MaskedLabel, hit.Label);
        Assert.Single(set.Ranges);
    }
}

/// <summary>Issue #2: Phonon stretched a word into the gap before the next line, and the mute went with it.</summary>
public class StretchedWordTests
{
    // Petrus's settings: word padding 100 / 95 ms, estimated padding 500 / 500 ms.
    private static HitCollectorOptions Options(params string[] single) => new([], single, 500, 500, 100, 95);

    private static readonly SyncFit InSync = new(true, 1.0, 0.0, 100, 0.1);

    private static PhononWord Word(double start, double end, string text) => new() { Start = start, End = end, Text = text };

    private static Cue Caption(double start, double end, string text) => new() { StartSeconds = start, EndSeconds = end, Text = text };

    /// <summary>A minute of lines heard as written, so the subtitles are shown to line up with the speech.</summary>
    private static (List<Cue> Cues, List<PhononWord> Words) InSyncFilm()
    {
        var cues = new List<Cue>();
        var words = new List<PhononWord>();

        for (var i = 0; i < 60; i++)
        {
            var start = i * 6.0;
            var token = $"WORD{i % 20:D2}";
            cues.Add(Caption(start, start + 2, $"{token} TEST"));

            var spoken = start + 6 / 2.0 / 11 * 2;
            words.Add(Word(spoken, spoken + 0.4, token));
            words.Add(Word(spoken + 0.5, spoken + 0.9, "test"));
        }

        return (cues, words);
    }

    [Fact]
    public void ModelCitizenGodIsCutBackToItsCaption()
    {
        var (cues, words) = InSyncFilm();
        cues.Add(Caption(4151.731, 4153.024, "Oh, my God."));
        cues.Add(Caption(4166.162, 4167.330, "Oh, my God."));

        var english = new PhononResult
        {
            Words = [.. words, Word(4151.90, 4152.10, "Oh,"), Word(4152.10, 4152.43, "my"), Word(4152.43, 4166.19, "god."),
                Word(4166.19, 4166.40, "Oh,"), Word(4166.40, 4166.60, "my"), Word(4166.60, 4167.00, "God.")],
        };
        var multilingual = new PhononResult
        {
            Words = [Word(4151.90, 4152.10, "Oh,"), Word(4152.10, 4152.42, "my"), Word(4152.42, 4166.18, "god."),
                Word(4166.18, 4166.40, "Oh,"), Word(4166.40, 4166.60, "my"), Word(4166.60, 4167.00, "God.")],
        };

        var set = HitCollector.Collect(Options("GOD"), 5609.642, 0, english, multilingual, cues, null, alignSubtitles: true);

        Assert.True(set.Fit.Accepted);

        // Was 4152.320-4166.285 (13.965 s), across the gap to the next line.
        var first = set.Ranges.Single(r => r.Start < 4160);
        Assert.Equal(4152.320, first.Start, 3);
        Assert.Equal(4153.024 + 0.095, first.End, 3);

        // The next "God." keeps its own short mute.
        var next = set.Ranges.Single(r => r.Start > 4160);
        Assert.Equal(4166.500, next.Start, 3);
        Assert.Equal(4167.095, next.End, 3);

        Assert.Equal(2, set.Trimmed!.Count);
        Assert.All(set.Trimmed, t => Assert.Equal(4153.024, t.TrimmedEnd, 3));
        Assert.Contains(set.Trimmed, t => t.Source == HitSource.Multilingual && t.End == 4166.18);
    }

    [Fact]
    public void YourMotherFuckNoLongerReachesTheNextFuck()
    {
        var (cues, words) = InSyncFilm();
        cues.Add(Caption(2047.125, 2049.833, "Oh, fuck!"));
        cues.Add(Caption(2055.958, 2057.000, "Fuck."));

        var english = new PhononResult
        {
            Words = [.. words, Word(2048.500, 2048.811, "Oh,"), Word(2048.811, 2049.300, "fuck!"), Word(2056.200, 2056.865, "Fuck.")],
        };
        var multilingual = new PhononResult
        {
            Words = [Word(2048.500, 2049.585, "Oh,"), Word(2049.585, 2056.145, "fuck."), Word(2056.145, 2056.600, "Fuck.")],
        };

        var set = HitCollector.Collect(Options("FUCK"), 6731.925, 0, english, multilingual, cues, null, alignSubtitles: true);

        // Was one 8.249 s range, 2048.711-2056.960. Now nothing is muted between the two lines.
        var line = set.Ranges.Where(r => r.Start is > 2000 and < 2050).ToList();
        Assert.Equal(2048.711, line.Min(r => r.Start), 3);
        // The word starts 0.25 s before its caption ends, so it keeps the shortest 0.3 s: 2049.585 + 0.3 + 0.095 pad.
        Assert.Equal(2049.980, line.Max(r => r.End), 3);

        var next = set.Ranges.Single(r => r.End > 2050);
        Assert.Equal(2056.045, next.Start, 3);
        Assert.Equal(2056.960, next.End, 3);

        var cut = Assert.Single(set.Trimmed!);
        Assert.Equal(HitSource.Multilingual, cut.Source);
    }

    [Theory]
    [InlineData("GOD", 1.35)]
    [InlineData("fucking", 1.70)]
    [InlineData("motherfucker!", 2.40)]
    public void TheLongestPlausibleWordGrowsWithItsSyllables(string word, double seconds) =>
        Assert.Equal(seconds, StretchedWords.LongestPlausible(word), 3);

    [Fact]
    public void ALongWordThatEndsWithinItsCaptionKeepsItsLength()
    {
        // A drawn-out shout, 3 s, inside a 4 s line.
        var transcript = new PhononResult { Words = [Word(10.5, 13.5, "fuuuck"), Word(13.5, 14, "you")] };

        var (result, trimmed) = StretchedWords.Trim(transcript, HitSource.English, [Caption(10, 13.4, "Fuuuck you")], InSync);

        Assert.Same(transcript, result);
        Assert.Empty(trimmed);
    }

    [Fact]
    public void AWordThatStartsJustAfterItsCaptionEndedKeepsAFewHundredMilliseconds()
    {
        var transcript = new PhononResult { Words = [Word(10.2, 20, "god")] };

        var (result, _) = StretchedWords.Trim(transcript, HitSource.English, [Caption(9, 10, "Oh my god")], InSync);

        Assert.Equal(10.5, result.Words[0].End, 3);
    }

    [Fact]
    public void WithNoCaptionAWordThatRunsIntoTheNextIsCappedAtTheLongestPlausible()
    {
        var transcript = new PhononResult { Words = [Word(10, 20, "god"), Word(20, 20.3, "help")] };

        var (result, trimmed) = StretchedWords.Trim(transcript, HitSource.English, null, SyncFit.Identity);

        Assert.Equal(11.35, result.Words[0].End, 3);
        Assert.Equal(20.3, result.Words[1].End, 3);
        Assert.Equal(20, Assert.Single(trimmed).End, 3);
        Assert.Equal(20, transcript.Words[0].End, 3); // the saved transcript is not changed
    }

    [Fact]
    public void WithNoCaptionALongWordFollowedBySilenceIsLeftAlone()
    {
        var transcript = new PhononResult { Words = [Word(10, 13, "god"), Word(25, 25.3, "help")] };

        var (result, trimmed) = StretchedWords.Trim(transcript, HitSource.English, null, SyncFit.Identity);

        Assert.Same(transcript, result);
        Assert.Empty(trimmed);
    }

    [Fact]
    public void CaptionsThatWereNotShownToLineUpAreNotEvidence()
    {
        // The fit was not accepted: the caption ending at 11 s may belong to another moment of the film.
        var transcript = new PhononResult { Words = [Word(10, 20, "god"), Word(20, 20.3, "help")] };

        var (result, _) = StretchedWords.Trim(transcript, HitSource.English, [Caption(10, 11, "god")], SyncFit.Identity);

        Assert.Equal(11.35, result.Words[0].End, 3);
    }
}