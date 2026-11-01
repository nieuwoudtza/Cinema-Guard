using CinemaGuard.Core.Ffmpeg;
using CinemaGuard.Core.Intervals;
using CinemaGuard.Core.Media;
using CinemaGuard.Core.Settings;

namespace CinemaGuard.Core.Tests;

internal static class ArgList
{
    public static int IndexOf(IReadOnlyList<string> arguments, string option)
    {
        for (var i = 0; i < arguments.Count; i++)
            if (arguments[i] == option) return i;

        return -1;
    }

    public static string Next(IReadOnlyList<string> arguments, string option)
    {
        var index = IndexOf(arguments, option);
        if (index < 0) throw new InvalidOperationException($"'{option}' is not in the argument list.");
        return arguments[index + 1];
    }
}

public class FfmpegCommandTests
{
    private static MediaProbe Probe(string codec = "aac", int channels = 6, string layout = "5.1") =>
        MediaProbeParser.Parse($$"""
            {
              "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264" },
                { "index": 1, "codec_type": "audio", "codec_name": "{{codec}}", "channels": {{channels}},
                  "channel_layout": "{{layout}}", "bit_rate": "384000", "disposition": { "default": 1 },
                  "tags": { "language": "eng" } }
              ],
              "format": { "format_name": "mov,mp4", "duration": "120.500", "start_time": "0.000000" }
            }
            """, @"C:\media\movie.mp4");

    private static AudioSelection Audio(MediaProbe probe, int ordinal = 0) =>
        new(probe.AudioStreams.First(), ordinal, 0);

    private static EncodeRequest Request(MediaProbe probe) => new()
    {
        InputPath = probe.Path,
        OutputPath = @"C:\media\movie_CG.partial.mp4",
        Probe = probe,
        Audio = Audio(probe),
    };

    [Fact]
    public void NoRangesCopiesTheAudioWithoutAFilter()
    {
        var probe = Probe();
        var arguments = FfmpegCommands.Encode(Request(probe));

        Assert.DoesNotContain("-/filter_complex", arguments);
        Assert.DoesNotContain("[muted]", arguments);
        Assert.Contains("0:a:0", arguments);
        Assert.Equal("copy", ArgList.Next(arguments, "-c:a:0"));
        Assert.Equal("copy", ArgList.Next(arguments, "-c:v"));
    }

    [Fact]
    public void RangesUseTheFilterScriptAndMuteTheSelectedTrack()
    {
        var probe = Probe();
        var request = Request(probe) with
        {
            Ranges = [new TimeRange(1, 2)],
            FilterScriptPath = @"C:\tmp\mute.txt",
        };

        var arguments = FfmpegCommands.Encode(request);

        Assert.Equal(@"C:\tmp\mute.txt", ArgList.Next(arguments, "-/filter_complex"));
        Assert.Contains("[muted]", arguments);
        Assert.Equal("aac", ArgList.Next(arguments, "-c:a:0"));
        Assert.Equal("6", ArgList.Next(arguments, "-ac:a:0"));
    }

    [Fact]
    public void KeepingTheOriginalAudioAddsASecondTrack()
    {
        var probe = Probe();
        var request = Request(probe) with
        {
            Ranges = [new TimeRange(1, 2)],
            FilterScriptPath = @"C:\tmp\mute.txt",
            KeepOriginalAudio = true,
        };

        var arguments = FfmpegCommands.Encode(request);

        Assert.Equal("copy", ArgList.Next(arguments, "-c:a:1"));
        Assert.Equal("0", ArgList.Next(arguments, "-disposition:a:1"));
        Assert.Equal(1, arguments.Count(a => a == "0:a:0"));
    }

    [Fact]
    public void EmbedsSubtitlesWithTheContainerCodec()
    {
        var probe = Probe();

        var mp4 = FfmpegCommands.Encode(Request(probe) with
        {
            Subtitles = [new SubtitleInput(@"C:\tmp\full.srt", "Full", "eng")],
        });

        Assert.Equal("mov_text", ArgList.Next(mp4, "-c:s:0"));
        Assert.Equal("default", ArgList.Next(mp4, "-disposition:s:0"));

        var mkv = FfmpegCommands.Encode(Request(probe) with
        {
            Container = OutputContainer.Mkv,
            Subtitles =
            [
                new SubtitleInput(@"C:\tmp\full.srt", "Full", "eng"),
                new SubtitleInput(@"C:\tmp\censored.srt", "Censored", "eng"),
            ],
        });

        // MP4 players that show only the handler name still see "Full", not "SubtitleHandler".
        Assert.Contains("handler_name=Full", mp4);
        Assert.DoesNotContain(mkv, argument => argument.StartsWith("handler_name=", StringComparison.Ordinal));

        Assert.Equal("srt", ArgList.Next(mkv, "-c:s:0"));
        Assert.Equal("srt", ArgList.Next(mkv, "-c:s:1"));
        Assert.Equal("default", ArgList.Next(mkv, "-disposition:s:0"));
        Assert.Equal("0", ArgList.Next(mkv, "-disposition:s:1"));
    }

