using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Speech;
using CinemaGuard.Core.Tools;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace Cinema_Guard.ViewModels;

public enum ModelRowKind { Pair, Model, Remote }

/// <summary>One choice on the Models page: the recommended pair, one model (with its variant and device), or the server.</summary>
public sealed partial class ModelRowViewModel : ObservableObject
{
    private readonly ModelsViewModel _owner;
    private int _variantIndex;
    private bool _useGpu;

    public ModelRowViewModel(ModelsViewModel owner, ModelRowKind kind, SpeechModelInfo? model, string name)
    {
        _owner = owner;
        Kind = kind;
        Model = model;
        Name = name;
        VariantLabels = model is { Variants.Count: > 1 }
            ? model.Variants.Select(v => $"{v.Label} · {ModelsViewModel.Size(v.DiskSize)}").ToList()
            : [];
    }

    public ModelRowKind Kind { get; }
    public SpeechModelInfo? Model { get; }
    public string Name { get; }
    public bool Older => Model?.Older ?? false;

    public IReadOnlyList<string> VariantLabels { get; }
    public bool HasVariants => VariantLabels.Count > 0;

    public ModelVariant? Variant => Model?.Variants[Math.Clamp(_variantIndex, 0, Model.Variants.Count - 1)];

    public int VariantIndex
    {
        get => _variantIndex;
        set
        {
            if (value < 0 || value == _variantIndex) return;
            var previous = _variantIndex;
            _variantIndex = value;
            OnPropertyChanged();
            _owner.OnVariantChanged(this, previous);
        }
    }

    internal void SetVariantSilently(int index)
    {
        _variantIndex = index;
        OnPropertyChanged(nameof(VariantIndex));
    }

    public bool UseGpu
    {
        get => _useGpu;
        set
        {
            if (value == _useGpu) return;
            _useGpu = value;
            OnPropertyChanged();
            _owner.OnGpuChanged(this, value);
        }
    }

    internal void SetGpuSilently(bool value)
    {
        _useGpu = value;
        OnPropertyChanged(nameof(UseGpu));
    }

    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isRecommended;
    [ObservableProperty] private bool _showGpuSwitch;
    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private bool _showDownload;
    [ObservableProperty] private bool _showDelete;
    [ObservableProperty] private bool _showSetUp;
    [ObservableProperty] private string _stateText = string.Empty;
    [ObservableProperty] private bool _hasStateText;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private string _deleteTip = "Delete";
}

public sealed partial class ModelSection : ObservableObject
{
    public ModelSection(string title, string subtitle)
    {
        Title = title;
        Subtitle = subtitle;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public ObservableCollection<ModelRowViewModel> Rows { get; } = [];

    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _showOlder;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private string _moreText = string.Empty;
}

public sealed partial class EngineRowViewModel : ObservableObject
{
    public EngineRowViewModel(EnginePack pack) => Pack = pack;

    public EnginePack Pack { get; }
    public string Name => Pack.Name;

    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private bool _showDownload;
    [ObservableProperty] private bool _showDelete;
    [ObservableProperty] private string _stateText = string.Empty;
}

/// <summary>What the page must do after a row was clicked.</summary>
public sealed record SelectDecision(bool Applied, string? Blocked, long DownloadBytes, string? DownloadText);

/// <summary>
/// The Models page. One radio choice across every section; picking something that isn't downloaded
/// asks once with the total size, keeps the current model until the download ends, then switches.
/// A singleton, so a download started here keeps its "switch when done" after leaving the page.
/// </summary>
public sealed partial class ModelsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ModelManager _models;
    private readonly SpeechService _speech;
    private readonly ToolLocator _locator;
    private readonly MainViewModel _main;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _refreshTimer;
    private bool _refreshQueued;
    private ModelRowViewModel? _pending;

    public ModelsViewModel(SettingsService settings, ModelManager models, SpeechService speech, ToolLocator locator, MainViewModel main)
    {
        _settings = settings;
        _models = models;
        _speech = speech;
        _locator = locator;
        _main = main;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _refreshTimer = _dispatcher.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(300);
        _refreshTimer.IsRepeating = false;
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshQueued = false;
            Refresh();
        };

