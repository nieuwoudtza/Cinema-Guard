using System.Text.Json;
using CinemaGuard.Core.Pipeline;

namespace CinemaGuard.Core.Tests;

public class HeadlessTests
{
    [Fact]
    public void ParsesPathsAndStartLikeBefore()
    {
        var arguments = LaunchArguments.Parse([@"D:\Films\Heat.mkv", "-START", @"D:\Films\Ran.mp4"]);

        Assert.True(arguments.Headless);
        Assert.Equal([@"D:\Films\Heat.mkv", @"D:\Films\Ran.mp4"], arguments.Paths);
        Assert.Null(arguments.ReportPath);
        Assert.False(arguments.Progress);
        Assert.Null(arguments.CancelEvent);
    }

    [Fact]
    public void ParsesTheCallerOptions()
    {
        var arguments = LaunchArguments.Parse(
            [@"D:\Films\Heat.mkv", "--start", "--report", @"C:\Temp\r.json", "--PROGRESS", "--cancel-event", @"Local\Stop.1"]);

        Assert.True(arguments.Headless);
        Assert.Equal([@"D:\Films\Heat.mkv"], arguments.Paths);
        Assert.Equal(@"C:\Temp\r.json", arguments.ReportPath);
        Assert.True(arguments.Progress);
        Assert.Equal(@"Local\Stop.1", arguments.CancelEvent);
    }

    [Fact]
    public void AnOptionWithoutItsValueIsIgnored()
    {
        var arguments = LaunchArguments.Parse(["file.mkv", "-start", "--report"]);

        Assert.Equal(["file.mkv"], arguments.Paths);
        Assert.Null(arguments.ReportPath);
    }

    [Fact]
    public void WithoutStartItIsAWindowLaunch()
    {
        var arguments = LaunchArguments.Parse([@"D:\Films\Heat.mkv"]);

        Assert.False(arguments.Headless);
        Assert.Equal([@"D:\Films\Heat.mkv"], arguments.Paths);
    }

    [Theory]
    [InlineData(1, 0, 0, 0, HeadlessExitCodes.Success)]
    [InlineData(1, 2, 0, 0, HeadlessExitCodes.Success)]
    [InlineData(0, 1, 0, 0, HeadlessExitCodes.Success)]
    [InlineData(1, 0, 1, 0, HeadlessExitCodes.Failed)]
    [InlineData(0, 0, 0, 1, HeadlessExitCodes.Failed)]
    [InlineData(0, 0, 0, 0, HeadlessExitCodes.NoFiles)]
    public void ExitCodeFollowsTheSummary(int succeeded, int skipped, int failed, int cancelled, int expected)
    {
        var summary = new QueueSummary(succeeded, skipped, failed, cancelled, TimeSpan.FromSeconds(3));

        Assert.Equal(expected, HeadlessExitCodes.For(summary));
    }

    [Fact]
    public void ReportListsEveryFile()
    {
        var item = new MediaItem(@"D:\Films\Heat.mkv")
        {
            Status = MediaStatus.Succeeded,
            OutputPath = @"D:\Films\Heat_CG.mp4",
            Message = "Done",
        };

        using var json = JsonDocument.Parse(HeadlessReport.Build([item], TimeSpan.FromSeconds(12.34), "1 done", 0));
        var root = json.RootElement;

        Assert.Equal("1 done", root.GetProperty("summary").GetString());
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(12.3, root.GetProperty("elapsedSeconds").GetDouble());

        var file = Assert.Single(root.GetProperty("files").EnumerateArray());
        Assert.Equal(@"D:\Films\Heat.mkv", file.GetProperty("input").GetString());
        Assert.Equal("Succeeded", file.GetProperty("status").GetString());
        Assert.Equal(@"D:\Films\Heat_CG.mp4", file.GetProperty("output").GetString());
    }

    [Fact]
    public void ProgressIsThrottledButStatusChangesAlwaysShow()
    {
        var time = new ManualTime();
        var output = new StringWriter();
        var progress = new HeadlessProgress(output, time);
        var item = new MediaItem(@"D:\Films\Heat.mkv") { Status = MediaStatus.Running, StatusText = "Encoding" };

        progress.Report(item);                // first line
        time.Advance(TimeSpan.FromSeconds(2));
        item.Progress = 0.5;
        progress.Report(item);                // under a percent changed
        item.Progress = 41;
        progress.Report(item);                // a percent and a second later
        item.Progress = 50;
        progress.Report(item);                // too soon
        item.Status = MediaStatus.Succeeded;
        progress.Report(item);                // finished: always

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.Contains("\"progress\":41", lines[1]);
        Assert.Contains("\"status\":\"Succeeded\"", lines[2]);
        Assert.Contains("\"step\":\"Done\"", lines[2]);
    }

    [Fact]
    public async Task CancelEventCancelsTheRun()
    {
        var name = @"Local\CinemaGuard.Tests.Cancel." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource();
        using var watch = HeadlessCancellation.Watch(name, cancellation);

        using (var signal = EventWaitHandle.OpenExisting(name)) signal.Set();

        await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token).ContinueWith(_ => { });
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public void ARunIsMarkedUntilItEnds()
    {
        var previous = HeadlessRuns.Name;
        HeadlessRuns.Name = @"Local\CinemaGuard.Tests.Headless." + Guid.NewGuid().ToString("N");

        try
        {
            Assert.False(HeadlessRuns.AnyActive());

            var first = HeadlessRuns.Enter();
            var second = HeadlessRuns.Enter();
            Assert.True(HeadlessRuns.AnyActive());

            first.Dispose();
            Assert.True(HeadlessRuns.AnyActive());

            second.Dispose();
            Assert.False(HeadlessRuns.AnyActive());
        }
        finally
        {
            HeadlessRuns.Name = previous;
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
