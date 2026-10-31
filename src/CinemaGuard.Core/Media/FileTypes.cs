namespace CinemaGuard.Core.Media;

public static class FileTypes
{
    public static IReadOnlyList<string> VideoExtensions { get; } =
        [".avi", ".flv", ".m4v", ".mkv", ".mp4", ".mov", ".webm", ".ts", ".m2ts", ".mpg", ".mpeg", ".wmv", ".vob"];

    public static IReadOnlyList<string> SubtitleExtensions { get; } = [".srt", ".vtt", ".ass", ".ssa"];

    public static bool IsSupportedVideo(string? extension) =>
        extension is not null && VideoExtensions.Contains(extension.ToLowerInvariant());

    public static bool IsSupportedSubtitle(string? extension) =>
        extension is not null && SubtitleExtensions.Contains(extension.ToLowerInvariant());
}
