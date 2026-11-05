namespace CinemaGuard.Core.Media;

/// <summary>
/// How much of the centre channel's speech another channel carries over a stretch of time. Films mix the
/// dialogue into the front (and sometimes surround) channels as well as the centre, often too quietly under the
/// music and effects for that channel's own transcript to pick out the word, yet loud enough to hear once the
/// centre is muted. Both channels are band-passed to the speech band and compared at lags up to 50 ms; the part
/// of the other channel that follows the centre is its copy of the dialogue, panned or as the room's reverb.
/// </summary>
public static class DialogueLeak
{
    /// <summary>A channel that carries the centre's speech this loud (dB, against the centre) or louder is muted with it.</summary>
    public const double MuteAtDb = -30;

    /// <summary>
    /// Below this correlation the channel's own sound is far louder than its copy of the words (about 18 dB), which
    /// it drowns out; chance likeness stays well under it.
    /// </summary>
    public const double MinimumCorrelation = 0.12;

    /// <summary>A panned copy is within a few milliseconds of the centre; the early reverb of the words arrives up to this late.</summary>
    private const double MaxLagSeconds = 0.050;

    /// <summary>Compared at this rate: the speech band ends at 3.4 kHz.</summary>
    private const int CompareRate = 8000;

    /// <summary>True when <paramref name="other"/> carries the centre's speech loud enough to be heard with the centre muted.</summary>
    public static bool CarriesSpeech(float[] centre, float[] other, int sampleRate)
    {
        var (correlation, levelDb) = Measure(centre, other, sampleRate);
        return Math.Abs(correlation) >= MinimumCorrelation && levelDb >= MuteAtDb;
    }

    /// <summary>
    /// The best correlation between the two in the speech band, and the level of the other channel's part that
    /// follows the centre, in dB against the centre (0 dB: the same words just as loud).
    /// </summary>
    public static (double Correlation, double LevelDb) Measure(float[] centre, float[] other, int sampleRate)
    {
        var length = Math.Min(centre.Length, other.Length);
        if (length == 0) return (0, double.NegativeInfinity);

        var factor = Math.Max(1, sampleRate / CompareRate);
        var a = SpeechBand(centre.AsSpan(0, length), sampleRate, factor);
        var b = SpeechBand(other.AsSpan(0, length), sampleRate, factor);

        var maxLag = (int)(MaxLagSeconds * sampleRate / factor);
        var best = 0.0;

        for (var lag = -maxLag; lag <= maxLag; lag++)
        {
            var correlation = Correlation(a, b, lag);
            if (Math.Abs(correlation) > Math.Abs(best)) best = correlation;
        }

        var rmsCentre = Rms(a);
        if (rmsCentre <= 0) return (0, double.NegativeInfinity);

        var gain = Math.Abs(best) * Rms(b) / rmsCentre;
        return (best, gain > 0 ? 20 * Math.Log10(gain) : double.NegativeInfinity);
    }

    /// <summary>The correlation of <paramref name="a"/> with <paramref name="b"/> moved <paramref name="lag"/> samples later.</summary>
    private static double Correlation(double[] a, double[] b, int lag)
    {
        double cross = 0, energyA = 0, energyB = 0;
        var from = Math.Max(0, -lag);
        var to = a.Length - Math.Max(0, lag);

        for (var i = from; i < to; i++)
        {
            var x = a[i];
            var y = b[i + lag];
            cross += x * y;
            energyA += x * x;
            energyB += y * y;
        }

        return energyA > 0 && energyB > 0 ? cross / Math.Sqrt(energyA * energyB) : 0;
    }

    private static double Rms(double[] samples)
    {
        double sum = 0;
        foreach (var sample in samples) sum += sample * sample;
        return Math.Sqrt(sum / samples.Length);
    }

