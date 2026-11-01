using CinemaGuard.Core.Pipeline;

namespace CinemaGuard.Core.Tests;

public class RunHistoryTests
{
    private static readonly StepFeatures Film = new()
    {
        Drive = "f:\\",
        DriveType = "Fixed",
        Bytes = 4L * 1024 * 1024 * 1024,
        DurationSeconds = 7200,
        VideoHeight = 1080,
        AudioCodec = "eac3",
        AudioChannels = 6,
    };

    private static RunHistory NewHistory(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), "cg-history-" + Guid.NewGuid().ToString("N") + ".json");
        return new RunHistory(path);
    }

    [Fact]
    public void FallsBackToTheGuessWithoutHistory()
    {
        var history = NewHistory(out _);
        Assert.Equal(42, history.Estimate(StepKind.Encode, Film, 42), 3);
    }

    [Fact]
    public void ScalesAnEarlierRunBySize()
    {
        var history = NewHistory(out var path);

        try
        {
            // Ten runs of 100 s on a 4 GB film: the history outweighs the guess.
            for (var i = 0; i < 10; i++) history.Record(StepKind.Encode, Film, TimeSpan.FromSeconds(100));

            var twiceAsBig = Film with { Bytes = Film.Bytes * 2, DurationSeconds = Film.DurationSeconds * 2 };
            var estimate = history.Estimate(StepKind.Encode, twiceAsBig, 1000);

            Assert.InRange(estimate, 200, 300);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PrefersRunsFromTheSameDrive()
    {
        var history = NewHistory(out var path);

        try
        {
            var network = Film with { Drive = "\\\\nas\\media", DriveType = "Network" };

            for (var i = 0; i < 5; i++)
            {
                history.Record(StepKind.Encode, Film, TimeSpan.FromSeconds(60));
                history.Record(StepKind.Encode, network, TimeSpan.FromSeconds(600));
            }

            var local = history.Estimate(StepKind.Encode, Film, 300);
            var remote = history.Estimate(StepKind.Encode, network, 300);

            Assert.True(local < 200, $"local {local}");
            Assert.True(remote > 400, $"remote {remote}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void KeepsTheHistoryBetweenRuns()
    {
        var history = NewHistory(out var path);

        try
        {
            for (var i = 0; i < 10; i++) history.Record(StepKind.ExtractAudio, Film, TimeSpan.FromSeconds(30));

            var reopened = new RunHistory(path);
            Assert.InRange(reopened.Estimate(StepKind.ExtractAudio, Film, 300), 30, 60);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TranscriptionOnlyLearnsFromTheSameEngine()
    {
        var history = NewHistory(out var path);

        try
        {
            var gpu = Film with { Engine = "phonon-2.5-en|GPU (Docker)" };
            var cpu = Film with { Engine = "phonon-2.5-en|CPU" };

            for (var i = 0; i < 10; i++) history.Record(StepKind.Transcribe, gpu, TimeSpan.FromSeconds(60));

            Assert.Equal(500, history.Estimate(StepKind.Transcribe, cpu, 500), 3);
            Assert.InRange(history.Estimate(StepKind.Transcribe, gpu, 500), 60, 100);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IgnoresShortClips()
    {
        var history = NewHistory(out var path);

        history.Record(StepKind.Encode, Film with { DurationSeconds = 30 }, TimeSpan.FromSeconds(5));

        Assert.False(File.Exists(path));
        Assert.Equal(42, history.Estimate(StepKind.Encode, Film, 42), 3);
    }
}
