using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Speech;

namespace CinemaGuard.Core.Tools;

/// <summary>One step of a tool install: bytes so far, total bytes (0 when the server did not say), and a status line.</summary>
public sealed record ToolProgress(long Done, long Total, string Status)
{
    public double Percent => Total > 0 ? Math.Clamp(Done * 100.0 / Total, 0, 100) : 0;
}

/// <summary>
/// Fetches the FFmpeg build Cinema Guard needs into %LOCALAPPDATA%\CinemaGuard\tools\ffmpeg.
/// The user never has to find, unzip or configure FFmpeg: one button on the Tools page.
/// </summary>
public static class ToolDownloader
{
    /// <summary>Gyan's "essentials" build: ffmpeg, ffprobe and the common codecs, about 90 MB.</summary>
    public const string FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static string Root => Path.Combine(SpeechPaths.Root, "tools", "ffmpeg");

    public static string FfmpegPath => Path.Combine(Root, "ffmpeg.exe");
    public static string FfprobePath => Path.Combine(Root, "ffprobe.exe");

    public static bool IsInstalled => File.Exists(FfmpegPath) && File.Exists(FfprobePath);

    /// <summary>The size the server reports, for a real percentage. 0 when it cannot be read.</summary>
    public static async Task<long> GetDownloadSizeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, FfmpegUrl);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode ? response.Content.Headers.ContentLength ?? 0 : 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the FFmpeg download size: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Gyan publishes the archive's SHA-256 next to it; the "release" URL moves with each FFmpeg release.</summary>
    public const string FfmpegSha256Url = FfmpegUrl + ".sha256";

    private static readonly SemaphoreSlim InstallLock = new(1, 1);

    /// <summary>
    /// Downloads the archive (resumable), unpacks only ffmpeg.exe and ffprobe.exe into a staging
    /// folder, and only then swaps it in, so a half-installed FFmpeg never looks usable.
    /// One install at a time: a second caller (the page was left and reopened) waits, then finds it done.
    /// </summary>
    public static async Task InstallAsync(IProgress<ToolProgress>? progress, CancellationToken cancellationToken)
    {
        await InstallLock.WaitAsync(cancellationToken);
        try
        {
            if (IsInstalled)
            {
                progress?.Report(new ToolProgress(1, 1, "FFmpeg installed"));
                return;
            }

            await InstallCoreAsync(progress, cancellationToken);
        }
        finally
        {
            InstallLock.Release();
        }
    }

    private static async Task InstallCoreAsync(IProgress<ToolProgress>? progress, CancellationToken cancellationToken)
    {
        var downloads = Path.Combine(Path.GetDirectoryName(Root)!, "downloads");
        Directory.CreateDirectory(downloads);

        var archive = Path.Combine(downloads, "ffmpeg-release-essentials.zip");
        var total = await GetDownloadSizeAsync(cancellationToken);
        var sha256 = await GetSha256Async(cancellationToken);

        // A finished archive from an earlier attempt is only reused when its size and hash still match
        // the server's current release; otherwise it is stale (a new FFmpeg came out) or damaged.
        if (File.Exists(archive) && (total <= 0 || sha256 is null || new FileInfo(archive).Length != total ||
                                     !string.Equals(await Downloader.HashAsync(archive, cancellationToken), sha256, StringComparison.OrdinalIgnoreCase)))
            TryDeleteFile(archive);

        progress?.Report(new ToolProgress(0, total, "Downloading FFmpeg"));
        await Downloader.DownloadAsync(FfmpegUrl, archive, total, sha256, bytes =>
        {
            var status = total > 0
                ? $"Downloading FFmpeg: {bytes / 1_000_000.0:0} of {total / 1_000_000.0:0} MB"
                : $"Downloading FFmpeg: {bytes / 1_000_000.0:0} MB";
            progress?.Report(new ToolProgress(bytes, total, status));
        }, cancellationToken);

        progress?.Report(new ToolProgress(0, 0, "Unpacking FFmpeg"));

        var staging = Root + ".partial";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        var source = new PackSource(FfmpegUrl, 0, null, PackFormat.Zip, Names: ["ffmpeg.exe", "ffprobe.exe"]);
        int extracted;
        try
        {
            extracted = Downloader.Extract(archive, source, staging, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A damaged archive must not be resumed or reused by the next attempt.
            TryDeleteDirectory(staging);
            TryDeleteFile(archive);
            throw new DownloadException($"The FFmpeg download could not be unpacked ({ex.Message}). Please try again.", ex);
        }

        if (extracted < 2 || !File.Exists(Path.Combine(staging, "ffmpeg.exe")) || !File.Exists(Path.Combine(staging, "ffprobe.exe")))
        {
            TryDeleteDirectory(staging);
            TryDeleteFile(archive);
            throw new DownloadException("The FFmpeg download did not contain ffmpeg.exe and ffprobe.exe. Please try again.");
        }

        TryDeleteDirectory(Root);
        if (Directory.Exists(Root))
        {
            TryDeleteDirectory(staging);
            throw new DownloadException("The old FFmpeg folder is in use. Wait for the queue to finish, then try again.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Root)!);
        Directory.Move(staging, Root);
        TryDeleteFile(archive);

        Log.Info($"FFmpeg installed to {Root}.");
        progress?.Report(new ToolProgress(1, 1, "FFmpeg installed"));
    }

    /// <summary>The published SHA-256 of the current archive, or null when it cannot be read (then size only).</summary>
    public static async Task<string?> GetSha256Async(CancellationToken cancellationToken)
    {
        try
        {
            var text = await Http.GetStringAsync(FfmpegSha256Url, cancellationToken);
            return ParseSha256(text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Could not read the FFmpeg checksum: {ex.Message}");
            return null;
        }
    }

    /// <summary>"&lt;hash&gt;" or "&lt;hash&gt;  file.zip" → the hash; anything else → null.</summary>
    public static string? ParseSha256(string? text)
    {
        var hash = text?.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return hash is { Length: 64 } && hash.All(Uri.IsHexDigit) ? hash.ToLowerInvariant() : null;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not delete {path}: {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not delete {path}: {ex.Message}");
        }
    }
}
