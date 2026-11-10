using CinemaGuard.Core.Intervals;
using CinemaGuard.Core.Media;
using CinemaGuard.Core.Phonon;
using CinemaGuard.Core.Pipeline;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Speech;
using CinemaGuard.Core.Subtitles;
using CinemaGuard.Core.Tools;

namespace CinemaGuard.Core.Tests;

/// <summary>Review words before censoring (#5): the found words as moments, and leaving some of them in.</summary>
public class ReviewTests
{
    [Fact]
    public void HitsOfTheSameMomentAreOneOccurrence()
    {
        // The English pass, the multilingual pass and the subtitles all found the word said at 10 s.
        var occurrences = WordOccurrences.Group(
        [
            new Hit(9.85, 10.75, "FUCK", HitSource.English, false),
            new Hit(9.9, 10.7, "FUCKING", HitSource.Multilingual, false),
            new Hit(9.6, 10.9, "FUCK", HitSource.Subtitle, false),
            new Hit(30, 30.6, "SHIT", HitSource.English, false),
        ]);

        Assert.Equal(2, occurrences.Count);
        Assert.Equal(3, occurrences[0].Hits.Count);
        Assert.Equal("FUCK / FUCKING", occurrences[0].Label);
        Assert.Equal(9.6, occurrences[0].Start, 3);
        Assert.Equal(10.9, occurrences[0].End, 3);
        Assert.Equal("SHIT", occurrences[1].Label);
    }

    [Fact]
    public void AWordInsideAMutedLineIsItsOwnOccurrence()
    {
        var occurrences = WordOccurrences.Group(
        [
            new Hit(8, 14, "JESUS", HitSource.Subtitle, WholeCue: true),
            new Hit(8.2, 13.8, "JESUS", HitSource.English, WholeCue: true),
            new Hit(11, 11.5, "SHIT", HitSource.English, WholeCue: false),
        ]);

        Assert.Equal(2, occurrences.Count);
        Assert.Contains(occurrences, occurrence => occurrence.WholeCue && occurrence.Hits.Count == 2);
        Assert.Contains(occurrences, occurrence => !occurrence.WholeCue && occurrence.Label == "SHIT");
    }

    [Fact]
    public void AWordLeftInLosesEveryHitAndItsMuteRange()
    {
        // "Fork" in a heavy accent: the model heard the word, the subtitles did not.
        var hits = new List<Hit>
        {
            new(9.85, 10.75, "FUCK", HitSource.English, false),
            new(9.9, 10.7, "FUCK", HitSource.Multilingual, false),
            new(30, 30.6, "SHIT", HitSource.English, false),
        };
        var set = new HitSet(hits, HitCollector.BuildRanges(hits, 100, 0), SyncFit.Identity);
        var occurrences = WordOccurrences.Group(hits);

        occurrences[0].Mute = false;
        var censored = WordOccurrences.LeaveIn(set, occurrences, 100, 0);

        var hit = Assert.Single(censored.Hits);
        Assert.Equal("SHIT", hit.Label);
        var range = Assert.Single(censored.Ranges);
        Assert.Equal(30, range.Start, 3);
    }

    [Fact]
    public void NothingLeftInKeepsTheHitsAsTheyWere()
    {
        var hits = new List<Hit> { new(10, 11, "FUCK", HitSource.English, false) };
        var set = new HitSet(hits, HitCollector.BuildRanges(hits, 100, 0), SyncFit.Identity);

        Assert.Same(set, WordOccurrences.LeaveIn(set, WordOccurrences.Group(hits), 100, 0));
    }

    [Fact]
    public void ASurroundChannelsOwnHitOfAWordLeftInIsLeftInToo()
    {
        var occurrences = WordOccurrences.Group([new Hit(9.85, 10.75, "FUCK", HitSource.English, false)]);
        occurrences[0].Mute = false;

        // The front-left channel's transcript times the same word a little differently.
        Assert.True(WordOccurrences.IsLeftIn(new Hit(9.95, 10.8, "FUCK", HitSource.English, false), occurrences));
        Assert.False(WordOccurrences.IsLeftIn(new Hit(20, 20.5, "FUCK", HitSource.English, false), occurrences));

        // A whole line around it is another kind of hit, decided on its own.
        Assert.False(WordOccurrences.IsLeftIn(new Hit(8, 12, "JESUS", HitSource.Subtitle, true), occurrences));
    }

