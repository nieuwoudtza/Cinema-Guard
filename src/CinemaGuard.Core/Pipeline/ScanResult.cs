using CinemaGuard.Core.Intervals;
using CinemaGuard.Core.Media;
using CinemaGuard.Core.Phonon;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Speech;
using CinemaGuard.Core.Subtitles;

namespace CinemaGuard.Core.Pipeline;

/// <summary>
/// What finding the words in a file produced, everything censoring it still needs. With "Review words
/// before censoring" it is held on the item, in memory only, between the scan and Censor; the review
/// unticks <see cref="Occurrences"/> to leave words in.
/// </summary>
public sealed class ScanResult
{
    public required MediaProbe Probe { get; init; }
    public required AudioSelection Audio { get; init; }

    /// <summary>The subtitles the words were found in (a file or a track), or null.</summary>
    public required List<Cue>? SubtitleCues { get; init; }

    /// <summary>The subtitles written into the output: the original ones, else the generated or one-word cues.</summary>
    public required List<Cue>? OutputCues { get; init; }

    public required PhononResult? English { get; init; }

    /// <summary>Every hit found, before the review; censoring leaves out the occurrences it unticked.</summary>
    public required HitSet Hits { get; init; }

    public required IReadOnlyList<WordOccurrence> Occurrences { get; init; }

    /// <summary>The speech model ran in the scan, rather than a saved transcript being read.</summary>
    public required bool Transcribed { get; init; }

    /// <summary>How long the scan took; added to the censoring for the file's time in the run history.</summary>
    public TimeSpan Elapsed { get; set; }

    /// <summary>The settings that decide which words are found (see <see cref="KeyFor"/>), as they were for the scan.</summary>
    public required string SettingsKey { get; init; }

    /// <summary>The FFmpeg and FFprobe the scan checked; a held scan checks them again before censoring.</summary>
    internal string Ffmpeg { get; init; } = string.Empty;
    internal string Ffprobe { get; init; } = string.Empty;

    /// <summary>The review was opened and saved (a file with nothing found needs none).</summary>
    public bool Reviewed { get; set; }

    public int LeftInCount => Occurrences.Count(occurrence => !occurrence.Mute);

    /// <summary>
    /// The settings a scan's words depend on. When they change before Censor, the scan is out of date and
    /// the file is scanned again rather than censored with words the filter no longer asks for.
    /// </summary>
    public static string KeyFor(AppSettings settings) => string.Join("|",
        string.Join(",", settings.WordsFull),
        string.Join(",", settings.WordsSingle),
        settings.EstimatedPadBeforeMs, settings.EstimatedPadAfterMs,
        settings.WordPadBeforeMs, settings.WordPadAfterMs,
        settings.LinePadBeforeMs, settings.LinePadAfterMs,
        settings.AlignSubtitleFiles,
        settings.PreferredLanguage,
        settings.DialogueDownmix,
        string.Join(",", SpeechService.Plan(settings).Passes.Select(pass => pass.CacheKey)));
}