    private static MediaProbe RemuxProbe() => MediaProbeParser.Parse("""
        {
          "streams": [
            { "index": 0, "codec_type": "video", "codec_name": "hevc" },
            { "index": 1, "codec_type": "audio", "codec_name": "truehd", "channels": 8, "disposition": { "default": 1 }, "tags": { "language": "eng" } },
            { "index": 2, "codec_type": "audio", "codec_name": "ac3", "channels": 6, "tags": { "language": "eng" } },
            { "index": 3, "codec_type": "audio", "codec_name": "ac3", "channels": 2, "disposition": { "comment": 1 }, "tags": { "language": "eng", "title": "Commentary" } },
            { "index": 4, "codec_type": "subtitle", "codec_name": "subrip", "tags": { "language": "eng" } },
            { "index": 5, "codec_type": "subtitle", "codec_name": "subrip", "disposition": { "forced": 1 }, "tags": { "language": "eng" } },
            { "index": 6, "codec_type": "subtitle", "codec_name": "hdmv_pgs_subtitle", "tags": { "language": "fre" } }
          ],
          "format": { "format_name": "matroska", "duration": "7200" }
        }
        """, @"C:\media\remux.mkv");

    [Fact]
    public void CopiesTheOtherTracksAfterTheCensoredOnesWithoutTheirDefaultFlag()
    {
        var probe = RemuxProbe();
        var others = probe.AudioStreams.Where(stream => stream.Index != 1).ToList();
        var request = Request(probe) with
        {
            OutputPath = @"C:\media\remux_CG.partial.mkv",
            Container = OutputContainer.Mkv,
            Ranges = [new TimeRange(1, 2)],
            FilterScriptPath = @"C:\tmp\mute.txt",
            KeepOriginalAudio = true,
            ExtraAudio = others,
            Subtitles = [new SubtitleInput(@"C:\tmp\full.srt", "Full", "eng")],
            ExtraSubtitles = [probe.Streams[6]],
        };

        var arguments = FfmpegCommands.Encode(request);

        // Muted, original, then the two others; the copies never take the default.
        Assert.Contains("0:2", arguments);
        Assert.Contains("0:3", arguments);
        Assert.Equal("copy", ArgList.Next(arguments, "-c:a:2"));
        Assert.Equal("copy", ArgList.Next(arguments, "-c:a:3"));
        Assert.Equal("-default", ArgList.Next(arguments, "-disposition:a:2"));
        Assert.Equal("-default", ArgList.Next(arguments, "-disposition:a:3"));
        Assert.Equal(4, request.ExpectedAudioStreams);

        // The written subtitle first, then the copied PGS track with its fonts' attachments.
        Assert.Contains("0:6", arguments);
        Assert.Equal("srt", ArgList.Next(arguments, "-c:s:0"));
        Assert.Equal("copy", ArgList.Next(arguments, "-c:s:1"));
        Assert.Equal("-default", ArgList.Next(arguments, "-disposition:s:1"));
        Assert.Contains("0:t?", arguments);
        Assert.Equal(2, request.ExpectedSubtitleStreams);
    }

    [Fact]
    public void AForcedTrackIsFlaggedForcedAndNeverTheDefault()
    {
        var probe = Probe();
        var arguments = FfmpegCommands.Encode(Request(probe) with
        {
            Subtitles =
            [
                new SubtitleInput(@"C:\tmp\forced.srt", "Forced", "eng", Forced: true),
                new SubtitleInput(@"C:\tmp\full.srt", "Full", "eng"),
            ],
        });

        Assert.Equal("forced", ArgList.Next(arguments, "-disposition:s:0"));
        Assert.Equal("default", ArgList.Next(arguments, "-disposition:s:1"));
    }

    [Fact]
    public void Mp4KeepsOnlyTextSubtitleTracks()
    {
        var probe = RemuxProbe();

        Assert.True(FfmpegCommands.CanKeepSubtitle(probe.Streams[4], OutputContainer.Mp4));
        Assert.False(FfmpegCommands.CanKeepSubtitle(probe.Streams[6], OutputContainer.Mp4));
        Assert.True(FfmpegCommands.CanKeepSubtitle(probe.Streams[6], OutputContainer.Mkv));
        Assert.False(FfmpegCommands.CanKeepSubtitle(probe.Streams[1], OutputContainer.Mkv));
    }

