using System.Globalization;
using CinemaGuard.Core.Intervals;
using CinemaGuard.Core.Media;
using CinemaGuard.Core.Settings;

namespace CinemaGuard.Core.Ffmpeg;

public sealed record SubtitleInput(string Path, string Title, string Language);

public sealed record EncodeRequest
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public required MediaProbe Probe { get; init; }
    public required AudioSelection Audio { get; init; }
    public IReadOnlyList<TimeRange> Ranges { get; init; } = [];
    public string? FilterScriptPath { get; init; }
    public bool KeepOriginalAudio { get; init; }
    public bool ForceSameAudioEncoding { get; init; } = true;
    public OutputContainer Container { get; init; } = OutputContainer.Mp4;
    public IReadOnlyList<SubtitleInput> Subtitles { get; init; } = [];
    public string Language { get; init; } = "eng";
    public string AudioTitle { get; init; } = "Cinema Guard";
    public bool AspectRatio { get; init; }
    public int AspectRatioWidth { get; init; } = 16;
    public int AspectRatioHeight { get; init; } = 9;
}

/// <summary>
/// Builds FFmpeg argument lists. Nothing here is a concatenated command string: the process runner
/// quotes each argument, so paths with spaces, brackets and quotes are safe.
/// </summary>
public static class FfmpegCommands
{
    public static IReadOnlyList<string> Probe(string inputPath) =>
        ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", inputPath];

    public static IReadOnlyList<string> MediaInfo(string inputPath) =>
        ["-hide_banner", "-i", inputPath];

    /// <summary>16 kHz mono wav, the only input Phonon reads.</summary>
    public static IReadOnlyList<string> ExtractAudio16k(string inputPath, int audioOrdinal, DownmixMode downmix, string channelLayout, string targetPath)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-i", inputPath,
            "-map", $"0:a:{audioOrdinal}", "-vn", "-sn", "-dn",
        };

        if (downmix == DownmixMode.CenterWeighted && ChannelLayouts.HasFrontCenter(channelLayout))
        {
            arguments.Add("-af");
            arguments.Add(CenterWeightedFilter(ChannelLayouts.ChannelsFor(channelLayout) ?? ["FC"]));
        }
        else
        {
            arguments.Add("-ac");
            arguments.Add("1");
        }

        arguments.AddRange(["-ar", "16000", "-c:a", "pcm_s16le", targetPath]);
        return arguments;
    }

    public static IReadOnlyList<string> ExtractChannel16k(string inputPath, int audioOrdinal, string channel, double gain, string targetPath) =>
    [
        "-hide_banner", "-nostdin", "-y", "-i", inputPath,
        "-map", $"0:a:{audioOrdinal}", "-vn", "-sn", "-dn",
        "-af", $"pan=mono|c0={channel},volume={gain.ToString("0.0", CultureInfo.InvariantCulture)}",
        "-ar", "16000", "-c:a", "pcm_s16le", targetPath,
    ];

    public static IReadOnlyList<string> ExtractSubtitle(string inputPath, int streamIndex, string targetPath) =>
    [
        "-hide_banner", "-nostdin", "-y", "-i", inputPath,
        "-map", $"0:{streamIndex}",
        "-c:s", "srt",
        targetPath,
    ];

    public static IReadOnlyList<string> Encode(EncodeRequest request)
    {
        var hasRanges = request.Ranges.Count > 0 && !string.IsNullOrWhiteSpace(request.FilterScriptPath);

        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats",
            "-i", request.InputPath,
        };

        foreach (var subtitle in request.Subtitles)
        {
            arguments.Add("-i");
            arguments.Add(subtitle.Path);
        }

        if (hasRanges)
        {
            arguments.Add("-/filter_complex");
            arguments.Add(request.FilterScriptPath!);
        }

        arguments.Add("-map");
        arguments.Add("0:v:0");

        if (hasRanges)
        {
            arguments.Add("-map");
            arguments.Add("[muted]");
        }
        else
        {
            arguments.Add("-map");
            arguments.Add($"0:a:{request.Audio.Ordinal}");
        }

        if (hasRanges && request.KeepOriginalAudio)
        {
            arguments.Add("-map");
            arguments.Add($"0:a:{request.Audio.Ordinal}");
        }

        for (var i = 0; i < request.Subtitles.Count; i++)
        {
            arguments.Add("-map");
            arguments.Add($"{i + 1}:0");
        }

        arguments.Add("-map_metadata");
        arguments.Add("0");
        arguments.Add("-map_chapters");
        arguments.Add("0");
        arguments.Add("-c:v");
        arguments.Add("copy");

        if (hasRanges)
        {
            var plan = AudioCodecPolicy.Plan(request.Audio.Stream, request.ForceSameAudioEncoding, request.Container);
            arguments.AddRange(plan.Arguments);

            if (request.KeepOriginalAudio)
            {
                arguments.Add("-c:a:1");
                arguments.Add("copy");
            }
        }
        else
        {
            // No muted words: never re-encode the audio (the old app did).
            arguments.Add("-c:a:0");
            arguments.Add("copy");
        }

        if (request.Subtitles.Count > 0)
        {
            arguments.Add("-c:s");
            arguments.Add(request.Container == OutputContainer.Mp4 ? "mov_text" : "srt");
        }

        arguments.Add("-disposition:a:0");
        arguments.Add("default");

        if (hasRanges && request.KeepOriginalAudio)
        {
            arguments.Add("-disposition:a:1");
            arguments.Add("0");
        }

        arguments.Add("-metadata:s:a:0");
        arguments.Add($"title={request.AudioTitle}");
        arguments.Add("-metadata:s:a:0");
        arguments.Add($"language={request.Language}");

        if (hasRanges && request.KeepOriginalAudio)
        {
            arguments.Add("-metadata:s:a:1");
            arguments.Add("title=Original");
            arguments.Add("-metadata:s:a:1");
            arguments.Add($"language={request.Language}");
        }

        for (var i = 0; i < request.Subtitles.Count; i++)
        {
            arguments.Add($"-metadata:s:s:{i}");
            arguments.Add($"title={request.Subtitles[i].Title}");
            arguments.Add($"-metadata:s:s:{i}");
            arguments.Add($"language={request.Subtitles[i].Language}");
        }

        // The first subtitle track (Full, or Censored when it is the only one) is the one to show.
        for (var i = 0; i < request.Subtitles.Count; i++)
        {
            arguments.Add($"-disposition:s:{i}");
            arguments.Add(i == 0 ? "default" : "0");
        }

        if (request.AspectRatio)
        {
            arguments.Add("-aspect");
            arguments.Add($"{request.AspectRatioWidth}:{request.AspectRatioHeight}");
        }

        arguments.Add(request.OutputPath);
        return arguments;
    }

    /// <summary>60 % centre, 20 % each side: dialogue-weighted mono for difficult mixes.</summary>
    public static string CenterWeightedFilter(IReadOnlyList<string> channels)
    {
        var weights = new List<string>();
        if (channels.Contains("FC")) weights.Add("0.6*FC");
        if (channels.Contains("FL")) weights.Add("0.2*FL");
        if (channels.Contains("FR")) weights.Add("0.2*FR");

        if (weights.Count == 0) return "pan=mono|c0=0.5*FL+0.5*FR";
        return "pan=mono|c0=" + string.Join("+", weights);
    }
}
