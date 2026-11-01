using CinemaGuard.Core.Ffmpeg;
using CinemaGuard.Core.Pipeline;
using CinemaGuard.Core.Settings;

namespace CinemaGuard.Core.Tests;

public class FileProgressTests
{
    [Fact]
    public void WeighsStepsByTheirExpectedTime()
    {
        var progress = new FileProgress();
        progress.Plan("audio", 10);
        progress.Plan("transcribe", 60);
        progress.Plan("encode", 30);

        progress.Complete("audio");
        Assert.Equal(10, progress.Percent, 3);

        progress.Report("transcribe", 0.5);
        Assert.Equal(40, progress.Percent, 3);
    }

    [Fact]
    public void NeverGoesBackWhenAStepIsAdded()
    {
        var progress = new FileProgress();
        progress.Plan("encode", 50);
        progress.Plan("audio", 50);
        progress.Complete("audio");
        Assert.Equal(50, progress.Percent, 3);

        progress.Plan("channel:FL", 100);
        Assert.Equal(50, progress.Percent, 3);
    }

    [Fact]
    public void DroppingAnUnusedStepMovesForward()
    {
        var progress = new FileProgress();
        progress.Plan("audio", 25);
        progress.Plan("channel:FL", 50);
        progress.Plan("encode", 25);
        progress.Complete("audio");

        progress.Drop("channel:FL");
        Assert.Equal(50, progress.Percent, 3);
    }

    [Fact]
    public void StopsShortOfOneHundredUntilThePipelineFinishes()
    {
        var progress = new FileProgress();
        progress.Plan("encode", 30);
        progress.Complete("encode");

        Assert.True(progress.Percent < 100);
    }

    [Fact]
    public void IgnoresReportsForUnplannedSteps()
    {
        var progress = new FileProgress();
        progress.Plan("encode", 30);
        progress.Report("audio", 1);

        Assert.Equal(0, progress.Percent, 3);
    }

    [Fact]
    public void AudioExtractionWritesProgress()
    {
        var arguments = FfmpegCommands.ExtractAudio16k("in.mkv", 0, DownmixMode.Plain, "5.1", "out.wav");
        Assert.Equal("pipe:1", ArgList.Next(arguments, "-progress"));

        var channel = FfmpegCommands.ExtractChannel16k("in.mkv", 0, "FL", 2.5, "out.wav");
        Assert.Equal("pipe:1", ArgList.Next(channel, "-progress"));
    }
}
