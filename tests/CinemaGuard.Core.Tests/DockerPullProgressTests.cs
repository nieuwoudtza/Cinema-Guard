using CinemaGuard.Core.Phonon;

namespace CinemaGuard.Core.Tests;

public class DockerImagesTests
{
    private const string Recommended = "ghcr.io/fermionresearch/phonon-cuda:1.0.12";

    [Theory]
    [InlineData(null, Recommended)]
    [InlineData("", Recommended)]
    [InlineData("ghcr.io/fermionresearch/phonon-cuda:1.0.11", Recommended)]
    [InlineData("ghcr.io/fermionresearch/phonon-cuda:1.0.12", Recommended)]
    [InlineData("ghcr.io/fermionresearch/phonon-cuda:1.1.0", "ghcr.io/fermionresearch/phonon-cuda:1.1.0")]
    [InlineData("ghcr.io/fermionresearch/phonon-cuda:nightly", "ghcr.io/fermionresearch/phonon-cuda:nightly")]
    [InlineData("myregistry:5000/phonon:1.0.0", "myregistry:5000/phonon:1.0.0")]
    public void AnOlderBuildFollowsTheAppAndOwnChoicesStay(string? configured, string expected) =>
        Assert.Equal(expected, DockerImages.Follow(configured, Recommended));

    [Fact]
    public void SplitsTheTagButNotARegistryPort()
    {
        Assert.Equal(("myregistry:5000/phonon", "1.0.0"), DockerImages.Split("myregistry:5000/phonon:1.0.0"));
        Assert.Equal(("myregistry:5000/phonon", "latest"), DockerImages.Split("myregistry:5000/phonon"));
    }
}

public class DockerPullProgressTests
{
    [Fact]
    public void CountsLayersDownloadedAndUnpacked()
    {
        var progress = new DockerPullProgress();

        progress.Feed("1.0.11: Pulling from fermionresearch/phonon-cuda");
        Assert.True(progress.IsIndeterminate);

        progress.Feed("aaaaaaaaaaaa: Already exists");
        progress.Feed("bbbbbbbbbbbb: Pulling fs layer");
        progress.Feed("cccccccccccc: Pulling fs layer");
        progress.Feed("dddddddddddd: Waiting");

        Assert.Equal(4, progress.Layers);
        Assert.Equal(25, progress.Percent, 3);

        progress.Feed("bbbbbbbbbbbb: Verifying Checksum");
        progress.Feed("bbbbbbbbbbbb: Download complete");
        Assert.Equal(2, progress.Downloaded);
        Assert.Equal(37.5, progress.Percent, 3);

        foreach (var layer in new[] { "bbbbbbbbbbbb", "cccccccccccc", "dddddddddddd" })
            progress.Feed($"{layer}: Pull complete");

        Assert.Equal(100, progress.Percent, 3);
    }

    [Fact]
    public void NeverGoesBackAndIgnoresOtherLines()
    {
        var progress = new DockerPullProgress();
        progress.Feed("aaaaaaaaaaaa: Pull complete");

        Assert.False(progress.Feed("aaaaaaaaaaaa: Waiting"));
        Assert.False(progress.Feed("Digest: sha256:0123"));
        Assert.False(progress.Feed("Status: Downloaded newer image for ghcr.io/fermionresearch/phonon-cuda:1.0.11"));
        Assert.Equal(100, progress.Percent, 3);
    }
}
