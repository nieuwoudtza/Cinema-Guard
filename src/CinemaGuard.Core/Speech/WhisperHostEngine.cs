using CinemaGuard.Core.Phonon;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace CinemaGuard.Core.Speech;

/// <summary>
/// Whisper.net inside the engine host. The native runtime (CUDA 13, CUDA 12, Vulkan or CPU) is fixed
/// for the life of the process; the model stays loaded until a different one is asked for.
/// Word edges use DTW token timestamps, which are tighter than Whisper's own segment timing.
/// </summary>
internal sealed class WhisperHostEngine : IDisposable
{
    private readonly Action<string> _log;
    private WhisperFactory? _factory;
    private string? _loadedKey;

    public WhisperHostEngine(HostRequest request, Action<string> log)
    {
        _log = log;

        if (!string.IsNullOrEmpty(request.RuntimeFolder))
        {
            // Whisper.net looks for runtimes\<kind>\win-x64 next to the folder of LibraryPath.
            var root = Path.GetFullPath(Path.Combine(request.RuntimeFolder, "..", "..", ".."));
            RuntimeOptions.LibraryPath = Path.Combine(root, "whisper.dll");
        }

        RuntimeOptions.RuntimeLibraryOrder = request.Runtime switch
        {
            "cuda13" => [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            "cuda12" => [RuntimeLibrary.Cuda12, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            "vulkan" => [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            "cpu-noavx" => [RuntimeLibrary.CpuNoAvx],
            _ => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
        };
    }

    public PhononResult Transcribe(HostRequest request, Action<double> progress)
    {
        var useGpu = request.Runtime is "cuda13" or "cuda12" or "vulkan";
        var key = $"{request.Model}|{request.HeadsPreset}";

        if (_factory is null || _loadedKey != key)
        {
            _factory?.Dispose();

            var heads = Enum.TryParse<WhisperAlignmentHeadsPreset>(request.HeadsPreset, out var preset) ? preset : WhisperAlignmentHeadsPreset.None;
            _factory = WhisperFactory.FromPath(request.Model, new WhisperFactoryOptions
            {
                UseGpu = useGpu,
                UseFlashAttention = false, // DTW timestamps need it off.
                UseDtwTimeStamps = heads != WhisperAlignmentHeadsPreset.None,
                HeadsPreset = heads,
            });

            _loadedKey = key;
            _log($"Whisper loaded {Path.GetFileName(request.Model)} on {RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown runtime"}");

            if (useGpu && RuntimeOptions.LoadedLibrary is RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx)
                _log("The GPU runtime could not be loaded, so Whisper is running on the CPU. Re-detect hardware on the Models page.");
        }

        var samples = WaveFile.ReadMono16k(request.Wav, out _);
        var duration = samples.Length / 16000.0;

        // No context from the previous window: on long films, conditioning on earlier text is what sends
        // Whisper into repetition loops or makes it stop early.
        var builder = _factory.CreateBuilder()
            .WithLanguage(string.IsNullOrWhiteSpace(request.Language) ? "auto" : request.Language)
            .WithNoContext()
            .WithTokenTimestamps();

        if (request.Threads > 0) builder = builder.WithThreads(request.Threads);
        if (!string.IsNullOrWhiteSpace(request.Prompt)) builder = builder.WithPrompt(request.Prompt);

        using var processor = builder.Build();

        var words = new List<PhononWord>();
        var segments = new List<PhononSegment>();

        // whisper.cpp's DTW timing silently ends the decode after its first 30 s window, so the audio is
        // fed as separate pieces of under 30 s. Each core is cut in a pause and decoded with 1.5 s of
        // audio either side; a word belongs to the piece whose core holds its middle, so a cut that
        // lands inside a word still yields the whole word exactly once.
        const int rate = 16000;
        var pad = (int)(1.5 * rate);
        var cores = WaveFile.SplitAtQuietPoints(samples, rate, 26, 8);
        long done = 0;

        for (var index = 0; index < cores.Count; index++)
        {
            var (coreStart, coreLength) = cores[index];
            var from = Math.Max(0, coreStart - pad);
            var to = Math.Min(samples.Length, coreStart + coreLength + pad);
            var offset = from / (double)rate;
            var ownFrom = coreStart / (double)rate;
            var ownTo = index == cores.Count - 1 ? double.MaxValue : (coreStart + coreLength) / (double)rate;

            bool Owns(double start, double end) => (start + end) / 2 >= ownFrom && (start + end) / 2 < ownTo;

            foreach (var segment in processor.ProcessAsync(new ReadOnlyMemory<float>(samples, from, to - from)).ToBlockingEnumerable())
            {
                var start = offset + segment.Start.TotalSeconds;
                var end = offset + segment.End.TotalSeconds;
                if (Owns(start, end)) segments.Add(new PhononSegment { Start = start, End = end, Text = segment.Text.Trim() });

                words.AddRange(WordsOf(segment)
                    .Select(w => new PhononWord { Start = w.Start + offset, End = w.End + offset, Text = w.Text })
                    .Where(w => Owns(w.Start, w.End)));
            }

            done += coreLength;
            progress(done * 100.0 / samples.Length);
        }

        return TranscriptShaper.Shape(words, duration, segments);
    }

    /// <summary>
    /// Whisper tokens are word pieces; a piece that starts with a space starts a word. A word starts at
    /// its first piece's DTW time and ends at its last piece's end, never past the next word's start.
    /// </summary>
    internal static List<PhononWord> WordsOf(SegmentData segment)
    {
        var pieces = segment.Tokens
            .Where(t => !string.IsNullOrWhiteSpace(t.Text) && !IsSpecial(t.Text))
            .ToList();

        var words = new List<(double Start, double End, string Text)>();

        foreach (var token in pieces)
        {
            var start = token.DtwTimestamp >= 0 ? token.DtwTimestamp / 100.0 : token.Start / 100.0;
            var end = Math.Max(token.End / 100.0, start);
            var text = token.Text!;

            var startsWord = words.Count == 0 || text.StartsWith(' ') || (char.IsLetterOrDigit(text.TrimStart()[0]) && IsPunctuationOnly(words[^1].Text));
            if (startsWord && !IsPunctuationOnly(text))
            {
                words.Add((start, end, text.Trim()));
            }
            else if (words.Count > 0)
            {
                var last = words[^1];
                words[^1] = (last.Start, Math.Max(last.End, end), last.Text + text.Trim());
            }
        }

        var result = new List<PhononWord>(words.Count);
        for (var i = 0; i < words.Count; i++)
        {
            var (start, end, text) = words[i];
            if (i + 1 < words.Count) end = Math.Min(end, Math.Max(start, words[i + 1].Start));
            end = Math.Min(Math.Max(end, start + 0.08), segment.End.TotalSeconds);
            if (text.Length == 0) continue;
            result.Add(new PhononWord { Start = start, End = Math.Max(end, start), Text = text });
        }

        return result;
    }

    private static bool IsSpecial(string text) =>
        text.StartsWith("[_", StringComparison.Ordinal) || text.StartsWith("<|", StringComparison.Ordinal);

    private static bool IsPunctuationOnly(string text) => text.Trim().Length > 0 && text.Trim().All(c => char.IsPunctuation(c) || char.IsSymbol(c));

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
    }
}
