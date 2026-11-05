using CinemaGuard.Core.Ffmpeg;
using CinemaGuard.Core.Intervals;
using CinemaGuard.Core.Media;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Tools;

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
            Subtitles = [new SubtitleInput(@"C:\tmp\full.srt", "Full", "eng", Shown: true)],
        });

        Assert.Equal("mov_text", ArgList.Next(mp4, "-c:s:0"));
        Assert.Equal("default", ArgList.Next(mp4, "-disposition:s:0"));

        var mkv = FfmpegCommands.Encode(Request(probe) with
        {
            Container = OutputContainer.Mkv,
            Subtitles =
            [
                new SubtitleInput(@"C:\tmp\full.srt", "Full", "eng", Shown: true),
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
                new SubtitleInput(@"C:\tmp\full.srt", "Full", "eng", Shown: true),
            ],
        });

        Assert.Equal("forced", ArgList.Next(arguments, "-disposition:s:0"));
        Assert.Equal("default", ArgList.Next(arguments, "-disposition:s:1"));
    }

    [Fact]
    public void TheShownSubtitleTrackIsTheDefaultAndOffLeavesNone()
    {
        var probe = Probe();
        SubtitleInput[] Tracks(bool showFull, bool showCensored) =>
        [
            new SubtitleInput(@"C:\tmp\full.srt", "Full", "eng", Shown: showFull),
            new SubtitleInput(@"C:\tmp\censored.srt", "Censored", "eng", Shown: showCensored),
        ];

        var censored = Request(probe) with { Subtitles = Tracks(false, true) };
        var arguments = FfmpegCommands.Encode(censored);
        Assert.Equal(1, censored.ShownSubtitle);
        Assert.Equal("0", ArgList.Next(arguments, "-disposition:s:0"));
        Assert.Equal("default", ArgList.Next(arguments, "-disposition:s:1"));

        var off = Request(probe) with { Subtitles = Tracks(false, false) };
        arguments = FfmpegCommands.Encode(off);
        Assert.Equal(-1, off.ShownSubtitle);
        Assert.Equal("0", ArgList.Next(arguments, "-disposition:s:0"));
        Assert.Equal("0", ArgList.Next(arguments, "-disposition:s:1"));
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
    public void FadesOutBeforeEachRangeAndBackInAfterIt()
    {
        var path = Path.Combine(Path.GetTempPath(), "cg-mute-" + Guid.NewGuid().ToString("N") + ".txt");

        try
        {
            MuteFilterWriter.Write(path, 0, [new TimeRange(0.01, 2), new TimeRange(3.5, 4.25)], 0.03);
            var text = File.ReadAllText(path);

            // Small frames first, so the level can move within a 30 ms fade.
            Assert.StartsWith("[0:a:0]asetnsamples=n=256:p=0,volume=", text);
            // The fade window never starts before the beginning of the file.
            Assert.Contains("volume=enable='between(t,0.000,2.030)':volume='min(1,max(0,max((0.010-t-256/sample_rate)/0.030,(t-2.000)/0.030)))':eval=frame,", text);
            Assert.Contains("volume=enable='between(t,3.470,4.280)':volume='min(1,max(0,max((3.500-t-256/sample_rate)/0.030,(t-4.250)/0.030)))':eval=frame[muted]", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NoFadeKeepsTheHardMute()
    {
        var graph = VocalsGraphBuilder.Build(0, "stereo", ["FL", "FR"], new Dictionary<string, IReadOnlyList<TimeRange>> { ["FL"] = [new TimeRange(1, 2)] }, 0);

        Assert.Contains("[FL]volume=enable='between(t,1.000,2.000)':volume=0[FLm]", graph);
        Assert.DoesNotContain("asetnsamples", graph);
    }

    [Fact]
    public void VocalsGraphFadesEachMutedChannel()
    {
        var graph = VocalsGraphBuilder.Build(0, "stereo", ["FL", "FR"], new Dictionary<string, IReadOnlyList<TimeRange>> { ["FL"] = [new TimeRange(1, 2)] }, 0.05);

        Assert.Contains("[FL]asetnsamples=n=256:p=0,volume=enable='between(t,0.950,2.050)'", graph);
        Assert.Contains("[FR]anull[FRm]", graph);
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

public class Mp4SubtitleFlagsTests
{
    private static byte[] Box(string type, params byte[][] content)
    {
        var body = content.SelectMany(part => part).ToArray();
        var box = new byte[8 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    /// <summary>A track whose stsd holds one sample entry: reserved, data reference index, then the entry's fields.</summary>
    private static byte[] Track(string entryType) =>
        Box("trak", Box("tkhd", new byte[84]), Box("mdia", Box("minf", Box("stbl",
            Box("stsd", [0, 0, 0, 0, 0, 0, 0, 1], Box(entryType, new byte[6], [0, 1], new byte[30]))))));

    [Fact]
    public void FlagsTheChosenTextTrackAndNothingElse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cg-mp4-{Guid.NewGuid():N}.mp4");

        try
        {
            // A 64-bit mdat before the movie header, as FFmpeg writes a large film.
            var mdat = new byte[16 + 32];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(mdat, 1);
            "mdat"u8.CopyTo(mdat.AsSpan(4));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(mdat.AsSpan(8), (ulong)mdat.Length);

            File.WriteAllBytes(path, [.. Box("ftyp", "isom"u8.ToArray()), .. mdat,
                .. Box("moov", Box("mvhd", new byte[100]), Track("avc1"), Track("tx3g"), Track("tx3g"))]);

            var before = File.ReadAllBytes(path);
            Assert.Equal([0u, 0u], Mp4SubtitleFlags.Read(path));

            Assert.True(Mp4SubtitleFlags.Show(path, 1));
            Assert.Equal([0u, Mp4SubtitleFlags.AllSamplesForced], Mp4SubtitleFlags.Read(path));

            // Only the flag's byte changed.
            var after = File.ReadAllBytes(path);
            Assert.Equal(before.Length, after.Length);
            Assert.Single(before.Zip(after), pair => pair.First != pair.Second);

            Assert.False(Mp4SubtitleFlags.Show(path, 2));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class DialogueLeakTests
{
    private const int Rate = 16000;

    private static float[] Noise(int seed, double seconds = 0.6, double level = 0.2)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, (int)(seconds * Rate)).Select(_ => (float)((random.NextDouble() * 2 - 1) * level)).ToArray();
    }

    private static float[] Mix(float[] a, double gainA, float[] b, double gainB, int delay = 0) =>
        a.Select((x, i) => (float)(x * gainA + (i >= delay ? b[i - delay] : 0) * gainB)).ToArray();

    [Fact]
    public void AChannelWithTheCentresWordsUnderItsOwnSoundCarriesThem()
    {
        var speech = Noise(1);
        var effects = Noise(2);

        // The words 20 dB down and a few samples late, under the channel's own effects (14 dB louder than them).
        var front = Mix(effects, 0.5, speech, 0.1, delay: 4);
        var (correlation, level) = DialogueLeak.Measure(speech, front, Rate);

        Assert.InRange(level, -21, -19);
        Assert.True(correlation > DialogueLeak.MinimumCorrelation);
        Assert.True(DialogueLeak.CarriesSpeech(speech, front, Rate));
    }

    [Fact]
    public void TheWordsReverbInASurroundChannelIsFound()
    {
        var speech = Noise(1);

        // The room's echo of the words 30 ms late and 15 dB down, over quiet ambience.
        var surround = Mix(Noise(4), 0.1, speech, 0.18, delay: Rate * 30 / 1000);

        Assert.True(DialogueLeak.CarriesSpeech(speech, surround, Rate));
    }

    [Fact]
    public void UnrelatedOrFaintSoundIsLeftPlaying()
    {
        var speech = Noise(1);

        Assert.False(DialogueLeak.CarriesSpeech(speech, Noise(3), Rate));
        Assert.False(DialogueLeak.CarriesSpeech(speech, Mix(Noise(3), 1, speech, 0.01), Rate));

        // Words 20 dB down under effects 6 dB louder than the dialogue are drowned out.
        Assert.False(DialogueLeak.CarriesSpeech(speech, Mix(Noise(3), 2, speech, 0.1), Rate));
        Assert.False(DialogueLeak.CarriesSpeech(speech, new float[speech.Length], Rate));
        Assert.False(DialogueLeak.CarriesSpeech(new float[speech.Length], Noise(3), Rate));
    }

    [Fact]
    public void WavReaderReadsAStretchByTime()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cg-wav-{Guid.NewGuid():N}.wav");

        try
        {
            var samples = Enumerable.Range(0, Rate * 2).Select(i => (short)(i % 1000)).ToArray();

            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write("RIFF"u8);
                writer.Write(36 + 12 + samples.Length * 2);
                writer.Write("WAVE"u8);
                writer.Write("fmt "u8);
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(Rate);
                writer.Write(Rate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                // ffmpeg's LIST chunk, which a reader has to step over.
                writer.Write("LIST"u8);
                writer.Write(4);
                writer.Write("INFO"u8);
                writer.Write("data"u8);
                writer.Write(samples.Length * 2);
                foreach (var sample in samples) writer.Write(sample);
            }

            using var reader = new MonoWavReader(path);
            var stretch = reader.Read(1.0, 1.5);

            Assert.Equal(Rate / 2, stretch.Length);
            Assert.Equal(Rate % 1000 / 32768f, stretch[0]);
            Assert.Equal(Rate / 2, reader.Read(1.5, 9).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class FfmpegVersionTests
{
    [Theory]
    // Releases: the number decides.
    [InlineData("ffmpeg version 8.1.2-full_build-www.gyan.dev Copyright (c) 2000-2026", "8.1.2", true)]
    [InlineData("ffmpeg version 7.1-essentials_build-www.gyan.dev Copyright (c) 2000-2024", "7.1", true)]
    [InlineData("ffmpeg version n7.1.1 Copyright (c) 2000-2025", "7.1.1", true)]
    [InlineData("ffmpeg version 7.0.2-full_build-www.gyan.dev Copyright (c) 2000-2024", "7.0.2", false)]
    [InlineData("ffmpeg version 4.4.2-0ubuntu0.22.04.1 Copyright (c) 2000-2021", "4.4.2", false)]
    // Git builds name a date or commit: libavutil decides (7.1 shipped 59.39).
    [InlineData("ffmpeg version 2023-10-12-git-a7663c9604-full_build-www.gyan.dev Copyright (c) 2000-2023\nlibavutil      58. 27.100 / 58. 27.100", "2023-10-12-git-a7663c9604-full_build-www.gyan.dev", false)]
    [InlineData("ffmpeg version N-114000-g1a2b3c4d5e-20240301 Copyright (c) 2000-2024\nlibavutil      59.  8.100 / 59.  8.100", "N-114000-g1a2b3c4d5e-20240301", false)]
    [InlineData("ffmpeg version N-117000-g1a2b3c4d5e-20240920 Copyright (c) 2000-2024\nlibavutil      59. 39.100 / 59. 39.100", "N-117000-g1a2b3c4d5e-20240920", true)]
    [InlineData("ffmpeg version 2026-10-01-git-0123456789-full_build-www.gyan.dev\r\nlibavutil      60. 30.100 / 60. 30.100\r\n", "2026-10-01-git-0123456789-full_build-www.gyan.dev", true)]
    public void ReadsTheFfmpegVersion(string banner, string version, bool supported)
    {
        var (read, problem) = ToolLocator.ReadFfmpegVersion(banner);

        Assert.Equal(version, read);
        Assert.Equal(supported, problem is null);
    }

    [Fact]
    public void ABuildWithNoVersionAtAllIsNotTrusted()
    {
        var (version, problem) = ToolLocator.ReadFfmpegVersion("ffmpeg version 2023-10-12-git-a7663c9604-full_build-www.gyan.dev Copyright (c) 2000-2023");

        Assert.Equal("2023-10-12-git-a7663c9604-full_build-www.gyan.dev", version);
        Assert.Equal("Cannot tell which FFmpeg version this is: 7.1 or newer is required", problem);
    }
}