using CinemaGuard.Core.Intervals;
using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Media;
using CinemaGuard.Core.Phonon;

namespace CinemaGuard.Core.Pipeline;

public enum MediaStatus { Waiting, Running, Succeeded, Skipped, Failed, Cancelled }

/// <summary>One queued file and everything the pipeline learns about it.</summary>
public sealed class MediaItem
{
    public MediaItem(string inputPath)
    {
        InputPath = Path.GetFullPath(inputPath);
        Stem = Path.GetFileNameWithoutExtension(InputPath);
        Folder = Path.GetDirectoryName(InputPath) ?? string.Empty;
    }

    public string InputPath { get; }
    public string Stem { get; }
    public string Folder { get; }

    public string SidecarPath => Path.ChangeExtension(InputPath, ".srt");

    /// <summary>The legacy one-word subtitles: written beside the video only when the user asks for them (Settings).</summary>
    public string OneWordPath => Path.ChangeExtension(InputPath, ".oneword.srt");

    /// <summary>
    /// Where this file's transcripts, generated subtitles and language report live: Cinema Guard's own
    /// folder, one subfolder per video (by its full path), so nothing is left beside the video.
    /// </summary>
    public string CacheFolder => Path.Combine(CacheRoot, CacheKey(InputPath));

    public string GeneratedPath => Path.Combine(CacheFolder, "generated.srt");
    public string EnglishCachePath => Path.Combine(CacheFolder, "phonon-en.json");
    public string MultilingualCachePath => Path.Combine(CacheFolder, "phonon-ml.json");

    /// <summary>The transcript of any model other than the two Phonon 2.5 ones (keyed on the model inside).</summary>
    public string TranscriptCachePath => Path.Combine(CacheFolder, "transcript.json");

    /// <summary>Phonon 2.5 keeps its original cache names, so files done before the Models page are not redone.</summary>
    public string CachePathFor(string cacheKey) => cacheKey switch
    {
        Speech.SpeechCatalog.PhononEnglishId => EnglishCachePath,
        Speech.SpeechCatalog.PhononMultilingualId => MultilingualCachePath,
        _ => TranscriptCachePath,
    };

    public bool HasAnyTranscriptCache =>
        File.Exists(EnglishCachePath) || File.Exists(MultilingualCachePath) || File.Exists(TranscriptCachePath);
    public string LanguageReportPath => Path.Combine(CacheFolder, "languages.json");

