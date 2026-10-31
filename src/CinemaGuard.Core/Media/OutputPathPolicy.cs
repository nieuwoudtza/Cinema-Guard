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

    public static OutputPlan Decide(string inputPath, AppSettings settings, MediaStreamInfo? audio)
    {
        var directory = Path.GetDirectoryName(inputPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var affix = AppSettings.SanitiseAffix(settings.CustomAffix);

        if (settings.SkipAlreadyProcessed && stem.EndsWith(affix, StringComparison.OrdinalIgnoreCase))
            return new OutputPlan(null, null, string.Empty, true, "already processed", null);

        var container = settings.OutputContainer;
        var audioCodec = audio?.CodecName.ToLowerInvariant();

        if (audioCodec is not null &&
            (MkvOnlyAudioCodecs.Contains(audioCodec) || (container == OutputContainer.Mp4 && !Mp4FriendlyAudio(audioCodec))))
        {
            container = OutputContainer.Mkv;
        }

        var extension = container == OutputContainer.Mp4 ? "mp4" : "mkv";
        var output = Path.Combine(directory, stem + affix + "." + extension);
        var partial = Path.Combine(directory, stem + affix + ".partial." + extension);

        if (!string.IsNullOrEmpty(directory) && PathGuard.PathsEqual(output, inputPath))
            return new OutputPlan(null, null, extension, false, null, "the output path would overwrite the input file");

        if (!string.IsNullOrEmpty(directory) && !PathGuard.IsWritableDirectory(directory))
            return new OutputPlan(null, null, extension, false, null, $"the output folder is read-only: {directory}");

        return new OutputPlan(output, partial, extension, false, null, null);
    }
}
