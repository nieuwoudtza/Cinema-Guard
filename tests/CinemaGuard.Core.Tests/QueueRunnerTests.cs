using CinemaGuard.Core.Phonon;
using CinemaGuard.Core.Pipeline;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Speech;
using CinemaGuard.Core.Tools;

namespace CinemaGuard.Core.Tests;

public class QueueRunnerTests
{
    [Fact]
    public async Task PicksUpAddedFilesAndSkipsRemovedOnes()
    {
        var root = Path.Combine(Path.GetTempPath(), "cg-queue-" + Guid.NewGuid().ToString("N"));
        var runner = new ProcessRunner();
        var locator = new ToolLocator(runner);
        var speech = new SpeechService(
            new ModelManager(new SettingsService(Path.Combine(root, "settings.json")), runner),
            new PhononService(runner, locator),
            locator);

        var pipeline = new CensorPipeline(runner, locator, speech);
        var queueRunner = new QueueRunner(pipeline, speech);

        // Files that do not exist fail at the first check, without any tool.
        var first = new MediaItem(Path.Combine(root, "first.mkv"));
        var removed = new MediaItem(Path.Combine(root, "removed.mkv"));
        var added = new MediaItem(Path.Combine(root, "added.mkv"));
        var queue = new List<MediaItem> { first, removed };

        pipeline.ItemChanged += item =>
        {
            if (item != first || queue.Contains(added)) return;

            // While the first file runs: take the second off the queue and add a third.
            queueRunner.Remove(removed);
            queue.Add(added);
        };

        MediaItem? Next()
        {
            if (queue.Count == 0) return null;
            var item = queue[0];
            queue.RemoveAt(0);
            return item;
        }

        var summary = await queueRunner.RunAsync(Next, AppSettings.CreateDefault(), CancellationToken.None);

        Assert.Equal(MediaStatus.Failed, first.Status);
        Assert.Equal(MediaStatus.Waiting, removed.Status);
        Assert.Equal(MediaStatus.Failed, added.Status);
        Assert.Equal(2, summary.Total);
    }
}
