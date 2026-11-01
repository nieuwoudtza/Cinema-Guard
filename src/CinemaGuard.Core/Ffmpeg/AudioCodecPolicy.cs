using System.Globalization;
using CinemaGuard.Core.Media;
using CinemaGuard.Core.Settings;

namespace CinemaGuard.Core.Ffmpeg;

public sealed record AudioPlan(IReadOnlyList<string> Arguments, string Encoder, bool Lossless);

/// <summary>
/// Which encoder the muted track gets. Follows the old app's ForceSameAudioEncoding behaviour,
/// with the 7.1 cases fixed (TrueHD and E-AC3 cannot encode 7.1, so they become FLAC).
/// </summary>
public static class AudioCodecPolicy
{
    public static AudioPlan Plan(MediaStreamInfo audio, bool forceSameEncoding, OutputContainer container)
    {
        var channels = audio.Channels ?? 2;
        var sourceCodec = audio.CodecName.ToLowerInvariant();
        var codec = forceSameEncoding ? sourceCodec : "aac";

        string encoder;
        var lossless = false;

        switch (codec)
        {
            case "truehd":
                if (channels > 6) { encoder = "flac"; lossless = true; }
                else encoder = "truehd";
                break;
            case "dts":
            case "dca":
                encoder = "dca";
                break;
            case "eac3":
                if (channels > 6) { encoder = "flac"; lossless = true; }
                else encoder = "eac3";
                break;
            case "ac3":
                encoder = "ac3";
                break;
            case "opus":
                encoder = "libopus";
                break;
            case "flac":
                encoder = "flac";
                lossless = true;
                break;
            case "alac":
                encoder = "alac";
                lossless = true;
                break;
            case "mp3":
                encoder = "libmp3lame";
                break;
            case "pcm_s16le":
            case "pcm_s24le":
            case "pcm_s32le":
                encoder = container == OutputContainer.Mkv ? "flac" : "pcm_s16le";
                lossless = true;
                break;
            default:
                encoder = "aac";
                break;
        }

        var arguments = new List<string> { "-c:a:0", encoder };

        if (encoder is "truehd" or "dca" or "flac" && container == OutputContainer.Mp4)
        {
            arguments.Add("-strict");
            arguments.Add("-2");
        }

        arguments.Add("-ac:a:0");
        arguments.Add(channels.ToString(CultureInfo.InvariantCulture));

        if (!lossless)
        {
            var bitrate = Bitrate(audio, encoder, channels);
            if (bitrate > 0)
            {
                arguments.Add("-b:a:0");
                arguments.Add(bitrate.ToString(CultureInfo.InvariantCulture) + "k");
            }
        }

        return new AudioPlan(arguments, encoder, lossless);
    }

    /// <summary>
    /// The encoder for an audio-only file, decided by the format it is written back in (see
    /// <see cref="Media.OutputPathPolicy.AudioFileExtension"/>): an MP3 stays MP3, an audiobook stays AAC.
    /// </summary>
    public static AudioPlan PlanAudioFile(MediaStreamInfo audio, string extension)
    {
        var channels = audio.Channels ?? 2;
        var sourceCodec = audio.CodecName.ToLowerInvariant();

        var encoder = extension.ToLowerInvariant() switch
        {
            ".mp3" => "libmp3lame",
            ".m4a" or ".m4b" => sourceCodec == "alac" ? "alac" : "aac",
            ".flac" => "flac",
            ".wav" => sourceCodec.StartsWith("pcm_", StringComparison.Ordinal) ? sourceCodec : "pcm_s16le",
            ".ogg" => sourceCodec == "opus" ? "libopus" : "libvorbis",
            ".opus" => "libopus",
            _ => "aac",
        };

        var lossless = encoder is "alac" or "flac" || encoder.StartsWith("pcm_", StringComparison.Ordinal);
        var arguments = new List<string> { "-c:a:0", encoder, "-ac:a:0", channels.ToString(CultureInfo.InvariantCulture) };

        if (!lossless)
        {
            // Audiobooks are often 64 kb/s mono: keep the source's rate rather than the film defaults.
            var bitrate = audio.BitRate is >= 32_000 and var source
                ? (int)Math.Min(source / 1000, 1536)
                : Bitrate(audio, encoder, channels);

            if (bitrate > 0)
            {
                arguments.Add("-b:a:0");
                arguments.Add(bitrate.ToString(CultureInfo.InvariantCulture) + "k");
            }
        }

        return new AudioPlan(arguments, encoder, lossless);
    }

    private static int Bitrate(MediaStreamInfo audio, string encoder, int channels)
    {
        if (audio.BitRate is > 0 and var source)
        {
            var kbps = (int)(source / 1000);
            if (kbps is >= 96 and <= 1536) return kbps;
        }

        return encoder switch
        {
            "aac" => Math.Clamp(64 * channels, 128, 512),
            "ac3" => channels >= 6 ? 640 : 448,
            "eac3" => channels >= 6 ? 640 : 448,
            "libopus" => Math.Clamp(96 * channels, 128, 512),
            "libvorbis" => Math.Clamp(96 * channels, 128, 512),
            "libmp3lame" => 320,
            _ => 0,
        };
    }
}
