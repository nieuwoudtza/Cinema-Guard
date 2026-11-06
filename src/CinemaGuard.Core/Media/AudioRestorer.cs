using CinemaGuard.Core.Ffmpeg;
using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Tools;

namespace CinemaGuard.Core.Media;

/// <summary>A censored film's original track (kept with "Keep the original audio") and the muted one it replaces.</summary>
public sealed record RestorePlan(MediaStreamInfo Original, int OriginalOrdinal, int? CensoredOrdinal);

public sealed record RestoreResult(bool Ok, string? OutputPath, string Message);

/// <summary>
/// Puts a censored film's original audio back: a copy of the film without the muted track, the kept
/// original first among the audio tracks and played by default. Nothing is re-encoded and the censored
/// file is left as it is.
/// </summary>
public static class AudioRestorer
{
    /// <summary>The titles <see cref="FfmpegCommands.Encode"/> gives the kept and the muted track.</summary>
    public const string OriginalTitle = "Original";

    private const string CensoredTitle = "Cinema Guard";

    public static RestorePlan? Plan(MediaProbe probe)
    {
        var audio = probe.AudioStreams.ToList();
        var original = audio.FindIndex(stream => string.Equals(stream.Title?.Trim(), OriginalTitle, StringComparison.OrdinalIgnoreCase));
        if (original < 0) return null;

        var censored = audio.FindIndex(stream => stream.Title?.Trim().StartsWith(CensoredTitle, StringComparison.OrdinalIgnoreCase) == true);
        return new RestorePlan(audio[original], original, censored < 0 ? null : censored);
    }

    public static IReadOnlyList<string> Arguments(string inputPath, RestorePlan plan, string outputPath)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats", "-i", inputPath,
            "-map", "0:v?", "-map", "0:a",
        };

        if (plan.CensoredOrdinal is { } censored)
        {
            arguments.Add("-map");
            arguments.Add($"-0:a:{censored}");
        }

        arguments.AddRange(["-map", "0:s?", "-map", "0:t?", "-map_metadata", "0", "-map_chapters", "0", "-c", "copy"]);

        // The muted track was the default; with it gone the original takes over.
        var restored = plan.CensoredOrdinal is { } muted && muted < plan.OriginalOrdinal ? plan.OriginalOrdinal - 1 : plan.OriginalOrdinal;
        arguments.AddRange([$"-disposition:a:{restored}", "default", outputPath]);
        return arguments;
    }

    /// <summary>"Film_CG.mkv" becomes "Film_Restored.mkv" beside it; "(2)" and so on when that name is taken.</summary>
    public static string OutputPath(string inputPath, string affix)
    {
        var directory = Path.GetDirectoryName(inputPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var extension = Path.GetExtension(inputPath);

        if (affix.Length > 0 && stem.EndsWith(affix, StringComparison.OrdinalIgnoreCase)) stem = stem[..^affix.Length];

        var path = Path.Combine(directory, $"{stem}_Restored{extension}");
        for (var copy = 2; File.Exists(path); copy++)
            path = Path.Combine(directory, $"{stem}_Restored ({copy}){extension}");

        return path;
    }

    public static async Task<RestoreResult> RestoreAsync(
        IProcessRunner runner, string ffmpeg, string ffprobe, string inputPath, string affix, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(inputPath);
        var probeResult = await runner.RunAsync(new ProcessSpec(ffprobe, FfmpegCommands.Probe(inputPath)), cancellationToken: cancellationToken);
        if (probeResult.Cancelled) throw new OperationCanceledException(cancellationToken);
        if (!probeResult.Success) return new RestoreResult(false, null, $"{name}: FFprobe could not read the file.");

        var probe = MediaProbeParser.Parse(probeResult.StdOut, inputPath);

        if (Plan(probe) is not { } plan)
            return new RestoreResult(false, null,
                $"{name} has no original audio track to restore. Cinema Guard only keeps one when Keep the original audio is on in Settings.");

        var output = OutputPath(inputPath, affix);
        var partial = Path.ChangeExtension(output, ".partial" + Path.GetExtension(output));
        var arguments = Arguments(inputPath, plan, partial);
        Log.Info($"Restore {name}: ffmpeg {CommandLine.Join(arguments)}");

        try
        {
            var result = await runner.RunAsync(new ProcessSpec(ffmpeg, arguments), cancellationToken: cancellationToken);
            if (result.Cancelled) throw new OperationCanceledException(cancellationToken);
            if (!result.Success || !File.Exists(partial))
                return new RestoreResult(false, null, $"{name}: FFmpeg could not write the restored copy. {LastLine(result.StdErr)}");

            // The copy must hold every audio track but the muted one.
            var check = await runner.RunAsync(new ProcessSpec(ffprobe, FfmpegCommands.Probe(partial)), cancellationToken: cancellationToken);
            var expected = probe.AudioStreams.Count() - (plan.CensoredOrdinal is null ? 0 : 1);
            if (!check.Success || MediaProbeParser.Parse(check.StdOut, partial).AudioStreams.Count() != expected)
                return new RestoreResult(false, null, $"{name}: the restored copy did not come out right, so it was removed.");

            File.Move(partial, output);
            return new RestoreResult(true, output, $"{name}: restored to {Path.GetFileName(output)}.");
        }
        finally
        {
            try
            {
                if (File.Exists(partial)) File.Delete(partial);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not delete {partial}: {ex.Message}");
            }
        }
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;
}
