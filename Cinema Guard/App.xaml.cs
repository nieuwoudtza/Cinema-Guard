using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Media;
using CinemaGuard.Core.Pipeline;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Speech;
using CinemaGuard.Core.Tools;
using Cinema_Guard.Services;
using Cinema_Guard.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace Cinema_Guard;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;
    public static IServiceProvider Services { get; private set; } = null!;
    public static MainWindow? Shell { get; private set; }
    public static MainViewModel? MainViewModel { get; private set; }
    public static SettingsService Settings { get; private set; } = null!;
    public static bool IsHeadless { get; private set; }

    private InstanceCoordinator? _instances;

    public App()
    {
        InitializeComponent();

        // Log and keep running: an exception in one click handler must not close the app mid-queue.
        UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Init();
        Log.Info($"Launch: {Environment.CommandLine}");

        var commandLine = LaunchArguments.Parse(Environment.GetCommandLineArgs().Skip(1));

        Services = AppServices.Build();
        Settings = Services.GetRequiredService<SettingsService>();

        // What the GPU and CPU can run decides every device option; found once, then on Re-detect.
        if (Settings.Current.Hardware is null)
        {
            Settings.Current.Hardware = HardwareDetector.Detect();
            Settings.Save();
        }

        WorkFolder.CleanupOld(TimeSpan.FromDays(2));
        _ = Task.Run(() => MediaItem.CleanupCache(TimeSpan.FromDays(30)));

        // Archives from an install that was closed or crashed: a fresh one would be downloaded again.
        _ = Task.Run(() => Services.GetRequiredService<ModelManager>().SweepDownloads(TimeSpan.FromDays(1)));

        if (commandLine.Headless)
        {
            RunHeadless(commandLine);
            return;
        }

        _instances = new InstanceCoordinator(InstanceCoordinator.WindowKey);

        if (!_instances.IsFirstInstance)
        {
            _instances.SendToRunningInstance(commandLine.Review ? commandLine.Paths.Append(LaunchArguments.ReviewOption) : commandLine.Paths);
            Exit();
            return;
        }

        _instances.LaunchReceived += lines =>
            Shell?.DispatcherQueue.TryEnqueue(() =>
            {
                Shell.BringToFront();

                var arguments = LaunchArguments.Parse(lines);
                if (arguments.Paths.Count > 0) MainViewModel?.AddPaths(arguments.Paths, recursive: false);
                if (arguments.Review && MainViewModel is not null) _ = MainViewModel.ScanForReviewAsync();
            });

        MainViewModel = Services.GetRequiredService<MainViewModel>();

        Shell = new MainWindow();
        Shell.Activate();

        if (commandLine.Paths.Count > 0) MainViewModel.AddPaths(commandLine.Paths, recursive: false);
        if (commandLine.Review) _ = MainViewModel.ScanForReviewAsync();
    }

    /// <summary>
    /// A -start run with Review words before censoring on scanned these files: they go to the window, opened
    /// if it is not, which finds their words again from the saved transcripts and waits for the review.
    /// </summary>
    private static void HandOverForReview(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        if (InstanceCoordinator.SendToWindow(InstanceCoordinator.WindowKey, paths.Append(LaunchArguments.ReviewOption)))
        {
            Log.Info($"Headless run: handed {paths.Count} file(s) to the open window for the review.");
            return;
        }

        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var path in paths) start.ArgumentList.Add(path);
            start.ArgumentList.Add(LaunchArguments.ReviewOption);

            System.Diagnostics.Process.Start(start);
            Log.Info($"Headless run: opened the window with {paths.Count} file(s) for the review.");
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the window for the review", ex);
        }
    }

    /// <summary>
    /// `Cinema Guard.exe "file" -start` — no window, no dialogs. The per-file outcome is written to
    /// last-run.json (and the --report path), the exit code sums it up (see <see cref="HeadlessExitCodes"/>),
    /// --progress writes JSON lines to stdout and --cancel-event lets the caller stop the run cleanly.
    /// </summary>
    private void RunHeadless(LaunchArguments arguments)
    {
        IsHeadless = true;
        RecycleBin.ShowErrorDialogs = false;

        // Held to the end, so a window that closes meanwhile does not install an update under this run.
        var running = HeadlessRuns.Enter();
        var cancellation = new CancellationTokenSource();
        var cancelEvent = HeadlessCancellation.Watch(arguments.CancelEvent, cancellation);

        if (arguments.Progress)
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            var progress = new HeadlessProgress(stdout);
            Services.GetRequiredService<CensorPipeline>().ItemChanged += progress.Report;
        }

        _ = Task.Run(async () =>
        {
            var items = new List<MediaItem>();
            var exitCode = HeadlessExitCodes.Crashed;

            try
            {
                var settings = Settings.Current;

                foreach (var path in arguments.Paths)
                {
                    if (Directory.Exists(path))
                    {
                        items.AddRange(FileTypes.MediaInFolder(path, recursive: true).Select(file => new MediaItem(file)));
                    }
                    else if (File.Exists(path))
                    {
                        items.Add(new MediaItem(path));
                    }
                }

                foreach (var item in items)
                {
                    var audioFile = FileTypes.IsAudioFile(item.InputPath);
                    item.GenerateSubtitles = audioFile || ShouldGenerate(item, settings);
                    item.ExtractSubtitles = settings.ExtractSubtitlesForNewFiles && !audioFile;
                }

                if (items.Count > 0)
                {
                    // With the review on, nothing is censored unseen: the files are scanned, then reviewed in the window.
                    var review = settings.ReviewBeforeCensoring;
                    var runner = Services.GetRequiredService<QueueRunner>();
                    var summary = await runner.RunAsync(items, settings, cancellation.Token, scanOnly: review);

                    exitCode = HeadlessExitCodes.For(summary);
                    HeadlessReport.Write(items, summary.Elapsed, summary.Describe(), exitCode, arguments.ReportPath);
                    Log.Info($"Headless run: {summary.Describe()} (exit code {exitCode})");

                    if (review) HandOverForReview(items.Where(item => item.Status == MediaStatus.Scanned).Select(item => item.InputPath).ToList());
                }
                else
                {
                    // Still replace last-run.json, so a caller never reads the previous run's result.
                    exitCode = HeadlessExitCodes.NoFiles;
                    HeadlessReport.Write(items, TimeSpan.Zero, "No video or audio files in the arguments.", exitCode, arguments.ReportPath);
                    Log.Info("Headless run: no video or audio files in the arguments.");
                }
            }
            catch (Exception ex)
            {
                Log.Error("The headless run failed", ex);
                exitCode = HeadlessExitCodes.Crashed;
                HeadlessReport.Write(items, TimeSpan.Zero, $"The run failed: {ex.Message}", exitCode, arguments.ReportPath);
            }
            finally
            {
                cancelEvent.Dispose();
                running.Dispose();
                Environment.Exit(exitCode);
            }
        });
    }

    /// <summary>
    /// A headless run has no one to tick Generate, so besides the setting it also transcribes a file
    /// that has no subtitles at all (else it could only fail, as the old app's -start never did).
    /// </summary>
    private static bool ShouldGenerate(MediaItem item, AppSettings settings)
    {
        if (settings.GenerateSubtitlesForNewFiles) return true;

        item.MoveLegacyFiles();
        return !File.Exists(item.SidecarPath) &&
               !File.Exists(item.GeneratedPath) &&
               !File.Exists(item.OneWordPath) &&
               !item.HasAnyTranscriptCache;
    }
}