        Build();

        _models.Changed += () => _dispatcher.TryEnqueue(QueueRefresh);
        _settings.Changed += () => _dispatcher.TryEnqueue(QueueRefresh);
        _main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsRunning)) QueueRefresh();
        };

        Refresh();
    }

    private AppSettings S => _settings.Current;
    private HardwareInfo Hardware => S.Hardware ?? _models.Hardware;

    public ObservableCollection<ModelSection> Sections { get; } = [];
    public ObservableCollection<EngineRowViewModel> Engines { get; } = [];
    public ModelRowViewModel PairRow { get; private set; } = null!;
    public ModelRowViewModel RemoteRow { get; private set; } = null!;
    public ModelSection RecommendedSection { get; private set; } = null!;
    public ModelSection ServerSection { get; private set; } = null!;

    [ObservableProperty] private string _hardwareText = string.Empty;
    [ObservableProperty] private string _diskText = "Checking…";
    [ObservableProperty] private bool _isNoticeOpen;
    [ObservableProperty] private string _noticeTitle = string.Empty;
    [ObservableProperty] private string _noticeMessage = string.Empty;
    [ObservableProperty] private InfoBarSeverity _noticeSeverity = InfoBarSeverity.Informational;

    private int _filterIndex;

    /// <summary>All / Downloaded / English / Multilingual.</summary>
    public int FilterIndex
    {
        get => _filterIndex;
        set
        {
            if (value < 0 || value == _filterIndex) return;
            _filterIndex = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    public string CurrentChoice => _speech.Describe(S);

    // ------------------------------------------------------------------ building

    private void Build()
    {
        RecommendedSection = new ModelSection("RECOMMENDED", string.Empty);
        PairRow = new ModelRowViewModel(this, ModelRowKind.Pair, null, "Phonon 2.5 English + Phonon 2.5");
        RecommendedSection.Rows.Add(PairRow);

        var phonon = new ModelSection("PHONON", "best word timing · CPU, or the GPU through Docker");
        var parakeet = new ModelSection("PARAKEET", "fast · CPU");
        var whisper = new ModelSection("WHISPER", Hardware.WhisperUsesGpu ? "most languages · uses the graphics card" : "most languages · CPU");

        foreach (var model in SpeechCatalog.Models)
        {
            var row = new ModelRowViewModel(this, ModelRowKind.Model, model, model.Name);
            var section = model.Family switch
            {
                SpeechFamily.Phonon => phonon,
                SpeechFamily.Parakeet => parakeet,
                _ => whisper,
            };
            section.Rows.Add(row);
        }

        ServerSection = new ModelSection("YOUR OWN SERVER", "Phonon in Docker on another machine, or a provider such as OpenAI or Groq");
        RemoteRow = new ModelRowViewModel(this, ModelRowKind.Remote, null, "Server URL");
        ServerSection.Rows.Add(RemoteRow);

        Sections.Add(RecommendedSection);
        Sections.Add(phonon);
        Sections.Add(parakeet);
        Sections.Add(whisper);
        Sections.Add(ServerSection);

        // Each dropdown starts on the saved build, else a downloaded one, else the default.
        foreach (var row in AllRows.Where(r => r.Model is not null))
        {
            var variants = row.Model!.Variants.ToList();
            var saved = S.SpeechVariants.GetValueOrDefault(row.Model.Id);
            var index = variants.FindIndex(v => v.Id == saved);
            if (index < 0) index = variants.FindIndex(v => _models.IsInstalled(row.Model, v));
            row.SetVariantSilently(Math.Max(0, index));
        }

        BuildEngines();
    }

    private void BuildEngines()
    {
        Engines.Clear();
        Engines.Add(new EngineRowViewModel(EnginePacks.PhononRuntime));
        if (EnginePacks.WhisperFor(Hardware) is { } whisper) Engines.Add(new EngineRowViewModel(whisper));

        // A Parakeet GPU pack from an earlier version: no longer used, but deletable.
        foreach (var legacy in EnginePacks.Legacy.Where(_models.IsInstalled)) Engines.Add(new EngineRowViewModel(legacy));
    }

    public IEnumerable<ModelRowViewModel> AllRows => Sections.SelectMany(s => s.Rows);

    // ------------------------------------------------------------------ state

    /// <summary>
    /// What the rows show from the disk and PATH. Gathered off the UI thread: measuring the engines alone
    /// walks tens of thousands of files, and the page must open at once.
    /// </summary>
    private sealed record DiskState(
        IReadOnlySet<string> InstalledModels,
        IReadOnlySet<string> InstalledPacks,
        IReadOnlyDictionary<string, long> PackSizes,
        bool DockerFound,
        string? ExternalFermion,
        string PairDevice);

    private DiskState? _state;
    private bool _scanning;
    private bool _rescan;

    /// <summary>False until the first look at the disk is in; the rows stay disabled until then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecking))]
    private bool _isReady;

    public bool IsChecking => !IsReady;

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        _refreshTimer.Start();
    }

    /// <summary>Shows the last known state at once, then looks at the disk again in the background.</summary>
    public void Refresh()
    {
        Apply();
        _ = ScanAsync();
        _ = UpdateDiskUsageAsync();
    }

    private async Task ScanAsync()
    {
        // A change that arrives mid-scan may not be in it: look once more when this one ends.
        if (_scanning)
        {
            _rescan = true;
            return;
        }

        _scanning = true;

        try
        {
            do
            {
                _rescan = false;

                // Read the settings here: the UI thread may change them while the scan runs.
                var hardware = Hardware;
                var pairPass = SpeechService.Pass(SpeechCatalog.PhononEnglish, S);

                _state = await Task.Run(() => ReadDisk(hardware, pairPass));
                IsReady = true;
                Apply();
            }
            while (_rescan);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not check the downloaded models: {ex.Message}");
        }
        finally
        {
            _scanning = false;
        }
    }

    private DiskState ReadDisk(HardwareInfo hardware, SpeechPass pairPass)
    {
        var models = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in SpeechCatalog.Models)
        {
            foreach (var variant in model.Variants.Where(v => _models.IsInstalled(model, v)))
                models.Add(ModelManager.ModelKey(model, variant));
        }

        var packs = new HashSet<string>(StringComparer.Ordinal);
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        var known = new List<EnginePack> { EnginePacks.PhononRuntime };
        if (EnginePacks.WhisperFor(hardware) is { } whisper) known.Add(whisper);
        known.AddRange(EnginePacks.Legacy);

        foreach (var pack in known.Where(_models.IsInstalled))
        {
            packs.Add(pack.Id);
            sizes[pack.Id] = _models.InstalledSize(pack);
        }

        return new DiskState(models, packs, sizes, _locator.ResolveDocker(S) is not null, _models.ExternalFermion, _speech.DeviceName(pairPass, S));
    }

    private bool Installed(SpeechModelInfo model, ModelVariant variant) =>
        _state?.InstalledModels.Contains(ModelManager.ModelKey(model, variant)) ?? false;

    private bool Installed(EnginePack pack) => _state?.InstalledPacks.Contains(pack.Id) ?? false;

    private void Apply()
    {
        HardwareText = Hardware.Summary;
        var plan = SpeechService.Plan(S);
        var dockerGpu = Hardware.HasNvidia && (_state?.DockerFound ?? false);
        var running = _main.IsRunning;

        foreach (var row in AllRows)
        {
            switch (row.Kind)
            {
                case ModelRowKind.Pair:
                    RefreshPair(row, dockerGpu, running);
                    break;
                case ModelRowKind.Remote:
                    RefreshRemote(row);
                    break;
                default:
                    RefreshModel(row, plan, dockerGpu, running);
                    break;
            }
        }

        foreach (var engine in Engines) RefreshEngine(engine, plan, running);

        ApplyFilter();
        OnPropertyChanged(nameof(CurrentChoice));
    }

    private void RefreshPair(ModelRowViewModel row, bool dockerGpu, bool running)
    {
        var english = SpeechCatalog.PhononEnglish;
        var multilingual = SpeechCatalog.PhononMultilingual;
        var installed = Installed(english, english.DefaultVariant) && Installed(multilingual, multilingual.DefaultVariant) &&
                        Installed(EnginePacks.PhononRuntime);

        var jobs = new[] { english, multilingual }
            .Select(m => _models.Job(ModelManager.ModelKey(m, m.DefaultVariant)))
            .Where(j => j is not null)
            .Cast<DownloadJob>()
            .ToList();

        var device = _state is null ? string.Empty : $" · {_state.PairDevice}";
        row.Description = $"Two passes: English first, then multilingual to catch what it missed and spot foreign dialogue{device}";
        row.IsSelected = S.SpeechModel == SpeechCatalog.PairId;
        row.IsRecommended = !row.IsSelected;
        row.ShowGpuSwitch = dockerGpu && row.IsSelected;
        row.SetGpuSilently(S.PhononRuntime != PhononRuntimeMode.Local);
        ApplyJobState(row, installed, jobs);
        row.ShowDelete = false;
    }

    private void RefreshRemote(ModelRowViewModel row)
    {
        var configured = !string.IsNullOrWhiteSpace(S.RemoteUrl);
        row.Description = configured
            ? $"{S.RemoteUrl}{(string.IsNullOrWhiteSpace(S.RemoteModel) ? string.Empty : " · " + S.RemoteModel)}"
            : "fermion serve on a GPU machine, or OpenAI / Groq with word timestamps";
        row.IsSelected = S.SpeechModel == SpeechCatalog.RemoteId;
        row.IsInstalled = true;
        row.IsDownloading = false;
        row.ShowDownload = false;
        row.ShowDelete = false;
        row.ShowSetUp = true;
        row.StateText = string.Empty;
        row.HasStateText = false;
    }

    private void RefreshModel(ModelRowViewModel row, SpeechPlan plan, bool dockerGpu, bool running)
    {
        var model = row.Model!;
        var variant = row.Variant!;
        var installed = Installed(model, variant) && (model.Family != SpeechFamily.Phonon || Installed(EnginePacks.PhononRuntime));
        var job = _models.Job(ModelManager.ModelKey(model, variant));

        row.Description = model.Family switch
        {
            SpeechFamily.Phonon => $"{model.Languages} · {Size(variant.DiskSize)}",
            SpeechFamily.Whisper => $"{model.Languages} · {(Hardware.WhisperUsesGpu ? "runs on the " + HardwareInfo.WhisperRuntimeName(Hardware.BestWhisperRuntime) : "runs on the CPU")}",
            _ => model.Languages,
        };

        row.IsSelected = S.SpeechModel == model.Id && S.SpeechVariants.GetValueOrDefault(model.Id, model.DefaultVariant.Id) == variant.Id;

        // The GPU switch is one global Phonon setting, so only the chosen row shows it.
        row.ShowGpuSwitch = model.Family == SpeechFamily.Phonon && dockerGpu && row.IsSelected;
        row.SetGpuSilently(model.Family == SpeechFamily.Phonon && S.PhononRuntime != PhononRuntimeMode.Local);
        ApplyJobState(row, installed, job is null ? [] : [job]);

        var inUse = plan.Passes.Any(p => p.Model?.Id == model.Id && p.Variant?.Id == variant.Id);
        row.ShowDelete = installed && !row.IsDownloading;
        row.DeleteTip = inUse ? "In use: pick another model first" : running ? "Wait for the queue to finish" : $"Delete {model.Name}";
    }

    private void ApplyJobState(ModelRowViewModel row, bool installed, List<DownloadJob> jobs)
    {
        row.IsInstalled = installed;
        row.IsDownloading = jobs.Count > 0;

        if (row.IsDownloading)
        {
            var done = jobs.Sum(j => j.Done);
            var total = Math.Max(1, jobs.Sum(j => j.Total));
            row.Progress = done * 100.0 / total;
            row.IsProgressIndeterminate = jobs.Any(j => j.IsIndeterminate);
            row.ProgressText = row.IsProgressIndeterminate ? string.Empty : $"{row.Progress:0}%";
            row.StateText = jobs[0].Status;
        }
        else
        {
            row.Progress = 0;
            row.IsProgressIndeterminate = false;
            row.ProgressText = string.Empty;
            row.StateText = installed ? "Downloaded" : string.Empty;
        }

        // Not yet known whether it is on disk: offer neither Download nor Delete.
        var known = _state is not null || row.IsDownloading;
        row.HasStateText = known && row.StateText.Length > 0 && !row.IsDownloading;
        row.ShowDownload = known && !installed && !row.IsDownloading;
        row.ShowSetUp = false;
    }

    private void RefreshEngine(EngineRowViewModel engine, SpeechPlan plan, bool running)
    {
        var installed = Installed(engine.Pack);
        var job = _models.Job(ModelManager.PackKey(engine.Pack));
        var external = engine.Pack.Kind == PackKind.PhononRuntime && _state?.ExternalFermion is not null;
        var legacy = EnginePacks.Legacy.Contains(engine.Pack);
        var size = _state?.PackSizes.GetValueOrDefault(engine.Pack.Id) ?? 0;
        var known = _state is not null;

        engine.IsInstalled = installed;
        engine.IsDownloading = job is not null;
        engine.IsProgressIndeterminate = job?.IsIndeterminate ?? false;
        engine.Progress = job?.Percent ?? 0;
        engine.ProgressText = job is null || job.IsIndeterminate ? string.Empty : $"{job.Percent:0}%";
        engine.Description = !known
            ? engine.Pack.Description
            : legacy
                ? $"No longer used: Parakeet runs on the CPU · {Size(size)} can be reclaimed"
                : external
                    ? $"Using {_state!.ExternalFermion} (set in Settings)"
                    : $"{engine.Pack.Description} · {Size(installed ? size : engine.Pack.DiskSize)}";
        engine.StateText = job is not null ? job.Status : installed ? "Installed" : "Not installed";
        engine.ShowDownload = known && !legacy && !installed && job is null;
        engine.ShowDelete = installed && job is null && !external;
    }

    private void ApplyFilter()
    {
        foreach (var section in Sections)
        {
            var older = section.Rows.Count(r => r.Older);

            foreach (var row in section.Rows)
            {
                var matches = row.Kind != ModelRowKind.Model || FilterIndex switch
                {
                    1 => row.IsInstalled || row.IsDownloading,
                    2 => row.Model!.HasEnglishOnly,
                    3 => row.Model!.HasMultilingual,
                    _ => true,
                };

                if (FilterIndex == 1 && row.Kind == ModelRowKind.Pair) matches = row.IsInstalled || row.IsDownloading;
                if (FilterIndex == 1 && row.Kind == ModelRowKind.Remote) matches = !string.IsNullOrWhiteSpace(S.RemoteUrl);

                // Older models fold away unless shown, picked, downloaded, or the filter asks for them.
                var folded = row.Older && !section.ShowOlder && !row.IsSelected && !row.IsInstalled && !row.IsDownloading && FilterIndex == 0;
                row.IsVisible = matches && !folded;
            }

            var hidden = section.Rows.Count(r => r.Older && !r.IsVisible);
            section.HasMore = FilterIndex == 0 && older > 0 && (section.ShowOlder || hidden > 0);
            section.MoreText = section.ShowOlder
                ? "Show fewer"
                : $"Show {hidden} more: {string.Join(", ", section.Rows.Where(r => r.Older && !r.IsVisible).Select(r => r.Name))}";
            section.IsVisible = section.Rows.Any(r => r.IsVisible);
        }
    }

    public void ToggleOlder(ModelSection section)
    {
        section.ShowOlder = !section.ShowOlder;
        ApplyFilter();
    }

    private async Task UpdateDiskUsageAsync()
    {
        try
        {
            var bytes = await Task.Run(_models.DiskUsage);
            DiskText = bytes == 0 ? "Nothing downloaded yet" : $"Using {Size(bytes)}";
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not measure the models folder: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ choosing

    /// <summary>What picking this row needs: applied at once, blocked, or a download first.</summary>
    public SelectDecision Prepare(ModelRowViewModel row)
    {
        if (_main.IsRunning) return new SelectDecision(false, "The queue is running. Stop it or let it finish before changing the speech model.", 0, null);
        if (row.IsSelected) return new SelectDecision(true, null, 0, null);

        if (row.Kind == ModelRowKind.Remote)
        {
            if (string.IsNullOrWhiteSpace(S.RemoteUrl)) return new SelectDecision(false, null, 0, null);
            Apply(row);
            return new SelectDecision(true, null, 0, null);
        }

        var missing = Missing(row);
        if (missing.Count == 0)
        {
            Apply(row);
            return new SelectDecision(true, null, 0, null);
        }

        var bytes = MissingBytes(row);
        var parts = missing.Select(m => $"{m.Name} ({Size(m.Bytes)})");
        var text = $"This downloads {Size(bytes)}: {string.Join(" and ", parts)}. " +
                   $"You can keep using Cinema Guard meanwhile; {_speech.Describe(S, shortForm: true)} stays in use until the download finishes.";

        return new SelectDecision(false, null, bytes, text);
    }

    private sealed record MissingPart(string Name, long Bytes);

    private List<MissingPart> Missing(ModelRowViewModel row)
    {
        var parts = new List<MissingPart>();

        void AddModel(SpeechModelInfo model, ModelVariant variant, bool gpu)
        {
            foreach (var pack in _models.PacksFor(model, gpu).Where(p => !_models.IsInstalled(p) && parts.All(x => x.Name != PackName(p))))
                parts.Add(new MissingPart(PackName(pack), pack.DownloadSize));

            if (!_models.IsInstalled(model, variant))
                parts.Add(new MissingPart($"{model.Name}{(model.Variants.Count > 1 ? " " + variant.Label : string.Empty)}", variant.DiskSize));
        }

        if (row.Kind == ModelRowKind.Pair)
        {
            AddModel(SpeechCatalog.PhononEnglish, SpeechCatalog.PhononEnglish.DefaultVariant, false);
            AddModel(SpeechCatalog.PhononMultilingual, SpeechCatalog.PhononMultilingual.DefaultVariant, false);
        }
        else if (row.Model is not null)
        {
            AddModel(row.Model, row.Variant!, false);
        }

        return parts;
    }

    private static string PackName(EnginePack pack) => pack.Kind == PackKind.PhononRuntime ? "the Phonon engine" : $"the {pack.Name}";

    private long MissingBytes(ModelRowViewModel row) => Missing(row).Sum(p => p.Bytes);

    /// <summary>Starts the download; the row becomes the choice once every file is in.</summary>
    public void DownloadAndSelect(ModelRowViewModel row, bool select)
    {
        var jobs = StartDownloads(row);
        if (select) _pending = row;

        Refresh();

        _ = Task.WhenAll(jobs.Select(j => j.Completion)).ContinueWith(_ => _dispatcher.TryEnqueue(() =>
        {
            var failed = jobs.FirstOrDefault(j => !j.Succeeded);

            if (failed is not null)
            {
                if (!failed.Cancelled) Notify("Download failed", failed.Error ?? "The download stopped.", InfoBarSeverity.Error);
                if (_pending == row) _pending = null;
            }
            else if (_pending == row)
            {
                _pending = null;

                if (_main.IsRunning)
                {
                    Notify("Downloaded", $"{row.Name} is ready. Pick it again once the queue has finished.", InfoBarSeverity.Success);
                }
                else
                {
                    Apply(row);
                    Notify("Ready", $"Now using {_speech.Describe(S)}.", InfoBarSeverity.Success);
                }
            }
            else
            {
                Notify("Downloaded", $"{row.Name} is ready.", InfoBarSeverity.Success);
            }

            Refresh();
        }));
    }

    private List<DownloadJob> StartDownloads(ModelRowViewModel row)
    {
        if (row.Kind == ModelRowKind.Pair)
        {
            return
            [
                _models.Download(SpeechCatalog.PhononEnglish, SpeechCatalog.PhononEnglish.DefaultVariant, false),
                _models.Download(SpeechCatalog.PhononMultilingual, SpeechCatalog.PhononMultilingual.DefaultVariant, false),
            ];
        }

        var gpu = row.UseGpu && row.Model!.Family == SpeechFamily.Phonon;
        return [_models.Download(row.Model!, row.Variant!, gpu)];
    }

    /// <summary>Downloads whatever the saved choice is missing (the Queue's "Download" when Start can't run).</summary>
    public void DownloadCurrentChoice()
    {
        var row = S.SpeechModel == SpeechCatalog.PairId
            ? PairRow
            : AllRows.FirstOrDefault(r => r.Model?.Id == S.SpeechModel &&
                                          r.Variant?.Id == S.SpeechVariants.GetValueOrDefault(r.Model.Id, r.Model.DefaultVariant.Id));

        if (row is not null && Missing(row).Count > 0) DownloadAndSelect(row, select: false);
    }

    public void Cancel(ModelRowViewModel row)
    {
        if (row.Kind == ModelRowKind.Pair)
        {
            _models.Cancel(ModelManager.ModelKey(SpeechCatalog.PhononEnglish, SpeechCatalog.PhononEnglish.DefaultVariant));
            _models.Cancel(ModelManager.ModelKey(SpeechCatalog.PhononMultilingual, SpeechCatalog.PhononMultilingual.DefaultVariant));
        }
        else if (row.Model is not null)
        {
            _models.Cancel(ModelManager.ModelKey(row.Model, row.Variant!));
        }

        if (_pending == row) _pending = null;
    }

    public void CancelEngine(EngineRowViewModel engine) => _models.Cancel(ModelManager.PackKey(engine.Pack));

    public void DownloadEngine(EngineRowViewModel engine)
    {
        _models.DownloadPack(engine.Pack);
        Refresh();
    }

    private void Apply(ModelRowViewModel row)
    {
        S.SpeechModel = row.Kind switch
        {
            ModelRowKind.Pair => SpeechCatalog.PairId,
            ModelRowKind.Remote => SpeechCatalog.RemoteId,
            _ => row.Model!.Id,
        };

        if (row.Model is not null) S.SpeechVariants[row.Model.Id] = row.Variant!.Id;

        _settings.Save();
        Log.Info($"Speech model: {_speech.Describe(S)}");
        Refresh();
    }

    internal void OnVariantChanged(ModelRowViewModel row, int previous)
    {
        if (row.Model is null) return;

        // The model in use only switches variant once that variant is on disk.
        var active = S.SpeechModel == row.Model.Id;
        if (!active || _models.IsInstalled(row.Model, row.Variant!))
        {
            S.SpeechVariants[row.Model.Id] = row.Variant!.Id;
            _settings.Save();
        }

        VariantChangedOnActiveRow?.Invoke(row, active && !_models.IsInstalled(row.Model, row.Variant!), previous);
        Refresh();
    }

    /// <summary>Raised when the in-use model's dropdown moves to a variant that still has to be downloaded.</summary>
    public event Action<ModelRowViewModel, bool, int>? VariantChangedOnActiveRow;

    internal void OnGpuChanged(ModelRowViewModel row, bool value)
    {
        if (_main.IsRunning)
        {
            Notify("The queue is running", "Change the device once the queue has finished.", InfoBarSeverity.Warning);
            Refresh();
            return;
        }

        if (row.Kind == ModelRowKind.Pair || row.Model?.Family == SpeechFamily.Phonon)
            S.PhononRuntime = value ? PhononRuntimeMode.Auto : PhononRuntimeMode.Local;

        _settings.Save();
        Refresh();
    }

    // ------------------------------------------------------------------ deleting

    /// <summary>Why this row can't be deleted now, or null.</summary>
    public string? DeleteBlocked(ModelRowViewModel row)
    {
        if (_main.IsRunning) return "The queue is running. Nothing can be deleted until it finishes.";

        var plan = SpeechService.Plan(S);
        if (plan.Passes.Any(p => p.Model?.Id == row.Model?.Id && p.Variant?.Id == row.Variant?.Id))
            return $"{row.Name} is in use. Pick another model first, then delete this one.";

        return null;
    }

    public long SizeOnDisk(ModelRowViewModel row) => row.Model is null ? 0 : _models.InstalledSize(row.Model, row.Variant!);

    /// <summary>Deletes the model; returns an engine no other downloaded model needs, to offer removing it too.</summary>
    public EnginePack? Delete(ModelRowViewModel row)
    {
        var model = row.Model!;
        _models.Delete(model, row.Variant!);
        Refresh();

        foreach (var pack in _models.PacksFor(model, useGpu: true).Where(_models.IsInstalled))
        {
            if (pack.Kind == PackKind.PhononRuntime && _models.ExternalFermion is not null) continue;
            if (_models.ModelsUsing(pack).Count == 0) return pack;
        }

        return null;
    }

    public string? DeleteEngineBlocked(EngineRowViewModel engine)
    {
        if (_main.IsRunning) return "The queue is running. Nothing can be deleted until it finishes.";

        var plan = SpeechService.Plan(S);
        var used = plan.Passes.Any(p => p.Model is { } m && _models.PacksFor(m, p.UseGpu).Any(pack => pack.Id == engine.Pack.Id));
        return used ? $"The {engine.Name} is used by the speech model you picked. Pick another model first." : null;
    }

    public long EngineSize(EngineRowViewModel engine) => _models.InstalledSize(engine.Pack);

    public void DeleteEngine(EnginePack pack)
    {
        _models.Delete(pack);
        Refresh();
    }

    // ------------------------------------------------------------------ hardware, server, folder

    public async Task RedetectAsync()
    {
        var hardware = await Task.Run(HardwareDetector.Detect);
        S.Hardware = hardware;
        _settings.Save();

        BuildEngines();
        Refresh();
        Notify("Hardware checked", hardware.Summary, InfoBarSeverity.Informational);
    }

    public void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(SpeechPaths.Models);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SpeechPaths.Root}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open the models folder: {ex.Message}");
        }
    }

    public RemoteSpec RemoteSpec => SpeechService.Remote(S);

    public void SaveRemote(string url, string model, string? apiKey, int maxUploadMb, bool select)
    {
        S.RemoteUrl = url.Trim();
        S.RemoteModel = model.Trim();
        S.RemoteMaxUploadMb = Math.Max(0, maxUploadMb);
        if (apiKey is not null) S.RemoteApiKeyProtected = RemoteTranscriber.Protect(apiKey);
        if (select && S.RemoteUrl.Length > 0) S.SpeechModel = SpeechCatalog.RemoteId;
        _settings.Save();
        Refresh();
    }

    /// <summary>Read from the disk: the guide may open before the first background check is in.</summary>
    public bool PairInstalled => Missing(PairRow).Count == 0;
    public long PairMissingBytes => MissingBytes(PairRow);
    public bool IsQueueRunning => _main.IsRunning;

    public void Notify(string title, string message, InfoBarSeverity severity)
    {
        NoticeTitle = title;
        NoticeMessage = message;
        NoticeSeverity = severity;
        IsNoticeOpen = true;
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:0.0} GB",
        >= 1_000_000 => $"{bytes / 1_000_000.0:0} MB",
        >= 1_000 => $"{bytes / 1_000.0:0} KB",
        _ => $"{bytes} bytes",
    };
}
