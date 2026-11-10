using CinemaGuard.Core.Ffmpeg;
using CinemaGuard.Core.Media;
using CinemaGuard.Core.Pipeline;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Sources;

namespace CinemaGuard.Core.Tests;

/// <summary>Audio files, the source's other tracks, single spellings, the OpenSubtitles limit and --help.</summary>
public class RequestedFeatureTests
{
    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cg-features-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Theory]
    [InlineData("Book.mp3", "Book_CG.mp3")]
    [InlineData("Book.m4b", "Book_CG.m4b")]
    [InlineData("Book.flac", "Book_CG.flac")]
    [InlineData("Book.aac", "Book_CG.m4a")]
    [InlineData("Book.wma", "Book_CG.m4a")]
    public void AnAudioFileIsWrittenBackInItsOwnFormat(string input, string output)
    {
        var directory = TempDirectory();

        try
        {
            var settings = AppSettings.CreateDefault();
            settings.OutputContainer = OutputContainer.Mkv;

            var plan = OutputPathPolicy.Decide(Path.Combine(directory, input), settings, null);

            Assert.Null(plan.Error);
            Assert.Equal(Path.Combine(directory, output), plan.OutputPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AKeptDtsTrackMovesTheOutputToMatroska()
    {
        var directory = TempDirectory();

        try
        {
            var input = Path.Combine(directory, "Movie.mkv");
            var main = new MediaStreamInfo { Index = 1, CodecType = "audio", CodecName = "aac", Channels = 6 };
            var dts = new MediaStreamInfo { Index = 2, CodecType = "audio", CodecName = "dts", Channels = 6 };

            Assert.Equal("mp4", OutputPathPolicy.Decide(input, AppSettings.CreateDefault(), main).Extension);
            Assert.Equal("mkv", OutputPathPolicy.Decide(input, AppSettings.CreateDefault(), main, [dts]).Extension);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(".mp3", "mp3", "libmp3lame")]
    [InlineData(".m4b", "aac", "aac")]
    [InlineData(".m4a", "alac", "alac")]
    [InlineData(".flac", "flac", "flac")]
    [InlineData(".wav", "pcm_s24le", "pcm_s24le")]
    [InlineData(".ogg", "vorbis", "libvorbis")]
    [InlineData(".ogg", "opus", "libopus")]
    [InlineData(".m4a", "wmav2", "aac")]
    public void AnAudioFileKeepsAnEncoderItsFormatHolds(string extension, string sourceCodec, string encoder)
    {
        var audio = new MediaStreamInfo { Index = 0, CodecType = "audio", CodecName = sourceCodec, Channels = 2 };

        Assert.Equal(encoder, AudioCodecPolicy.PlanAudioFile(audio, extension).Encoder);
    }

    [Fact]
    public void RecognisesAudioFiles()
    {
        Assert.True(FileTypes.IsSupportedMedia(".M4B"));
        Assert.True(FileTypes.IsSupportedAudio(".mp3"));
        Assert.False(FileTypes.IsSupportedVideo(".mp3"));
        Assert.True(FileTypes.IsAudioFile(@"D:\Books\Dune.m4b"));
        Assert.False(FileTypes.IsAudioFile(@"D:\Films\Dune.mkv"));
    }

    [Fact]
    public void AFilmFolderIgnoresItsThemeSongButAnAudiobookFolderIsAdded()
    {
        var directory = TempDirectory();

        try
        {
            var film = Path.Combine(directory, "Film");
            var book = Path.Combine(directory, "Book");
            Directory.CreateDirectory(film);
            Directory.CreateDirectory(book);

            File.WriteAllText(Path.Combine(film, "Heat (1995).mkv"), "x");
            File.WriteAllText(Path.Combine(film, "theme.mp3"), "x");
            File.WriteAllText(Path.Combine(book, "Chapter 01.mp3"), "x");
            File.WriteAllText(Path.Combine(book, "Chapter 02.mp3"), "x");
            File.WriteAllText(Path.Combine(book, "cover.jpg"), "x");

            Assert.Equal(["Heat (1995).mkv"], FileTypes.MediaInFolder(film, recursive: false).Select(Path.GetFileName));
            Assert.Equal(2, FileTypes.MediaInFolder(book, recursive: false).Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadsCoverArtFromTheProbe()
    {
        var probe = MediaProbeParser.Parse("""
            {
              "streams": [
                { "index": 0, "codec_type": "audio", "codec_name": "mp3", "channels": 2 },
                { "index": 1, "codec_type": "video", "codec_name": "png", "disposition": { "attached_pic": 1 } }
              ],
              "format": { "format_name": "mp3", "duration": "600" }
            }
            """, @"C:\books\book.mp3");

        Assert.Equal(1, probe.CoverArt?.Index);
    }

    [Fact]
    public void AFamilyCanBeHalfOn()
    {
        var words = new List<string>();
        FilterCatalog.EnableFamily(words, "JESUS", true);
        Assert.True(FilterCatalog.FamilyState(words, "JESUS"));

        FilterCatalog.EnableWord(words, "JEEZUS", false);
        Assert.Null(FilterCatalog.FamilyState(words, "JESUS"));
        Assert.Contains("JESUS", words);
        Assert.DoesNotContain("JEEZUS", words);

        FilterCatalog.EnableFamily(words, "JESUS", false);
        Assert.False(FilterCatalog.FamilyState(words, "JESUS"));
    }

    [Fact]
    public void TellsTheOpenSubtitlesLimitPlainly()
    {
        var message = SubloaderService.QuotaMessage(
            "Error: You have downloaded your allowed 20 subtitles for 24h. Your quota will be renewed in 05 hours and 12 minutes (2026-11-02 23:00:00 UTC)");

        Assert.NotNull(message);
        Assert.Contains("(20 subtitles)", message);
        Assert.Contains("resets in 05 hours and 12 minutes (2026-11-02 23:00:00 UTC)", message);

        Assert.NotNull(SubloaderService.QuotaMessage("Maximum downloads reached"));
        Assert.Null(SubloaderService.QuotaMessage(@"No subtitles found for D:\Films\Movie 1429.mkv"));
    }

    [Fact]
    public void HelpIsAnOptionNotAPath()
    {
        foreach (var option in new[] { "--help", "-h", "-?", "/?", "--HELP" })
        {
            var arguments = LaunchArguments.Parse([option]);
            Assert.True(arguments.Help);
            Assert.Empty(arguments.Paths);
        }

        Assert.False(LaunchArguments.Parse([@"D:\Films\Heat.mkv"]).Help);
    }

    [Fact]
    public void TheHelpTextNamesEveryOption()
    {
        foreach (var option in new[] { "-start", LaunchArguments.ReportOption, LaunchArguments.ProgressOption, LaunchArguments.CancelEventOption, LaunchArguments.ReviewOption, "--help" })
            Assert.Contains(option, LaunchArguments.HelpText);

        foreach (var code in new[] { HeadlessExitCodes.Success, HeadlessExitCodes.Failed, HeadlessExitCodes.NoFiles, HeadlessExitCodes.Crashed })
            Assert.Contains($"  {code}   ", LaunchArguments.HelpText);
    }
}
