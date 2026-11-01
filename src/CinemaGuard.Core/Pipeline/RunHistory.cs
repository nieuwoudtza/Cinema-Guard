using System.Text.Json;
using System.Text.Json.Serialization;
using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Media;
using CinemaGuard.Core.Speech;

namespace CinemaGuard.Core.Pipeline;

/// <summary>The kinds of work whose times are kept: each scales with the file's size or its running time.</summary>
public enum StepKind { ExtractAudio, Encode, Transcribe, File }

/// <summary>What decides how long a step takes on a file: where it lives, how big and how dense it is, what has to be decoded.</summary>
public sealed record StepFeatures
{
    /// <summary>The drive root ("f:\") or network share ("\\nas\media") the file is read from and written to.</summary>
    public string Drive { get; init; } = string.Empty;

    /// <summary>Fixed, Removable, Network, ...: the fallback when this drive has no history yet.</summary>
    public string DriveType { get; init; } = string.Empty;

    public long Bytes { get; init; }
    public double DurationSeconds { get; init; }
    public int VideoHeight { get; init; }
    public string AudioCodec { get; init; } = string.Empty;
    public int AudioChannels { get; init; }

    /// <summary>The encode re-encodes the muted track (rather than copying the audio).</summary>
    public bool Reencode { get; init; }

    /// <summary>Transcription only: the model and device ("phonon-2.5-en|GPU (Docker)").</summary>
    public string Engine { get; init; } = string.Empty;

    /// <summary>Whole files only: the run included transcribing (the longest step by far).</summary>
    public bool Transcribed { get; init; }

    [JsonIgnore]
    public double BitsPerSecond => DurationSeconds > 0 ? Bytes * 8 / DurationSeconds : 0;

    public static StepFeatures ForFile(string path)
    {
        var (drive, type) = DriveOf(path);
        long bytes = 0;

        try { bytes = new FileInfo(path).Length; }
        catch (Exception) { /* gone or unreadable: no size to scale by */ }

        return new StepFeatures { Drive = drive, DriveType = type, Bytes = bytes };
    }

    public static StepFeatures ForMedia(string path, MediaProbe probe, AudioSelection audio) => ForFile(path) with
    {
        DurationSeconds = probe.DurationSeconds,
        VideoHeight = probe.VideoStreams.FirstOrDefault()?.Height ?? 0,
        AudioCodec = audio.Stream.CodecName,
        AudioChannels = audio.Stream.Channels ?? 0,
    };

    internal static (string Drive, string Type) DriveOf(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
            if (root.StartsWith(@"\\", StringComparison.Ordinal)) return (root.TrimEnd('\\').ToLowerInvariant(), "Network");

            return (root.ToLowerInvariant(), new DriveInfo(root).DriveType.ToString());
        }
        catch (Exception)
        {
            return (string.Empty, string.Empty);
        }
    }
}

/// <summary>
/// How long earlier runs took, so each step of a file (and each file of the queue) can be weighed by its
/// expected time. Only what that needs is kept: the step, the file's drive, size, running time,
/// resolution and audio, and the seconds it took. Nothing is shown to the user; it makes the percentages
/// move more evenly. Stored in %LOCALAPPDATA%\CinemaGuard\run-history.json, newest 400 samples.
/// </summary>
public sealed class RunHistory(string path)
{
    public static RunHistory Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinemaGuard", "run-history.json"));

    private const int MaxSamples = 400;

    /// <summary>Short clips are mostly start-up time; they would make a film look far slower than it is.</summary>
    private const double MinimumSampleSeconds = 300;

    /// <summary>How much the built-in guess counts against the history, in samples' worth of weight.</summary>
    private const double PriorWeight = 0.5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private List<RunSample>? _samples;

    public sealed record RunSample(StepKind Kind, StepFeatures Features, double Seconds, DateTimeOffset At);

    public void Record(StepKind kind, StepFeatures features, TimeSpan elapsed)
    {
        if (features.DurationSeconds < MinimumSampleSeconds || elapsed.TotalSeconds < 0.5) return;
        if (Scale(kind, features) <= 0) return;

        lock (_gate)
        {
            var samples = Load();
            samples.Add(new RunSample(kind, features, elapsed.TotalSeconds, DateTimeOffset.Now));
            if (samples.Count > MaxSamples) samples.RemoveRange(0, samples.Count - MaxSamples);
            Save(samples);
        }
    }