    private static MediaProbe AudiobookProbe() => MediaProbeParser.Parse("""
        {
          "streams": [
            { "index": 0, "codec_type": "audio", "codec_name": "aac", "channels": 1, "bit_rate": "64000", "tags": { "language": "eng" } },
            { "index": 1, "codec_type": "video", "codec_name": "mjpeg", "disposition": { "attached_pic": 1 } }
          ],
          "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "36000" }
        }
        """, @"C:\books\book.m4b");

    [Fact]
    public void AnAudioFileKeepsItsCoverAndFormatAndHasNoFilmOrSubtitles()
    {
        var probe = AudiobookProbe();
        var request = Request(probe) with
        {
            OutputPath = @"C:\books\book_CG.partial.m4b",
            Ranges = [new TimeRange(1, 2)],
            FilterScriptPath = @"C:\tmp\mute.txt",
            KeepOriginalAudio = true,
            Subtitles = [new SubtitleInput(@"C:\tmp\full.srt", "Full", "eng")],
            AudioFileExtension = ".m4b",
        };

        var arguments = FfmpegCommands.Encode(request);

        Assert.DoesNotContain("0:v:0", arguments);
        Assert.Equal("0:1", ArgList.Next(arguments, "-map"));
        Assert.Equal("attached_pic", ArgList.Next(arguments, "-disposition:v:0"));
        Assert.Equal("aac", ArgList.Next(arguments, "-c:a:0"));
        Assert.Equal("64k", ArgList.Next(arguments, "-b:a:0"));
        Assert.Equal("1", ArgList.Next(arguments, "-ac:a:0"));
        Assert.DoesNotContain(@"C:\tmp\full.srt", arguments);
        Assert.DoesNotContain("-metadata:s:a:0", arguments);
        Assert.DoesNotContain("-aspect", arguments);
        Assert.Equal(1, request.ExpectedAudioStreams);
        Assert.Equal(0, request.ExpectedSubtitleStreams);
    }

    [Fact]
    public void OggHasNoRoomForCoverArt()
    {
        var probe = AudiobookProbe();
        var arguments = FfmpegCommands.Encode(Request(probe) with { AudioFileExtension = ".ogg" });

        Assert.DoesNotContain("-disposition:v:0", arguments);
        Assert.DoesNotContain("0:1", arguments);
    }

    [Fact]
    public void ParsesFfprobeJsonWithoutTextScraping()
    {
        var probe = Probe("dts", 6, "5.1(side)");

        Assert.Equal(120.5, probe.DurationSeconds, 3);
        Assert.Single(probe.AudioStreams);
        Assert.Equal("dts", probe.AudioStreams.First().CodecName);
        Assert.Equal("5.1(side)", probe.AudioStreams.First().ChannelLayout);
        Assert.Equal("eng", probe.AudioStreams.First().Language);
        Assert.Equal(0, probe.AudioOrdinal(probe.AudioStreams.First()));
        Assert.True(ChannelLayouts.CanSplitVocals("5.1(side)"));
        Assert.Equal(6, ChannelLayouts.ChannelsFor("5.1(side)")!.Length);
    }

    [Fact]
    public void SelectsThePreferredLanguageAudioTrack()
    {
        var probe = MediaProbeParser.Parse("""
            {
              "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264" },
                { "index": 1, "codec_type": "audio", "codec_name": "aac", "channels": 2, "tags": { "language": "fre" } },
                { "index": 2, "codec_type": "audio", "codec_name": "aac", "channels": 2, "disposition": { "default": 1 }, "tags": { "language": "eng" } },
                { "index": 3, "codec_type": "audio", "codec_name": "aac", "channels": 2, "disposition": { "comment": 1 }, "tags": { "language": "eng", "title": "Director commentary" } }
              ],
              "format": { "format_name": "matroska", "duration": "60" }
            }
            """, @"C:\media\movie.mkv");

        var selection = StreamSelector.SelectAudio(probe, "eng");

        Assert.NotNull(selection);
        Assert.Equal(2, selection!.Stream.Index);
        Assert.Equal(1, selection.Ordinal);
    }

    [Fact]
    public void PrefersFullSubtitlesOverForcedOnes()
    {
        var probe = MediaProbeParser.Parse("""
            {
              "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264" },
                { "index": 1, "codec_type": "subtitle", "codec_name": "subrip", "disposition": { "forced": 1 }, "tags": { "language": "eng" } },
                { "index": 2, "codec_type": "subtitle", "codec_name": "subrip", "tags": { "language": "eng" } }
              ],
              "format": { "format_name": "matroska", "duration": "60" }
            }
            """, @"C:\media\movie.mkv");

        var stream = StreamSelector.SelectSubtitle(probe, "eng", out _);

        Assert.NotNull(stream);
        Assert.Equal(2, stream!.Index);
    }

