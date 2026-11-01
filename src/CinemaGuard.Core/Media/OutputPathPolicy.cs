using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Tools;

namespace CinemaGuard.Core.Media;

public sealed record OutputPlan(
    string? OutputPath,
    string? PartialPath,
    string Extension,
    bool Skip,
    string? SkipReason,
    string? Error);

/// <summary>
/// Where the output goes and which container it uses. Guards the old app's worst failure:
/// an output path that equals the input, which deleted the user's source file before encoding.
/// </summary>
public static class OutputPathPolicy
{
    private static readonly string[] MkvOnlyAudioCodecs = ["truehd", "opus", "dts", "dca", "vorbis"];

    public static bool Mp4FriendlyAudio(string? codecName) =>
        codecName?.ToLowerInvariant() is "aac" or "ac3" or "eac3" or "mp3" or "alac" or "flac" or "mp2";

    /// <summary>
    /// The format an audio-only file is written back in: its own, except raw AAC and WMA, which become M4A
    /// (an AAC stream without a container has no room for chapters or tags; FFmpeg has no good WMA encoder).
    /// </summary>
    public static string AudioFileExtension(string inputPath) => Path.GetExtension(inputPath).ToLowerInvariant() switch
    {
        ".aac" or ".wma" => ".m4a",
        var extension => extension,
    };

    /// <param name="otherAudio">The source's other audio tracks that are copied too; their codecs also decide the container.</param>
    public static OutputPlan Decide(string inputPath, AppSettings settings, MediaStreamInfo? audio, IEnumerable<MediaStreamInfo>? otherAudio = null)
    {
        var directory = Path.GetDirectoryName(inputPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var affix = AppSettings.SanitiseAffix(settings.CustomAffix);

        if (settings.SkipAlreadyProcessed && stem.EndsWith(affix, StringComparison.OrdinalIgnoreCase))
            return new OutputPlan(null, null, string.Empty, true, "already processed", null);

        string extension;

        if (FileTypes.IsAudioFile(inputPath))
        {
            extension = AudioFileExtension(inputPath).TrimStart('.');
        }
        else
        {
            var container = settings.OutputContainer;
            var codecs = new[] { audio }.Concat(otherAudio ?? [])
                .Where(stream => stream is not null)
                .Select(stream => stream!.CodecName.ToLowerInvariant());

            if (codecs.Any(codec => MkvOnlyAudioCodecs.Contains(codec) || (container == OutputContainer.Mp4 && !Mp4FriendlyAudio(codec))))
                container = OutputContainer.Mkv;

            extension = container == OutputContainer.Mp4 ? "mp4" : "mkv";
        }

        var output = Path.Combine(directory, stem + affix + "." + extension);
        var partial = Path.Combine(directory, stem + affix + ".partial." + extension);

        if (!string.IsNullOrEmpty(directory) && PathGuard.PathsEqual(output, inputPath))
            return new OutputPlan(null, null, extension, false, null, "the output path would overwrite the input file");

        if (!string.IsNullOrEmpty(directory) && !PathGuard.IsWritableDirectory(directory))
            return new OutputPlan(null, null, extension, false, null, $"the output folder is read-only: {directory}");

        return new OutputPlan(output, partial, extension, false, null, null);
    }
}
