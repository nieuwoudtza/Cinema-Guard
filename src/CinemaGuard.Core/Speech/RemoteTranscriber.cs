using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CinemaGuard.Core.Phonon;

namespace CinemaGuard.Core.Speech;

public sealed record RemoteSpec(string Url, string Model, string? ApiKey, int MaxUploadMb, string? Hotwords)
{
    /// <summary>Base URL as typed ("http://gpu-box:8000", ".../v1" or the full endpoint) to the endpoint.</summary>
    public string Endpoint
    {
        get
        {
            var url = Url.Trim().TrimEnd('/');
            if (url.EndsWith("/audio/transcriptions", StringComparison.OrdinalIgnoreCase)) return url;
            if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return url + "/audio/transcriptions";
            return url + "/v1/audio/transcriptions";
        }
    }

    public string CacheKey => $"remote:{Url.Trim().TrimEnd('/')}:{Model}";
}

/// <summary>
/// Any OpenAI-compatible /v1/audio/transcriptions endpoint that returns word timestamps: `fermion serve`
/// (Phonon on a GPU machine or in Docker), OpenAI whisper-1, Groq, and the like. Large audio is split
/// into overlapping chunks for providers with an upload cap, and the words stitched back by offset.
/// </summary>
public static class RemoteTranscriber
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromHours(3) };

    private const int SampleRate = 16000;
    private const double OverlapSeconds = 2.0;

    public static async Task<PhononResult> TranscribeAsync(RemoteSpec spec, string wavPath, Action<string>? status, CancellationToken cancellationToken)
    {
        var length = new FileInfo(wavPath).Length;
        var limit = spec.MaxUploadMb > 0 ? spec.MaxUploadMb * 1024L * 1024 : long.MaxValue;

        if (length <= limit)
        {
            status?.Invoke("Sending the audio to the server");
            var whole = await SendAsync(spec, await File.ReadAllBytesAsync(wavPath, cancellationToken), cancellationToken);
            return TranscriptShaper.Shape(whole.Words, whole.Duration, whole.Segments);
        }

        var samples = WaveFile.ReadMono16k(wavPath, out _);
        var chunkSeconds = Math.Max(30, (limit - 1024) / 2.0 / SampleRate - OverlapSeconds);
        var words = new List<PhononWord>();
        var segments = new List<PhononSegment>();
        var total = samples.Length / (double)SampleRate;
        var chunks = (int)Math.Ceiling(total / chunkSeconds);

        for (var index = 0; index < chunks; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            status?.Invoke($"Sending part {index + 1} of {chunks} to the server");

            var start = index * chunkSeconds;
            var from = (int)(Math.Max(0, start - (index == 0 ? 0 : OverlapSeconds)) * SampleRate);
            var to = (int)Math.Min(samples.Length, (start + chunkSeconds) * SampleRate);
            var offset = from / (double)SampleRate;

            var part = await SendAsync(spec, ToWav(samples, from, to), cancellationToken);

            // Each chunk owns [start, start + chunkSeconds); the overlap only gives the first word context.
            var end = start + chunkSeconds;
            words.AddRange(part.Words.Select(w => Shift(w, offset)).Where(w => Owns(w.Start, w.End, start, end, index == chunks - 1)));
            segments.AddRange(part.Segments.Select(s => new PhononSegment { Start = s.Start + offset, End = s.End + offset, Text = s.Text })
                .Where(s => Owns(s.Start, s.End, start, end, index == chunks - 1)));
        }

        return TranscriptShaper.Shape(words, total, segments);
    }

    /// <summary>Sends five seconds of silence plus a tone and checks the server answers with word times.</summary>
    public static async Task<string> TestAsync(RemoteSpec spec, CancellationToken cancellationToken)
    {
        var samples = new float[SampleRate * 5];
        for (var i = SampleRate; i < SampleRate * 2; i++) samples[i] = (float)(0.1 * Math.Sin(2 * Math.PI * 440 * i / SampleRate));

        var response = await SendAsync(spec, ToWav(samples, 0, samples.Length), cancellationToken, allowEmpty: true);
        return response.WordTimestampsSupported
            ? "Connected. The server returns word timestamps."
            : "Connected, but the server did not say it supports word timestamps; mutes may be wide.";
    }

    private static bool Owns(double start, double end, double from, double to, bool last)
    {
        var middle = (start + end) / 2;
        return middle >= from && (last || middle < to);
    }

    private static PhononWord Shift(PhononWord word, double offset) =>
        new() { Start = word.Start + offset, End = word.End + offset, Text = word.Text };

    private sealed record Reply(List<PhononWord> Words, List<PhononSegment> Segments, double Duration, bool WordTimestampsSupported);

    private static async Task<Reply> SendAsync(RemoteSpec spec, byte[] wav, CancellationToken cancellationToken, bool allowEmpty = false)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "audio.wav");
        form.Add(new StringContent(string.IsNullOrWhiteSpace(spec.Model) ? "whisper-1" : spec.Model), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("word"), "timestamp_granularities[]");
        form.Add(new StringContent("segment"), "timestamp_granularities[]");
        if (!string.IsNullOrWhiteSpace(spec.Hotwords)) form.Add(new StringContent(spec.Hotwords), "hotwords");

        using var request = new HttpRequestMessage(HttpMethod.Post, spec.Endpoint) { Content = form };
        if (!string.IsNullOrWhiteSpace(spec.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", spec.ApiKey);

        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new PhononException($"The server answered {(int)response.StatusCode}: {Shorten(body)}");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var words = new List<PhononWord>();
        var hasWords = root.TryGetProperty("words", out var wordArray) && wordArray.ValueKind == JsonValueKind.Array;
        if (hasWords)
        {
            foreach (var word in wordArray.EnumerateArray())
            {
                var text = word.TryGetProperty("word", out var w) ? w.GetString() : word.TryGetProperty("text", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(text)) continue;
                words.Add(new PhononWord { Start = Number(word, "start"), End = Number(word, "end"), Text = text.Trim() });
            }
        }

        var segments = new List<PhononSegment>();
        if (root.TryGetProperty("segments", out var segmentArray) && segmentArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in segmentArray.EnumerateArray())
            {
                segments.Add(new PhononSegment
                {
                    Start = Number(segment, "start"),
                    End = Number(segment, "end"),
                    Text = segment.TryGetProperty("text", out var t) ? (t.GetString() ?? string.Empty).Trim() : string.Empty,
                });
            }
        }

        if (!allowEmpty && !hasWords && segments.Count == 0 && root.TryGetProperty("text", out var plain) && !string.IsNullOrWhiteSpace(plain.GetString()))
            throw new PhononException("The server returned text without timestamps, so nothing could be muted. Use a server that supports verbose_json with word timestamps.");

        var duration = root.TryGetProperty("duration", out _) ? Number(root, "duration") : wav.Length / 2.0 / SampleRate;
        return new Reply(words, segments, duration, hasWords);
    }

    private static double Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    private static byte[] ToWav(float[] samples, int from, int to)
    {
        var count = Math.Max(0, to - from);
        using var stream = new MemoryStream(44 + count * 2);
        using var writer = new BinaryWriter(stream);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + count * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(count * 2);

        for (var i = from; i < to; i++) writer.Write((short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue));

        writer.Flush();
        return stream.ToArray();
    }

    private static string Shorten(string text) => text.Length <= 300 ? text.Trim() : text[..300].Trim() + "…";

    // ------------------------------------------------------------------ API key storage

    public static string? Protect(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception)
        {
            // Copied from another user or PC: the key has to be entered again.
            return null;
        }
    }
}