    [Fact]
    public void ReportsImageBasedSubtitlesAsNeedingOcr()
    {
        var probe = MediaProbeParser.Parse("""
            {
              "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264" },
                { "index": 1, "codec_type": "subtitle", "codec_name": "hdmv_pgs_subtitle", "tags": { "language": "eng" } }
              ],
              "format": { "format_name": "matroska", "duration": "60" }
            }
            """, @"C:\media\movie.mkv");

        Assert.Null(StreamSelector.SelectSubtitle(probe, "eng", out var reason));
        Assert.Contains("OCR", reason);
    }
}

public class AudioCodecPolicyTests
{
    private static MediaStreamInfo Audio(string codec, int channels) => new()
    {
        Index = 1,
        CodecType = "audio",
        CodecName = codec,
        Channels = channels,
    };

    [Fact]
    public void SevenPointOneTrueHdBecomesFlac()
    {
        var plan = AudioCodecPolicy.Plan(Audio("truehd", 8), forceSameEncoding: true, OutputContainer.Mkv);

        Assert.Equal("flac", plan.Encoder);
        Assert.True(plan.Lossless);
    }

    [Fact]
    public void SixChannelTrueHdStaysTrueHd()
    {
        Assert.Equal("truehd", AudioCodecPolicy.Plan(Audio("truehd", 6), true, OutputContainer.Mkv).Encoder);
    }

    [Fact]
    public void SevenPointOneEac3BecomesFlacButSixChannelKeepsEac3()
    {
        Assert.Equal("flac", AudioCodecPolicy.Plan(Audio("eac3", 8), true, OutputContainer.Mkv).Encoder);
        Assert.Equal("eac3", AudioCodecPolicy.Plan(Audio("eac3", 6), true, OutputContainer.Mkv).Encoder);
    }

    [Fact]
    public void DtsAndOpusKeepTheirCodec()
    {
        Assert.Equal("dca", AudioCodecPolicy.Plan(Audio("dts", 6), true, OutputContainer.Mkv).Encoder);
        Assert.Equal("libopus", AudioCodecPolicy.Plan(Audio("opus", 6), true, OutputContainer.Mkv).Encoder);
    }

    [Fact]
    public void TurningSameEncodingOffUsesAac()
    {
        var plan = AudioCodecPolicy.Plan(Audio("truehd", 8), forceSameEncoding: false, OutputContainer.Mp4);

        Assert.Equal("aac", plan.Encoder);
        Assert.Contains("-ac:a:0", plan.Arguments);
        Assert.Contains("8", plan.Arguments);
    }
}

public class FfmpegProgressTests
{
    [Fact]
    public void ReportsPercentSpeedAndCompletion()
    {
        var progress = new FfmpegProgress(100);

        progress.Feed("out_time_us=50000000");
        Assert.Equal(50, progress.Percent, 3);

        progress.Feed("speed=1.5x");
        Assert.Equal("1.5x", progress.Speed);

        progress.Feed("progress=end");
        Assert.True(progress.Completed);
        Assert.Equal(100, progress.Percent, 3);
    }

    [Fact]
    public void IgnoresJunkLines()
    {
        var progress = new FfmpegProgress(100);
        progress.Feed("not a progress line");
        progress.Feed("");

        Assert.Equal(0, progress.Percent, 3);
    }
}

public class FilterScriptTests
{
    [Fact]
    public void WritesOneVolumeFilterPerRangeOntoTheSelectedTrack()
    {
        var path = Path.Combine(Path.GetTempPath(), "cg-mute-" + Guid.NewGuid().ToString("N") + ".txt");

        try
        {
            MuteFilterWriter.Write(path, 1, [new TimeRange(1, 2), new TimeRange(3.5, 4.25)]);
            var text = File.ReadAllText(path);

            Assert.StartsWith("[0:a:1]", text);
            Assert.Contains("between(t,1.000,2.000)", text);
            Assert.Contains("between(t,3.500,4.250)", text);
            Assert.EndsWith("[muted]", text);
            Assert.DoesNotContain(",]", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VocalsGraphSplitsMutesAndJoins()
    {
        var channels = new[] { "FL", "FR", "FC", "LFE", "SL", "SR" };

        var graph = VocalsGraphBuilder.Build(
            0,
            "5.1(side)",
            channels,
            new Dictionary<string, IReadOnlyList<TimeRange>>
            {
                ["FC"] = [new TimeRange(1, 2)],
                ["SL"] = [new TimeRange(5, 6)],
            });

        Assert.Contains("channelsplit=channel_layout=5.1(side)", graph);
        Assert.Contains("[FC]volume=enable='between(t,1.000,2.000)':volume=0[FCm]", graph);
        Assert.Contains("[LFE]anull[LFEm]", graph);
        Assert.Contains("join=inputs=6:channel_layout=5.1(side):map=0.0-FL|1.0-FR|2.0-FC|3.0-LFE|4.0-SL|5.0-SR[muted]", graph);
    }
}