    /// <summary>300-3400 Hz (two second-order high-passes and two low-passes, RBJ biquads), then every <paramref name="factor"/>th sample.</summary>
    private static double[] SpeechBand(ReadOnlySpan<float> samples, int sampleRate, int factor)
    {
        var filtered = new double[samples.Length];
        for (var i = 0; i < samples.Length; i++) filtered[i] = samples[i];

        var highPass = Biquad.HighPass(300, sampleRate);
        var lowPass = Biquad.LowPass(Math.Min(3400, sampleRate * 0.45), sampleRate);

        highPass.Run(filtered);
        highPass.Run(filtered);
        lowPass.Run(filtered);
        lowPass.Run(filtered);

        if (factor == 1) return filtered;

        var output = new double[(filtered.Length + factor - 1) / factor];
        for (var i = 0; i < output.Length; i++) output[i] = filtered[i * factor];
        return output;
    }

    private readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2)
    {
        private const double Q = 0.7071;

        public static Biquad HighPass(double frequency, int sampleRate)
        {
            var (cos, alpha) = Shape(frequency, sampleRate);
            var a0 = 1 + alpha;
            return new Biquad((1 + cos) / 2 / a0, -(1 + cos) / a0, (1 + cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
        }

        public static Biquad LowPass(double frequency, int sampleRate)
        {
            var (cos, alpha) = Shape(frequency, sampleRate);
            var a0 = 1 + alpha;
            return new Biquad((1 - cos) / 2 / a0, (1 - cos) / a0, (1 - cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
        }

        private static (double Cos, double Alpha) Shape(double frequency, int sampleRate)
        {
            var omega = 2 * Math.PI * frequency / sampleRate;
            return (Math.Cos(omega), Math.Sin(omega) / (2 * Q));
        }

        public void Run(double[] samples)
        {
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (var i = 0; i < samples.Length; i++)
            {
                var x = samples[i];
                var y = B0 * x + B1 * x1 + B2 * x2 - A1 * y1 - A2 * y2;
                x2 = x1; x1 = x;
                y2 = y1; y1 = y;
                samples[i] = y;
            }
        }
    }
}

/// <summary>Reads stretches of a mono 16-bit wav by time, without loading the whole file.</summary>
public sealed class MonoWavReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly long _dataStart;
    private readonly long _dataLength;

    public string Path { get; }
    public int SampleRate { get; }

    public MonoWavReader(string path)
    {
        Path = path;
        _stream = File.OpenRead(path);
        using var reader = new BinaryReader(_stream, System.Text.Encoding.ASCII, leaveOpen: true);

        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a wav file.");
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a wav file.");

        var sampleRate = 0;
        while (_stream.Position + 8 <= _stream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();

            if (id == "fmt ")
            {
                var format = reader.ReadInt16();
                var channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                var bits = reader.ReadInt16();
                if (size > 16) _stream.Seek(size - 16, SeekOrigin.Current);
                if (format is not (1 or -2) || channels != 1 || bits != 16)
                    throw new InvalidDataException("Only mono 16-bit PCM wav files are supported.");
            }
            else if (id == "data")
            {
                // ffmpeg writes 0xFFFFFFFF as the size of a stream it could not seek back into.
                var available = _stream.Length - _stream.Position;
                _dataStart = _stream.Position;
                _dataLength = size == uint.MaxValue || size > available ? available : size;
                break;
            }
            else
            {
                _stream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }

        if (sampleRate <= 0 || _dataStart == 0) throw new InvalidDataException("The wav file has no audio data.");
        SampleRate = sampleRate;
    }

    /// <summary>The samples from <paramref name="start"/> to <paramref name="end"/> seconds, cut short at the end of the file.</summary>
    public float[] Read(double start, double end)
    {
        var total = _dataLength / 2;
        var first = Math.Clamp((long)(Math.Max(0, start) * SampleRate), 0, total);
        var last = Math.Clamp((long)(Math.Max(0, end) * SampleRate), first, total);

        var bytes = new byte[(last - first) * 2];
        _stream.Seek(_dataStart + first * 2, SeekOrigin.Begin);
        _stream.ReadExactly(bytes);

        var samples = new float[bytes.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        return samples;
    }

    public void Dispose() => _stream.Dispose();
}
