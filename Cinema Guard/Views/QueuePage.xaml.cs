using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CinemaGuard.Core.Media;
using Cinema_Guard.Services;
using Cinema_Guard.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace Cinema_Guard.Views;

public sealed partial class QueuePage : Page
{
    public MainViewModel ViewModel { get; } = App.MainViewModel!;

    public QueuePage()
    {
        InitializeComponent();

        // Also from a right-click the list already handled, so the menu never acts on a stale row.
        MediaList.AddHandler(RightTappedEvent, new RightTappedEventHandler((_, e) =>
        {
            if (RowFrom(e.OriginalSource) is { } row) _contextRow = row;
        }), handledEventsToo: true);

        Loaded += (_, _) =>
        {
            ViewModel.Items.CollectionChanged += Items_CollectionChanged;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            UpdateSelectionBar();
        };

        Unloaded += (_, _) =>
        {
            ViewModel.Items.CollectionChanged -= Items_CollectionChanged;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        };
    }

    // ---------------------------------------------------------------- selection

    private void SelectAll_Click(object sender, RoutedEventArgs e) => MediaList.SelectAll();

    private void SelectNone_Click(object sender, RoutedEventArgs e) => MediaList.SelectedItems.Clear();

    private void InvertSelection_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedTargets().ToHashSet();
        MediaList.SelectedItems.Clear();

