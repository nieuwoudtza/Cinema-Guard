using CinemaGuard.Core.Intervals;
using CinemaGuard.Core.Phonon;
using CinemaGuard.Core.Subtitles;

namespace CinemaGuard.Core.Tests;

public class WordAlignerTests
{
    private static HitCollectorOptions Options(params string[] single) => new([], single, 500, 500, 150, 250);

    /// <summary>The model's words for a line: 0.3 s each, 0.1 s apart, the first at <paramref name="start"/>.</summary>
    private static IEnumerable<PhononWord> Speak(double start, string text) =>
        text.Split(' ').Select((word, i) => new PhononWord { Start = start + 0.4 * i, End = start + 0.4 * i + 0.3, Text = word });

    private static PhononResult Transcript(params IEnumerable<PhononWord>[] lines) => new() { Words = lines.SelectMany(l => l).ToList() };

    private static Cue Cue(double start, double end, string text) => new() { StartSeconds = start, EndSeconds = end, Text = text };

    private static HitSet Collect(List<Cue> cues, PhononResult transcript, params string[] single) =>
        HitCollector.Collect(Options(single), 600, 0, transcript, null, cues, null, alignSubtitles: true);

    [Fact]
    public void AMaskedWordIsTimedBetweenTheWordsAroundIt()
    {
        var cues = new List<Cue> { Cue(10, 16, "I told you to get the f*** out of my house right now") };
        var transcript = Transcript(Speak(10, "I told you to get the film out of my house right now"));

        var hit = Assert.Single(Collect(cues, transcript).Hits);

        // From the end of "the" (12.3 s) to the start of "out" (12.8 s), with the word padding.
        Assert.Equal(SwearMatcher.MaskedLabel, hit.Label);
        Assert.Equal(12.15, hit.Start, 3);
        Assert.Equal(13.05, hit.End, 3);
    }

    [Fact]
    public void WithNoPaddingAWordInAClosedGapStillMutes()
    {
        // Issue #3: the model ran "the" straight into "out", leaving the masked word no time between them.
        var cues = new List<Cue> { Cue(10, 16, "I told you to get the f*** out of my house right now") };
        var words = "I told you to get the out of my house right now".Split(' ')
            .Select((word, i) => new PhononWord { Start = 10 + 0.4 * i, End = 10 + 0.4 * (i + 1), Text = word });
        var options = new HitCollectorOptions([], [], 0, 0, 0, 0);

        var hit = Assert.Single(HitCollector.Collect(options, 600, 0, Transcript(words), null, cues, null, alignSubtitles: true).Hits);

        // Its share of the line (4 of 52 characters of 6 s) around the end of "the" at 12.4 s.
        Assert.Equal(12.4, (hit.Start + hit.End) / 2, 3);
        Assert.Equal(0.462, hit.End - hit.Start, 3);
    }

    [Fact]
    public void AWordTheModelHeardAsAnotherIsTimedFromTheTranscript()
    {
        var cues = new List<Cue> { Cue(20, 21.5, "Come in, sit down."), Cue(21.6, 24.5, "What the fuck is going on here?") };
        var transcript = Transcript(Speak(20, "come in sit down what the film is going on here"));

        var set = Collect(cues, transcript, "FUCK");
        var hit = Assert.Single(set.Hits);

        Assert.Equal("FUCK", hit.Label);
        Assert.Equal(22.15, hit.Start, 3);
        Assert.Equal(23.05, hit.End, 3);
        Assert.NotNull(set.Alignment);
    }

    [Fact]
    public void FollowsAJumpAStraightLineCannot()
    {
        // This cut of the film runs 30 s longer between the two lines than the subtitles were timed for.
        var cues = new List<Cue>
        {
            Cue(10, 13, "We should leave before the guards come back"),
            Cue(20, 23, "Get the f*** out of here right now, Tommy"),
        };
        var transcript = Transcript(
            Speak(10, "we should leave before the guards come back"),
            Speak(50, "get the heck out of here right now tommy"));

        var hit = Assert.Single(Collect(cues, transcript).Hits);

        Assert.Equal(50.55, hit.Start, 3);
        Assert.Equal(51.45, hit.End, 3);
    }

    [Fact]
    public void AShareOfALongerGapStaysBetweenItsNeighbours()
    {
        var cues = new List<Cue> { Cue(10, 12, "Come in and sit down"), Cue(12, 15, "The f***ing gizmo is broken again") };
        var transcript = Transcript(Speak(10, "come in and sit down the freaking gadget is broken again"));

        var hit = Assert.Single(Collect(cues, transcript).Hits);

        // Two unmatched words share the gap from "the" (ends 12.3 s) to "is" (starts 13.2 s) by syllables: the
        // masked word gets the first third. The wide padding is cut off where the neighbours are.
        Assert.Equal(12.15, hit.Start, 3);
        Assert.Equal(13.1, hit.End, 3);
    }

