using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Subtitles;

namespace CinemaGuard.Core.Tests;

public class SubtitleReaderTests
{
    [Fact]
    public void KeepsTheLastCue()
    {
        const string srt = """
                           1
                           00:00:01,000 --> 00:00:02,000
                           Hello

                           2
                           00:00:03,000 --> 00:00:04,000
                           World
                           """;

        var cues = SubtitleReader.ParseSrt(srt);

        Assert.Equal(2, cues.Count);
        Assert.Equal("World", cues[1].Text);
    }

    [Theory]
    [InlineData("00:00:01,000 --> 00:00:02,000  ")]           // trailing spaces
    [InlineData("00:00:01,000 --> 00:00:02,000 X1:1 X2:2")]    // position tags
    [InlineData("00:00:01.000 --> 00:00:02.000")]              // dot milliseconds
    [InlineData("0:00:01,000 --> 0:00:02,000")]                // one digit hour
    public void AcceptsLooseTimestampLines(string timing)
    {
        var srt = "1\n" + timing + "\nHello\n";

        Assert.True(SubtitleReader.TryParseTiming(timing, out var start, out var end));
        Assert.Equal(1, start.TotalSeconds, 3);
        Assert.Equal(2, end.TotalSeconds, 3);

        var cues = SubtitleReader.ParseSrt(srt);
        Assert.Single(cues);
        Assert.Equal("Hello", cues[0].Text);
    }

    [Fact]
    public void HandlesMissingBlankLineBetweenCues()
    {
        const string srt = """
                           1
                           00:00:01,000 --> 00:00:02,000
                           Hello
                           2
                           00:00:03,000 --> 00:00:04,000
                           World
                           """;

        var cues = SubtitleReader.ParseSrt(srt);

        Assert.Equal(2, cues.Count);
        Assert.Equal("Hello", cues[0].Text);
        Assert.Equal("World", cues[1].Text);
    }

    [Fact]
    public void KeepsMultiLineText()
    {
        const string srt = """
                           1
                           00:00:01,000 --> 00:00:02,000
                           First line
                           Second line
                           """;

        var cues = SubtitleReader.ParseSrt(srt);

        Assert.Single(cues);
        Assert.Equal("First line\nSecond line", cues[0].Text);
    }

    [Fact]
    public void ReadsWebVtt()
    {
        const string vtt = """
                           WEBVTT

                           00:01.000 --> 00:02.000
                           Hello

                           NOTE a comment
                           that spans a line

                           00:03.000 --> 00:04.000
                           World
                           """;

        var cues = SubtitleReader.Parse(vtt, SubtitleFormat.Vtt);

        Assert.Equal(2, cues.Count);
        Assert.Equal(1, cues[0].Start.TotalSeconds, 3);
        Assert.Equal("World", cues[1].Text);
    }

    [Fact]
    public void ReadsAssDialogue()
    {
        const string ass = """
                           [Script Info]
                           Title: Test

                           [Events]
                           Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
                           Dialogue: 0,0:00:01.00,0:00:02.50,Default,,0,0,0,,{\i1}Hello\NWorld{\i0}
                           """;

        var cues = SubtitleReader.Parse(ass, SubtitleFormat.Ass);

        Assert.Single(cues);
        Assert.Equal(1, cues[0].Start.TotalSeconds, 3);
        Assert.Equal(2.5, cues[0].End.TotalSeconds, 3);
        Assert.Equal("Hello\nWorld", cues[0].Text);
    }

    [Fact]
    public void DecodesUtf16AndBom()
    {
        var text = "1\r\n00:00:01,000 --> 00:00:02,000\r\nHéllo\r\n";
        var bytes = System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes(text)).ToArray();

        Assert.Contains("Héllo", SubtitleEncoding.Decode(bytes));
    }

    [Fact]
    public void DecodesCp1252SmartQuotes()
    {
        byte[] bytes = [(byte)0x93, (byte)'H', (byte)'i', (byte)0x94];
        var decoded = SubtitleEncoding.Decode(bytes);

        // Invalid UTF-8 falls back to Windows-1252, where 0x93/0x94 are curly quotes.
        Assert.Contains('\u201C', decoded);
        Assert.Contains('\u201D', decoded);
    }
}

public class SubtitleFormatterTests
{
    [Fact]
    public void StripsTagsAndSdhDescriptions()
    {
        Assert.Equal("WHAT THE HELL", SubtitleFormatter.FormatLine("<i>WHAT THE HELL</i>"));
        Assert.Equal("HELLO WORLD", SubtitleFormatter.FormatLine("HELLO [door slams] WORLD"));
        Assert.Equal("HELLO", SubtitleFormatter.FormatLine("{\\an8}HELLO"));
    }

    [Fact]
    public void KeepsDashesSoHyphenatedEntriesMatch()
    {
        Assert.Equal("FUCKED-UP", SubtitleFormatter.FormatLine("FUCKED-UP"));
    }

    [Fact]
    public void CollapsesWhitespaceAndNonBreakingSpaces()
    {
        Assert.Equal("A B", SubtitleFormatter.FormatLine("A\u00A0 B"));
    }
}

public class SwearMatcherTests
{
    [Fact]
    public void MatchesWholeWordsOnly()
    {
        Assert.Single(SwearMatcher.Find("FUCK'S", ["FUCK"]));
        Assert.Empty(SwearMatcher.Find("SCUNTHORPE", ["CUNT"]));
        Assert.Empty(SwearMatcher.Find("ASSASSIN", ["ASS"]));
        Assert.Single(SwearMatcher.Find("FUCKED-UP", ["FUCKED-UP"]));
    }

