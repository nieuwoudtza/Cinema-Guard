namespace CinemaGuard.Core;

/// <summary>One release in CHANGELOG.md: "2026.11.06.1" and its date, or "Unreleased" (no version), with its changes.</summary>
public sealed record ChangelogEntry(string Title, Version? Version, string? Date, IReadOnlyList<string> Changes);

/// <summary>
/// The release notes, from CHANGELOG.md at the repository root (embedded in this assembly). A release is a
/// "## 2026.11.06.1 — 6 November 2026" heading followed by "- " lines; a line indented under one carries it on.
/// "## Unreleased" collects changes until publish.ps1 gives it the version it releases.
/// </summary>
public static class Changelog
{
    private const string ResourceName = "CHANGELOG.md";
    private const string DateSeparator = " — ";

    /// <summary>Every release, newest first, as written in the file.</summary>
    public static IReadOnlyList<ChangelogEntry> Entries { get; } = Parse(ReadEmbedded());

    public static IReadOnlyList<ChangelogEntry> Parse(string text)
    {
        var entries = new List<ChangelogEntry>();
        string? title = null;
        string? date = null;
        List<string> changes = [];

        void Finish()
        {
            if (title is null) return;
            entries.Add(new ChangelogEntry(title, Version.TryParse(title, out var version) ? version : null, date, changes));
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Finish();
                var heading = line[3..].Trim();
                var separator = heading.IndexOf(DateSeparator, StringComparison.Ordinal);
                title = separator < 0 ? heading : heading[..separator].Trim();
                date = separator < 0 ? null : heading[(separator + DateSeparator.Length)..].Trim();
                changes = [];
            }
            else if (title is null)
            {
                // The file's own title and introduction.
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                changes.Add(line[2..].Trim());
            }
            else if (line.Length > 0 && char.IsWhiteSpace(line[0]) && line.Trim().Length > 0 && changes.Count > 0)
            {
                changes[^1] += " " + line.Trim();
            }
        }

        Finish();
        return entries;
    }

    /// <summary>
    /// The releases after <paramref name="previous"/> up to and including <paramref name="current"/>, newest first:
    /// what is new to someone who ran <paramref name="previous"/>. With no previous version, only
    /// <paramref name="current"/>'s own entry. Empty when <paramref name="current"/> is not a release number.
    /// </summary>
    public static IReadOnlyList<ChangelogEntry> Since(string? previous, string current, IReadOnlyList<ChangelogEntry>? entries = null)
    {
        entries ??= Entries;
        if (!Version.TryParse(current, out var to)) return [];

        if (!Version.TryParse(previous, out var from))
            return entries.Where(entry => entry.Version == to).ToList();

        return entries.Where(entry => entry.Version is { } version && version > from && version <= to).ToList();
    }

    private static string ReadEmbedded()
    {
        using var stream = typeof(Changelog).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return string.Empty;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
