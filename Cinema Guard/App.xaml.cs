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

        _instances = new InstanceCoordinator("CinemaGuard2.Instance");

        if (!_instances.IsFirstInstance)
        {
            _instances.SendToRunningInstance(commandLine.Paths);
            Exit();
            return;
        }

        _instances.PathsReceived += paths =>
            Shell?.DispatcherQueue.TryEnqueue(() => MainViewModel?.AddPaths(paths, recursive: false));

        MainViewModel = Services.GetRequiredService<MainViewModel>();

        Shell = new MainWindow();
        Shell.Activate();

        if (commandLine.Paths.Count > 0) MainViewModel.AddPaths(commandLine.Paths, recursive: false);
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
                        try
                        {
                            items.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                                .Where(file => FileTypes.IsSupportedVideo(Path.GetExtension(file)))
                                .Select(file => new MediaItem(file)));
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"Could not read {path}: {ex.Message}");
                        }
                    }
                    else if (File.Exists(path))
                    {
                        items.Add(new MediaItem(path));
                    }
                }

                foreach (var item in items)
                {
                    item.GenerateSubtitles = ShouldGenerate(item, settings);
                    item.ExtractSubtitles = settings.ExtractSubtitlesForNewFiles;
                }

                if (items.Count > 0)
                {
                    var runner = Services.GetRequiredService<QueueRunner>();
                    var summary = await runner.RunAsync(items, settings, cancellation.Token);

                    exitCode = HeadlessExitCodes.For(summary);
                    HeadlessReport.Write(items, summary.Elapsed, summary.Describe(), exitCode, arguments.ReportPath);
                    Log.Info($"Headless run: {summary.Describe()} (exit code {exitCode})");
                }
                else
                {
                    // Still replace last-run.json, so a caller never reads the previous run's result.
                    exitCode = HeadlessExitCodes.NoFiles;
                    HeadlessReport.Write(items, TimeSpan.Zero, "No video files in the arguments.", exitCode, arguments.ReportPath);
                    Log.Info("Headless run: no video files in the arguments.");
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
