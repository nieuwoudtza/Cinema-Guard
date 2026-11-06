using CinemaGuard.Core.Ffmpeg;
using CinemaGuard.Core.Media;

namespace CinemaGuard.Core.Tests;

public class RestoreTests
{
    /// <summary>What Cinema Guard writes with Keep the original audio and Keep the other audio tracks on.</summary>
    private static MediaProbe Censored(bool keptOriginal = true) => MediaProbeParser.Parse($$"""
        {
          "streams": [
            { "index": 0, "codec_type": "video", "codec_name": "hevc" },
            { "index": 1, "codec_type": "audio", "codec_name": "eac3", "channels": 6, "channel_layout": "5.1(side)",
              "disposition": { "default": 1 }, "tags": { "language": "eng", "title": "Cinema Guard (Vocals Only)" } },
            {{(keptOriginal ? """{ "index": 2, "codec_type": "audio", "codec_name": "eac3", "tags": { "language": "eng", "title": "Original" } },""" : "")}}
            { "index": 3, "codec_type": "audio", "codec_name": "ac3", "tags": { "language": "fre" } },
            { "index": 4, "codec_type": "subtitle", "codec_name": "subrip", "tags": { "language": "eng", "title": "Full" } }
          ],
          "format": { "format_name": "matroska,webm", "duration": "5400.0" }
        }
        """, @"C:\films\Film_CG.mkv");

    [Fact]
    public void FindsTheKeptOriginalAndTheMutedTrack()
    {
        var plan = AudioRestorer.Plan(Censored());

        Assert.NotNull(plan);
        Assert.Equal(1, plan.OriginalOrdinal);
        Assert.Equal(0, plan.CensoredOrdinal);
    }

    [Fact]
    public void AFilmWithoutAKeptOriginalCannotBeRestored() => Assert.Null(AudioRestorer.Plan(Censored(keptOriginal: false)));

    [Fact]
    public void CopiesEverythingButTheMutedTrackAndPlaysTheOriginal()
    {
        var plan = AudioRestorer.Plan(Censored())!;
        var arguments = AudioRestorer.Arguments(@"C:\films\Film_CG.mkv", plan, @"C:\films\Film_Restored.mkv");

        Assert.Contains("-0:a:0", arguments);
        Assert.Equal("copy", ArgList.Next(arguments, "-c"));

        // The original moves up to the first audio track once the muted one is gone.
        Assert.Equal("default", ArgList.Next(arguments, "-disposition:a:0"));
        Assert.Equal(@"C:\films\Film_Restored.mkv", arguments[^1]);
    }

    [Fact]
    public void NamesTheCopyAfterTheFilmWithoutTheAffix()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cg-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var input = Path.Combine(directory, "Film_CG.mkv");
            Assert.Equal(Path.Combine(directory, "Film_Restored.mkv"), AudioRestorer.OutputPath(input, "_CG"));

            File.WriteAllText(Path.Combine(directory, "Film_Restored.mkv"), string.Empty);
            Assert.Equal(Path.Combine(directory, "Film_Restored (2).mkv"), AudioRestorer.OutputPath(input, "_CG"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PlaysOneChannelThroughBothSpeakers()
    {
        var arguments = FfmpegCommands.ChannelPreview(@"C:\films\Film.mkv", 1, "FC", 600, 20, @"C:\temp\FC.wav");

        Assert.Equal("600", ArgList.Next(arguments, "-ss"));
        Assert.Equal("20", ArgList.Next(arguments, "-t"));
        Assert.Equal("0:a:1", ArgList.Next(arguments, "-map"));
        Assert.Equal("pan=stereo|c0=FC|c1=FC", ArgList.Next(arguments, "-af"));
        Assert.Equal("2", ArgList.Next(FfmpegCommands.ChannelPreview(@"C:\films\Film.mkv", 1, null, 600, 20, @"C:\temp\mix.wav"), "-ac"));
    }

    [Fact]
    public void NamesChannelsAndNumbersThoseWithoutALayout()
    {
        var unnamed = new MediaStreamInfo { CodecType = "audio", CodecName = "pcm_s24le", Channels = 3 };

        Assert.Equal(["c0", "c1", "c2"], ChannelLayouts.ChannelsOf(unnamed));
        Assert.Equal("Channel 3", ChannelLayouts.Describe("c2"));
        Assert.Equal("Centre (FC)", ChannelLayouts.Describe("FC"));
    }

    [Fact]
    public void AChosenSubtitleTrackBeatsTheLanguage()
    {
        var probe = MediaProbeParser.Parse("""
            {
              "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264" },
                { "index": 2, "codec_type": "subtitle", "codec_name": "subrip", "tags": { "language": "eng" } },
                { "index": 3, "codec_type": "subtitle", "codec_name": "subrip", "disposition": { "hearing_impaired": 1 }, "tags": { "language": "eng", "title": "SDH" } },
                { "index": 4, "codec_type": "subtitle", "codec_name": "hdmv_pgs_subtitle", "tags": { "language": "eng" } }
              ],
              "format": { "format_name": "matroska,webm", "duration": "60" }
            }
            """, @"C:\films\Film.mkv");

        Assert.Equal(2, StreamSelector.SelectSubtitle(probe, "eng", null, out _)!.Index);
        Assert.Equal(3, StreamSelector.SelectSubtitle(probe, "eng", 3, out _)!.Index);

        // An image track cannot be read, so the choice falls back to the best text track.
        Assert.Equal(2, StreamSelector.SelectSubtitle(probe, "eng", 4, out _)!.Index);

        var sdh = probe.SubtitleStreams.Single(s => s.Index == 3);
        Assert.Equal(2, probe.SubtitleNumber(sdh));
        Assert.Equal("eng, SubRip, \u201cSDH\u201d, hearing impaired", sdh.Describe());
    }
}
