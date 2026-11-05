using System;
using System.Threading.Tasks;
using CinemaGuard.Core.Speech;
using Cinema_Guard.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Cinema_Guard.Views;

public sealed partial class ModelsPage : Page
{
    /// <summary>Set by the first-run flow so the page opens the setup guide once it is on screen.</summary>
    public static bool ShowGuideOnLoad { get; set; }

    public ModelsViewModel ViewModel { get; } = App.Services.GetRequiredService<ModelsViewModel>();

    public ModelsPage()
    {
        InitializeComponent();
        Loaded += Page_Loaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.VariantChangedOnActiveRow += OnVariantChangedOnActiveRow;
        ViewModel.Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.VariantChangedOnActiveRow -= OnVariantChangedOnActiveRow;
        base.OnNavigatedFrom(e);
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (!ShowGuideOnLoad) return;
        ShowGuideOnLoad = false;
        await ShowGuideAsync();
    }

    // ------------------------------------------------------------------ choosing

    private async void Row_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!ViewModel.IsReady) return;

        // Taps on the dropdown, the switch or a button belong to them, not to "pick this row".
        if (IsInsideControl(e.OriginalSource as DependencyObject, sender as DependencyObject)) return;
        if ((sender as FrameworkElement)?.DataContext is ModelRowViewModel row) await SelectAsync(row);
    }

    private async void Radio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radio || radio.DataContext is not ModelRowViewModel row) return;

        // The click already ticked it; the row decides (a download may be declined), so show its real state.
        radio.IsChecked = row.IsSelected;
        await SelectAsync(row);
        radio.IsChecked = row.IsSelected;
    }

    private async Task SelectAsync(ModelRowViewModel row)
    {
        var decision = ViewModel.Prepare(row);

        if (decision.Blocked is not null)
        {
            ViewModel.Notify("Not right now", decision.Blocked, InfoBarSeverity.Warning);
            return;
        }

        if (decision.Applied) return;

        if (row.Kind == ModelRowKind.Remote)
        {
            await SetUpServerAsync();
            return;
        }

        if (decision.DownloadText is null) return;

        var title = row.Kind == ModelRowKind.Pair ? "Download the recommended model?" : $"Download {row.Name}?";
        if (await Dialogs.ConfirmAsync(XamlRoot, title, decision.DownloadText, "Download", "Cancel"))
            ViewModel.DownloadAndSelect(row, select: true);
    }

    private static bool IsInsideControl(DependencyObject? source, DependencyObject? row)
    {
        for (var node = source; node is not null && node != row; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or ComboBox or ToggleSwitch) return true;
        }

        return false;
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } row) ViewModel.DownloadAndSelect(row, select: false);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } row) ViewModel.Cancel(row);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } row) return;

        if (ViewModel.DeleteBlocked(row) is { } blocked)
        {
            await Dialogs.MessageAsync(XamlRoot, $"{row.Name} can't be deleted now", blocked);
            return;
        }

        var name = row.HasVariants ? $"{row.Name} ({row.Variant!.Label})" : row.Name;
        if (!await Dialogs.ConfirmDeleteAsync(XamlRoot, name, ViewModel.SizeOnDisk(row))) return;

        EnginePack? unused;

        try
        {
            unused = ViewModel.Delete(row);
        }
        catch (Exception ex)
        {
            await Dialogs.MessageAsync(XamlRoot, "Could not delete it", ex.Message);
            return;
        }

        ViewModel.Notify("Deleted", $"{name} was deleted.", InfoBarSeverity.Success);

        if (unused is null) return;

        var engineName = unused.Kind == PackKind.PhononRuntime ? "Phonon engine" : unused.Name;
        if (await Dialogs.ConfirmDeleteAsync(XamlRoot, $"the {engineName} too", ViewModel.EngineSize(new EngineRowViewModel(unused)),
                $"No other downloaded model uses the {engineName}. It is downloaded again if you pick such a model later."))
        {
            try
            {
                ViewModel.DeleteEngine(unused);
            }
            catch (Exception ex)
            {
                await Dialogs.MessageAsync(XamlRoot, "Could not delete it", ex.Message);
            }
        }
    }

    private async void SetUp_Click(object sender, RoutedEventArgs e) => await SetUpServerAsync();

    private async Task SetUpServerAsync()
    {
        var spec = ViewModel.RemoteSpec;
        var result = await Dialogs.ServerSetupAsync(XamlRoot, spec, !string.IsNullOrEmpty(spec.ApiKey));
        if (result is null) return;

        if (result.Use && ViewModel.IsQueueRunning)
        {
            ViewModel.SaveRemote(result.Url, result.Model, result.ApiKey, result.MaxUploadMb, select: false);
            ViewModel.Notify("Saved, not switched", "The queue is running. Pick Your own server once it has finished.", InfoBarSeverity.Warning);
            return;
        }

        ViewModel.SaveRemote(result.Url, result.Model, result.ApiKey, result.MaxUploadMb, result.Use);
        if (result.Use && result.Url.Length > 0) ViewModel.Notify("Ready", "Cinema Guard now sends the audio to your server.", InfoBarSeverity.Success);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ModelSection section) ViewModel.ToggleOlder(section);
    }

    private async void OnVariantChangedOnActiveRow(ModelRowViewModel row, bool needsDownload, int previous)
    {
        if (!needsDownload) return;

        var decision = ViewModel.Prepare(row);
        if (decision.DownloadText is not null &&
            await Dialogs.ConfirmAsync(XamlRoot, $"Download {row.Name} ({row.Variant!.Label})?", decision.DownloadText, "Download", "Cancel"))
        {
            ViewModel.DownloadAndSelect(row, select: true);
        }
        else
        {
            row.SetVariantSilently(previous);
            ViewModel.Refresh();
        }
    }

    // ------------------------------------------------------------------ engines

    private void EngineDownload_Click(object sender, RoutedEventArgs e)
    {
        if (Engine(sender) is { } engine) ViewModel.DownloadEngine(engine);
    }

    private void EngineCancel_Click(object sender, RoutedEventArgs e)
    {
        if (Engine(sender) is { } engine) ViewModel.CancelEngine(engine);
    }

    private async void EngineDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Engine(sender) is not { } engine) return;

        if (ViewModel.DeleteEngineBlocked(engine) is { } blocked)
        {
            await Dialogs.MessageAsync(XamlRoot, $"The {engine.Name} can't be deleted now", blocked);
            return;
        }

        if (!await Dialogs.ConfirmDeleteAsync(XamlRoot, $"the {engine.Name}", ViewModel.EngineSize(engine),
                "Models that need it stay downloaded; the engine is fetched again when one of them is used.")) return;

        try
        {
            ViewModel.DeleteEngine(engine.Pack);
            ViewModel.Notify("Deleted", $"The {engine.Name} was deleted.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            await Dialogs.MessageAsync(XamlRoot, "Could not delete it", ex.Message);
        }
    }

    // ------------------------------------------------------------------ header

    private void Filter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ViewModel.FilterIndex = sender.Items.IndexOf(sender.SelectedItem);

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => ViewModel.OpenFolder();

    private async void Redetect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) button.IsEnabled = false;
        try
        {
            await ViewModel.RedetectAsync();
        }
        finally
        {
            if (sender is Button again) again.IsEnabled = true;
        }
    }

    private async void Guide_Click(object sender, RoutedEventArgs e) => await ShowGuideAsync();

    private async Task ShowGuideAsync()
    {
        var installed = ViewModel.PairInstalled;

        if (await Dialogs.SetupGuideAsync(XamlRoot, installed, ViewModel.PairMissingBytes))
        {
            if (ViewModel.IsQueueRunning)
                ViewModel.Notify("Not right now", "The queue is running. Download the model once it has finished.", InfoBarSeverity.Warning);
            else
                ViewModel.DownloadAndSelect(ViewModel.PairRow, select: true);
        }
    }

    private static ModelRowViewModel? Row(object sender) => (sender as FrameworkElement)?.DataContext as ModelRowViewModel;
    private static EngineRowViewModel? Engine(object sender) => (sender as FrameworkElement)?.DataContext as EngineRowViewModel;
}