    [Fact]
    public void KeepsTheEstimateWhenTheWordsAroundItAreFarApart()
    {
        var cues = new List<Cue> { Cue(10, 13, "Come in and sit down now"), Cue(13, 17, "Get the f*** out of my house tonight") };
        var transcript = Transcript(Speak(10, "come in and sit down now get the"), Speak(16, "out of my house tonight"));

        var hit = Assert.Single(Collect(cues, transcript).Hits);
        var estimate = Assert.Single(HitCollector.FromSubtitles(cues, SyncFit.Identity, Options(), []));

        Assert.Equal(estimate.Start, hit.Start, 3);
        Assert.Equal(estimate.End, hit.End, 3);
    }

    [Fact]
    public void AWordNextToAShortPauseIsStillPlaced()
    {
        var cues = new List<Cue> { Cue(10, 13, "Come in and sit down now"), Cue(13, 17, "Get the f*** out of my house tonight") };
        var transcript = Transcript(Speak(10, "come in and sit down now get the"), Speak(14.7, "out of my house tonight"));

        var hit = Assert.Single(Collect(cues, transcript).Hits);

        // 1.6 s from "the" (ends 13.1 s) to "out": a pause around the word, still short enough to trust.
        Assert.Equal(12.95, hit.Start, 3);
        Assert.Equal(14.95, hit.End, 3);
    }

    [Fact]
    public void ARepeatedLineKeepsItsOwnPlace()
    {
        var cues = new List<Cue>
        {
            Cue(10, 12, "Get the f*** out of here"),
            Cue(20, 23, "The weather in Paris was lovely this spring"),
            Cue(30, 33, "Get the f*** out of here before I call the police"),
        };
        var transcript = Transcript(
            Speak(10, "get the heck out of here"),
            Speak(20, "the weather in paris was lovely this spring"),
            Speak(70, "get the heck out of here before i call the police"));

        var hits = Collect(cues, transcript).Hits;

        // The second "get the f*** out of here" is spoken at 70 s, not where the first one is.
        var late = Assert.Single(hits, h => h.Start > 60);
        Assert.Equal(70.55, late.Start, 3);
        Assert.Equal(71.45, late.End, 3);
        Assert.DoesNotContain(hits, h => h.Start is > 25 and < 35);
    }

    [Fact]
    public void RejectsSubtitlesInAnotherLanguage()
    {
        var cues = new List<Cue> { Cue(10, 13, "Nous devons partir avant que les gardes reviennent"), Cue(14, 16, "Sortez d'ici tout de suite") };
        var transcript = Transcript(Speak(10, "we should leave before the guards come back"), Speak(14, "get out of here right now"));

        Assert.Null(WordAligner.Align(cues, transcript));
        Assert.Null(Collect(cues, transcript).Alignment);
    }

    [Fact]
    public void ContractionsMatchTheirSpelledOutForm()
    {
        var cues = new List<Cue> { Cue(10, 14, "I don't know what you're gonna do with that") };
        var transcript = Transcript(Speak(10, "I do not know what you are going to do with that"));

        var alignment = WordAligner.Align(cues, transcript);

        Assert.NotNull(alignment);
        Assert.Equal(alignment.SubtitleWords, alignment.MatchedWords);
    }

    [Fact]
    public void AlignsAFilmLengthTranscriptWithMistakesInIt()
    {
        // 15,000 words in 1,500 cues; the model gets one word in ten wrong and drops one in thirty.
        var random = new Random(7);
        var cues = new List<Cue>();
        var words = new List<PhononWord>();
        var time = 0.0;

        for (var line = 0; line < 1500; line++)
        {
            var text = Enumerable.Range(0, 10).Select(_ => $"word{random.Next(3000)}").ToList();
            cues.Add(Cue(time, time + 4, string.Join(' ', text)));

            foreach (var word in text)
            {
                var roll = random.NextDouble();
                if (roll >= 1 / 30.0) words.Add(new PhononWord { Start = time, End = time + 0.3, Text = roll < 0.1 ? "misheard" : word });
                time += 0.4;
            }
        }

        var alignment = WordAligner.Align(cues, new PhononResult { Words = words });

        Assert.NotNull(alignment);
        Assert.InRange(alignment.MatchedShare, 0.8, 1.0);
    }

    [Fact]
    public void KeepingMasksTurnsBleepsIntoAMaskedWord()
    {
        var text = SubtitleFormatter.FormatCue("<i>What the f***?</i>\n[bleep] you", keepMasks: true);

        Assert.Equal("What the f***? BLEEP** you", text);
        Assert.Equal(2, SwearMatcher.FindMasked(text).Count);
    }
}
