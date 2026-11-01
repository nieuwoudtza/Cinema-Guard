using System.Globalization;
using CinemaGuard.Core.Intervals;
using CinemaGuard.Core.Media;
using CinemaGuard.Core.Settings;

namespace CinemaGuard.Core.Ffmpeg;

/// <summary>A subtitle file to embed. <paramref name="Forced"/>: the source's forced track, masked, shown only for foreign lines.</summary>
public sealed record SubtitleInput(string Path, string Title, string Language, bool Forced = false);

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

    /// <summary>The source's other audio tracks, copied unchanged after the muted one (and the original).</summary>
    public IReadOnlyList<MediaStreamInfo> ExtraAudio { get; init; } = [];

    /// <summary>The source's subtitle tracks copied unchanged, after the ones in <see cref="Subtitles"/>.</summary>
    public IReadOnlyList<MediaStreamInfo> ExtraSubtitles { get; init; } = [];

    /// <summary>The output extension of an audio-only file (".mp3", ".m4b"…); null for a video.</summary>
    public string? AudioFileExtension { get; init; }

    public bool IsAudioFile => AudioFileExtension is not null;

    private bool HasRanges => Ranges.Count > 0 && !string.IsNullOrWhiteSpace(FilterScriptPath);

    /// <summary>The original is only added beside a muted track, and never to an audio file (an MP3 holds one).</summary>
    public bool AddsOriginalAudio => HasRanges && KeepOriginalAudio && !IsAudioFile;

    public int ExpectedAudioStreams => 1 + (AddsOriginalAudio ? 1 : 0) + (IsAudioFile ? 0 : ExtraAudio.Count);

    public int ExpectedSubtitleStreams => IsAudioFile ? 0 : Subtitles.Count + ExtraSubtitles.Count;
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
            "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats", "-i", inputPath,
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
        "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats", "-i", inputPath,
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

    /// <summary>
    /// Whether a source subtitle track can be copied into the container: MKV takes every kind, MP4 only
    /// text (converted to mov_text). Image tracks (PGS, VobSub) cannot go into an MP4.
    /// </summary>
    public static bool CanKeepSubtitle(MediaStreamInfo stream, OutputContainer container) =>
        stream.IsSubtitle && (container == OutputContainer.Mkv || stream.IsTextSubtitle);

    /// <summary>Audio formats whose muxer stores cover art (an attached picture).</summary>
    public static bool KeepsCoverArt(string audioFileExtension) =>
        audioFileExtension.ToLowerInvariant() is ".mp3" or ".m4a" or ".m4b" or ".flac";

    public static IReadOnlyList<string> Encode(EncodeRequest request)
    {
        var hasRanges = request.Ranges.Count > 0 && !string.IsNullOrWhiteSpace(request.FilterScriptPath);
        var audioFile = request.IsAudioFile;
        var keepOriginal = request.AddsOriginalAudio;
        var subtitles = audioFile ? [] : request.Subtitles;
        var extraAudio = audioFile ? [] : request.ExtraAudio;
        var extraSubtitles = audioFile ? [] : request.ExtraSubtitles;

        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats",
            "-i", request.InputPath,
        };

        foreach (var subtitle in subtitles)
        {
            arguments.Add("-i");
            arguments.Add(subtitle.Path);
        }

        if (hasRanges)
        {
            arguments.Add("-/filter_complex");
            arguments.Add(request.FilterScriptPath!);
        }

        // An audio file has no film to copy; its cover art goes along where the format can hold it.
        var cover = audioFile && KeepsCoverArt(request.AudioFileExtension!) ? request.Probe.CoverArt : null;

        if (!audioFile)
        {
            arguments.Add("-map");
            arguments.Add("0:v:0");
        }
        else if (cover is not null)
        {
            arguments.Add("-map");
            arguments.Add($"0:{cover.Index}");
        }

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

        if (keepOriginal)
        {
            arguments.Add("-map");
            arguments.Add($"0:a:{request.Audio.Ordinal}");
        }

        foreach (var stream in extraAudio)
        {
            arguments.Add("-map");
            arguments.Add($"0:{stream.Index}");
        }

        for (var i = 0; i < subtitles.Count; i++)
        {
            arguments.Add("-map");
            arguments.Add($"{i + 1}:0");
        }

        foreach (var stream in extraSubtitles)
        {
            arguments.Add("-map");
            arguments.Add($"0:{stream.Index}");
        }

        // The fonts an ASS track needs travel with it (MKV only; MP4 has no attachments).
        if (extraSubtitles.Count > 0 && request.Container == OutputContainer.Mkv)
        {
            arguments.Add("-map");
            arguments.Add("0:t?");
        }

        arguments.Add("-map_metadata");
        arguments.Add("0");
        arguments.Add("-map_chapters");
        arguments.Add("0");
        arguments.Add("-c:v");
        arguments.Add("copy");

        if (hasRanges)
        {
            var plan = audioFile
                ? AudioCodecPolicy.PlanAudioFile(request.Audio.Stream, request.AudioFileExtension!)
                : AudioCodecPolicy.Plan(request.Audio.Stream, request.ForceSameAudioEncoding, request.Container);
            arguments.AddRange(plan.Arguments);

            if (keepOriginal)
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

        var firstExtraAudio = keepOriginal ? 2 : 1;

        for (var i = 0; i < extraAudio.Count; i++)
        {
            arguments.Add($"-c:a:{firstExtraAudio + i}");
            arguments.Add("copy");
        }

        // Each subtitle track gets its own codec: the written files become the container's text format,
        // the source's tracks are copied (MP4 can only take text, as mov_text).
        for (var i = 0; i < subtitles.Count; i++)
        {
            arguments.Add($"-c:s:{i}");
            arguments.Add(request.Container == OutputContainer.Mp4 ? "mov_text" : "srt");
        }

        for (var i = 0; i < extraSubtitles.Count; i++)
        {
            arguments.Add($"-c:s:{subtitles.Count + i}");
            arguments.Add(request.Container == OutputContainer.Mp4 ? "mov_text" : "copy");
        }

        if (extraSubtitles.Count > 0 && request.Container == OutputContainer.Mkv)
        {
            arguments.Add("-c:t");
            arguments.Add("copy");
        }

        if (cover is not null)
        {
            arguments.Add("-disposition:v:0");
            arguments.Add("attached_pic");
        }

        arguments.Add("-disposition:a:0");
        arguments.Add("default");

        if (keepOriginal)
        {
            arguments.Add("-disposition:a:1");
            arguments.Add("0");
        }

        // The copied tracks keep their own flags (commentary, language) but never take the default from
        // the censored track, or a player would start with uncensored audio.
        for (var i = 0; i < extraAudio.Count; i++)
        {
            arguments.Add($"-disposition:a:{firstExtraAudio + i}");
            arguments.Add("-default");
        }

        // An audio file's only stream holds its tags in some formats (Ogg): naming the track would
        // overwrite the chapter's title.
        // MP4 stores a title as the track's "name"; some players only show its handler name, so that
        // carries the title too (otherwise Full and Censored both read "SubtitleHandler").
        var mp4 = request.Container == OutputContainer.Mp4;

        void Title(string specifier, string title)
        {
            arguments.Add($"-metadata:s:{specifier}");
            arguments.Add($"title={title}");

            if (!mp4) return;
            arguments.Add($"-metadata:s:{specifier}");
            arguments.Add($"handler_name={title}");
        }

        if (!audioFile)
        {
            Title("a:0", request.AudioTitle);
            arguments.Add("-metadata:s:a:0");
            arguments.Add($"language={request.Language}");
        }

        if (keepOriginal)
        {
            Title("a:1", "Original");
            arguments.Add("-metadata:s:a:1");
            arguments.Add($"language={request.Language}");
        }

        for (var i = 0; i < subtitles.Count; i++)
        {
            Title($"s:{i}", subtitles[i].Title);
            arguments.Add($"-metadata:s:s:{i}");
            arguments.Add($"language={subtitles[i].Language}");
        }

        // The first full track (Full, or Censored when it is the only one) is the one to show. A forced
        // track is flagged forced, so players show it by themselves for the foreign lines.
        var defaultSubtitle = -1;
        for (var i = 0; i < subtitles.Count; i++)
        {
            if (!subtitles[i].Forced)
            {
                defaultSubtitle = i;
                break;
            }
        }

        for (var i = 0; i < subtitles.Count; i++)
        {
            arguments.Add($"-disposition:s:{i}");
            arguments.Add(subtitles[i].Forced ? "forced" : i == defaultSubtitle ? "default" : "0");
        }

        for (var i = 0; i < extraSubtitles.Count; i++)
        {
            arguments.Add($"-disposition:s:{subtitles.Count + i}");
            arguments.Add("-default");
        }

        if (request.AspectRatio && !audioFile)
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