    public static string CacheRoot { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinemaGuard", "cache");

    /// <summary>"Film name-1a2b3c4d5e6f7a8b": readable, and unique per full path.</summary>
    internal static string CacheKey(string inputPath)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(inputPath.ToUpperInvariant())))[..16].ToLowerInvariant();

        var stem = Path.GetFileNameWithoutExtension(inputPath);
        if (stem.Length > 40) stem = stem[..40];
        return $"{stem.TrimEnd(' ', '.')}-{hash}";
    }

    /// <summary>Creates the cache folder before something is written to it.</summary>
    public void EnsureCacheFolder() => Directory.CreateDirectory(CacheFolder);

    /// <summary>
    /// Earlier versions left the transcripts and generated subtitles beside the video. They are moved
    /// into the cache folder (still reused, no longer cluttering the folder). Never throws.
    /// </summary>
    public void MoveLegacyFiles()
    {
        (string Legacy, string Cache)[] moves =
        [
            (Path.ChangeExtension(InputPath, ".generated.srt"), GeneratedPath),
            (Path.ChangeExtension(InputPath, ".phonon-en.json"), EnglishCachePath),
            (Path.ChangeExtension(InputPath, ".phonon-ml.json"), MultilingualCachePath),
            (Path.ChangeExtension(InputPath, ".transcript.json"), TranscriptCachePath),
            (Path.ChangeExtension(InputPath, ".languages.json"), LanguageReportPath),
        ];

        foreach (var (legacy, cache) in moves)
        {
            try
            {
                if (!File.Exists(legacy)) continue;

                EnsureCacheFolder();
                if (File.Exists(cache) && File.GetLastWriteTimeUtc(cache) >= File.GetLastWriteTimeUtc(legacy)) File.Delete(legacy);
                else File.Move(legacy, cache, overwrite: true);

                Log.Info($"Moved {Path.GetFileName(legacy)} into Cinema Guard's cache.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not move {legacy} into the cache: {ex.Message}");
            }
        }
    }

    /// <summary>Removes this file's transcripts and generated subtitles. Never throws.</summary>
    public void DeleteCache()
    {
        try
        {
            if (Directory.Exists(CacheFolder)) Directory.Delete(CacheFolder, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not delete the cache for {Stem}: {ex.Message}");
        }
    }

    /// <summary>Removes cache folders not used for <paramref name="maxAge"/> (a run touches its folder). Never throws.</summary>
    public static void CleanupCache(TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(CacheRoot)) return;
            var cutoff = DateTime.UtcNow - maxAge;

            foreach (var directory in Directory.EnumerateDirectories(CacheRoot))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) >= cutoff) continue;
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not delete the old cache {directory}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not scan the cache: {ex.Message}");
        }
    }

    /// <summary>Marks the cache as used, so the 30-day clean-up keeps it.</summary>
    public void TouchCache()
    {
        try
        {
            if (Directory.Exists(CacheFolder)) Directory.SetLastWriteTimeUtc(CacheFolder, DateTime.UtcNow);
        }
        catch (Exception)
        {
            // Only affects when it is cleaned up.
        }
    }

    public string? CustomSubtitlePath { get; set; }
    public bool GenerateSubtitles { get; set; } = true;

    /// <summary>Read the subtitle track inside the video and use it to find words (no file is written beside it).</summary>
    public bool ExtractSubtitles { get; set; } = true;
    public string SubtitleSource { get; set; } = "None";

    /// <summary>The subtitle track inside the video chosen for this file (its stream index); null lets the preferred language pick.</summary>
    public int? SubtitleStreamIndex { get; set; }

    /// <summary>Which subtitles exactly, in words ("track 3 inside the video: eng, SubRip, “SDH”"); shown on the badge's tooltip.</summary>
    public string? SubtitleDetail { get; set; }

    /// <summary>What the last run worked from, a line each: subtitles, speech model, word matching, audio. Shown in Peek.</summary>
    public List<string> RunDetails { get; set; } = [];

    /// <summary>
    /// The subtitles a run will find beside the film, in the order the pipeline looks for them, so the
    /// queue badge is right before the first run. A track inside the video is only known after the probe.
    /// </summary>
    public string SubtitleSourceOnDisk()
    {
        if (!string.IsNullOrWhiteSpace(CustomSubtitlePath) && File.Exists(CustomSubtitlePath)) return "Selected file";
        if (File.Exists(SidecarPath)) return SubtitleFileSource;
        if (File.Exists(GeneratedPath)) return "Generated";
        if (File.Exists(OneWordPath)) return "One-word subtitles";
        return "None";
    }

    /// <summary>The badge for &lt;name&gt;.srt beside the film.</summary>
    public const string SubtitleFileSource = "Subtitle file";

    public MediaStatus Status { get; set; } = MediaStatus.Waiting;
    public string StatusText { get; set; } = "Waiting";
    public double Progress { get; set; }

    /// <summary>The seconds this file is expected to take, which weighs it in the queue's overall percentage.</summary>
    public double ExpectedSeconds { get; set; } = 1;

    /// <summary>When the last run of this file started and finished; the row shows the time between.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Time spent on the last run: up to now while it runs, the total once it has finished.</summary>
    public TimeSpan? Elapsed => StartedAt is { } start ? (FinishedAt ?? DateTimeOffset.Now) - start : null;

    /// <summary>
    /// The time this file still needs: waiting, its expected time; running, a blend of what is left of the
    /// expected time and the pace so far, trusting the pace more the further it gets. Null once finished.
    /// </summary>
    public TimeSpan? EstimatedRemaining
    {
        get
        {
            if (Status == MediaStatus.Waiting) return TimeSpan.FromSeconds(ExpectedSeconds);
            if (Status != MediaStatus.Running || Elapsed is not { } elapsed) return null;

            var done = Math.Clamp(Progress / 100, 0, 1);
            var byPlan = ExpectedSeconds * (1 - done);

            // Before any step reports progress (probing, starting the engine) there is no pace yet.
            if (done < 0.02) return TimeSpan.FromSeconds(byPlan);

            var byPace = elapsed.TotalSeconds * (1 - done) / done;
            return TimeSpan.FromSeconds(done * byPace + (1 - done) * byPlan);
        }
    }
    public string? Message { get; set; }
    public string? OutputPath { get; set; }

    /// <summary>Why a transcription pass ran on the CPU instead of the GPU, said on the finished row.</summary>
    public string? FallbackNote { get; set; }

    public MediaProbe? Probe { get; set; }
    public AudioSelection? Audio { get; set; }
    public HitSet? Hits { get; set; }
    public List<ForeignSpan> ForeignSpans { get; set; } = [];
    public SyncFit SyncFit { get; set; } = SyncFit.Identity;
    public string Language { get; set; } = "und";

    public int HitCount => Hits?.HitCount ?? 0;
    public bool IsFinished => Status is MediaStatus.Succeeded or MediaStatus.Skipped or MediaStatus.Failed or MediaStatus.Cancelled;

    public string StatusLabel => Status switch
    {
        MediaStatus.Waiting => "Waiting",
        MediaStatus.Running => StatusText,
        MediaStatus.Succeeded => "Done",
        MediaStatus.Skipped => "Skipped",
        MediaStatus.Failed => "Failed",
        MediaStatus.Cancelled => "Cancelled",
        _ => StatusText,
    };
}

/// <summary>Short-lived scratch space per file; nothing is written next to the media until it is finished.</summary>
public sealed class WorkFolder : IDisposable
{
    public static string Root { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinemaGuard", "work");

    private readonly bool _keep;

    private WorkFolder(string path, bool keep)
    {
        Path = path;
        _keep = keep;
    }

    public string Path { get; }

    public static WorkFolder Create(bool keep)
    {
        Directory.CreateDirectory(Root);
        var path = System.IO.Path.Combine(Root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24]);
        Directory.CreateDirectory(path);
        return new WorkFolder(path, keep);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        if (_keep)
        {
            Log.Info($"Keeping the work folder {Path}");
            return;
        }

        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not delete the work folder {Path}: {ex.Message}");
        }
    }

    public static void CleanupOld(TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            var cutoff = DateTime.UtcNow - maxAge;

            foreach (var directory in Directory.EnumerateDirectories(Root))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) >= cutoff) continue;
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not delete the old work folder {directory}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not scan the work folder: {ex.Message}");
        }
    }
}
