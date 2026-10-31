using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Media;
using CinemaGuard.Core.Pipeline;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Speech;
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

        var commandLine = CommandLineArguments.Parse(Environment.GetCommandLineArgs().Skip(1));

        Services = AppServices.Build();
        Settings = Services.GetRequiredService<SettingsService>();

        // What the GPU and CPU can run decides every device option; found once, then on Re-detect.
        if (Settings.Current.Hardware is null)
        {
            Settings.Current.Hardware = HardwareDetector.Detect();
            Settings.Save();
        }

        WorkFolder.CleanupOld(TimeSpan.FromDays(2));

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
    /// `Cinema Guard.exe "file" -start` — no window, no dialogs, exit code 0 like the old app.
    /// The per-file outcome is written to last-run.json for the caller to inspect.
    /// </summary>
    private void RunHeadless(CommandLineArguments arguments)
    {
        IsHeadless = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var settings = Settings.Current;
                var items = new List<MediaItem>();

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

                foreach (var item in items) item.GenerateSubtitles = ShouldGenerate(item, settings);

                if (items.Count > 0)
                {
                    var runner = Services.GetRequiredService<QueueRunner>();
                    var summary = await runner.RunAsync(items, settings, CancellationToken.None);

                    WriteLastRun(items, summary.Elapsed, summary.Describe());
                    Log.Info($"Headless run: {summary.Describe()}");
                }
                else
                {
                    // Still replace last-run.json, so a caller never reads the previous run's result.
                    WriteLastRun(items, TimeSpan.Zero, "No video files in the arguments.");
                    Log.Info("Headless run: no video files in the arguments.");
                }
            }
            catch (Exception ex)
            {
                Log.Error("The headless run failed", ex);
            }
            finally
            {
                Environment.Exit(0);
            }
        });
    }

    private static bool ShouldGenerate(MediaItem item, AppSettings settings)
    {
        if (settings.AlwaysGenerateSubtitles) return true;

        return !File.Exists(item.SidecarPath) &&
               !File.Exists(item.GeneratedPath) &&
               !File.Exists(item.OneWordPath) &&
               !item.HasAnyTranscriptCache;
    }

    private static void WriteLastRun(IReadOnlyList<MediaItem> items, TimeSpan elapsed, string summary)
    {
        try
        {
            var path = Path.Combine(Log.Directory, "last-run.json");

            var payload = new
            {
                finishedAt = DateTimeOffset.Now,
                elapsedSeconds = Math.Round(elapsed.TotalSeconds, 1),
                summary,
                files = items.Select(item => new
                {
                    input = item.InputPath,
                    status = item.Status.ToString(),
                    message = item.Message,
                    output = item.OutputPath,
                    hits = item.HitCount,
                }),
            };

            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write last-run.json: {ex.Message}");
        }
    }
}
