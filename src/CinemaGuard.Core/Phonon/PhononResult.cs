using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CinemaGuard.Core.Phonon;

public sealed class PhononSegment
{
    [JsonPropertyName("start")] public double Start { get; set; }
    [JsonPropertyName("end")] public double End { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
}

public sealed class PhononWord
{
    [JsonPropertyName("start")] public double Start { get; set; }
    [JsonPropertyName("end")] public double End { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
}

/// <summary>The JSON object `fermion transcribe --json` prints: text, segments, words, duration, truncated.</summary>
public sealed class PhononResult : IJsonOnDeserialized
{
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("segments")] public List<PhononSegment> Segments { get; set; } = [];
    [JsonPropertyName("words")] public List<PhononWord> Words { get; set; } = [];
    [JsonPropertyName("duration_s")] public double DurationSeconds { get; set; }
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }

    /// <summary>
    /// Any field the model does not know. Fermion 0.2.x writes `duration_seconds`, older JSON writes
    /// `duration_s`; the unknown one lands here and <see cref="UseAlternateDuration"/> reads it.
    /// Writing the model back (the transcript cache) keeps the field, so a re-read is unaffected.
    /// </summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (DurationSeconds > 0 || Extra is null || !Extra.TryGetValue("duration_seconds", out var value)) return;

        var seconds = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };

        if (seconds > 0) DurationSeconds = seconds;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>The decoder may print log lines before the object; the JSON is the last line that starts with '{'.</summary>
    public static PhononResult? TryParse(string standardOutput)
    {
        if (string.IsNullOrWhiteSpace(standardOutput)) return null;

        var lines = standardOutput.Replace("\r\n", "\n").Split('\n');

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith('{')) continue;

            try
            {
                var result = JsonSerializer.Deserialize<PhononResult>(line, Options);
                if (result is not null) return result;
            }
            catch (JsonException)
            {
                // Try the previous line: a log line can start with a brace.
            }
        }

        return null;
    }

    public bool IsEmpty => Segments.Count == 0 && Words.Count == 0;
}

public sealed class PhononException(string message) : Exception(message);