    [Fact]
    public void FindsPhrasesAcrossWords()
    {
        Assert.Single(SwearMatcher.Find("OH MY GOD DAMN IT", ["GOD DAMN"]));
    }

    [Theory]
    [InlineData("Holy shit!")]
    [InlineData("HOLY... SHIT")]
    [InlineData("Oh, holy-shit, look")]
    [InlineData("holy, shit")]
    public void FindsAPhraseAcrossPunctuation(string text)
    {
        var match = Assert.Single(SwearMatcher.Find(text, ["HOLY  SHIT"]));

        Assert.Equal("HOLY SHIT", match.Entry);
        Assert.StartsWith("HOLY", text.ToUpperInvariant()[match.Index..]);
        Assert.EndsWith("SHIT", text.ToUpperInvariant()[..match.End]);
    }

    [Theory]
    [InlineData("HOLY SHITTY day")]
    [InlineData("WHOLY SHIT")]
    [InlineData("HOLY COW, SHIT")]
    public void APhraseStillMatchesWholeWordsOnly(string text)
    {
        Assert.Empty(SwearMatcher.Find(text, ["HOLY SHIT"]));
    }

    [Fact]
    public void DetectsWordsTheSourceAlreadyMasked()
    {
        Assert.True(SwearMatcher.HasMaskedWord("what the f***"));
        Assert.True(SwearMatcher.HasMaskedWord("you s**t"));
        Assert.True(SwearMatcher.HasMaskedWord("[bleep]"));
        Assert.False(SwearMatcher.HasMaskedWord("a normal sentence"));
    }
}

public class CensoredSubtitleWriterTests
{
    private static AppSettings Settings()
    {
        var settings = AppSettings.CreateDefault();
        settings.CustomAffix = "_CG";
        return settings;
    }

    [Fact]
    public void MasksTheWholeWordAtTheEndOfACue()
    {
        // The old app produced "WHAT THE ***L" here.
        var masked = CensoredSubtitleWriter.Mask("WHAT THE HELL", ["HELL"], '*', out var count);

        Assert.Equal("WHAT THE ****", masked);
        Assert.Equal(1, count);
    }

    [Fact]
    public void MasksEveryOccurrence()
    {
        var masked = CensoredSubtitleWriter.Mask("FUCK this FUCKING thing", ["FUCK"], '#', out var count);

        Assert.Equal("#### this FUCKING thing", masked);
        Assert.Equal(1, count);
    }

    [Fact]
    public void WritesFullAndCensoredFilesWithoutMutatingTheInput()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cg-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var cues = new List<Cue>
            {
                new() { StartSeconds = 1, EndSeconds = 2, Text = "WHAT THE FUCK" },
                new() { StartSeconds = 3, EndSeconds = 4, Text = "This is fine" },
            };

            var basePath = Path.Combine(directory, "movie");
            var result = CensoredSubtitleWriter.Write(cues, basePath, Settings(), SubtitleOutputType.Dual);

            Assert.Equal(Path.Combine(directory, "movie_Full_CG.srt"), result.FullPath);
            Assert.Equal(Path.Combine(directory, "movie_Censored_CG.srt"), result.CensoredPath);
            Assert.Equal(CensoredSubtitleWriter.FullPath(basePath, Settings()), result.FullPath);
            Assert.Equal(CensoredSubtitleWriter.CensoredPath(basePath, Settings()), result.CensoredPath);
            Assert.Equal("WHAT THE FUCK", cues[0].Text);

            var full = SubtitleReader.ReadFile(result.FullPath!).Cues;
            var censored = SubtitleReader.ReadFile(result.CensoredPath!).Cues;

            Assert.Equal(2, full.Count);
            Assert.Equal("WHAT THE ****", full[0].Text);

            // The Censored track keeps the full timeline: the clean cue is present but blank.
            Assert.Equal(2, censored.Count);
            Assert.Equal(full[0].Start, censored[0].Start);
            Assert.Equal(full[1].Start, censored[1].Start);
            Assert.Equal("WHAT THE ****", censored[0].Text);
            Assert.True(string.IsNullOrEmpty(censored[1].Text), $"expected a blank cue, got '{censored[1].Text}'");

            var raw = File.ReadAllText(result.CensoredPath!);
            Assert.Contains("\n\u00A0\n", raw.Replace("\r\n", "\n"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WritesNothingForNone()
    {
        var result = CensoredSubtitleWriter.Write([new Cue { Text = "FUCK" }], Path.Combine(Path.GetTempPath(), "x"), Settings(), SubtitleOutputType.None);

        Assert.Null(result.FullPath);
        Assert.Null(result.CensoredPath);
    }

    [Fact]
    public void AnAllCleanFilmStillGetsBlankCues()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cg-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var basePath = Path.Combine(directory, "movie");
            var result = CensoredSubtitleWriter.Write(
                [new Cue { StartSeconds = 0, EndSeconds = 1, Text = "This is fine" }],
                basePath, Settings(), SubtitleOutputType.Dual);

            Assert.NotNull(result.FullPath);
            Assert.NotNull(result.CensoredPath);
            Assert.True(File.Exists(CensoredSubtitleWriter.CensoredPath(basePath, Settings())));

            var censored = SubtitleReader.ReadFile(result.CensoredPath!).Cues;
            Assert.Single(censored);
            Assert.True(string.IsNullOrEmpty(censored[0].Text));

            var raw = File.ReadAllText(result.CensoredPath!);
            Assert.Contains("\n\u00A0\n", raw.Replace("\r\n", "\n"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
