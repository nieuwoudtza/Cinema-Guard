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
