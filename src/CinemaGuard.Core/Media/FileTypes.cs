using CinemaGuard.Core.Logging;

namespace CinemaGuard.Core.Media;

public static class FileTypes
{
    public static IReadOnlyList<string> VideoExtensions { get; } =
        [".avi", ".flv", ".m4v", ".mkv", ".mp4", ".mov", ".webm", ".ts", ".m2ts", ".mpg", ".mpeg", ".wmv", ".vob"];

    /// <summary>Audiobooks, podcasts and music: muted and written back in their own format.</summary>
    public static IReadOnlyList<string> AudioExtensions { get; } =
        [".mp3", ".m4a", ".m4b", ".aac", ".flac", ".wav", ".ogg", ".opus", ".wma"];

    public static IReadOnlyList<string> MediaExtensions { get; } = [.. VideoExtensions, .. AudioExtensions];

    public static IReadOnlyList<string> SubtitleExtensions { get; } = [".srt", ".vtt", ".ass", ".ssa"];

    public static bool IsSupportedVideo(string? extension) =>
        extension is not null && VideoExtensions.Contains(extension.ToLowerInvariant());

    public static bool IsSupportedAudio(string? extension) =>
        extension is not null && AudioExtensions.Contains(extension.ToLowerInvariant());

    public static bool IsSupportedMedia(string? extension) => IsSupportedVideo(extension) || IsSupportedAudio(extension);

    public static bool IsSupportedSubtitle(string? extension) =>
        extension is not null && SubtitleExtensions.Contains(extension.ToLowerInvariant());

    public static bool IsAudioFile(string path) => IsSupportedAudio(Path.GetExtension(path));

    /// <summary>
    /// The media in a folder. Audio files only count when the folder holds no video: a film folder's
    /// theme.mp3 or soundtrack is not something to censor, but a folder of audiobook chapters is.
    /// </summary>
    public static List<string> MediaInFolder(string folder, bool recursive)
    {
        try
        {
            var files = Directory.EnumerateFiles(folder, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList();
            var videos = files.Where(file => IsSupportedVideo(Path.GetExtension(file))).ToList();

            return videos.Count > 0 ? videos : files.Where(file => IsSupportedAudio(Path.GetExtension(file))).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read {folder}: {ex.Message}");
            return [];
        }
    }
}
