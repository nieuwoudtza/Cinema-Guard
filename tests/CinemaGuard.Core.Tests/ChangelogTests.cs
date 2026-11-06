namespace CinemaGuard.Core.Tests;

public class ChangelogTests
{
    private const string Sample = """
        # Changelog

        Intro text, not a change.

        - Not a change either: before the first release.

        ## Unreleased

        - Coming next.

        ## 2026.11.06.1 — 6 November 2026

        - Peek says what each run worked from.
        - A change written
          over two lines.

        ## 2026.11.05.2 — 5 November 2026

        - Vocals-only mode.

        ## 2026.11.01.1 — 1 November 2026

        - The first release.
        """;

    private static readonly IReadOnlyList<ChangelogEntry> Entries = Changelog.Parse(Sample);

    [Fact]
    public void ReadsEachReleaseWithItsDateAndChanges()
    {
        Assert.Equal(["Unreleased", "2026.11.06.1", "2026.11.05.2", "2026.11.01.1"], Entries.Select(entry => entry.Title));

        Assert.Null(Entries[0].Version);
        Assert.Null(Entries[0].Date);
        Assert.Equal(["Coming next."], Entries[0].Changes);

        Assert.Equal(new Version(2026, 11, 6, 1), Entries[1].Version);
        Assert.Equal("6 November 2026", Entries[1].Date);
        Assert.Equal(["Peek says what each run worked from.", "A change written over two lines."], Entries[1].Changes);
    }

    [Fact]
    public void ReadsWindowsLineEndings()
    {
        var entries = Changelog.Parse(Sample.Replace("\n", "\r\n"));

        Assert.Equal("6 November 2026", entries[1].Date);
        Assert.Equal("A change written over two lines.", entries[1].Changes[1]);
    }

    [Theory]
    [InlineData("2026.11.05.2", "2026.11.06.1", new[] { "2026.11.06.1" })]
    [InlineData("2026.11.01.1", "2026.11.06.1", new[] { "2026.11.06.1", "2026.11.05.2" })]
    [InlineData("2026.11.01.1", "2026.11.05.2", new[] { "2026.11.05.2" })]
    // An update past a release with no entry of its own (or a development build) still shows the ones between.
    [InlineData("2026.11.03.4", "2026.11.06.3", new[] { "2026.11.06.1", "2026.11.05.2" })]
    // No version on record: only the one running now.
    [InlineData(null, "2026.11.06.1", new[] { "2026.11.06.1" })]
    [InlineData("2026.11.06.1", "2026.11.06.1", new string[0])]
    // Going back to an older version shows nothing.
    [InlineData("2026.11.06.1", "2026.11.05.2", new string[0])]
    [InlineData("2026.11.05.2", "0.0.0-dev", new string[0])]
    public void SinceListsTheReleasesAfterThePreviousOne(string? previous, string current, string[] expected) =>
        Assert.Equal(expected, Changelog.Since(previous, current, Entries).Select(entry => entry.Title));

    [Fact]
    public void TheEmbeddedChangelogIsWellFormed()
    {
        var entries = Changelog.Entries;
        Assert.NotEmpty(entries);

        // Only the top one may be unreleased; the rest are dated release numbers, newest first, each with a change.
        var released = entries.SkipWhile(entry => entry.Title == "Unreleased").ToList();
        Assert.True(entries.Count - released.Count <= 1);
        Assert.All(released, entry =>
        {
            Assert.NotNull(entry.Version);
            Assert.Equal(4, entry.Title.Split('.').Length);
            Assert.False(string.IsNullOrEmpty(entry.Date));
            Assert.NotEmpty(entry.Changes);
        });
        Assert.Equal(released.Select(entry => entry.Version).OrderByDescending(version => version), released.Select(entry => entry.Version));
        Assert.Contains(entries, entry => entry.Title == "2026.11.01.1");
    }
}
