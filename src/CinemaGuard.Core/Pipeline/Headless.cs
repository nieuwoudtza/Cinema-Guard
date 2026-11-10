using System.Text.Json;
using CinemaGuard.Core.Logging;

namespace CinemaGuard.Core.Pipeline;

/// <summary>
/// The command line: paths, `-start` for a headless run, and the options a calling program uses with it.
/// Anything that is not an option is a path, so an older build given these options only sees paths that
/// do not exist (and ignores them).
/// </summary>
public sealed record LaunchArguments(
    bool Headless,
    IReadOnlyList<string> Paths,
    string? ReportPath = null,
    bool Progress = false,
    string? CancelEvent = null,
    bool Help = false,
    bool Review = false)
{
    public const string ReportOption = "--report";
    public const string ProgressOption = "--progress";
    public const string CancelEventOption = "--cancel-event";

    /// <summary>Open the window with the files and scan them for the review; how a -start run hands them over.</summary>
    public const string ReviewOption = "--review";

    /// <summary>
    /// What `--help` prints and Settings → Command line shows: every option, the exit codes and where the
    /// report goes. Kept beside the parser so the two cannot drift apart.
    /// </summary>
    public static string HelpText { get; } = """
        Cinema Guard command line

        Usage
          Cinema Guard.exe [files or folders...]            Open the queue with these files
          Cinema Guard.exe [files or folders...] -start     Censor them without a window, then exit

        Files can be videos (MKV, MP4, AVI, M4V, MOV, WEBM, TS, ...) or audio files (MP3, M4B, M4A,
        FLAC, WAV, OGG, OPUS, ...). A folder adds its videos, subfolders included with -start; a folder
        with no videos adds its audio files instead. A second launch hands its files to the open window.

        Options
          -start, --start           Run headless: no window and no dialogs. Uses the saved settings.
                                    With Review words before censoring on, the files are only scanned
                                    (status Scanned, no output, nothing deleted) and then wait in the
                                    window, which opens if needed, for the review and Censor.
          --review                  Open the window with the files and find their words for the
                                    review. What -start hands over with; it works on its own too.
          --report <path>           Also write the run's JSON report to <path> (with -start).
          --progress                One JSON line on stdout per change of step or whole percent, at most
                                    one a second, plus every status change (with -start):
                                    {"file":"...","status":"Running","step":"Encoding","progress":42.5}
          --cancel-event <name>     Stop the run when the caller signals this named event (Local\...).
                                    The file being worked on is cancelled cleanly (with -start).
          -h, --help, -?, /?        Show this text and exit.

        Report
          Every headless run writes %LOCALAPPDATA%\CinemaGuard\logs\last-run.json: per file the input,
          status, message, output and hit count, plus the summary and exit code. A file waiting for the
          review has status Scanned and no output yet.

        Exit codes (-start)
          0   Every file succeeded, was skipped or waits for the review
          1   At least one file failed or was cancelled
          2   No video or audio files in the arguments
          3   The run itself crashed (see the log in %LOCALAPPDATA%\CinemaGuard\logs)

        Example
          "Cinema Guard.exe" "D:\Films\Heat (1995).mkv" -start --report "D:\Films\heat.json"
        """;

    public static LaunchArguments Parse(IEnumerable<string> arguments)
    {
        var paths = new List<string>();
        var headless = false;
        var progress = false;
        var help = false;
        var review = false;
        string? report = null;
        string? cancelEvent = null;

        using var enumerator = arguments.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var argument = enumerator.Current;

            if (Is(argument, "-start") || Is(argument, "--start"))
            {
                headless = true;
            }
            else if (Is(argument, "--help") || Is(argument, "-h") || Is(argument, "-?") || Is(argument, "/?"))
            {
                help = true;
            }
            else if (Is(argument, ProgressOption))
            {
                progress = true;
            }
            else if (Is(argument, ReviewOption))
            {
                review = true;
            }
            else if (Is(argument, ReportOption))
            {
                if (enumerator.MoveNext()) report = enumerator.Current;
            }
            else if (Is(argument, CancelEventOption))
            {
                if (enumerator.MoveNext()) cancelEvent = enumerator.Current;
            }
            else
            {
                paths.Add(argument);
            }
        }

        return new LaunchArguments(
            headless,
            paths,
            string.IsNullOrWhiteSpace(report) ? null : report,
            progress,
            string.IsNullOrWhiteSpace(cancelEvent) ? null : cancelEvent,
            help,
            review);
    }

    private static bool Is(string argument, string option) =>
        string.Equals(argument, option, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a headless run's exit code means to the program that started it.</summary>
public static class HeadlessExitCodes
{
    /// <summary>Every file succeeded or was skipped.</summary>
    public const int Success = 0;

    /// <summary>At least one file failed or was cancelled; the report says which.</summary>
    public const int Failed = 1;

    /// <summary>The arguments named no video or audio file.</summary>
    public const int NoFiles = 2;

    /// <summary>The run itself crashed; the log has the exception.</summary>
    public const int Crashed = 3;

    public static int For(QueueSummary summary) =>
        summary.Total == 0 ? NoFiles : summary.Fits ? Success : Failed;
}

/// <summary>The JSON a headless run leaves behind: last-run.json, and the caller's own file with --report.</summary>
public static class HeadlessReport
{
    public static string LastRunPath => Path.Combine(Log.Directory, "last-run.json");

    public static string Build(IReadOnlyList<MediaItem> items, TimeSpan elapsed, string summary, int exitCode) =>
        JsonSerializer.Serialize(new
        {
            finishedAt = DateTimeOffset.Now,
            elapsedSeconds = Math.Round(elapsed.TotalSeconds, 1),
            summary,
            exitCode,
            files = items.Select(item => new
            {
                input = item.InputPath,
                status = item.Status.ToString(),
                message = item.Message,
                output = item.OutputPath,
                hits = item.HitCount,
            }),
        }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Writes the report to last-run.json and, when given, the caller's path. Never throws.</summary>
    public static void Write(IReadOnlyList<MediaItem> items, TimeSpan elapsed, string summary, int exitCode, string? reportPath)
    {
        var json = Build(items, elapsed, summary, exitCode);

        foreach (var path in reportPath is null ? new[] { LastRunPath } : [LastRunPath, reportPath])
        {
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                // Written beside it and moved in, so a caller never reads half a report.
                var partial = path + ".partial";
                File.WriteAllText(partial, json);
                File.Move(partial, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not write the report {path}: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// One JSON line per change of a file's step or whole percent, at most one a second while the file runs,
/// for --progress: {"file":"D:\\Films\\Heat.mkv","status":"Running","step":"Encoding (3.1x)","progress":42.5}.
/// A change of status (started, done, failed) is always written.
/// </summary>
public sealed class HeadlessProgress(TextWriter output, TimeProvider? time = null)
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<MediaItem, (MediaStatus Status, string Step, double Progress, DateTimeOffset At)> _last = [];

    public void Report(MediaItem item)
    {
        var status = item.Status;
        var step = item.StatusLabel;
        var progress = Math.Round(item.Progress, 1);
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            if (_last.TryGetValue(item, out var last) && last.Status == status)
            {
                var changed = last.Step != step || Math.Abs(progress - last.Progress) >= 1;
                if (!changed || now - last.At < Interval) return;
            }

            _last[item] = (status, step, progress, now);

            try
            {
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    file = item.InputPath,
                    status = status.ToString(),
                    step,
                    progress,
                }));
                output.Flush();
            }
            catch (Exception ex)
            {
                // The caller stopped reading; the run carries on.
                Log.Debug($"Could not write progress: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// --cancel-event: the caller signals this named event to stop the run. The pipeline then cancels the
/// file it is on and removes its .partial output, which a killed process cannot do.
/// </summary>
public sealed class HeadlessCancellation : IDisposable
{
    private readonly EventWaitHandle? _event;
    private readonly RegisteredWaitHandle? _wait;

    private HeadlessCancellation(EventWaitHandle? handle, RegisteredWaitHandle? wait)
    {
        _event = handle;
        _wait = wait;
    }

    public static HeadlessCancellation Watch(string? eventName, CancellationTokenSource cancellation)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return new HeadlessCancellation(null, null);

        try
        {
            // Created if the caller has not made it yet; either way both sides share it by name.
            var handle = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            var wait = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) =>
            {
                Log.Info("Headless run: the caller asked to stop.");
                try { cancellation.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
            }, null, Timeout.Infinite, executeOnlyOnce: true);

            return new HeadlessCancellation(handle, wait);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not watch the cancel event {eventName}: {ex.Message}");
            return new HeadlessCancellation(null, null);
        }
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _event?.Dispose();
    }
}

/// <summary>
/// Marks a headless run as in progress, so the window does not install an update (which replaces the
/// folder the run is executing from) until it is done. Every run keeps a handle to one named mutex
/// without owning it; the name exists while any run is alive, and Windows closes the handle even when
/// the process is killed.
/// </summary>
public static class HeadlessRuns
{
    public const string DefaultName = @"Local\CinemaGuard.Headless";

    /// <summary>Tests use their own name.</summary>
    public static string Name { get; set; } = DefaultName;

    /// <summary>Held until disposed. Never throws: a run that cannot mark itself still runs.</summary>
    public static IDisposable Enter()
    {
        try
        {
            return new Mutex(initiallyOwned: false, Name);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not mark the headless run: {ex.Message}");
            return new Unmarked();
        }
    }

    /// <summary>True while any headless run is alive.</summary>
    public static bool AnyActive()
    {
        try
        {
            if (!Mutex.TryOpenExisting(Name, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not check for headless runs: {ex.Message}");
            return false;
        }
    }

    private sealed class Unmarked : IDisposable
    {
        public void Dispose() { }
    }
}
