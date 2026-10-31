using System.Runtime.InteropServices;
using CinemaGuard.Core.Phonon;
using SherpaOnnx;

namespace CinemaGuard.Core.Speech;

/// <summary>
/// Parakeet (NeMo TDT) through sherpa-onnx inside the engine host. A film is too long for one decode,
/// so it is cut into overlapping pieces, decoded in small batches; the token timestamps (plus TDT
/// durations) become word times.
/// </summary>
internal sealed class ParakeetHostEngine : IDisposable
{
    private const int SampleRate = 16000;
    private const int BatchSize = 8;

    private readonly Action<string> _log;
    private OfflineRecognizer? _recognizer;
    private string? _loadedKey;

    public ParakeetHostEngine(HostRequest request, Action<string> log)
    {
        _log = log;

        if (request.Runtime == "cuda" && !string.IsNullOrEmpty(request.RuntimeFolder))
            UseGpuLibraries(request.RuntimeFolder);
    }

    /// <summary>
    /// The GPU build of sherpa-onnx and onnxruntime live in the Parakeet GPU pack. Loading them by full
    /// path first means the CPU copies next to the app are never picked up by name.
    /// </summary>
    private void UseGpuLibraries(string folder)
    {
        foreach (var name in new[] { "onnxruntime.dll", "onnxruntime_providers_shared.dll" })
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path)) NativeLibrary.Load(path);
        }

        var api = NativeLibrary.Load(Path.Combine(folder, "sherpa-onnx-c-api.dll"));

        NativeLibrary.SetDllImportResolver(typeof(OfflineRecognizer).Assembly, (name, _, _) =>
            name.Contains("sherpa-onnx-c-api", StringComparison.OrdinalIgnoreCase) ? api : IntPtr.Zero);

        _log("Parakeet is using the GPU libraries.");
    }

    public PhononResult Transcribe(HostRequest request, Action<double> progress)
    {
        var key = $"{request.Model}|{request.Runtime}";

        if (_recognizer is null || _loadedKey != key)
        {
            _recognizer?.Dispose();
            _recognizer = new OfflineRecognizer(Config(request));
            _loadedKey = key;
            _log($"Parakeet loaded {Path.GetFileName(request.Model)} on the {(request.Runtime == "cuda" ? "GPU" : "CPU")}");
        }

        var samples = WaveFile.ReadMono16k(request.Wav, out _);
        var duration = samples.Length / (double)SampleRate;
        var pieces = Pieces(samples);

        var words = new List<PhononWord>();
        long done = 0;

        foreach (var batch in pieces.Chunk(BatchSize))
        {
            var streams = batch.Select(piece =>
            {
                var stream = _recognizer.CreateStream();
                stream.AcceptWaveform(SampleRate, samples.AsSpan(piece.From, piece.To - piece.From).ToArray());
                return stream;
            }).ToList();

            try
            {
                _recognizer.Decode(streams);

                for (var i = 0; i < batch.Length; i++)
                {
                    var piece = batch[i];
                    var offset = piece.From / (double)SampleRate;

                    words.AddRange(WordsOf(streams[i].Result, offset, piece.To / (double)SampleRate)
                        .Where(w => piece.Owns((w.Start + w.End) / 2)));

                    done += piece.CoreLength;
                }
            }
            finally
            {
                foreach (var stream in streams) stream.Dispose();
            }

            progress(done * 100.0 / Math.Max(1, samples.Length));
        }

        return TranscriptShaper.Shape(words, duration);
    }

    private sealed record Piece(int From, int To, double OwnFrom, double OwnTo, int CoreLength)
    {
        public bool Owns(double seconds) => seconds >= OwnFrom && seconds < OwnTo;
    }

    /// <summary>
    /// Pieces of about 24 s cut in pauses, decoded with 1.5 s of audio either side. A word belongs to
    /// the piece whose core holds its middle. No voice detector: it can drop the first word of a
    /// sentence, and it skips singing, where filtered words must be found too.
    /// </summary>
    private static List<Piece> Pieces(float[] samples)
    {
        var pad = (int)(1.5 * SampleRate);
        var cores = WaveFile.SplitAtQuietPoints(samples, SampleRate, 24, 8);

        return cores.Select((core, index) => new Piece(
                Math.Max(0, core.Start - pad),
                Math.Min(samples.Length, core.Start + core.Length + pad),
                core.Start / (double)SampleRate,
                index == cores.Count - 1 ? double.MaxValue : (core.Start + core.Length) / (double)SampleRate,
                core.Length))
            .ToList();
    }

    private static OfflineRecognizerConfig Config(HostRequest request)
    {
        var folder = request.Model;
        string Find(string stem) =>
            Directory.EnumerateFiles(folder, stem + "*.onnx").FirstOrDefault()
            ?? throw new FileNotFoundException($"The Parakeet model is missing its {stem} file.");

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = Find("encoder");
        config.ModelConfig.Transducer.Decoder = Find("decoder");
        config.ModelConfig.Transducer.Joiner = Find("joiner");
        config.ModelConfig.Tokens = Path.Combine(folder, "tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = request.Threads > 0 ? request.Threads : Math.Max(1, Environment.ProcessorCount / 2);
        config.ModelConfig.Provider = request.Runtime == "cuda" ? "cuda" : "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        return config;
    }

    /// <summary>SentencePiece pieces: one that starts with a space (or ▁) starts a word.</summary>
    internal static List<PhononWord> WordsOf(OfflineRecognizerResult result, double offset, double spanEnd)
    {
        var tokens = result.Tokens ?? [];
        var times = result.Timestamps ?? [];
        var durations = result.Durations ?? [];
        var words = new List<PhononWord>();

        for (var i = 0; i < tokens.Length && i < times.Length; i++)
        {
            var piece = tokens[i];
            if (string.IsNullOrEmpty(piece)) continue;

            var start = offset + times[i];
            var end = i < durations.Length && durations[i] > 0
                ? start + durations[i]
                : i + 1 < times.Length ? offset + times[i + 1] : Math.Min(spanEnd, start + 0.2);

            var startsWord = piece.StartsWith(' ') || piece.StartsWith('▁') || words.Count == 0;
            var text = piece.Replace('▁', ' ').Trim();
            if (text.Length == 0) continue;

            if (startsWord && !(text.All(c => char.IsPunctuation(c)) && words.Count > 0))
            {
                words.Add(new PhononWord { Start = start, End = end, Text = text });
            }
            else
            {
                var last = words[^1];
                last.Text += text;
                last.End = Math.Max(last.End, end);
            }
        }

        for (var i = 0; i + 1 < words.Count; i++)
            words[i].End = Math.Min(words[i].End, Math.Max(words[i].Start, words[i + 1].Start));

        return words;
    }

    public void Dispose()
    {
        _recognizer?.Dispose();
        _recognizer = null;
    }
}
