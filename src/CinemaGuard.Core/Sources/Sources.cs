using CinemaGuard.Core.Ffmpeg;
using CinemaGuard.Core.Media;
using CinemaGuard.Core.Tools;

namespace CinemaGuard.Core.Sources;

public sealed record DownloadResult(bool Success, string? SubtitlePath, bool QuotaReached, string? Message);

/// <summary>Wraps subloader-cli (OpenSubtitles). Its output is read as it streams, never after WaitForExit.</summary>
public sealed class SubloaderService(IProcessRunner runner)
{
    public async Task<DownloadResult> DownloadAsync(
        string subloaderPath, string inputPath, string preferredLanguage, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(inputPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var started = DateTime.UtcNow.AddSeconds(-2);

        var result = await runner.RunAsync(
            new ProcessSpec(subloaderPath, ["file", "--path", inputPath]),
            cancellationToken: cancellationToken);

        var output = result.StdOut + "\n" + result.StdErr;
        var quota = output.Contains("quota will be renewed", StringComparison.OrdinalIgnoreCase);

        if (quota)
            return new DownloadResult(false, null, true, "OpenSubtitles' daily quota is used up; it renews tomorrow.");

        if (result.Cancelled) throw new OperationCanceledException(cancellationToken);

        if (!result.Success)
            return new DownloadResult(false, null, false, LastLine(output) ?? $"subloader-cli exited {result.ExitCode}.");

        try
        {
            var candidates = Directory.EnumerateFiles(folder, "*.srt")
                .Where(file => Path.GetFileName(file).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                .Where(file => File.GetLastWriteTimeUtc(file) >= started)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();

            if (candidates.Count == 0)
                return new DownloadResult(false, null, false, "subloader-cli finished but wrote no subtitle file.");

            var best = candidates.FirstOrDefault(file => file.Contains(preferredLanguage, StringComparison.OrdinalIgnoreCase))
                       ?? candidates[0];

            return new DownloadResult(true, best, false, null);
        }
        catch (Exception ex)
        {
            return new DownloadResult(false, null, false, ex.Message);
        }
    }

    private static string? LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? null : lines[^1];
    }
}

/// <summary>Pulls an embedded text subtitle track out of the container (ASS is converted to SRT by FFmpeg).</summary>
public sealed class EmbeddedSubtitleExtractor(IProcessRunner runner)
{
    public async Task<string?> ExtractAsync(
        string ffmpegPath,
        MediaProbe probe,
        MediaStreamInfo stream,
        string targetPath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(targetPath))
        {
            try { File.Delete(targetPath); } catch { /* overwritten below */ }
        }

        var result = await runner.RunAsync(
            new ProcessSpec(ffmpegPath, FfmpegCommands.ExtractSubtitle(probe.Path, stream.Index, targetPath)),
            cancellationToken: cancellationToken);

        if (result.Cancelled)
        {
            // FFmpeg was stopped part-way: never leave a half-written .srt beside the video.
            try { File.Delete(targetPath); } catch { /* best effort */ }
            throw new OperationCanceledException(cancellationToken);
        }

        if (!result.Success || !File.Exists(targetPath))
        {
            CinemaGuard.Core.Logging.Log.Warn(
                $"Could not extract subtitle stream #{stream.Index} from {Path.GetFileName(probe.Path)}: {result.StdErr.Trim()}");
            return null;
        }

        return targetPath;
    }
}
