using System.Text.Json;
using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Settings;

namespace CinemaGuard.Core.Phonon;

/// <summary>What the cached transcript was made from, so a re-run only reuses a matching decode.</summary>
public sealed class TranscriptSource
{
    public long Size { get; set; }
    public long ModifiedTicks { get; set; }
    public string Model { get; set; } = string.Empty;
    public int AudioOrdinal { get; set; }
    public string Downmix { get; set; } = string.Empty;

    public bool Matches(TranscriptSource other) =>
        Size == other.Size &&
        ModifiedTicks == other.ModifiedTicks &&
        string.Equals(Model, other.Model, StringComparison.Ordinal) &&
        AudioOrdinal == other.AudioOrdinal &&
        string.Equals(Downmix, other.Downmix, StringComparison.Ordinal);

    public static TranscriptSource ForFile(string inputPath, string model, int audioOrdinal, DownmixMode downmix)
    {
        var info = new FileInfo(inputPath);
        return new TranscriptSource
        {
            Size = info.Exists ? info.Length : 0,
            ModifiedTicks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0,
            Model = model,
            AudioOrdinal = audioOrdinal,
            Downmix = downmix.ToString(),
        };
    }
}

public sealed class TranscriptCacheFile
{
    public TranscriptSource Source { get; set; } = new();
    public PhononResult Result { get; set; } = new();
}

/// <summary>Stores the raw Phonon JSON next to the media so a re-run never decodes again.</summary>
public static class TranscriptCache
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    /// <param name="anyModel">
    /// Accept a transcript made by another model (same file, track and downmix). Used when the user
    /// unticked Generate: any transcript of this audio beats none.
    /// </param>
    public static PhononResult? Load(string path, TranscriptSource expected, bool anyModel = false)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var cache = JsonSerializer.Deserialize<TranscriptCacheFile>(File.ReadAllText(path), Options);
            if (cache?.Result is null) return null;

            if (anyModel)
            {
                var sameAudio = new TranscriptSource
                {
                    Size = expected.Size,
                    ModifiedTicks = expected.ModifiedTicks,
                    Model = cache.Source.Model,
                    AudioOrdinal = expected.AudioOrdinal,
                    Downmix = expected.Downmix,
                };

                return cache.Source.Matches(sameAudio) ? cache.Result : null;
            }

            return cache.Source.Matches(expected) ? cache.Result : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Ignoring an unreadable transcript cache {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    public static void Save(string path, TranscriptSource source, PhononResult result)
    {
        try
        {
            var cache = new TranscriptCacheFile { Source = source, Result = result };
            File.WriteAllText(path, JsonSerializer.Serialize(cache, Options));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save the transcript cache {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}
