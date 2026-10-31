namespace CinemaGuard.Core.Media;

public sealed record AudioSelection(MediaStreamInfo Stream, int Ordinal, double OffsetSeconds);

/// <summary>Picks the audio and subtitle track to work on, the way a person would: the film's main track.</summary>
public static class StreamSelector
{
    public static AudioSelection? SelectAudio(MediaProbe probe, string preferredLanguage)
    {
        var audio = probe.AudioStreams.ToList();
        if (audio.Count == 0) return null;

        // Commentary and audio-description tracks are never the film's dialogue.
        var candidates = audio.Where(s => !s.IsComment && !s.IsVisualImpaired).ToList();
        if (candidates.Count == 0) candidates = audio;

        var ranked = candidates
            .Select(stream => new
            {
                Stream = stream,
                Score = (LanguageMatches(stream.Language, preferredLanguage) ? 4 : 0) + (stream.IsDefault ? 2 : 0),
                Ordinal = probe.AudioOrdinal(stream),
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Ordinal)
            .ToList();

        var best = ranked[0];
        var offset = Math.Max(0, (best.Stream.StartTime ?? 0) - probe.StartSeconds);

        return new AudioSelection(best.Stream, best.Ordinal, offset);
    }

    /// <summary>Text subtitle tracks, best first. Forced tracks are last: they only carry foreign lines.</summary>
    public static IReadOnlyList<MediaStreamInfo> RankSubtitles(MediaProbe probe, string preferredLanguage) =>
        probe.SubtitleStreams
            .Where(s => s.IsTextSubtitle)
            .OrderByDescending(s => LanguageMatches(s.Language, preferredLanguage) ? 8 : 0)
            .ThenByDescending(s => s.IsForced ? 0 : 4)
            .ThenByDescending(s => s.IsHearingImpaired ? 0 : 2)
            .ThenByDescending(s => s.IsDefault ? 1 : 0)
            .ThenBy(s => s.Index)
            .ToList();

    public static MediaStreamInfo? SelectSubtitle(MediaProbe probe, string preferredLanguage, out string? reason)
    {
        var ranked = RankSubtitles(probe, preferredLanguage);
        if (ranked.Count > 0)
        {
            reason = null;
            return ranked[0];
        }

        reason = probe.SubtitleStreams.Any(s => s.IsBitmapSubtitle)
            ? "the only subtitles are image-based (PGS/VobSub), which need OCR"
            : null;

        return null;
    }

    public static bool LanguageMatches(string? tag, string preferred)
    {
        if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(preferred)) return false;

        var a = tag.Trim().ToLowerInvariant();
        var b = preferred.Trim().ToLowerInvariant();
        if (a == b) return true;

        // "en" vs "eng", "pt" vs "por", "zh" vs "chi"/"zho".
        return a.Length >= 2 && b.Length >= 2 && a[..2] == b[..2];
    }
}
