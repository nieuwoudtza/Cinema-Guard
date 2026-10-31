using CinemaGuard.Core.Phonon;
using CinemaGuard.Core.Subtitles;

namespace CinemaGuard.Core.Tests;

public class PhononResultTests
{
    [Fact]
    public void ParsesTheJsonObjectAfterLogLines()
    {
        const string output = """
                              [phonon] loading model
                              [phonon] decoding 1800 s
                              {"text":"Hello there.","segments":[{"start":0.0,"end":1.5,"text":"Hello there."}],"words":[{"start":0.0,"end":0.6,"text":"Hello"},{"start":0.7,"end":1.5,"text":"there."}],"duration_s":1.5,"truncated":false}
                              """;

        var result = PhononResult.TryParse(output);

        Assert.NotNull(result);
        Assert.Equal("Hello there.", result!.Text);
        Assert.Single(result.Segments);
        Assert.Equal(2, result.Words.Count);
        Assert.Equal(1.5, result.DurationSeconds, 3);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void ReturnsNullWhenThereIsNoJson()
    {
        Assert.Null(PhononResult.TryParse("[phonon] nothing"));
    }

    [Fact]
    public void AcceptsNumbersSentAsStrings()
    {
        var result = PhononResult.TryParse("""{"segments":[{"start":"1.25","end":"2.5","text":"x"}],"duration_s":"2.5"}""");

        Assert.NotNull(result);
        Assert.Equal(1.25, result!.Segments[0].Start, 3);
    }

    [Fact]
    public void AcceptsTheDurationKeyFermionActuallyWrites()
    {
        // fermion 0.2.15 prints duration_seconds; the model was built for the older duration_s.
        var result = PhononResult.TryParse("""{"text":"x","words":[],"duration_seconds":5400.5,"truncated":false}""");

        Assert.NotNull(result);
        Assert.Equal(5400.5, result!.DurationSeconds, 3);
    }

    [Theory]
    [InlineData("""{"duration_s":12.5,"duration_seconds":99.0}""", 12.5)]
    [InlineData("""{"duration_seconds":99.0,"duration_s":0}""", 99.0)]
    [InlineData("""{"duration_seconds":"42.25"}""", 42.25)]
    public void PrefersAKnownDurationOverTheFallback(string json, double expected)
    {
        var result = PhononResult.TryParse(json);

        Assert.NotNull(result);
        Assert.Equal(expected, result!.DurationSeconds, 3);
    }

    [Fact]
    public void TheTranscriptCacheRoundTripsTheExtraFields()
    {
        var path = Path.Combine(Path.GetTempPath(), "cg-cache-" + Guid.NewGuid().ToString("N") + ".json");
        var source = new TranscriptSource { Size = 10, ModifiedTicks = 20, Model = "phonon-2.5-en", Downmix = "Plain" };

        try
        {
            var parsed = PhononResult.TryParse("""{"text":"hi","words":[{"start":1,"end":2,"text":"hi"}],"duration_seconds":61.5,"model":"x"}""")!;

            // Twice: the second save writes what the first load read, extension data included.
            TranscriptCache.Save(path, source, parsed);
            var first = TranscriptCache.Load(path, source)!;
            TranscriptCache.Save(path, source, first);
            var second = TranscriptCache.Load(path, source)!;

            Assert.Equal(61.5, second.DurationSeconds, 3);
            Assert.Single(second.Words);

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var names = document.RootElement.GetProperty("Result").EnumerateObject().Select(p => p.Name).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class CueBuilderTests
{
    private static PhononResult Transcript() => new()
    {
        Segments =
        [
            new PhononSegment { Start = 0, End = 2, Text = "This is a test of the cue builder" },
            new PhononSegment { Start = 2.4, End = 5, Text = "It should keep every cue inside seven seconds" },
        ],
        Words =
        [
            new PhononWord { Start = 0.1, End = 0.3, Text = "This" },
            new PhononWord { Start = 0.35, End = 0.45, Text = "is" },
            new PhononWord { Start = 0.5, End = 0.55, Text = "a" },
            new PhononWord { Start = 0.6, End = 0.9, Text = "test" },
            new PhononWord { Start = 0.95, End = 1.1, Text = "of" },
            new PhononWord { Start = 1.15, End = 1.3, Text = "the" },
            new PhononWord { Start = 1.35, End = 1.6, Text = "cue" },
            new PhononWord { Start = 1.65, End = 1.95, Text = "builder" },
        ],
    };

    [Fact]
    public void BuildsOrderedCuesInsideTheLimits()
    {
        var cues = CueBuilder.Build(Transcript(), 21);

        Assert.Equal(2, cues.Count);

        foreach (var cue in cues)
        {
            Assert.True(cue.End > cue.Start);
            Assert.InRange(cue.End.TotalSeconds - cue.Start.TotalSeconds, 0.79, 7.001);
            Assert.False(string.IsNullOrWhiteSpace(cue.Text));
        }

        Assert.True(cues[0].Start < cues[1].Start);
        Assert.True(cues[0].End <= cues[1].Start);
    }

    [Fact]
    public void KeepsEveryWordWhenNoBalancedSplitFits()
    {
        var result = new PhononResult
        {
            Segments =
            [
                new PhononSegment
                {
                    Start = 0,
                    End = 4,
                    Text = "A fairly long line of dialogue that needs to be balanced across two subtitle lines neatly",
                },
            ],
        };

        var cue = Assert.Single(CueBuilder.Build(result, 21));
        var lines = cue.Text.Split('\n');

        Assert.Equal(result.Segments[0].Text, cue.Text.Replace("\n", " "));
        Assert.All(lines, line => Assert.True(line.Length <= 42, $"'{line}' is {line.Length} characters"));
    }

    [Fact]
    public void HoldsADenseCueLongerInsteadOfDroppingWords()
    {
        var result = new PhononResult
        {
            Segments = [new PhononSegment { Start = 0, End = 1, Text = new string('a', 60) }],
        };

        var cue = Assert.Single(CueBuilder.Build(result, 21));

        Assert.Equal(new string('a', 60), cue.Text);
        Assert.True(cue.End.TotalSeconds >= 2.8, $"ended at {cue.End.TotalSeconds}");
        Assert.True(cue.Text.Length / cue.End.TotalSeconds <= 21.1);
    }

    [Fact]
    public void LayoutPrefersTwoBalancedLines()
    {
        var layout = CueBuilder.Layout("One two three four five six seven eight nine ten eleven twelve");

        Assert.Contains('\n', layout);
        Assert.All(layout.Split('\n'), line => Assert.True(line.Length <= 42));
    }

    [Fact]
    public void WrapNeverExceedsTheWidthUnlessAWordIsLonger()
    {
        var lines = CueBuilder.Wrap("one two three four five six seven eight nine ten");

        Assert.All(lines.Take(lines.Count - 1), line => Assert.True(line.Length <= 42));
    }
}

public class LanguageTests
{
    [Theory]
    [InlineData("No sé qué dijo él pero creo que sí", "es")]
    [InlineData("I don't know what he said to me", "en")]
    [InlineData("Je ne sais pas ce qu'il a dit", "fr")]
    [InlineData("Привет как дела у тебя", "ru")]
    [InlineData("こんにちは元気ですか", "ja")]
    public void IdentifiesTheLanguage(string text, string expected)
    {
        var guess = LanguageIdentifier.Identify(text);

        Assert.True(guess.IsKnown, $"unknown for '{text}'");
        Assert.Equal(expected, guess.Code);
    }

    [Fact]
    public void ReportsForeignDialogueAsSpan()
    {
        var english = new PhononResult
        {
            Segments = [new PhononSegment { Start = 10, End = 14, Text = "I don't know what he said" }],
        };

        var multilingual = new PhononResult
        {
            Segments =
            [
                new PhononSegment { Start = 10, End = 11.5, Text = "No sé qué dijo" },
                new PhononSegment { Start = 11.8, End = 14, Text = "él pero creo que sí" },
            ],
        };

        var spans = ForeignLanguageReporter.Analyse(english, multilingual);

        var span = Assert.Single(spans);
        Assert.Equal("es", span.LanguageCode);
        Assert.Equal(10, span.Start, 3);
        Assert.Equal(14, span.End, 3);
        Assert.Equal(2, span.Segments);
        Assert.Contains("No sé", span.Text);
        Assert.Contains("don't know", span.EnglishText);
    }

    [Fact]
    public void EnglishOnlyDialogueHasNoForeignSpan()
    {
        var english = new PhononResult
        {
            Segments = [new PhononSegment { Start = 0, End = 4, Text = "I don't know what he said to me" }],
        };

        Assert.Empty(ForeignLanguageReporter.Analyse(english, english));
    }
}
