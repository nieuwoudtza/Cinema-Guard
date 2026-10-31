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
    public static void Write(string path, int audioOrdinal, IReadOnlyList<TimeRange> ranges)
    {
        if (ranges.Count == 0) throw new ArgumentException("At least one range is required.", nameof(ranges));

        var builder = new StringBuilder();
        builder.Append($"[0:a:{audioOrdinal}]");

        foreach (var range in ranges)
        {
            builder.Append("volume=enable='between(t,")
                .Append(Format(range.Start))
                .Append(',')
                .Append(Format(range.End))
                .Append(")':volume=0,");
        }

        builder.Length--; // trailing comma
        builder.Append("[muted]");

        File.WriteAllText(path, builder.ToString());
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
        IReadOnlyDictionary<string, IReadOnlyList<TimeRange>> rangesByChannel)
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

                foreach (var range in ranges)
                {
                    builder.Append("volume=enable='between(t,")
                        .Append(MuteFilterWriter.Format(range.Start))
                        .Append(',')
                        .Append(MuteFilterWriter.Format(range.End))
                        .Append(")':volume=0,");
                }

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
        IReadOnlyDictionary<string, IReadOnlyList<TimeRange>> rangesByChannel) =>
        File.WriteAllText(path, Build(audioOrdinal, channelLayout, channels, rangesByChannel));
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
