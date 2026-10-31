using System.Text;
using CinemaGuard.Core.Phonon;

namespace CinemaGuard.Core.Speech;

/// <summary>
/// Turns any engine's words into Phonon-shaped segments: one per sentence, split again at long pauses
/// and capped in length. "Mute the whole sentence" mutes a segment, so a Whisper window of 30 s or a
/// Parakeet voice span of 20 s must not become one mute.
/// </summary>
public static class TranscriptShaper
{
    public const double PauseSplitSeconds = 0.8;
    public const double MaxSegmentSeconds = 8.0;

    public static PhononResult Shape(List<PhononWord> words, double durationSeconds, List<PhononSegment>? fallbackSegments = null)
    {
        var ordered = words
            .Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.End >= w.Start)
            .OrderBy(w => w.Start)
            .Select(w => new PhononWord { Start = Round(w.Start), End = Round(Math.Max(w.End, w.Start)), Text = w.Text.Trim() })
            .ToList();

        var segments = ordered.Count > 0 ? Split(ordered) : (fallbackSegments ?? []);

        return new PhononResult
        {
            Words = ordered,
            Segments = segments,
            Text = string.Join(" ", segments.Select(s => s.Text)).Trim(),
            DurationSeconds = durationSeconds,
        };
    }

    private static List<PhononSegment> Split(List<PhononWord> words)
    {
        var segments = new List<PhononSegment>();
        var current = new List<PhononWord>();

        foreach (var word in words)
        {
            if (current.Count > 0)
            {
                var previous = current[^1];
                var pause = word.Start - previous.End;
                var length = word.End - current[0].Start;

                if (EndsSentence(previous.Text) || pause >= PauseSplitSeconds || length > MaxSegmentSeconds)
                {
                    segments.Add(ToSegment(current));
                    current = [];
                }
            }

            current.Add(word);
        }

        if (current.Count > 0) segments.Add(ToSegment(current));
        return segments;
    }

    private static bool EndsSentence(string text)
    {
        var trimmed = text.TrimEnd('"', '\'', ')', ']', '»', '”', '’');
        return trimmed.EndsWith('.') || trimmed.EndsWith('?') || trimmed.EndsWith('!') || trimmed.EndsWith('…') ||
               trimmed.EndsWith('。') || trimmed.EndsWith('？') || trimmed.EndsWith('！');
    }

    private static PhononSegment ToSegment(List<PhononWord> words)
    {
        var text = new StringBuilder();
        foreach (var word in words)
        {
            if (text.Length > 0) text.Append(' ');
            text.Append(word.Text);
        }

        return new PhononSegment { Start = words[0].Start, End = words[^1].End, Text = text.ToString() };
    }

    private static double Round(double seconds) => Math.Round(seconds, 3);
}

/// <summary>Reads the 16 kHz mono 16-bit wav the pipeline extracts, as floats.</summary>
public static class WaveFile
{
    /// <summary>
    /// Pieces of at most <paramref name="maxSeconds"/>, each cut at the quietest 200 ms in the last
    /// <paramref name="searchSeconds"/> before the limit, so a cut lands between words rather than in one.
    /// </summary>
    public static List<(int Start, int Length)> SplitAtQuietPoints(float[] samples, int sampleRate, double maxSeconds, double searchSeconds)
    {
        var pieces = new List<(int, int)>();
        var max = (int)(maxSeconds * sampleRate);
        var search = (int)(searchSeconds * sampleRate);
        var window = sampleRate / 5;
        var start = 0;

        while (start < samples.Length)
        {
            if (samples.Length - start <= max)
            {
                pieces.Add((start, samples.Length - start));
                break;
            }

            var bestEnd = start + max;
            var bestEnergy = double.MaxValue;

            for (var end = start + max; end >= start + max - search; end -= window / 2)
            {
                double energy = 0;
                for (var i = Math.Max(start, end - window); i < end; i++) energy += samples[i] * samples[i];
                if (energy < bestEnergy)
                {
                    bestEnergy = energy;
                    bestEnd = end - window / 2;
                }
            }

            pieces.Add((start, bestEnd - start));
            start = bestEnd;
        }

        return pieces;
    }

    public static float[] ReadMono16k(string path, out int sampleRate)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a wav file.");
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a wav file.");

        int channels = 1, bits = 16;
        sampleRate = 16000;

        while (stream.Position + 8 <= stream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();

            if (id == "fmt ")
            {
                var format = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bits = reader.ReadInt16();
                if (size > 16) stream.Seek(size - 16, SeekOrigin.Current);
                if (format is not (1 or -2)) throw new InvalidDataException("Only PCM wav files are supported.");
            }
            else if (id == "data")
            {
                if (bits != 16) throw new InvalidDataException("Only 16-bit wav files are supported.");

                // ffmpeg writes 0xFFFFFFFF as the size of a stream it could not seek back into.
                var available = stream.Length - stream.Position;
                var bytes = size == uint.MaxValue || size > available ? available : size;
                var frames = (int)(bytes / 2 / channels);
                var samples = new float[frames];
                var raw = reader.ReadBytes(frames * 2 * channels);

                for (var i = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (var c = 0; c < channels; c++) sum += BitConverter.ToInt16(raw, (i * channels + c) * 2) / 32768f;
                    samples[i] = sum / channels;
                }

                return samples;
            }
            else
            {
                stream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }

        throw new InvalidDataException("The wav file has no audio data.");
    }
}
