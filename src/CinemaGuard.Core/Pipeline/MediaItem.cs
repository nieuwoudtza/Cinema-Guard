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
    public string GeneratedPath => Path.ChangeExtension(InputPath, ".generated.srt");
    public string OneWordPath => Path.ChangeExtension(InputPath, ".oneword.srt");
    public string EnglishCachePath => Path.ChangeExtension(InputPath, ".phonon-en.json");
    public string MultilingualCachePath => Path.ChangeExtension(InputPath, ".phonon-ml.json");

    /// <summary>The transcript of any model other than the two Phonon 2.5 ones (keyed on the model inside).</summary>
    public string TranscriptCachePath => Path.ChangeExtension(InputPath, ".transcript.json");

    /// <summary>Phonon 2.5 keeps its original cache names, so files done before the Models page are not redone.</summary>
    public string CachePathFor(string cacheKey) => cacheKey switch
    {
        Speech.SpeechCatalog.PhononEnglishId => EnglishCachePath,
        Speech.SpeechCatalog.PhononMultilingualId => MultilingualCachePath,
        _ => TranscriptCachePath,
    };

    public bool HasAnyTranscriptCache =>
        File.Exists(EnglishCachePath) || File.Exists(MultilingualCachePath) || File.Exists(TranscriptCachePath);
    public string LanguageReportPath => Path.ChangeExtension(InputPath, ".languages.json");

    public string? CustomSubtitlePath { get; set; }
    public bool GenerateSubtitles { get; set; } = true;
    public string SubtitleSource { get; set; } = "None";

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
