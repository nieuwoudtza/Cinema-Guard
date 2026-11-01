using System.Globalization;
using System.Text.Json;

namespace CinemaGuard.Core.Media;

public sealed class MediaStreamInfo
{
    public int Index { get; init; }
    public string CodecType { get; init; } = string.Empty;
    public string CodecName { get; init; } = string.Empty;
    public string? Profile { get; init; }
    public int? Channels { get; init; }
    public string? ChannelLayout { get; init; }
    public int? SampleRate { get; init; }
    public long? BitRate { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? StartTime { get; init; }
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }
    public bool IsComment { get; init; }
    public bool IsHearingImpaired { get; init; }
    public bool IsVisualImpaired { get; init; }

    /// <summary>Cover art in an audio file (or an MP4): a still picture stored as a video stream.</summary>
    public bool IsAttachedPicture { get; init; }

    public string? Language { get; init; }
    public string? Title { get; init; }

    public bool IsAudio => string.Equals(CodecType, "audio", StringComparison.OrdinalIgnoreCase);
    public bool IsVideo => string.Equals(CodecType, "video", StringComparison.OrdinalIgnoreCase);
    public bool IsSubtitle => string.Equals(CodecType, "subtitle", StringComparison.OrdinalIgnoreCase);

    public bool IsTextSubtitle => IsSubtitle && CodecName.ToLowerInvariant() is
        "subrip" or "srt" or "mov_text" or "ass" or "ssa" or "webvtt" or "text" or "sami" or "microdvd" or "subviewer" or "subviewer1" or "jacosub" or "realtext";

    public bool IsBitmapSubtitle => IsSubtitle && !IsTextSubtitle;

    public string Display =>
        $"{CodecType} #{Index} {CodecName}" +
        (Channels is not null ? $" {Channels}ch" : string.Empty) +
        (string.IsNullOrWhiteSpace(Language) ? string.Empty : $" [{Language}]") +
        (string.IsNullOrWhiteSpace(Title) ? string.Empty : $" \"{Title}\"") +
        (IsForced ? " (forced)" : string.Empty);
}

public sealed class MediaProbe
{
    public string Path { get; init; } = string.Empty;
    public string FormatName { get; init; } = string.Empty;
    public double DurationSeconds { get; init; }
    public double StartSeconds { get; init; }
    public IReadOnlyList<MediaStreamInfo> Streams { get; init; } = [];

    public IEnumerable<MediaStreamInfo> AudioStreams => Streams.Where(s => s.IsAudio);
    public IEnumerable<MediaStreamInfo> VideoStreams => Streams.Where(s => s.IsVideo);
    public IEnumerable<MediaStreamInfo> SubtitleStreams => Streams.Where(s => s.IsSubtitle);

    /// <summary>The cover art of an audio file, if it has one.</summary>
    public MediaStreamInfo? CoverArt => VideoStreams.FirstOrDefault(s => s.IsAttachedPicture);

    public int AudioOrdinal(MediaStreamInfo stream) =>
        AudioStreams.TakeWhile(s => s.Index != stream.Index).Count();

    public IReadOnlyList<string> Summary()
    {
        var lines = new List<string>
        {
            $"format {FormatName}, {DurationSeconds:0.0} s",
        };
        lines.AddRange(Streams.Select(s => s.Display));
        return lines;
    }
}

/// <summary>Reads `ffprobe -print_format json` output. No text scraping of ffmpeg's human output.</summary>
public static class MediaProbeParser
{
    public static MediaProbe Parse(string json, string path)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var format = root.TryGetProperty("format", out var f) ? f : default;
        var streams = new List<MediaStreamInfo>();

        if (root.TryGetProperty("streams", out var streamArray) && streamArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streamArray.EnumerateArray())
                streams.Add(ParseStream(stream));
        }

        return new MediaProbe
        {
            Path = path,
            FormatName = GetString(format, "format_name") ?? string.Empty,
            DurationSeconds = GetDouble(format, "duration") ?? 0,
            StartSeconds = GetDouble(format, "start_time") ?? 0,
            Streams = streams,
        };
    }

    private static MediaStreamInfo ParseStream(JsonElement stream)
    {
        var disposition = stream.TryGetProperty("disposition", out var d) ? d : default;
        var tags = stream.TryGetProperty("tags", out var t) ? t : default;

        return new MediaStreamInfo
        {
            Index = GetInt(stream, "index") ?? 0,
            CodecType = GetString(stream, "codec_type") ?? string.Empty,
            CodecName = GetString(stream, "codec_name") ?? string.Empty,
            Profile = GetString(stream, "profile"),
            Channels = GetInt(stream, "channels"),
            ChannelLayout = GetString(stream, "channel_layout"),
            SampleRate = GetInt(stream, "sample_rate"),
            BitRate = GetLong(stream, "bit_rate"),
            Width = GetInt(stream, "width"),
            Height = GetInt(stream, "height"),
            StartTime = GetDouble(stream, "start_time"),
            IsDefault = GetInt(disposition, "default") == 1,
            IsForced = GetInt(disposition, "forced") == 1,
            IsComment = GetInt(disposition, "comment") == 1,
            IsHearingImpaired = GetInt(disposition, "hearing_impaired") == 1,
            IsVisualImpaired = GetInt(disposition, "visual_impaired") == 1,
            IsAttachedPicture = GetInt(disposition, "attached_pic") == 1,
            Language = NormaliseLanguage(GetString(tags, "language")),
            // MP4 keeps a track's title as "name".
            Title = GetString(tags, "title") ?? GetString(tags, "name"),
        };
    }

    private static string? NormaliseLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        var value = language.Trim().ToLowerInvariant();
        return value is "und" or "unknown" or "" ? null : value;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    private static int? GetInt(JsonElement element, string name)
    {
        var text = GetString(element, name);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static long? GetLong(JsonElement element, string name)
    {
        var text = GetString(element, name);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static double? GetDouble(JsonElement element, string name)
    {
        var text = GetString(element, name);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