    [Fact]
    public void AnOccurrenceSaysWhatTheSubtitlesShowAndWhatTheModelHeard()
    {
        var words = new[] { "Take", "the", "fuck", "in", "the", "road", "and", "go", "left" };
        var transcript = new PhononResult
        {
            Words = words.Select((text, index) => new PhononWord { Start = 10 + index * 0.4, End = 10.3 + index * 0.4, Text = text }).ToList(),
        };
        var cues = new List<Cue>
        {
            new() { StartSeconds = 5, EndSeconds = 9, Text = "Before." },
            new() { StartSeconds = 9.8, EndSeconds = 13.5, Text = "<i>Take the fork\nin the road</i>" },
        };

        var hit = new Hit(10.8, 11.1, "FUCK", HitSource.English, false);
        var occurrences = WordOccurrences.Group([hit]);
        WordOccurrences.Describe(occurrences, transcript, cues, SyncFit.Identity);

        Assert.Equal("Take the fork in the road", occurrences[0].SubtitleText);
        Assert.Equal("Take the fuck in the road and …", occurrences[0].HeardText);
    }

    [Fact]
    public void AWordLeftInStaysReadableInItsSubtitleLineOnly()
    {
        // GOD at 4:06 was left in; GOD at 55:20 is still muted, and the subtitles came with the film.
        var cues = new List<Cue>
        {
            new() { StartSeconds = 245, EndSeconds = 248, Text = "You are not God." },
            new() { StartSeconds = 3319, EndSeconds = 3322, Text = "Not the AI and, oh, my God, not the NEST." },
        };
        var occurrences = WordOccurrences.Group(
        [
            new Hit(246, 246.8, "GOD", HitSource.English, false),
            new Hit(246.1, 246.7, "GOD", HitSource.Multilingual, false),
            new Hit(3320.2, 3320.9, "GOD", HitSource.English, false),
        ]);
        occurrences[0].Mute = false;

        var spared = WordOccurrences.SparedInCues(occurrences, cues, SyncFit.Identity);

        Assert.Equal("You are not God.", CensoredSubtitleWriter.Mask(cues[0].Text, ["GOD", "OH MY GOD"], '*', out var keptCount, spared[0]));
        Assert.Equal(0, keptCount);
        Assert.Null(spared[1]);
        Assert.DoesNotContain("God", CensoredSubtitleWriter.Mask(cues[1].Text, ["GOD"], '*', out _, spared[1]));
    }

    [Fact]
    public void ALineWithTheSameWordLeftInAndMutedStaysMasked()
    {
        // "God, oh God": one unticked, one not. Which match is which cannot be told, so both stay masked.
        var cues = new List<Cue> { new() { StartSeconds = 10, EndSeconds = 14, Text = "God, oh God." } };
        var occurrences = WordOccurrences.Group(
        [
            new Hit(10.2, 10.6, "GOD", HitSource.English, false),
            new Hit(12.5, 12.9, "GOD", HitSource.English, false),
        ]);
        occurrences[0].Mute = false;

        var spared = WordOccurrences.SparedInCues(occurrences, cues, SyncFit.Identity);

        Assert.Equal("***, oh ***.", CensoredSubtitleWriter.Mask(cues[0].Text, ["GOD"], '*', out _, spared[0]));
    }

    [Fact]
    public void TheSubtitleFilesShowAWordLeftIn()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cg-review-srt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            var settings = AppSettings.CreateDefault();
            settings.WordsSingle = ["GOD"];
            var cues = new List<Cue>
            {
                new() { StartSeconds = 245, EndSeconds = 248, Text = "You are not God." },
                new() { StartSeconds = 2791, EndSeconds = 2794, Text = "I lost Wanda. God." },
            };
            var occurrences = WordOccurrences.Group(
            [
                new Hit(246, 246.8, "GOD", HitSource.English, false),
                new Hit(2792.5, 2793.2, "GOD", HitSource.Subtitle, false),
            ]);
            occurrences[0].Mute = false;

            var result = CensoredSubtitleWriter.Write(
                cues, Path.Combine(folder, "Film"), settings, SubtitleOutputType.Dual, WordOccurrences.SparedInCues(occurrences, cues, SyncFit.Identity));

            var full = File.ReadAllText(result.FullPath!);
            Assert.Contains("You are not God.", full);
            Assert.Contains("I lost Wanda. ***.", full);

