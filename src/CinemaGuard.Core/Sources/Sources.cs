using System.Text.RegularExpressions;
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

        if (QuotaMessage(output) is { } quota)
            return new DownloadResult(false, null, true, quota);

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

    private static readonly string[] QuotaPhrases =
    [
        "quota will be renewed", "download quota", "downloads quota", "maximum downloads", "download limit",
        "downloads limit", "allowed downloads", "too many requests",
    ];

    private static readonly Regex RenewRegex = new(@"renewed in\s+(?<when>[^.\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AllowedRegex = new(@"allowed\s+(?<count>\d+)\s+subtitles", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// OpenSubtitles' daily download limit, said plainly with when it resets (when the site says so), or
    /// null when the output is about something else. Free accounts get a handful of downloads a day; the
    /// API words it as "You have downloaded your allowed 20 subtitles for 24h. Your quota will be renewed in 5 hours".
    /// </summary>
    public static string? QuotaMessage(string output)
    {
        if (!QuotaPhrases.Any(phrase => output.Contains(phrase, StringComparison.OrdinalIgnoreCase))) return null;

        var message = AllowedRegex.Match(output) is { Success: true } allowed
            ? $"OpenSubtitles' daily download limit ({allowed.Groups["count"].Value} subtitles) is reached."
            : "OpenSubtitles' daily download limit is reached.";

        message += RenewRegex.Match(output) is { Success: true } renew
            ? $" It resets in {renew.Groups["when"].Value.Trim()}."
            : " It resets within a day.";

        return message + " Until then, use the video's own subtitles or Generate subtitles instead.";
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
