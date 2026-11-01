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
        SpeechService speech)
    {
        _updates = updates;
        _speech = speech;
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
        _clockTimer.Tick += (_, _) => ElapsedText = Format(_clock.Elapsed);

        _pipeline.ItemChanged += OnItemChanged;
        _settingsService.Changed += () => _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(Settings));
            OnPropertyChanged(nameof(SpeechSummary));
        });
        _speech.Models.Changed += () => _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(SpeechSummary)));
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
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _queueSummaryText = "No files queued";
    [ObservableProperty] private string _windowSubtitle = string.Empty;
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
        media.GenerateSubtitles = ShouldGenerate(media);
        media.SubtitleSource = media.SubtitleSourceOnDisk();

        Items.Add(new MediaItemViewModel(media));
        Log.Info($"Queued {Path.GetFileName(full)}");
    }

    private bool ShouldGenerate(MediaItem item)
    {
        if (Settings.AlwaysGenerateSubtitles) return true;

        return !File.Exists(item.SidecarPath) &&
               !File.Exists(item.GeneratedPath) &&
               !File.Exists(item.OneWordPath) &&
               !item.HasAnyTranscriptCache;
    }

    public void Remove(IEnumerable<MediaItemViewModel> items)
    {
        if (IsRunning) return;

        foreach (var item in items.ToList()) Items.Remove(item);
        UpdateAggregates();
    }

    public void Clear()
    {
        if (IsRunning) return;

        Items.Clear();
        UpdateAggregates();
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
                _ => ShouldGenerate(item.Item),
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
            var media = Items.Select(item => item.Item).ToList();
            var token = _cancellation.Token;

            var summary = await Task.Run(() => _queueRunner.RunAsync(media, Settings, token), token);

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
                item.StatusText = "Subtitles extracted";
                item.Message = Path.GetFileName(path);
                target.GenerateSubtitles = false;
                extracted++;
            }
            catch (Exception ex)
            {
                item.StatusText = "Extraction failed";
                item.Message = ex.Message;
                Log.Warn($"{item.Stem}: {ex.Message}");
            }

            target.Sync();
        }

        ShowSummary("Extract subtitles", $"{extracted}/{attempted} extracted successfully.", extracted == attempted ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
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

            var options = new HitCollectorOptions(
                Settings.WordsFull, Settings.WordsSingle,
                Settings.EstimatedPadBeforeMs, Settings.EstimatedPadAfterMs,
                Settings.WordPadBeforeMs, Settings.WordPadAfterMs);

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
            QueueSummaryText = "No files queued";
            WindowSubtitle = string.Empty;
            return;
        }

        var finished = Items.Count(item => item.Item.IsFinished);
        var progress = Items.Average(item => item.Item.Status switch
        {
            MediaStatus.Succeeded or MediaStatus.Skipped or MediaStatus.Failed or MediaStatus.Cancelled => 100,
            _ => item.Item.Progress,
        });

        OverallProgress = progress;
        QueueSummaryText = $"{Items.Count} file{(Items.Count == 1 ? string.Empty : "s")} · {finished} finished";
        WindowSubtitle = QueueSummaryText;

        if (!IsRunning) return;

        var current = Items.FirstOrDefault(item => item.Item.Status == MediaStatus.Running);

        if (current is not null)
        {
            var index = Items.IndexOf(current) + 1;
            WindowSubtitle = $"Cinema Guard [{index}/{Items.Count}]";
        }
    }

    private void ShowSummary(string title, string message, InfoBarSeverity severity)
    {
        SummaryTitle = title;
        SummaryMessage = message;
        SummarySeverity = severity;
        IsSummaryOpen = true;
        Log.Info($"{title}: {message}");
    }

    private static string Format(TimeSpan time) =>
        $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
}

public sealed record ForeignSpanDto(string Range, string Language, string Segments, string Text, string EnglishText);
