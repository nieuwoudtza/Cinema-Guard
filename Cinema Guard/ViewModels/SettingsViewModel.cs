using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Speech;
using CinemaGuard.Core.Tools;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace Cinema_Guard.ViewModels;

/// <summary>Settings page: edits the live <see cref="AppSettings"/> object, saves on demand.</summary>
public sealed partial class SettingsViewModel(SettingsService service, SpeechService speech) : ObservableObject
{
    public AppSettings S => service.Current;

    /// <summary>The Speech model row: the choice and its device; "Change…" opens the Models page.</summary>
    public string SpeechSummary => speech.Describe(S);

    public IReadOnlyList<string> Languages => LanguageNames.CommonLanguages;

    public string Version => $"v{CinemaGuard.Core.CoreInfo.Version}";
    public string SettingsPath => service.SettingsPath;
    public string FilterSummary => $"{S.WordsFull.Count + S.WordsSingle.Count} active word(s)";

    public int ContainerIndex
    {
        get => (int)S.OutputContainer;
        set
        {
            if (value < 0 || value == ContainerIndex) return;
            S.OutputContainer = (OutputContainer)value;
            OnPropertyChanged();
        }
    }

    public int SubtitleTypeIndex
    {
        get => (int)S.SubtitleOutput;
        set
        {
            if (value < 0 || value == SubtitleTypeIndex) return;
            S.SubtitleOutput = (SubtitleOutputType)value;
            OnPropertyChanged();
        }
    }

    public int DownmixIndex
    {
        get => (int)S.DialogueDownmix;
        set
        {
            if (value < 0 || value == DownmixIndex) return;
            S.DialogueDownmix = (DownmixMode)value;
            OnPropertyChanged();
        }
    }

    public int LanguageIndex
    {
        get
        {
            var index = Languages.ToList().IndexOf(S.PreferredLanguage);
            return index < 0 ? 0 : index;
        }
        set
        {
            if (value < 0 || value >= Languages.Count) return;
            S.PreferredLanguage = Languages[value];
            OnPropertyChanged();
        }
    }

    // NumberBox works in doubles; the settings keep integers.
    public double AspectWidth
    {
        get => S.AspectRatioWidth;
        set { S.AspectRatioWidth = (int)Math.Round(value); OnPropertyChanged(); }
    }

    public double AspectHeight
    {
        get => S.AspectRatioHeight;
        set { S.AspectRatioHeight = (int)Math.Round(value); OnPropertyChanged(); }
    }

    public double MaxCpsValue
    {
        get => S.MaxCps;
        set { S.MaxCps = Math.Round(value, 1); OnPropertyChanged(); }
    }

    public double EstimatedPadBefore
    {
        get => S.EstimatedPadBeforeMs;
        set { S.EstimatedPadBeforeMs = (int)Math.Round(value); OnPropertyChanged(); }
    }

    public double EstimatedPadAfter
    {
        get => S.EstimatedPadAfterMs;
        set { S.EstimatedPadAfterMs = (int)Math.Round(value); OnPropertyChanged(); }
    }

    public double WordPadBefore
    {
        get => S.WordPadBeforeMs;
        set { S.WordPadBeforeMs = (int)Math.Round(value); OnPropertyChanged(); }
    }

    public double WordPadAfter
    {
        get => S.WordPadAfterMs;
        set { S.WordPadAfterMs = (int)Math.Round(value); OnPropertyChanged(); }
    }

    public (bool Ok, string Message) Save()
    {
        var problems = S.Validate();

        if (problems.Count > 0) return (false, string.Join(" ", problems));

        service.SaveAs(S);
        OnPropertyChanged(nameof(FilterSummary));
        return (true, "Settings saved.");
    }

    public void ResetToDefaults()
    {
        // The hardware check and the setup guide are facts about this PC, not preferences.
        var defaults = AppSettings.CreateDefault();
        defaults.Hardware = S.Hardware;
        defaults.FirstRun = false;
        defaults.SetupGuideShown = true;
        service.SaveAs(defaults);
    }
}

public sealed record ToolRow(string Name, string Status, string Location, string Version, string Glyph, Brush Brush);

