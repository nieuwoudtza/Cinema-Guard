using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Threading;
using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Phonon;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Speech;
using CinemaGuard.Core.Tools;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace Cinema_Guard.ViewModels;

/// <summary>Settings page: edits the live <see cref="AppSettings"/> object, saves on demand.</summary>
public sealed partial class SettingsViewModel(SettingsService service, SpeechService speech) : ObservableObject
{
    public AppSettings S => service.Current;

    /// <summary>The Speech model row: the choice and its device; "Change…" opens the Models page.</summary>
    public string SpeechSummary => speech.Describe(S);

    public IReadOnlyList<string> Languages => LanguageNames.CommonLanguages;

    public string RecommendedDockerImage => AppSettings.RecommendedDockerImage;

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

    public int ShowSubtitlesIndex
    {
        get => (int)S.ShowSubtitles;
        set
        {
            if (value < 0 || value == ShowSubtitlesIndex) return;
            S.ShowSubtitles = (ShownSubtitles)value;
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

    /// <summary>What happens to the originals after a validated run: 0 keep, 1 Recycle Bin, 2 delete permanently.</summary>
    public int OriginalsIndex
    {
        get => !S.DeleteOriginalFiles ? 0 : S.DeleteOriginalsPermanently ? 2 : 1;
        set
        {
            if (value is < 0 or > 2 || value == OriginalsIndex) return;
            S.DeleteOriginalFiles = value > 0;
            S.DeleteOriginalsPermanently = value == 2;
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

    public double LinePadBefore
    {
        get => S.LinePadBeforeMs;
        set { S.LinePadBeforeMs = (int)Math.Round(value); OnPropertyChanged(); }
    }

    public double LinePadAfter
    {
        get => S.LinePadAfterMs;
        set { S.LinePadAfterMs = (int)Math.Round(value); OnPropertyChanged(); }
    }

    public double MuteFade
    {
        get => S.MuteFadeMs;
        set { S.MuteFadeMs = (int)Math.Round(value); OnPropertyChanged(); }
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

/// <summary>One tool on the Tools page: listed from the start with a spinner, filled in when its check finishes.</summary>
public sealed partial class ToolRow(string key, string name) : ObservableObject
{
    public string Key { get; } = key;

    [ObservableProperty] private string _name = name;
    [ObservableProperty] private string _status = "Checking…";
    [ObservableProperty] private string _location = string.Empty;
    [ObservableProperty] private string _version = string.Empty;
    [ObservableProperty] private string _glyph = string.Empty;
    [ObservableProperty] private Brush? _brush;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultVisibility))]
    private bool _isChecking = true;

    /// <summary>The tick or cross, hidden while the spinner turns in its place.</summary>
    public Microsoft.UI.Xaml.Visibility ResultVisibility =>
        IsChecking ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>The row's button ("Download image", "Get Docker Desktop", "Cancel"); empty for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAction))]
    private string _actionText = string.Empty;

    [ObservableProperty] private string _actionTip = string.Empty;

    public bool HasAction => ActionText.Length > 0;

    /// <summary>A download or start-up the row's button began is running.</summary>
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate = true;

    public void BeginCheck()
    {
        IsChecking = true;
        Status = "Checking…";
    }

    public void Show(ToolStatus tool, Brush brush)
    {
        Name = tool.DisplayName;
        Status = tool.Ready ? "Ready" : tool.Problem ?? "Missing";
        Location = tool.Path ?? "Not found";
        Version = tool.Version ?? string.Empty;
        Glyph = char.ConvertFromUtf32(tool.Ready ? 0xE73E : 0xE711);
        Brush = brush;
        IsChecking = false;
    }
}

/// <summary>What the Docker row's button does.</summary>
public enum DockerAction { None, GetDocker, StartDocker, DownloadImage }

/// <summary>Preflight page: what is installed, what is missing, and the one-click first-run setup.</summary>
public sealed partial class ToolsViewModel(
    ToolLocator locator,
    SettingsService settings,
    SpeechService speech,
    ModelManager models,
    PhononService phonon,
    DockerImagePuller puller) : ObservableObject
{
    public const string DockerDownloadUrl = "https://www.docker.com/products/docker-desktop/";

    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private DockerAction _dockerAction;
    private bool _startingDocker;

    /// <summary>The GPU route is the saved choice and this PC has the NVIDIA card it needs.</summary>
    private bool WantsGpu => speech.UsesDockerGpu(settings.Current);

    private ToolRow DockerRow => Tools.First(row => row.Key == "docker");

    /// <summary>Docker is running but Phonon's CUDA image is not downloaded, while the GPU route is chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSetup))]
    private bool _gpuImageMissing;

    [ObservableProperty] private string _gpuImageState = "Not checked";

    /// <summary>Follows the image download, which outlives this page; call <see cref="Detach"/> when the page goes.</summary>
    public void Attach()
    {
        puller.Changed += OnPullerChanged;
        ApplyPullState();
    }

    public void Detach() => puller.Changed -= OnPullerChanged;

    private void OnPullerChanged() => _dispatcher.TryEnqueue(ApplyPullState);

    private void ApplyPullState()
    {
        var row = DockerRow;

        if (puller.IsPulling)
        {
            row.IsBusy = true;
            row.Progress = puller.Percent;
            row.IsProgressIndeterminate = puller.IsIndeterminate;
            if (puller.Status.Length > 0) row.Status = puller.Status;
            row.ActionText = "Cancel";
            row.ActionTip = "Stop the download. What is already downloaded is kept for the next try.";

            if (IsSettingUp)
            {
                SetupStatus = puller.Status;
                SetupProgress = puller.Percent;
                SetupIndeterminate = puller.IsIndeterminate;
            }
        }
        else if (row.IsBusy && !_startingDocker)
        {
            // Finished, wherever it was started: check again so the row and the setup card are current.
            row.IsBusy = false;
            ApplyDockerAction();
            _ = RefreshAsync();
        }
    }

    private void ApplyDockerAction()
    {
        var row = DockerRow;
        (row.ActionText, row.ActionTip) = _dockerAction switch
        {
            DockerAction.GetDocker => ("Get Docker Desktop",
                "Opens Docker's download page. With Docker Desktop installed, Phonon runs on your NVIDIA graphics card, many times faster than on the CPU. Press Check again once it is installed."),
            DockerAction.StartDocker => ("Start Docker Desktop", "Starts Docker Desktop so Cinema Guard can check for the GPU image."),
            DockerAction.DownloadImage => ("Download image",
                "Downloads Phonon's graphics-card image for Docker (several GB, once). Without it Phonon runs on the CPU."),
            _ => (string.Empty, string.Empty),
        };
    }

    /// <summary>The Docker row's button. Returns a message for the page to show, or null.</summary>
    public async Task<(bool Ok, string Message)?> RunDockerActionAsync()
    {
        if (puller.IsPulling)
        {
            puller.Cancel();
            return null;
        }

        switch (_dockerAction)
        {
            case DockerAction.GetDocker:
                try
                {
                    Process.Start(new ProcessStartInfo(DockerDownloadUrl) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not open {DockerDownloadUrl}: {ex.Message}");
                }

                return null;

            case DockerAction.StartDocker:
                var started = await StartDockerAsync();
                await RefreshAsync();
                return started ? null : (false, "Docker Desktop did not start. Start it from the Start menu, then press Check again.");

            case DockerAction.DownloadImage:
                var result = await puller.PullAsync(settings.Current);
                await RefreshAsync();
                return result;

            default:
                return null;
        }
    }

    private async Task<bool> StartDockerAsync()
    {
        var docker = locator.ResolveDocker(settings.Current);
        if (docker is null) return false;

        var row = DockerRow;
        _startingDocker = true;
        row.IsBusy = true;
        row.IsProgressIndeterminate = true;
        row.Status = "Starting Docker Desktop…";
        row.ActionText = string.Empty;

        try
        {
            return await phonon.EnsureEngineAsync(docker, null, CancellationToken.None);
        }
        finally
        {
            _startingDocker = false;
            row.IsBusy = false;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck))]
    private bool _isChecking;

    public bool CanCheck => !IsChecking;

    [ObservableProperty] private string _runtimeSummary = "Checking the tools…";

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

    /// <summary>Every tool is listed from the start, so nothing jumps in when the checks finish.</summary>
    public ObservableCollection<ToolRow> Tools { get; } =
        new(ToolLocator.CheckedTools.Select(tool => new ToolRow(tool.Key, tool.DisplayName)));

    public async Task RefreshAsync()
    {
        if (IsChecking) return;

        IsChecking = true;
        RuntimeSummary = "Checking the tools…";
        // A running image download keeps its own status on the Docker row.
        foreach (var row in Tools.Where(row => !row.IsBusy)) row.BeginCheck();

        try
        {
            // Created on the UI thread, so each report lands there.
            var onTool = new Progress<ToolStatus>(tool =>
                Tools.FirstOrDefault(row => row.Key == tool.Key)?.Show(tool, ReadyBrush(tool.Ready)));

            var snapshot = await locator.CheckAsync(settings.Current, onTool);

            // A Progress report can still be queued behind this line; the snapshot has the final word.
            foreach (var tool in snapshot.Tools)
                Tools.FirstOrDefault(row => row.Key == tool.Key)?.Show(tool, ReadyBrush(tool.Ready));

            var ffmpeg = snapshot.Tools.First(t => t.Key == "ffmpeg");
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

            var docker = snapshot.Tools.First(t => t.Key == "docker");
            var hasNvidia = (settings.Current.Hardware ?? models.Hardware).HasNvidia;

            _dockerAction = !docker.Found ? (hasNvidia ? DockerAction.GetDocker : DockerAction.None)
                : !snapshot.DockerEngineRunning ? DockerAction.StartDocker
                : !snapshot.DockerImagePresent ? DockerAction.DownloadImage
                : DockerAction.None;

            ApplyDockerAction();

            // Only when Docker answers: with Docker Desktop closed, whether the image is there is unknown.
            // (DockerEngineReady means running and usable, which is false while the image is missing.)
            GpuImageMissing = WantsGpu && snapshot.DockerEngineRunning && !snapshot.DockerImagePresent;
            GpuImageState = GpuImageMissing ? "Not downloaded" : snapshot.DockerImagePresent ? "Ready" : "Docker Desktop is not running";

            ShowSetupCard = FfmpegMissing || ModelMissing || GpuImageMissing;

            var problem = plan.Passes.Select(pass => speech.Problem(pass, settings.Current)).FirstOrDefault(p => p is not null);

            RuntimeSummary = problem is null
                ? $"Transcription: {speech.Describe(settings.Current)}."
                : $"Transcription: {speech.Describe(settings.Current)}. {problem}";
        }
        catch (Exception ex)
        {
            Log.Error("The tool check failed", ex);
            RuntimeSummary = $"The tool check failed: {ex.Message}";

            foreach (var row in Tools.Where(row => row.IsChecking))
            {
                row.IsChecking = false;
                row.Status = "Check failed";
            }
        }
        finally
        {
            IsChecking = false;
            ApplyPullState();
        }
    }

    /// <summary>
    /// The one button on the first run: install FFmpeg, download every model the saved choice needs,
    /// then Phonon's GPU image when Docker and an NVIDIA card are there. Defaults are already the
    /// recommended Phonon pair, so this is the whole setup.
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

            // The GPU image is a speed-up: without it Phonon still runs on the CPU, so a failure is said, not thrown.
            string? gpuProblem = null;

            if (GpuImageMissing)
            {
                SetupStatus = "Downloading the GPU image…";
                SetupIndeterminate = true;

                var (ok, message) = await puller.PullAsync(settings.Current);
                if (!ok) gpuProblem = message;
            }

            await RefreshAsync();
            SetupStatus = gpuProblem is not null
                ? $"{gpuProblem} Phonon runs on the CPU until it is downloaded; try again with Download image on the Docker row."
                : ShowSetupCard
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
