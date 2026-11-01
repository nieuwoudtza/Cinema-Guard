using CinemaGuard.Core.Media;
using CinemaGuard.Core.Tools;

namespace CinemaGuard.Core.Ffmpeg;

public sealed record ValidationResult(bool Ok, string? Error, bool Cancelled);

/// <summary>
/// Proof the encode actually produced a usable file, before anything is renamed or deleted.
/// The old app deleted originals when the output merely exceeded 1 KB, even when FFmpeg had failed.
/// </summary>
public static class OutputValidator
{
    public static async Task<ValidationResult> ValidateAsync(
        IProcessRunner runner,
        string ffprobePath,
        string outputPath,
        MediaProbe input,
        int expectedAudioStreams,
        int expectedSubtitleStreams,
        CancellationToken cancellationToken,
        bool expectVideo = true)
    {
        var info = new FileInfo(outputPath);
        if (!info.Exists) return new ValidationResult(false, "the file was not written", false);

        // An audio file can be small (a short podcast at 64 kb/s); the duration check below still applies.
        var minimum = expectVideo ? 1024 * 1024 : 16 * 1024;
        if (info.Length < minimum)
            return new ValidationResult(false, expectVideo ? "the file is smaller than 1 MB" : "the file is smaller than 16 KB", false);

        var probe = await runner.RunAsync(new ProcessSpec(ffprobePath, FfmpegCommands.Probe(outputPath)),
            cancellationToken: cancellationToken);

        if (probe.Cancelled) return new ValidationResult(false, "cancelled", true);
        if (!probe.Success || string.IsNullOrWhiteSpace(probe.StdOut))
            return new ValidationResult(false, "the output file cannot be read", false);

        MediaProbe parsed;

        try
        {
            parsed = MediaProbeParser.Parse(probe.StdOut, outputPath);
        }
        catch (Exception ex)
        {
            return new ValidationResult(false, ex.Message, false);
        }

        if (expectVideo && !parsed.VideoStreams.Any())
            return new ValidationResult(false, "the output has no video stream", false);

        var audioCount = parsed.AudioStreams.Count();
        if (audioCount != expectedAudioStreams)
            return new ValidationResult(false, $"expected {expectedAudioStreams} audio stream(s), found {audioCount}", false);

        var subtitleCount = parsed.SubtitleStreams.Count();
        if (subtitleCount != expectedSubtitleStreams)
            return new ValidationResult(false, $"expected {expectedSubtitleStreams} subtitle stream(s), found {subtitleCount}", false);

        if (input.DurationSeconds > 0)
        {
            var tolerance = Math.Max(1.0, input.DurationSeconds * 0.005);
            var difference = Math.Abs(parsed.DurationSeconds - input.DurationSeconds);

            if (difference > tolerance)
                return new ValidationResult(false, $"the duration is off by {difference:0.0} s (limit {tolerance:0.0} s)", false);
        }

        return new ValidationResult(true, null, false);
    }
}
