using System.Globalization;
using System.Text;
using CinemaGuard.Core.Intervals;
using CinemaGuard.Core.Media;

namespace CinemaGuard.Core.Ffmpeg;

/// <summary>
/// Writes the filter script that silences the muted words. It goes in a file because a film with
/// hundreds of hits produces a command line past Windows' 32,767-character limit.
/// </summary>
public static class MuteFilterWriter
{
    /// <summary>
    /// Audio is cut into frames this many samples long before a fade, because the volume filter works out its
    /// level once per frame: a decoder's frame (often 1024 samples, 21 ms) would turn a short fade into one step.
    /// </summary>
    internal const int FadeFrameSamples = 256;

    public static void Write(string path, int audioOrdinal, IReadOnlyList<TimeRange> ranges, double fadeSeconds = 0)
    {
        if (ranges.Count == 0) throw new ArgumentException("At least one range is required.", nameof(ranges));

        var builder = new StringBuilder();
        builder.Append($"[0:a:{audioOrdinal}]");
        AppendMutes(builder, ranges, fadeSeconds);
        builder.Length--; // trailing comma
        builder.Append("[muted]");

        File.WriteAllText(path, builder.ToString());
    }

    /// <summary>
    /// One volume filter per range, each followed by a comma. With a fade, the level falls from full to silent over
    /// the fade before the range and rises again over the fade after it; the range itself stays silent. The level is
    /// set per frame, so the fade out is timed by where a frame ends and the fade in by where it starts: no frame that
    /// reaches into the range is heard. A frame is <see cref="FadeFrameSamples"/> long (the volume filter's own
    /// nb_samples is not set when the level is worked out, so it cannot be used).
    /// </summary>
    internal static void AppendMutes(StringBuilder builder, IReadOnlyList<TimeRange> ranges, double fadeSeconds)
    {
        if (fadeSeconds <= 0)
        {
            foreach (var range in ranges)
            {
                builder.Append("volume=enable='between(t,")
                    .Append(Format(range.Start))
                    .Append(',')
                    .Append(Format(range.End))
                    .Append(")':volume=0,");
            }

            return;
        }

        var fade = Format(fadeSeconds);
        builder.Append("asetnsamples=n=").Append(FadeFrameSamples).Append(":p=0,");

        foreach (var range in ranges)
        {
            var start = Format(range.Start);
            var end = Format(range.End);

            builder.Append("volume=enable='between(t,")
                .Append(Format(Math.Max(0, range.Start - fadeSeconds)))
                .Append(',')
                .Append(Format(range.End + fadeSeconds))
                .Append(")':volume='min(1,max(0,max((")
                .Append(start).Append("-t-").Append(FadeFrameSamples).Append("/sample_rate)/").Append(fade)
                .Append(",(t-")
                .Append(end).Append(")/").Append(fade)
                .Append(")))':eval=frame,");
        }
    }

    internal static string Format(double seconds) => seconds.ToString("0.000", CultureInfo.InvariantCulture);
}

/// <summary>
/// The vocals-only filter: split the selected track into channels, silence each channel at its own
/// word times, join them back. One encode, no full-size channel wavs on disk.
/// </summary>
public static class VocalsGraphBuilder
{
    public static string Build(
        int audioOrdinal,
        string channelLayout,
        IReadOnlyList<string> channels,
        IReadOnlyDictionary<string, IReadOnlyList<TimeRange>> rangesByChannel,
        double fadeSeconds = 0)
    {
        if (channels.Count == 0) throw new ArgumentException("A channel layout is required.", nameof(channels));

        var builder = new StringBuilder();
        builder.Append($"[0:a:{audioOrdinal}]channelsplit=channel_layout={channelLayout}");

        foreach (var channel in channels) builder.Append($"[{channel}]");
        builder.Append(';');

        foreach (var channel in channels)
        {
            var output = channel + "m";

            if (rangesByChannel.TryGetValue(channel, out var ranges) && ranges.Count > 0)
            {
                builder.Append($"[{channel}]");
                MuteFilterWriter.AppendMutes(builder, ranges, fadeSeconds);
                builder.Length--;
                builder.Append($"[{output}];");
            }
            else
            {
                builder.Append($"[{channel}]anull[{output}];");
            }
        }

        foreach (var channel in channels) builder.Append($"[{channel}m]");

        builder.Append($"join=inputs={channels.Count}:channel_layout={channelLayout}:map=");
        builder.Append(string.Join("|", channels.Select((channel, index) => $"{index}.0-{channel}")));
        builder.Append("[muted]");

        return builder.ToString();
    }

    public static void Write(string path, int audioOrdinal, string channelLayout, IReadOnlyList<string> channels,
        IReadOnlyDictionary<string, IReadOnlyList<TimeRange>> rangesByChannel, double fadeSeconds = 0) =>
        File.WriteAllText(path, Build(audioOrdinal, channelLayout, channels, rangesByChannel, fadeSeconds));
}

/// <summary>Reads `-progress pipe:1` output.</summary>
public sealed class FfmpegProgress(double durationSeconds)
{
    public double Percent { get; private set; }
    public double Seconds { get; private set; }
    public string? Speed { get; private set; }
    public bool Completed { get; private set; }

    public void Feed(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        var separator = line.IndexOf('=');
        if (separator <= 0) return;

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();

        switch (key)
        {
            case "out_time_us":
            case "out_time_ms":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds) && microseconds >= 0)
                {
                    Seconds = microseconds / 1_000_000.0;
                    UpdatePercent();
                }
                break;

            case "out_time":
                if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var time))
                {
                    Seconds = time.TotalSeconds;
                    UpdatePercent();
                }
                break;

            case "speed":
                Speed = value;
                break;

            case "progress":
                Completed = string.Equals(value, "end", StringComparison.OrdinalIgnoreCase);
                if (Completed) Percent = 100;
                break;
        }
    }

    private void UpdatePercent()
    {
        if (durationSeconds <= 0) return;
        Percent = Math.Clamp(Seconds / durationSeconds * 100, 0, 100);
    }
}
