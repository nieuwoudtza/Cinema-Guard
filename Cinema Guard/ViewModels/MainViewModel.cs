using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CinemaGuard.Core;
using CinemaGuard.Core.Ffmpeg;
using CinemaGuard.Core.Intervals;
using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Media;
using CinemaGuard.Core.Phonon;
using CinemaGuard.Core.Pipeline;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Sources;
using CinemaGuard.Core.Speech;
using CinemaGuard.Core.Subtitles;
using CinemaGuard.Core.Tools;
using Cinema_Guard.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cinema_Guard.ViewModels;

public sealed record PeekRow(string Word, int Count, string Times);

public sealed record PeekContent(string Header, string Subtitle, IReadOnlyList<PeekRow> Rows);

public enum GenerateMode { All, Missing, None }

/// <summary>The queue window's state: the file list, the run loop, and the per-row actions.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly QueueRunner _queueRunner;
    private readonly CensorPipeline _pipeline;
    private readonly SettingsService _settingsService;
    private readonly ToolLocator _locator;
    private readonly IProcessRunner _runner;
    private readonly SubloaderService _subloader;
    private readonly EmbeddedSubtitleExtractor _extractor;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _clockTimer;
    private readonly Stopwatch _clock = new();

    private CancellationTokenSource? _cancellation;

    /// <summary>The files a running queue has still to start, in queue order. The runner takes from it on its own thread.</summary>
    private readonly List<MediaItem> _pending = [];

    private readonly SpeechService _speech;

    public MainViewModel(
        QueueRunner queueRunner,
        CensorPipeline pipeline,
        SettingsService settingsService,
        ToolLocator locator,
        IProcessRunner runner,
        SubloaderService subloader,
        EmbeddedSubtitleExtractor extractor,
        UpdateService updates,
        SpeechService speech,
        PhononService phonon,
        DockerImagePuller puller)
    {
        _updates = updates;
        _speech = speech;
        _phonon = phonon;
        _puller = puller;
        _queueRunner = queueRunner;
        _pipeline = pipeline;
        _settingsService = settingsService;
        _locator = locator;
        _runner = runner;
        _subloader = subloader;
        _extractor = extractor;

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _clockTimer = _dispatcher.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += (_, _) =>
        {
            ElapsedText = Format(_clock.Elapsed);
            foreach (var item in Items.Where(item => item.Item.Status == MediaStatus.Running)) item.RefreshElapsed();
            UpdateAggregates();
        };

        _pipeline.ItemChanged += OnItemChanged;
        _settingsService.Changed += () => _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(Settings));
            OnPropertyChanged(nameof(SpeechSummary));
        });
        _speech.Models.Changed += () => _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(SpeechSummary)));
        _puller.Changed += OnPullerChanged;
    }

    /// <summary>The chip next to Start: which model the next run uses, and on what.</summary>
    public string SpeechSummary => _speech.Describe(Settings, shortForm: true);

    /// <summary>
    /// Why Start would fail for want of a download (in words), or null. Only files that still need a
    /// transcript count; a file with a saved transcript of this model runs without the model.
    /// </summary>
    public string? MissingSpeechModel()
    {
        var plan = SpeechService.Plan(Settings);
        var needsModel = Items.Any(item => item.GenerateSubtitles && !File.Exists(item.Item.CachePathFor(plan.Primary.CacheKey)));
        if (!needsModel) return null;

        return plan.Passes.Select(pass => _speech.Problem(pass, Settings)).FirstOrDefault(problem => problem is not null);
    }

    public ObservableCollection<MediaItemViewModel> Items { get; } = [];

    public SettingsService SettingsService => _settingsService;
    public AppSettings Settings => _settingsService.Current;

    [ObservableProperty] private string _statusText = "Drop video files here, or add them with the buttons above.";
    [ObservableProperty] private double _overallProgress;

    /// <summary>"37% overall · file 2 of 5" in the footer while the queue runs; empty otherwise.</summary>
    [ObservableProperty] private string _overallProgressText = string.Empty;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _queueSummaryText = "No files queued";
    [ObservableProperty] private string _elapsedText = string.Empty;
    [ObservableProperty] private bool _isSummaryOpen;
    [ObservableProperty] private string _summaryTitle = string.Empty;
    [ObservableProperty] private string _summaryMessage = string.Empty;
    [ObservableProperty] private InfoBarSeverity _summarySeverity = InfoBarSeverity.Informational;

    public string Version => $"v{CoreInfo.Version}";

    // ---------------------------------------------------------------- queue

    public void AddPaths(IEnumerable<string> paths, bool recursive)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in EnumerateVideos(path, recursive)) AddFile(file);
            }
            else
            {
                AddFile(path);
            }
        }

        UpdateAggregates();
    }

    private static IEnumerable<string> EnumerateVideos(string folder, bool recursive)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(file => FileTypes.IsSupportedVideo(Path.GetExtension(file)))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read {folder}: {ex.Message}");
            return [];
        }
    }

    private void AddFile(string path)
    {
        string full;

        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Ignoring '{path}': {ex.Message}");
            return;
        }

        if (!File.Exists(full) || !FileTypes.IsSupportedVideo(Path.GetExtension(full))) return;
        if (Items.Any(item => string.Equals(item.Item.InputPath, full, StringComparison.OrdinalIgnoreCase))) return;

        var media = new MediaItem(full);

        // Files an earlier version left beside the video go into Cinema Guard's cache straight away.
        media.MoveLegacyFiles();
        media.GenerateSubtitles = Settings.GenerateSubtitlesForNewFiles;
        media.ExtractSubtitles = Settings.ExtractSubtitlesForNewFiles;
        media.SubtitleSource = media.SubtitleSourceOnDisk();
        media.ExpectedSeconds = RunHistory.Default.EstimateFile(full, media.GenerateSubtitles);

        Items.Add(new MediaItemViewModel(media));

        // A file added while the queue runs joins the end of it.
        if (IsRunning)
        {
            lock (_pending) _pending.Add(media);
        }

        Log.Info($"Queued {Path.GetFileName(full)}");
    }

    /// <summary>For "Selected files without any subtitles": no subtitle file and nothing transcribed before.</summary>
    private static bool HasNoSubtitles(MediaItem item) =>
        !File.Exists(item.SidecarPath) &&
        !File.Exists(item.GeneratedPath) &&
        !File.Exists(item.OneWordPath) &&
        !item.HasAnyTranscriptCache;

    /// <summary>Takes files off the queue. While it runs, a waiting file never starts and the running one is cancelled.</summary>
    public void Remove(IEnumerable<MediaItemViewModel> items)
    {
        foreach (var item in items.ToList())
        {
            if (IsRunning)
            {
                lock (_pending) _pending.Remove(item.Item);
                _queueRunner.Remove(item.Item);
            }

            Items.Remove(item);
        }

        UpdateAggregates();
    }

    public void Clear() => Remove(Items);

    /// <summary>The next file for the running queue, or null when none is left.</summary>
    private MediaItem? TakeNext()
    {
        lock (_pending)
        {
            if (_pending.Count == 0) return null;

            var next = _pending[0];
            _pending.RemoveAt(0);
            return next;
        }
    }

    public void SetGenerateMode(GenerateMode mode, IEnumerable<MediaItemViewModel>? targets = null)
    {
        if (IsRunning) return;

        foreach (var item in targets ?? Items)
        {
            item.GenerateSubtitles = mode switch
            {
                GenerateMode.All => true,
                GenerateMode.None => false,
                _ => HasNoSubtitles(item.Item),
            };
        }
    }

    public void SetCustomSubtitle(MediaItemViewModel item, string path)
    {
        if (!File.Exists(path) || !FileTypes.IsSupportedSubtitle(Path.GetExtension(path))) return;

        item.Item.CustomSubtitlePath = path;
        item.Item.SubtitleSource = "Selected file";
        item.GenerateSubtitles = false;
        item.Sync();
    }

    public void OpenFolder(MediaItemViewModel item)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Item.InputPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open the folder: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- run

    public async Task StartAsync()
    {
        if (IsRunning) return;

        if (Items.Count == 0)
        {
            ShowSummary("No media queued", "Add at least one video file before starting.", InfoBarSeverity.Warning);
            return;
        }

        if (Settings.WordsFull.Count == 0 && Settings.WordsSingle.Count == 0)
        {
            ShowSummary("No filter applied", "Open the Filter page and select at least one word.", InfoBarSeverity.Warning);
            return;
        }

        var problems = Settings.Validate();
        if (problems.Count > 0)
        {
            ShowSummary("Check the settings", string.Join(" ", problems), InfoBarSeverity.Error);
            return;
        }

        IsRunning = true;
        IsSummaryOpen = false;
        OverallProgress = 0;
        _cancellation = new CancellationTokenSource();
        _clock.Restart();
        _clockTimer.Start();

        foreach (var item in Items) item.ResetForRun();
        UpdateAggregates();

        try
        {
            lock (_pending)
            {
                _pending.Clear();
                _pending.AddRange(Items.Select(item => item.Item));
            }

            var token = _cancellation.Token;

            var summary = await Task.Run(() => _queueRunner.RunAsync(TakeNext, Settings, token), token);

            StatusText = summary.Describe();
            ShowSummary(
                summary.Fits ? "Finished" : "Finished with problems",
                $"{summary.Describe()} in {Format(summary.Elapsed)}.",
                summary.Fits ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled";
            ShowSummary("Cancelled", "The queue was stopped.", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            Log.Error("The queue failed", ex);
            StatusText = "Failed";
            ShowSummary("Something went wrong", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsRunning = false;
            lock (_pending) _pending.Clear();
            _clock.Stop();
            _clockTimer.Stop();
            _cancellation?.Dispose();
            _cancellation = null;
            UpdateAggregates();
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;

        StatusText = "Stopping…";
        _cancellation?.Cancel();
    }

    // ---------------------------------------------------------------- subtitle actions

    /// <summary>Extracts an embedded text track to &lt;name&gt;.srt (never overwriting an existing one).</summary>
    public async Task ExtractSubtitlesAsync(IEnumerable<MediaItemViewModel> targets)
    {
        if (IsRunning) return;

        var ffmpeg = _locator.ResolveFfmpeg(Settings);
        var ffprobe = _locator.ResolveFfprobe(Settings);

        if (ffmpeg is null || ffprobe is null)
        {
            ShowSummary("FFmpeg is missing", "Open Tools and press Set up to install it.", InfoBarSeverity.Error);
            return;
        }

        var extracted = 0;
        var attempted = 0;

        foreach (var target in targets)
        {
            attempted++;
            var item = target.Item;
            item.StatusText = "Extracting subtitles";
            target.Sync();

            try
            {
                var probeResult = await _runner.RunAsync(new ProcessSpec(ffprobe, FfmpegCommands.Probe(item.InputPath)));
                if (!probeResult.Success) throw new Exception("FFprobe could not read the file.");

                var probe = MediaProbeParser.Parse(probeResult.StdOut, item.InputPath);
                var stream = StreamSelector.SelectSubtitle(probe, Settings.PreferredLanguage, out var reason)
                             ?? throw new Exception(reason ?? "No text subtitle track was found.");

                var targetPath = File.Exists(item.SidecarPath)
                    ? Path.Combine(item.Folder, $"{item.Stem}.{stream.Language ?? "eng"}.srt")
                    : item.SidecarPath;

                var path = await _extractor.ExtractAsync(ffmpeg, probe, stream, targetPath, CancellationToken.None);

                if (path is null) throw new Exception("FFmpeg could not extract the track.");

                item.CustomSubtitlePath = path;
                item.SubtitleSource = "Embedded";
                item.StatusText = "Subtitles saved";
                item.Message = Path.GetFileName(path);
                target.GenerateSubtitles = false;
                extracted++;
            }
            catch (Exception ex)
            {
                item.StatusText = "Could not save the subtitles";
                item.Message = ex.Message;
                Log.Warn($"{item.Stem}: {ex.Message}");
            }

            target.Sync();
        }

        ShowSummary("Save subtitles as .srt", $"{extracted}/{attempted} saved beside the video.", extracted == attempted ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    public async Task DownloadSubtitlesAsync(IEnumerable<MediaItemViewModel> targets)
    {
        if (IsRunning) return;

        var subloader = _locator.ResolveSubloader(Settings);

        if (subloader is null)
        {
            ShowSummary("Subloader is missing", "Install subloader-cli or set its path in Settings.", InfoBarSeverity.Error);
            return;
        }

        var downloaded = 0;
        var attempted = 0;
        var quota = false;

        foreach (var target in targets)
        {
            if (quota) break;

            attempted++;
            var item = target.Item;
            item.StatusText = "Downloading subtitles";
            target.Sync();

            try
            {
                var result = await _subloader.DownloadAsync(subloader, item.InputPath, Settings.PreferredLanguage, CancellationToken.None);
                quota = result.QuotaReached;

                if (result.Success && result.SubtitlePath is not null)
                {
                    item.CustomSubtitlePath = result.SubtitlePath;
                    item.SubtitleSource = "Downloaded";
                    item.GenerateSubtitles = false;
                    item.StatusText = "Subtitles downloaded";
                    item.Message = Path.GetFileName(result.SubtitlePath);
                    downloaded++;
                }
                else
                {
                    item.StatusText = "Download failed";
                    item.Message = result.Message;
                }
            }
            catch (Exception ex)
            {
                item.StatusText = "Download failed";
                item.Message = ex.Message;
            }

            target.Sync();
        }

        ShowSummary(
            quota ? "OpenSubtitles quota reached" : "Download subtitles",
            quota
                ? "The daily quota is used up; the rest were left alone."
                : $"{downloaded}/{attempted} downloaded successfully.",
            quota || downloaded < attempted ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    public Task<PeekContent> BuildPeekAsync(MediaItemViewModel target)
    {
        var item = target.Item;

        return Task.Run(() =>
        {
            if (item.Hits is not null) return Build(item.Hits);

            var options = HitCollectorOptions.From(Settings);

            List<Cue>? cues = null;

            foreach (var path in new[] { item.CustomSubtitlePath, item.SidecarPath, item.GeneratedPath, item.OneWordPath })
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

                try
                {
                    cues = SubtitleReader.ReadFile(path).Cues;
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not read {path}: {ex.Message}");
                }
            }

            var set = HitCollector.Collect(options, item.Probe?.DurationSeconds ?? 0, 0, null, null, cues, null, false);
            return Build(set);
        });

        PeekContent Build(HitSet set)
        {
            var rows = set.Hits
                .GroupBy(hit => hit.Label)
                .OrderByDescending(group => group.Count())
                .Select(group =>
                {
                    var times = string.Join(", ", group.OrderBy(hit => hit.Start).Take(6)
                        .Select(hit => TimeSpan.FromSeconds(Math.Max(0, hit.Start)).ToString(@"hh\:mm\:ss\.fff")));

                    if (group.Count() > 6) times += ", …";
                    return new PeekRow(group.Key, group.Count(), times);
                })
                .ToList();

            var subtitle = $"{Plural(set.Hits.Count, "hit")} · {Plural(set.Ranges.Count, "mute range")}";
            if (set.Fit.Accepted) subtitle += $" · subtitles retimed ({(set.Fit.Slope - 1) * 100:+0.0;-0.0} %, {set.Fit.Intercept:+0.00;-0.00} s)";

            return new PeekContent(item.Stem, subtitle, rows);
        }

        static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
    }

    public IReadOnlyList<ForeignSpanDto> GetForeignSpans(MediaItemViewModel target)
    {
        var spans = target.Item.ForeignSpans;

        if (spans.Count == 0 && File.Exists(target.Item.LanguageReportPath))
        {
            try
            {
                var json = File.ReadAllText(target.Item.LanguageReportPath);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<List<ForeignSpan>>(json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (loaded is not null) spans = loaded;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read the language report: {ex.Message}");
            }
        }

        return spans.Select(span => new ForeignSpanDto(
                span.Range,
                $"{span.LanguageName} ({span.LanguageCode})",
                span.Segments.ToString(),
                span.Text,
                string.IsNullOrWhiteSpace(span.EnglishText) ? "—" : span.EnglishText))
            .ToList();
    }

    // ---------------------------------------------------------------- helpers

    private void OnItemChanged(MediaItem item)
    {
        _dispatcher.TryEnqueue(() =>
        {
            var target = Items.FirstOrDefault(candidate => ReferenceEquals(candidate.Item, item));
            target?.Sync();
            UpdateAggregates();
        });
    }

    private void UpdateAggregates()
    {
        IsEmpty = Items.Count == 0;

        if (Items.Count == 0)
        {
            OverallProgress = 0;
            OverallProgressText = string.Empty;
            QueueSummaryText = "No files queued";
            return;
        }

        var finished = Items.Count(item => item.Item.IsFinished);
        // Each file weighs what it is expected to take, so a long film moves the total more than a short episode.
        var weights = Items.Sum(item => Math.Max(1, item.Item.ExpectedSeconds));
        var progress = Items.Sum(item => Math.Max(1, item.Item.ExpectedSeconds) * (item.Item.Status switch
        {
            MediaStatus.Succeeded or MediaStatus.Skipped or MediaStatus.Failed or MediaStatus.Cancelled => 100,
            _ => item.Item.Progress,
        })) / weights;

        OverallProgress = progress;
        QueueSummaryText = $"{Items.Count} file{(Items.Count == 1 ? string.Empty : "s")} · {finished} finished";
        OverallProgressText = string.Empty;

        if (!IsRunning) return;

        OverallProgressText = $"{Math.Floor(progress):0}% overall";

        var current = Items.FirstOrDefault(item => item.Item.Status == MediaStatus.Running);

        if (current is not null)
            OverallProgressText += $" · file {Items.IndexOf(current) + 1} of {Items.Count}";

        // The running file's time left plus every waiting file's expected time.
        var remaining = Items.Sum(item => item.Item.EstimatedRemaining?.TotalSeconds ?? 0);
        if (remaining > 0) OverallProgressText += " · " + FormatRemaining(TimeSpan.FromSeconds(remaining));
    }

    private void ShowSummary(string title, string message, InfoBarSeverity severity)
    {
        SummaryTitle = title;
        SummaryMessage = message;
        SummarySeverity = severity;
        IsSummaryOpen = true;
        Log.Info($"{title}: {message}");
    }

    private static string Format(TimeSpan time) => FormatElapsed(time);

    internal static string FormatElapsed(TimeSpan time) =>
        $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";

    /// <summary>"less than a minute left", "about 12 min left", "about 1 h 20 min left": an estimate, so rounded.</summary>
    internal static string FormatRemaining(TimeSpan time)
    {
        if (time.TotalSeconds < 60) return "less than a minute left";

        var minutes = (int)Math.Ceiling(time.TotalMinutes);
        return minutes < 60 ? $"about {minutes} min left" : $"about {minutes / 60} h {minutes % 60} min left";
    }
}

public sealed record ForeignSpanDto(string Range, string Language, string Segments, string Text, string EnglishText);
