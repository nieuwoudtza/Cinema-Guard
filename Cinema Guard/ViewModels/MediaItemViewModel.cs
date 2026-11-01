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

    [ObservableProperty] private string _status = "Waiting";
    [ObservableProperty] private string _detail = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private Visibility _progressVisibility = Visibility.Collapsed;
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

    public void ResetForRun()
    {
        Item.Status = MediaStatus.Waiting;
        Item.StatusText = "Waiting";
        Item.Message = null;
        Item.Progress = 0;
        Item.Hits = null;
        Sync();
    }
}