    /// <summary>
    /// The expected seconds for a step: each earlier sample of the same kind gives a rate (seconds per byte,
    /// or per second of audio for transcription), weighted by how alike the two files are, and the
    /// built-in <paramref name="fallback"/> counts as a little extra weight so a thin history does not swing it.
    /// </summary>
    public double Estimate(StepKind kind, StepFeatures features, double fallback)
    {
        var scale = Scale(kind, features);
        if (scale <= 0) return fallback;

        double weighted = 0, weights = 0;

        lock (_gate)
        {
            var samples = Load();
            var age = 0;

            for (var i = samples.Count - 1; i >= 0; i--)
            {
                var sample = samples[i];
                if (sample.Kind != kind) continue;

                var sampleScale = Scale(kind, sample.Features);
                if (sampleScale <= 0) continue;

                var weight = Similarity(kind, features, sample.Features) * Math.Pow(0.98, age++);
                if (weight <= 0) continue;

                weighted += weight * sample.Seconds / sampleScale;
                weights += weight;
            }
        }

        if (weights <= 0) return fallback;

        return (weighted * scale + PriorWeight * fallback) / (weights + PriorWeight);
    }

    /// <summary>A whole file before it is probed (its queue weight): its size, its drive and whether it will be transcribed.</summary>
    public double EstimateFile(string path, bool transcribes)
    {
        var features = StepFeatures.ForFile(path) with { Transcribed = transcribes };
        var fallback = 30 + features.Bytes / (60.0 * 1024 * 1024) * (transcribes ? 3 : 1);
        return Estimate(StepKind.File, features, fallback);
    }

    public static string EngineKey(SpeechPass pass, string device) => $"{pass.CacheKey}|{device}";

    /// <summary>Reading and writing the file dominates the FFmpeg steps; the speech model works through the audio.</summary>
    private static double Scale(StepKind kind, StepFeatures features) =>
        kind == StepKind.Transcribe ? features.DurationSeconds : features.Bytes;

    internal static double Similarity(StepKind kind, StepFeatures a, StepFeatures b)
    {
        if (kind == StepKind.Transcribe)
        {
            // The audio is a wav in the work folder: only the engine matters, and the speech density a little.
            return string.Equals(a.Engine, b.Engine, StringComparison.Ordinal) ? 1 : 0;
        }

        var weight = a.Drive == b.Drive && a.Drive.Length > 0 ? 1.0
            : a.DriveType == b.DriveType && a.DriveType.Length > 0 ? 0.3
            : 0.1;

        // Twice or half the bitrate halves the weight: files of a like density cost about the same per byte.
        if (a.BitsPerSecond > 0 && b.BitsPerSecond > 0)
            weight *= Math.Exp(-Math.Abs(Math.Log(a.BitsPerSecond / b.BitsPerSecond)));

        if (kind == StepKind.File) return a.Transcribed == b.Transcribed ? weight : weight * 0.1;

        if (HeightBucket(a.VideoHeight) != HeightBucket(b.VideoHeight)) weight *= 0.6;
        if (!string.Equals(a.AudioCodec, b.AudioCodec, StringComparison.OrdinalIgnoreCase)) weight *= 0.7;
        if (a.AudioChannels != b.AudioChannels) weight *= 0.8;
        if (kind == StepKind.Encode && a.Reencode != b.Reencode) weight *= 0.4;

        return weight;
    }

    private static int HeightBucket(int height) => height switch
    {
        <= 0 => 0,
        <= 576 => 1,
        <= 800 => 2,
        <= 1200 => 3,
        _ => 4,
    };

    private List<RunSample> Load()
    {
        if (_samples is not null) return _samples;

        try
        {
            if (File.Exists(path))
                _samples = JsonSerializer.Deserialize<List<RunSample>>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the run history: {ex.Message}");
        }

        return _samples ??= [];
    }

    private void Save(List<RunSample> samples)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(samples, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save the run history: {ex.Message}");
        }
    }

    /// <summary>A guess for a model with no history: seconds of audio per second of wall time, model load included.</summary>
    public static double DefaultTranscribeSpeed(SpeechPass pass, string device)
    {
        if (pass.Remote is not null) return 20;

        var gpu = !device.StartsWith("CPU", StringComparison.OrdinalIgnoreCase);

        return pass.Model!.Family switch
        {
            SpeechFamily.Phonon => gpu ? 100 : 50,
            SpeechFamily.Whisper => gpu ? 30 : 5,
            _ => 20,
        };
    }
}