            // The Censored track only shows lines with a masked word: the line left in is blank there.
            var censored = File.ReadAllText(result.CensoredPath!);
            Assert.DoesNotContain("You are not God.", censored);
            Assert.Contains("I lost Wanda. ***.", censored);
            Assert.Equal(1, result.CensoredCues);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ChangingTheFilterMakesAScanStale()
    {
        var settings = AppSettings.CreateDefault();
        var before = ScanResult.KeyFor(settings);

        Assert.Equal(before, ScanResult.KeyFor(settings.Clone()));

        settings.WordsSingle = [.. settings.WordsSingle, "FORK"];
        Assert.NotEqual(before, ScanResult.KeyFor(settings));
    }

    [Fact]
    public void DroppingAScanSendsTheFileBackToWaiting()
    {
        var item = new MediaItem(@"D:\Films\Oscar.mkv") { Status = MediaStatus.Scanned, Scan = Scan(@"D:\Films\Oscar.mkv") };

        item.DropScan();

        Assert.Null(item.Scan);
        Assert.Null(item.Hits);
        Assert.Equal(MediaStatus.Waiting, item.Status);
    }

    [Fact]
    public async Task AHeldScanIsCensoredFromAndKeptWhenCensoringFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "cg-review-" + Guid.NewGuid().ToString("N"));
        var runner = new ProcessRunner();
        var locator = new ToolLocator(runner);
        var speech = new SpeechService(
            new ModelManager(new SettingsService(Path.Combine(root, "settings.json")), runner),
            new PhononService(runner, locator),
            locator);

        var pipeline = new CensorPipeline(runner, locator, speech);

        // The film is gone by the time Censor is pressed: the held scan is used (no tool is looked for), and kept.
        var path = Path.Combine(root, "Oscar.mkv");
        var scan = Scan(path);
        var item = new MediaItem(path) { Status = MediaStatus.Scanned, Scan = scan };

        await pipeline.RunAsync(item, AppSettings.CreateDefault(), CancellationToken.None);

        Assert.Equal(MediaStatus.Failed, item.Status);
        Assert.Equal("The input file no longer exists.", item.Message);
        Assert.Same(scan, item.Scan);
    }

    [Fact]
    public void TheSummaryCountsFilesReadyForReview()
    {
        var summary = new QueueSummary(0, 1, 0, 0, TimeSpan.FromMinutes(3), Scanned: 2);

        Assert.Equal("2 ready for review, 1 skipped", summary.Describe());
        Assert.Equal(3, summary.Total);
        Assert.True(summary.Fits);
    }

    [Fact]
    public void AHeadlessRunThatOnlyScannedSucceedsWithoutOutput()
    {
        // MediaPilot's -start with the review on: the files wait for the review, which is not a failure.
        var summary = new QueueSummary(0, 0, 0, 0, TimeSpan.FromMinutes(12), Scanned: 1);
        Assert.Equal(HeadlessExitCodes.Success, HeadlessExitCodes.For(summary));

        var item = new MediaItem(@"F:\Torrents\VisionQuest.mkv") { Status = MediaStatus.Scanned, Message = "9 words to review" };
        var report = HeadlessReport.Build([item], TimeSpan.FromMinutes(12), summary.Describe(), 0);

        Assert.Contains("\"status\": \"Scanned\"", report);
        Assert.Contains("\"output\": null", report);
    }

    [Fact]
    public void TheWindowIsToldToScanHandedOverFiles()
    {
        var arguments = LaunchArguments.Parse([@"F:\Torrents\VisionQuest.mkv", LaunchArguments.ReviewOption]);

        Assert.True(arguments.Review);
        Assert.False(arguments.Headless);
        Assert.Equal([@"F:\Torrents\VisionQuest.mkv"], arguments.Paths);
        Assert.False(LaunchArguments.Parse([@"F:\Torrents\VisionQuest.mkv"]).Review);
    }

    private static ScanResult Scan(string path)
    {
        var hits = new List<Hit> { new(10, 11, "FUCK", HitSource.English, false) };
        var stream = new MediaStreamInfo { Index = 1, CodecType = "audio", CodecName = "aac", Channels = 2, ChannelLayout = "stereo" };

        return new ScanResult
        {
            Probe = new MediaProbe { Path = path, DurationSeconds = 100, Streams = [stream] },
            Audio = new AudioSelection(stream, 0, 0),
            SubtitleCues = null,
            OutputCues = null,
            English = null,
            Hits = new HitSet(hits, HitCollector.BuildRanges(hits, 100, 0), SyncFit.Identity),
            Occurrences = WordOccurrences.Group(hits),
            Transcribed = false,
            SettingsKey = ScanResult.KeyFor(AppSettings.CreateDefault()),
        };
    }
}
