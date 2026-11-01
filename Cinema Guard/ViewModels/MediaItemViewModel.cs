using System;
using System.IO;
using CinemaGuard.Core.Pipeline;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Cinema_Guard.ViewModels;

/// <summary>One row in the queue. Wraps a <see cref="MediaItem"/> and mirrors its state for binding.</summary>
public sealed partial class MediaItemViewModel : ObservableObject
{
    private static readonly SolidColorBrush NeutralBrush = new(Color.FromArgb(255, 138, 138, 142));
    private static readonly SolidColorBrush RunningBrush = new(Color.FromArgb(255, 96, 165, 250));
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromArgb(255, 76, 195, 138));
    private static readonly SolidColorBrush WarningBrush = new(Color.FromArgb(255, 245, 211, 103));
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromArgb(255, 255, 129, 141));

    public MediaItemViewModel(MediaItem item)
    {
        Item = item;
        Sync();
    }

    public MediaItem Item { get; }

    public string FileName => Path.GetFileName(Item.InputPath);
    public string Folder => Item.Folder;
    public string FullPath => Item.InputPath;

    public bool GenerateSubtitles
    {
        get => Item.GenerateSubtitles;
        set
        {
            if (Item.GenerateSubtitles == value) return;
            Item.GenerateSubtitles = value;
            OnPropertyChanged();
        }
    }

    public bool ExtractSubtitles
    {
        get => Item.ExtractSubtitles;
        set
        {
            if (Item.ExtractSubtitles == value) return;
            Item.ExtractSubtitles = value;
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private string _status = "Waiting";
    [ObservableProperty] private string _detail = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private Visibility _progressVisibility = Visibility.Collapsed;

    /// <summary>The whole file's percentage beside the bar; empty while the bar still animates (preparing, loading).</summary>
    [ObservableProperty] private string _progressText = string.Empty;

    /// <summary>Time spent on this file: counting while it runs, the total once it has finished; empty before it starts.</summary>
    [ObservableProperty] private string _elapsedText = string.Empty;

    /// <summary>"about 5 min left" while the file runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemaining))]
    private string _remainingText = string.Empty;

    public bool HasRemaining => RemainingText.Length > 0;
    [ObservableProperty] private string _sourceBadge = "No subtitles";
    [ObservableProperty] private int _hitCount;
    [ObservableProperty] private string _hitText = string.Empty;
    [ObservableProperty] private Visibility _hitsVisibility = Visibility.Collapsed;
    [ObservableProperty] private string _resultGlyph = "\uE823";
    [ObservableProperty] private Brush _resultBrush = NeutralBrush;

    /// <summary>Mirrors the row's selection in the queue list, for the tick box inside the card.</summary>
    [ObservableProperty] private bool _isSelected;

    public bool HasHits => HitCount > 0;

    /// <summary>The full detail on hover (the row trims it); null when there is none, so no empty tooltip shows.</summary>
    public string? DetailToolTip => string.IsNullOrEmpty(Detail) ? null : Detail;

    partial void OnDetailChanged(string value) => OnPropertyChanged(nameof(DetailToolTip));

    public void Sync()
    {
        Status = Item.StatusLabel;

        // The step text is the status itself while waiting or running; never show it twice ("Waiting Waiting").
        Detail = !string.IsNullOrWhiteSpace(Item.Message) ? Item.Message!
            : Item.StatusText == Status ? string.Empty
            : Item.StatusText;
        Progress = Item.Progress;

        var running = Item.Status == MediaStatus.Running;
        IsIndeterminate = running && Item.Progress <= 0.1;
        ProgressVisibility = running ? Visibility.Visible : Visibility.Collapsed;
        ProgressText = running && !IsIndeterminate ? $"{Math.Floor(Item.Progress):0}%" : string.Empty;
        RefreshElapsed();

        SourceBadge = string.IsNullOrWhiteSpace(Item.SubtitleSource) || Item.SubtitleSource == "None"
            ? "No subtitles"
            : Item.SubtitleSource;

        HitCount = Item.HitCount;
        HitText = HitCount == 1 ? "1 word" : $"{HitCount} words";
        HitsVisibility = HitCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        OnPropertyChanged(nameof(HasHits));

        var (glyph, brush) = Item.Status switch
        {
            MediaStatus.Running => ("\uE895", RunningBrush),
            MediaStatus.Succeeded => ("\uE73E", SuccessBrush),
            MediaStatus.Skipped => ("\uE7BA", WarningBrush),
            MediaStatus.Failed => ("\uE711", ErrorBrush),
            MediaStatus.Cancelled => ("\uE711", NeutralBrush),
            _ => ("\uE823", NeutralBrush),
        };

        ResultGlyph = glyph;
        ResultBrush = brush;
    }

    /// <summary>Called every second for the running file, so its time counts up and the time left counts down.</summary>
    public void RefreshElapsed()
    {
        ElapsedText = Item.Elapsed is { } elapsed ? MainViewModel.FormatElapsed(elapsed) : string.Empty;
        RemainingText = Item.Status == MediaStatus.Running && Item.EstimatedRemaining is { } left
            ? MainViewModel.FormatRemaining(left)
            : string.Empty;
    }

    public void ResetForRun()
    {
        Item.Status = MediaStatus.Waiting;
        Item.StatusText = "Waiting";
        Item.Message = null;
        Item.Progress = 0;
        Item.Hits = null;
        Item.StartedAt = null;
        Item.FinishedAt = null;

        // Its weight in the overall percentage until the run knows more (Generate may have changed since it was added).
        Item.ExpectedSeconds = RunHistory.Default.EstimateFile(Item.InputPath, Item.GenerateSubtitles);
        Sync();
    }
}
