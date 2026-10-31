namespace CinemaGuard.Core.Settings;

/// <summary>Maps the old app's language names and two-letter codes to ISO 639-2, which FFmpeg uses.</summary>
public static class LanguageNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["English"] = "eng", ["Spanish"] = "spa", ["French"] = "fre", ["German"] = "ger",
        ["Italian"] = "ita", ["Portuguese"] = "por", ["Dutch"] = "dut", ["Russian"] = "rus",
        ["Japanese"] = "jpn", ["Chinese"] = "chi", ["Korean"] = "kor", ["Arabic"] = "ara",
        ["Hindi"] = "hin", ["Turkish"] = "tur", ["Polish"] = "pol", ["Swedish"] = "swe",
        ["Danish"] = "dan", ["Norwegian"] = "nor", ["Afrikaans"] = "afr", ["Greek"] = "gre",
        ["Hebrew"] = "heb", ["Czech"] = "cze", ["Hungarian"] = "hun", ["Romanian"] = "rum",
        ["Finnish"] = "fin", ["Ukrainian"] = "ukr", ["Thai"] = "tha", ["Vietnamese"] = "vie",
        ["Indonesian"] = "ind", ["Malay"] = "may", ["Persian"] = "per", ["Zulu"] = "zul",
    };

    private static readonly Dictionary<string, string> TwoLetter = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "eng", ["es"] = "spa", ["fr"] = "fre", ["de"] = "ger", ["it"] = "ita",
        ["pt"] = "por", ["nl"] = "dut", ["ru"] = "rus", ["ja"] = "jpn", ["zh"] = "chi",
        ["ko"] = "kor", ["ar"] = "ara", ["hi"] = "hin", ["tr"] = "tur", ["pl"] = "pol",
        ["sv"] = "swe", ["da"] = "dan", ["no"] = "nor", ["af"] = "afr", ["el"] = "gre",
        ["he"] = "heb", ["cs"] = "cze", ["hu"] = "hun", ["ro"] = "rum", ["fi"] = "fin",
        ["uk"] = "ukr", ["th"] = "tha", ["vi"] = "vie", ["id"] = "ind", ["ms"] = "may",
    };

    public static string ToIso(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "eng";
        if (value.Equals("Detect", StringComparison.OrdinalIgnoreCase)) return "eng";

        var trimmed = value.Trim();

        if (Names.TryGetValue(trimmed, out var name)) return name;
        if (TwoLetter.TryGetValue(trimmed, out var two)) return two;
        if (trimmed.Length == 3) return trimmed.ToLowerInvariant();

        return "eng";
    }

    public static IReadOnlyList<string> CommonLanguages { get; } =
    [
        "eng", "spa", "fre", "ger", "ita", "por", "dut", "rus", "jpn", "chi", "kor", "ara",
        "hin", "tur", "pol", "swe", "afr", "gre", "heb", "cze", "hun", "rum", "fin", "ukr",
    ];
}
