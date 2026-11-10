using System.Text.Json;
using System.Text.Json.Serialization;
using CinemaGuard.Core.Speech;

namespace CinemaGuard.Core.Settings;

public enum OutputContainer { Mp4, Mkv }

public enum SubtitleOutputType { None, Full, Censored, Dual }

/// <summary>Which embedded subtitle track a player shows when the video opens.</summary>
public enum ShownSubtitles { Off, Full, Censored }

public enum PhononRuntimeMode { Auto, Docker, Local }

public enum DownmixMode { Plain, CenterWeighted }

/// <summary>
/// Everything the user can change, stored as JSON in %LOCALAPPDATA%\CinemaGuard\settings.json.
/// Field names and defaults follow the old WinForms app (see Settings.cs in the old project).
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 5;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public bool FirstRun { get; set; } = true;
    public string PreferredLanguage { get; set; } = "eng";

    // Video
    public OutputContainer OutputContainer { get; set; } = OutputContainer.Mp4;
    public string CustomAffix { get; set; } = "_CG";
    public bool AspectRatio { get; set; }
    public int AspectRatioWidth { get; set; } = 16;
    public int AspectRatioHeight { get; set; } = 9;

    // Subtitles
    public bool CreateSubtitles { get; set; } = true;
    public SubtitleOutputType SubtitleOutput { get; set; } = SubtitleOutputType.Dual;
    public bool EmbedSubtitles { get; set; } = true;

    /// <summary>
    /// The embedded track shown when the video opens (flagged default, and in an MP4 also the flag VLC needs).
    /// When only the other one is written, that one is shown.
    /// </summary>
    public ShownSubtitles ShowSubtitles { get; set; } = ShownSubtitles.Full;

    public string CensorCharacter { get; set; } = "*";

    /// <summary>The source's forced subtitle tracks (foreign lines only), masked with the filter and kept.</summary>
    public bool KeepForcedSubtitles { get; set; } = true;

    /// <summary>The source's other subtitle tracks, copied unchanged. They are not censored, so off by default.</summary>
    public bool KeepOtherSubtitles { get; set; }

    // Filter
    public List<string> WordsFull { get; set; } = [];
    public List<string> WordsSingle { get; set; } = [];
    // Extra mute either side of a hit. Single words: around the speech model's word timing, or around
    // the guessed spot when only the subtitles have the word. Whole lines (the sentence list): around
    // the subtitle line or the model's sentence, which covers subtitles timed early or late.
    public int EstimatedPadBeforeMs { get; set; } = 500;
    public int EstimatedPadAfterMs { get; set; } = 500;
    public int WordPadBeforeMs { get; set; } = 150;
    public int WordPadAfterMs { get; set; } = 250;
    public int LinePadBeforeMs { get; set; } = 250;
    public int LinePadAfterMs { get; set; } = 500;

    /// <summary>
    /// How long the sound fades to silence before each mute and back after it, so a mute never clicks.
    /// 0 cuts straight to silence. The fade sits outside the mute: the muted stretch itself stays silent.
    /// </summary>
    public int MuteFadeMs { get; set; } = 30;

    // Transcription
    public PhononRuntimeMode PhononRuntime { get; set; } = PhononRuntimeMode.Auto;

    /// <summary>
    /// The Phonon CUDA image this version of Cinema Guard is tested with. A new app version moves it on
    /// (bump it here); a saved setting left on an older build follows (<see cref="Normalise"/>), the
    /// queue and the Tools page then offer the download, and the old image is removed once the new one is in.
    /// </summary>
    public const string RecommendedDockerImage = "ghcr.io/fermionresearch/phonon-cuda:1.0.11";

    public string DockerImage { get; set; } = RecommendedDockerImage;
    public string? DockerPath { get; set; }
    public string? LocalFermionPath { get; set; }
    public string? FfmpegPath { get; set; }
    public string? FfprobePath { get; set; }
    public string? SubloaderPath { get; set; }
    public bool StartDockerDesktop { get; set; } = true;
    public bool RunMultilingualPass { get; set; } = true;
    public string PhononHotwords { get; set; } = string.Empty;
    public double MaxCps { get; set; } = 21.0;
    public DownmixMode DialogueDownmix { get; set; } = DownmixMode.Plain;

    // Speech model (the Models page)
    /// <summary>A catalogue model id, <see cref="SpeechCatalog.PairId"/> (the default) or <see cref="SpeechCatalog.RemoteId"/>.</summary>
    public string SpeechModel { get; set; } = SpeechCatalog.PairId;

    /// <summary>The variant picked in each model row's dropdown, by model id.</summary>
    public Dictionary<string, string> SpeechVariants { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Parakeet rows' CPU/GPU switch, by model id. Phonon's GPU switch is <see cref="PhononRuntime"/>.</summary>
    public Dictionary<string, bool> SpeechUseGpu { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string RemoteUrl { get; set; } = string.Empty;
    public string RemoteModel { get; set; } = string.Empty;

    /// <summary>The server's API key, encrypted for this Windows user (DPAPI).</summary>
    public string? RemoteApiKeyProtected { get; set; }

    /// <summary>Split uploads above this size for providers with a cap (OpenAI: 25). 0 sends the whole file.</summary>
    public int RemoteMaxUploadMb { get; set; }

    public HardwareInfo? Hardware { get; set; }
    public bool SetupGuideShown { get; set; }

    /// <summary>The version an update was started to (on restart or close); the next start says whether it installed.</summary>
    public string? PendingUpdateVersion { get; set; }

    /// <summary>The version that started last; the first start of a newer one shows what changed since (the changelog).</summary>
    public string? LastRunVersion { get; set; }

    // Behaviour
    public bool MuteVocalsOnly { get; set; }
    public bool ForceSameAudioEncoding { get; set; } = true;
    public bool KeepOriginalAudio { get; set; }

    /// <summary>The source's other audio tracks (other languages, commentary), copied unchanged. Not censored, so off by default.</summary>
    public bool KeepOtherAudioTracks { get; set; }

    public bool SkipMediaNoSwearing { get; set; } = true;

    /// <summary>
    /// Censor first only finds each file's words; they are reviewed (and any left in) before the second Censor
    /// mutes them. A headless run (-start) then only scans, and hands its files to the window for the review.
    /// </summary>
    public bool ReviewBeforeCensoring { get; set; }

    public bool DeleteOriginalFiles { get; set; }

    /// <summary>With <see cref="DeleteOriginalFiles"/> on: delete the originals for good instead of sending them to the Recycle Bin.</summary>
    public bool DeleteOriginalsPermanently { get; set; }
    /// <summary>Tick Generate subtitles on every file added to the queue. Off: nothing is transcribed unless ticked by hand.</summary>
    public bool GenerateSubtitlesForNewFiles { get; set; }

    /// <summary>Tick Extract subtitles on every file added: the subtitle track inside the video is read and used to find words.</summary>
    public bool ExtractSubtitlesForNewFiles { get; set; } = true;

    /// <summary>
    /// Keep each file's transcripts and generated subtitles in Cinema Guard's own folder (never beside the
    /// video), so censoring it again skips transcription. Unused ones are removed after 30 days. Off: a
    /// file's are removed as soon as it is done.
    /// </summary>
    public bool KeepTranscripts { get; set; } = true;
    public bool AlwaysOverwriteSubtitles { get; set; }
    public bool AlignSubtitleFiles { get; set; } = true;
    public bool RetimeOutputSubtitles { get; set; } = true;
    public bool SpliceForeignCues { get; set; }
    public bool WriteOneWordSrt { get; set; }
    public bool SkipAlreadyProcessed { get; set; } = true;
    public bool KeepWorkFiles { get; set; }

    [JsonIgnore]
    public bool CreatesSubtitles => CreateSubtitles && SubtitleOutput != SubtitleOutputType.None;

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings CreateDefault()
    {
        var settings = new AppSettings();
        settings.WordsFull = FilterCatalog.DefaultWordsFull();
        settings.WordsSingle = FilterCatalog.DefaultWordsSingle();
        settings.Normalise();
        return settings;
    }

    public AppSettings Clone()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? CreateDefault();
    }

    /// <summary>Repairs anything hand-edited or imported from the old app. Never throws.</summary>
    public void Normalise()
    {
        CustomAffix = SanitiseAffix(CustomAffix);

        if (!IsValidCensorCharacter(CensorCharacter))
            CensorCharacter = "*";

        if (AspectRatioWidth is < 1 or > 100) AspectRatioWidth = 16;
        if (AspectRatioHeight is < 1 or > 100) AspectRatioHeight = 9;

        EstimatedPadBeforeMs = Math.Clamp(EstimatedPadBeforeMs, 0, 5000);
        EstimatedPadAfterMs = Math.Clamp(EstimatedPadAfterMs, 0, 5000);
        WordPadBeforeMs = Math.Clamp(WordPadBeforeMs, 0, 2000);
        WordPadAfterMs = Math.Clamp(WordPadAfterMs, 0, 2000);
        LinePadBeforeMs = Math.Clamp(LinePadBeforeMs, 0, 5000);
        LinePadAfterMs = Math.Clamp(LinePadAfterMs, 0, 5000);
        MuteFadeMs = Math.Clamp(MuteFadeMs, 0, 500);
        MaxCps = Math.Clamp(MaxCps, 8, 45);

        WordsFull = CleanWords(WordsFull);
        WordsSingle = CleanWords(WordsSingle);

        if (string.IsNullOrWhiteSpace(PreferredLanguage) || PreferredLanguage.Equals("Detect", StringComparison.OrdinalIgnoreCase))
            PreferredLanguage = "eng";

        DockerImage = Phonon.DockerImages.Follow(DockerImage, RecommendedDockerImage);

        // Version 3 replaced the multilingual-pass checkbox with the speech model choice.
        if (SchemaVersion < 3)
        {
            if (!RunMultilingualPass) SpeechModel = SpeechCatalog.PhononEnglishId;
        }

        // Versions 4 and 5 added spellings to the word families (Motherfuck, Goddammit, Fukin, ...).
        if (SchemaVersion < 5) FilterCatalog.AddNewSpellings(WordsFull, WordsSingle, SchemaVersion);

        SchemaVersion = CurrentSchemaVersion;

        if (!SpeechCatalog.IsKnownSelection(SpeechModel)) SpeechModel = SpeechCatalog.PairId;
        SpeechVariants = new Dictionary<string, string>(SpeechVariants ?? [], StringComparer.OrdinalIgnoreCase);
        SpeechUseGpu = new Dictionary<string, bool>(SpeechUseGpu ?? [], StringComparer.OrdinalIgnoreCase);
        RemoteUrl = (RemoteUrl ?? string.Empty).Trim();
        RemoteModel = (RemoteModel ?? string.Empty).Trim();
        RemoteMaxUploadMb = Math.Clamp(RemoteMaxUploadMb, 0, 4096);

        PhononHotwords = (PhononHotwords ?? string.Empty).Trim();
        foreach (var path in new[] { DockerPath, LocalFermionPath, FfmpegPath, FfprobePath, SubloaderPath })
        {
            // Paths are free text; nothing to normalise at this point.
            _ = path;
        }
    }

    /// <summary>One symbol, or a space to leave the word blank. Letters, digits, tabs and line breaks are refused.</summary>
    public static bool IsValidCensorCharacter(string? value) =>
        value is { Length: 1 } && (value[0] == ' ' || !(char.IsLetterOrDigit(value[0]) || char.IsWhiteSpace(value[0]) || char.IsControl(value[0])));

    /// <summary>
    /// The character written over each letter. A space becomes a non-breaking space: a cue that was only the
    /// word would otherwise be a blank line, which FFmpeg drops from the embedded track (see CensoredSubtitleWriter).
    /// </summary>
    public char MaskCharacter() =>
        !IsValidCensorCharacter(CensorCharacter) ? '*' : CensorCharacter[0] == ' ' ? ' ' : CensorCharacter[0];

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(CustomAffix) || CustomAffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            problems.Add("The custom affix cannot be empty or contain invalid file name characters.");

        if (!IsValidCensorCharacter(CensorCharacter))
            problems.Add("The censor character must be a single symbol or a space.");

        if (WordsFull.Count == 0 && WordsSingle.Count == 0)
            problems.Add("The filter is empty: add at least one word.");

        if (!string.IsNullOrWhiteSpace(PhononHotwords) && PhononHotwords.Split(',').Length > 25)
            problems.Add("Phonon hotwords accept at most 25 terms.");

        return problems;
    }

    public static string SanitiseAffix(string? affix)
    {
        if (string.IsNullOrWhiteSpace(affix)) return "_CG";

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(affix.Where(c => Array.IndexOf(invalid, c) < 0).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "_CG" : cleaned;
    }

    private static List<string> CleanWords(IEnumerable<string>? words)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var word in words ?? [])
        {
            if (string.IsNullOrWhiteSpace(word)) continue;
            // One space between the words of a phrase, so "HOLY  SHIT" and "HOLY SHIT" are one entry.
            var cleaned = string.Join(' ', word.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
            if (cleaned.Length == 0) continue;
            if (seen.Add(cleaned)) result.Add(cleaned);
        }

        return result;
    }
}