/// <summary>Preflight page: what is installed, what is missing, and the one-click first-run setup.</summary>
public sealed partial class ToolsViewModel(ToolLocator locator, SettingsService settings, SpeechService speech, ModelManager models) : ObservableObject
{
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private string _runtimeSummary = "Not checked yet.";
    [ObservableProperty] private string _ffmpegVersion = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSetup))]
    private bool _ffmpegMissing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSetup))]
    private bool _modelMissing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSetup))]
    private bool _showSetupCard;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSetup))]
    private bool _isSettingUp;

    [ObservableProperty] private double _setupProgress;
    [ObservableProperty] private bool _setupIndeterminate;
    [ObservableProperty] private string _setupStatus = string.Empty;
    [ObservableProperty] private string _ffmpegState = "Not checked";
    [ObservableProperty] private string _modelState = "Not checked";

    public bool CanSetup => ShowSetupCard && !IsSettingUp;

    public ObservableCollection<ToolRow> Tools { get; } = [];

    public async Task RefreshAsync()
    {
        if (IsChecking) return;

        IsChecking = true;

        try
        {
            var snapshot = await locator.CheckAsync(settings.Current);

            Tools.Clear();

            foreach (var tool in snapshot.Tools)
            {
                Tools.Add(new ToolRow(
                    tool.DisplayName,
                    tool.Ready ? "Ready" : tool.Problem ?? "Missing",
                    tool.Path ?? "Not found",
                    tool.Version ?? string.Empty,
                    tool.Ready ? "\uE73E" : "\uE711",
                    ReadyBrush(tool.Ready)));
            }

            var ffmpeg = snapshot.Tools.First(t => t.Key == "ffmpeg");
            FfmpegVersion = ffmpeg.Version ?? string.Empty;
            FfmpegMissing = !ffmpeg.Ready;
            FfmpegState = ffmpeg.Ready ? "Ready" : ffmpeg.Problem ?? "Not installed";

            var plan = SpeechService.Plan(settings.Current);
            var missing = plan.Passes
                .Where(pass => pass.Model is not null && speech.Problem(pass, settings.Current) is not null)
                .ToList();

            ModelMissing = missing.Count > 0;
            ModelState = plan.Primary.Remote is not null
                ? (string.IsNullOrWhiteSpace(settings.Current.RemoteUrl) ? "Set it up on the Models page" : "Ready")
                : (ModelMissing ? "Not downloaded" : "Ready");

            ShowSetupCard = FfmpegMissing || ModelMissing;

            var problem = plan.Passes.Select(pass => speech.Problem(pass, settings.Current)).FirstOrDefault(p => p is not null);

            RuntimeSummary = problem is null
                ? $"Transcription: {speech.Describe(settings.Current)}."
                : $"Transcription: {speech.Describe(settings.Current)}. {problem}";
        }
        catch (Exception ex)
        {
            Log.Error("The tool check failed", ex);
            RuntimeSummary = $"The tool check failed: {ex.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>
    /// The one button on the first run: install FFmpeg, then download every model the saved choice
    /// needs. Defaults are already the recommended Phonon pair, so this is the whole setup.
    /// </summary>
    public async Task<bool> SetupAsync()
    {
        if (IsSettingUp) return false;

        IsSettingUp = true;
        SetupProgress = 0;
        SetupIndeterminate = false;
        SetupStatus = "Starting…";

        try
        {
            if (FfmpegMissing)
            {
                var progress = new Progress<ToolProgress>(value =>
                {
                    SetupStatus = value.Status + (value.Total > 0 ? $" ({value.Percent:0}%)" : string.Empty);
                    SetupProgress = value.Percent;
                    SetupIndeterminate = value.Total <= 0;
                });

                await ToolDownloader.InstallAsync(progress, CancellationToken.None);
                SetupStatus = "FFmpeg is ready. Next: the speech model.";
                await RefreshAsync();
            }

            var plan = SpeechService.Plan(settings.Current);
            var jobs = new List<DownloadJob>();

            foreach (var pass in plan.Passes)
            {
                if (pass.Model is null || speech.Problem(pass, settings.Current) is null) continue;
                jobs.Add(models.Download(pass.Model, pass.Variant!, pass.UseGpu));
            }

            if (jobs.Count > 0)
            {
                SetupStatus = "Downloading the speech model…";
                SetupIndeterminate = true;

                while (jobs.Any(job => !job.IsFinished))
                {
                    SetupProgress = jobs.Sum(job => job.Done) * 100.0 / Math.Max(1, jobs.Sum(job => job.Total));
                    SetupIndeterminate = jobs.Any(job => job.IsIndeterminate);
                    SetupStatus = jobs.Select(job => job.Status).FirstOrDefault(status => status.Contains("Downloading", StringComparison.OrdinalIgnoreCase))
                                   ?? "Downloading the speech model…";
                    await Task.Delay(300);
                }

                var failed = jobs.FirstOrDefault(job => !job.Succeeded);
                if (failed is not null)
                    throw new Exception(failed.Error ?? "The speech model download stopped. Try again from the Models page.");
            }

            await RefreshAsync();
            SetupStatus = ShowSetupCard
                ? "Some downloads are still missing. Check the list below, or pick a different model."
                : "Everything is ready. Add a film to the queue and press Censor.";

            return !ShowSetupCard;
        }
        catch (Exception ex)
        {
            Log.Error("The first-run setup failed", ex);
            SetupStatus = "Setup could not finish: " + ex.Message;
            return false;
        }
        finally
        {
            IsSettingUp = false;
        }
    }

    private static SolidColorBrush ReadyBrush(bool ready) => ready
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 195, 138))
        : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 129, 141));
}