        foreach (var item in ViewModel.Items)
        {
            if (!selected.Contains(item)) MediaList.SelectedItems.Add(item);
        }
    }

    private void MediaList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SelectedTargets().ToHashSet();
        foreach (var item in ViewModel.Items) item.IsSelected = selected.Contains(item);

        UpdateSelectionBar();
    }

    /// <summary>The tick box inside a card selects or deselects that row (clicking the row itself does too).</summary>
    private void SelectBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: MediaItemViewModel target } box) return;

        if (box.IsChecked == true)
        {
            if (!MediaList.SelectedItems.Contains(target)) MediaList.SelectedItems.Add(target);
        }
        else
        {
            MediaList.SelectedItems.Remove(target);
        }
    }

    private void Items_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateSelectionBar();

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsRunning)) UpdateSelectionBar();
    }

    /// <summary>Says what the subtitle buttons will work on, and enables removing only when it can happen.</summary>
    private void UpdateSelectionBar()
    {
        var selected = MediaList.SelectedItems.Count;
        var total = ViewModel.Items.Count;

        SelectionText.Text = selected == 0
            ? "No files selected: the buttons above work on every file"
            : $"{selected} of {total} selected";

        RemoveSelectedButton.IsEnabled = selected > 0 && !ViewModel.IsRunning;
        ClearButton.IsEnabled = total > 0 && !ViewModel.IsRunning;

        // These do nothing while the queue runs.
        GenerateButton.IsEnabled = ExtractButton.IsEnabled = DownloadButton.IsEnabled = !ViewModel.IsRunning;
    }

    // ---------------------------------------------------------------- context menu

    /// <summary>The row the context menu was opened on (right-click, or the menu key on the focused row).</summary>
    private MediaItemViewModel? _contextRow;

    private void MediaList_ContextRequested(UIElement sender, ContextRequestedEventArgs e) =>
        _contextRow = RowFrom(e.OriginalSource);

    /// <summary>
    /// The row an element belongs to. A right-click is raised by the row's ListViewItem, whose
    /// DataContext is not the row, so the container is asked for its item; content inside the card
    /// carries the row as its DataContext.
    /// </summary>
    private MediaItemViewModel? RowFrom(object source)
    {
        for (var element = source as DependencyObject; element is not null && element != MediaList; element = VisualTreeHelper.GetParent(element))
        {
            if (element is ListViewItem container) return MediaList.ItemFromContainer(container) as MediaItemViewModel;
            if (element is FrameworkElement { DataContext: MediaItemViewModel row }) return row;
        }

        return null;
    }

    /// <summary>The single file a menu item such as Peek works on: the row clicked, else the first selected.</summary>
    private MediaItemViewModel? ContextRow() => _contextRow ?? SelectedTargets().FirstOrDefault();

    /// <summary>
    /// The files a bulk menu item works on, as in Explorer: the whole selection when the clicked row is
    /// part of it, otherwise only the clicked row.
    /// </summary>
    private List<MediaItemViewModel> ContextTargets()
    {
        var selected = SelectedTargets().ToList();
        if (_contextRow is null || selected.Contains(_contextRow)) return selected;
        return [_contextRow];
    }

    /// <summary>While the queue runs, only the read-only items (Peek, Language report, Open folder) are offered.</summary>
    private void ContextMenu_Opening(object? sender, object e)
    {
        if (sender is not MenuFlyout menu) return;

        var visibility = ViewModel.IsRunning ? Visibility.Collapsed : Visibility.Visible;
        foreach (var item in menu.Items)
        {
            if (item.Tag as string == "idle") item.Visibility = visibility;
        }
    }

    private IEnumerable<MediaItemViewModel> SelectedTargets() =>
        MediaList.SelectedItems.OfType<MediaItemViewModel>().ToList();

    private IEnumerable<MediaItemViewModel> Targets()
    {
        var selected = SelectedTargets().ToList();
        return selected.Count > 0 ? selected : ViewModel.Items;
    }

    private static IntPtr WindowHandle => WindowHelper.GetHandle(App.Shell);

    // ---------------------------------------------------------------- add files

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
            ViewMode = PickerViewMode.List,
        };

        foreach (var extension in FileTypes.VideoExtensions) picker.FileTypeFilter.Add(extension);

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);

        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;

        ViewModel.AddPaths(files.Select(file => file.Path), recursive: false);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        var recursive = await Dialogs.ConfirmAsync(
            XamlRoot,
            "Include subfolders?",
            $"Add videos from subfolders of \u201c{folder.Name}\u201d too?",
            "Yes, all subfolders",
            "No, this folder only");

        ViewModel.AddPaths([folder.Path], recursive);
    }

    // ---------------------------------------------------------------- run

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        // A model that still has to be downloaded is offered here instead of failing every file.
        if (ViewModel.MissingSpeechModel() is { } missing)
        {
            var remote = App.Settings.Current.SpeechModel == CinemaGuard.Core.Speech.SpeechCatalog.RemoteId;
            var go = await Dialogs.ConfirmAsync(XamlRoot, "The speech model isn't ready",
                remote ? missing : $"{missing.Replace(" Open Models to download it.", string.Empty)} Download it now? Start the queue again once it has finished.",
                remote ? "Open Models" : "Download", "Cancel");

            if (!go) return;

            if (!remote) App.Services.GetRequiredService<ModelsViewModel>().DownloadCurrentChoice();
            App.Shell?.NavigateTo("models");
            return;
        }

        await ViewModel.StartAsync();
    }

    private void SpeechModel_Click(object sender, RoutedEventArgs e) => App.Shell?.NavigateTo("models");

    private void Stop_Click(object sender, RoutedEventArgs e) => ViewModel.Stop();

    private void GenerateSplit_Click(SplitButton sender, SplitButtonClickEventArgs args) => ViewModel.SetGenerateMode(GenerateMode.All, Targets());

    private void GenerateAll_Click(object sender, RoutedEventArgs e) => ViewModel.SetGenerateMode(GenerateMode.All, Targets());

    private void GenerateMissing_Click(object sender, RoutedEventArgs e) => ViewModel.SetGenerateMode(GenerateMode.Missing, Targets());

    private void GenerateNone_Click(object sender, RoutedEventArgs e) => ViewModel.SetGenerateMode(GenerateMode.None, Targets());

    private void GenerateMenu_Click(object sender, RoutedEventArgs e) => ViewModel.SetGenerateMode(GenerateMode.All, ContextTargets());

    private async void Extract_Click(object sender, RoutedEventArgs e) => await ViewModel.ExtractSubtitlesAsync(Targets());

    private async void Download_Click(object sender, RoutedEventArgs e) => await ViewModel.DownloadSubtitlesAsync(Targets());

    private async void ExtractMenu_Click(object sender, RoutedEventArgs e) => await ViewModel.ExtractSubtitlesAsync(ContextTargets());

    private async void DownloadMenu_Click(object sender, RoutedEventArgs e) => await ViewModel.DownloadSubtitlesAsync(ContextTargets());

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsRunning || ViewModel.Items.Count == 0) return;

        if (await Dialogs.ConfirmAsync(XamlRoot, "Clear the queue?", "Remove every file from the queue?", "Clear"))
            ViewModel.Clear();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e) => await RemoveAsync(SelectedTargets().ToList());

    private async void RemoveMenu_Click(object sender, RoutedEventArgs e) => await RemoveAsync(ContextTargets());

    private async Task RemoveAsync(List<MediaItemViewModel> targets)
    {
        if (targets.Count == 0) return;

        if (await Dialogs.ConfirmAsync(XamlRoot, "Remove from the queue?",
                targets.Count == 1 ? "Remove this file from the queue?" : $"Remove these {targets.Count} files from the queue?",
                "Remove"))
        {
            ViewModel.Remove(targets);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var target = ContextRow();
        if (target is not null) ViewModel.OpenFolder(target);
    }

    private async void SelectSubtitles_Click(object sender, RoutedEventArgs e)
    {
        var target = ContextRow();
        if (target is null) return;

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var extension in FileTypes.SubtitleExtensions) picker.FileTypeFilter.Add(extension);

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);

        var file = await picker.PickSingleFileAsync();
        if (file is not null) ViewModel.SetCustomSubtitle(target, file.Path);
    }

    // ---------------------------------------------------------------- peek / report

    private async void Peek_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MediaItemViewModel target) return;
        await ShowPeekAsync(target);
    }

    private async void PeekMenu_Click(object sender, RoutedEventArgs e)
    {
        var target = ContextRow();
        if (target is not null) await ShowPeekAsync(target);
    }

    private async Task ShowPeekAsync(MediaItemViewModel target)
    {
        var content = await ViewModel.BuildPeekAsync(target);
        await Dialogs.ShowPeekAsync(XamlRoot, content);
    }

    private async void LanguageReport_Click(object sender, RoutedEventArgs e)
    {
        var target = ContextRow();
        if (target is null) return;

        await Dialogs.ShowLanguageReportAsync(XamlRoot, target.FileName, ViewModel.GetForeignSpans(target));
    }

    // ---------------------------------------------------------------- drag and drop

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to the queue";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var storageItems = await e.DataView.GetStorageItemsAsync();
        var videos = new List<string>();
        var subtitles = new List<string>();
        var folders = new List<StorageFolder>();

        foreach (var item in storageItems)
        {
            switch (item)
            {
                case StorageFile file when FileTypes.IsSupportedVideo(Path.GetExtension(file.Path)):
                    videos.Add(file.Path);
                    break;
                case StorageFile file when FileTypes.IsSupportedSubtitle(Path.GetExtension(file.Path)):
                    subtitles.Add(file.Path);
                    break;
                case StorageFolder folder:
                    folders.Add(folder);
                    break;
            }
        }

        // A single subtitle dropped on a single selected row attaches to that row.
        if (subtitles.Count == 1 && videos.Count == 0 && folders.Count == 0 && MediaList.SelectedItems.Count == 1)
        {
            ViewModel.SetCustomSubtitle((MediaItemViewModel)MediaList.SelectedItems[0], subtitles[0]);
            return;
        }

        if (videos.Count > 0) ViewModel.AddPaths(videos, recursive: false);

        if (folders.Count > 0)
        {
            var recursive = await Dialogs.ConfirmAsync(
                XamlRoot,
                "Include subfolders?",
                "Add videos from subfolders too?",
                "Yes, all subfolders",
                "No, only this folder");

            foreach (var folder in folders) ViewModel.AddPaths([folder.Path], recursive);
        }
    }

    // ---------------------------------------------------------------- keyboard

    private void MediaList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete || ViewModel.IsRunning) return;

        var selected = SelectedTargets().ToList();
        if (selected.Count == 0) return;

        ViewModel.Remove(selected);
        e.Handled = true;
    }
}
