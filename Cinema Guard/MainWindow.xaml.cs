using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CinemaGuard.Core.Tools;
using Cinema_Guard.Services;
using Microsoft.Extensions.DependencyInjection;
using Cinema_Guard.ViewModels;
using Cinema_Guard.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Cinema_Guard;

public sealed partial class MainWindow : Window
{
    /// <summary>The smallest window, in effective (96 DPI) pixels.</summary>
    private const int MinimumWidth = 1040;
    private const int MinimumHeight = 680;

    /// <summary>The browser extension's page in the Chrome Web Store.</summary>
    public const string ExtensionUrl = "https://chromewebstore.google.com/detail/cinema-guard/maidlcfjpdcmfbbkdbijfhegbbnoicfn";

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private readonly TaskbarProgress _taskbar;
    private readonly CancellationTokenSource _updateCheck = new();

    /// <summary>Closing was confirmed despite scans waiting for Censor; the next close goes through.</summary>
    private bool _closeConfirmed;

    public MainViewModel ViewModel => App.MainViewModel!;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);

        // Sizes are in physical pixels: scale by the display's DPI, and never past its work area.
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0) scale = 1;

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(1240 * scale), area.Width);
        var height = Math.Min((int)(860 * scale), area.Height);

        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(area.X + Math.Max(0, (area.Width - width) / 2), area.Y + Math.Max(0, (area.Height - height) / 2)));

        // The queue's header and a file's row (two tick boxes, word count, times) need this much room.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min((int)(MinimumWidth * scale), area.Width);
            presenter.PreferredMinimumHeight = Math.Min((int)(MinimumHeight * scale), area.Height);
        }

        // Taskbar, Alt+Tab and the window menu; an unpackaged app does not get the exe's icon by itself.
        var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (System.IO.File.Exists(icon)) AppWindow.SetIcon(icon);

        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        _taskbar = new TaskbarProgress(WinRT.Interop.WindowNative.GetWindowHandle(this));

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        // Scans waiting for Censor live in memory only: closing forgets them and their review, so say so first.
        AppWindow.Closing += async (_, e) =>
        {
            var held = ViewModel.HeldScanCount;
            if (held == 0 || _closeConfirmed) return;

            e.Cancel = true;
            bool close;

            try
            {
                close = await Dialogs.ConfirmAsync(
                    Content.XamlRoot,
                    "Close Cinema Guard?",
                    $"{(held == 1 ? "One file has" : $"{held} files have")} been scanned but not censored yet. Closing forgets " +
                    $"{(held == 1 ? "its words and your review of them; it is" : "their words and your review of them; they are")} scanned again next time.",
                    "Close",
                    "Keep open");
            }
            catch (Exception ex)
            {
                // Another dialog is open (only one can be): the close was asked for, so it goes ahead.
                CinemaGuard.Core.Logging.Log.Warn($"Could not ask before closing: {ex.Message}");
                close = true;
            }

            if (!close) return;

            _closeConfirmed = true;
            Close();
        };

        Closed += (_, _) =>
        {
            _updateCheck.Cancel();
            ViewModel.Stop();
            ViewModel.ApplyUpdateOnExit();
        };

        // The first start opens Tools, where one button installs FFmpeg and the speech model.
        var settings = App.Settings.Current;
        var firstRun = settings.FirstRun || !settings.SetupGuideShown;

        if (firstRun)
        {
            settings.FirstRun = false;
            settings.SetupGuideShown = true;
            App.Settings.Save();
        }

        // Also whenever FFmpeg is still missing: closing the app before pressing Set up must not leave
        // the user on a queue that cannot run, with nothing saying why.
        var openTools = firstRun || App.Services.GetRequiredService<ToolLocator>().ResolveFfmpeg(settings) is null;

        NavigateTo(openTools ? "tools" : "queue");

        // SelectionChanged may not fire before the window is shown; the frame must not start empty.
        var start = openTools ? typeof(ToolsPage) : typeof(QueuePage);
        if (ContentFrame.CurrentSourcePageType != start) ContentFrame.Navigate(start);

        // Before the update is reported, which forgets it.
        ViewModel.TakeWhatsNew();

        // Once shown (App activates the window after this): the installer opens it without focus.
        if (ViewModel.ReportUpdateResult()) DispatcherQueue.TryEnqueue(BringToFront);

        // The first start after an update shows what changed; a dialog needs the window's XamlRoot.
        if (ViewModel.HasWhatsNew) Nav.Loaded += ShowWhatsNewOnce;

        _ = ViewModel.CheckForUpdatesAsync(_updateCheck.Token);
        _ = ViewModel.CheckGpuImageAsync(_updateCheck.Token);

        // Once the window is up: the Models page then opens on what is already known about the disk.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => App.Services.GetRequiredService<ModelsViewModel>());
    }

    /// <summary>
    /// The app was opened again: show this window where the user is looking. Minimised, or left on
    /// another virtual desktop, it otherwise looks as if the app runs on in the background.
    /// </summary>
    public void BringToFront()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        VirtualDesktops.MoveToCurrent(handle);

        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();

        Activate();
        SetForegroundWindow(handle);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    /// <summary>Selects a page by its nav tag ("queue", "filter", "models", "tools", "settings").</summary>
    public void NavigateTo(string tag)
    {
        if (tag == "settings")
        {
            Nav.SelectedItem = Nav.SettingsItem;
            return;
        }

        foreach (var item in Nav.MenuItems)
        {
            if (item is NavigationViewItem navigationItem && (string)navigationItem.Tag == tag)
            {
                Nav.SelectedItem = navigationItem;
                return;
            }
        }
    }

    private async void RestartToUpdate_Click(object sender, RoutedEventArgs e) => await ViewModel.RestartToUpdateAsync();

    private async void DownloadGpuImage_Click(object sender, RoutedEventArgs e) => await ViewModel.DownloadGpuImageAsync();

    private async void WhatsNew_Click(object sender, RoutedEventArgs e) => await ShowWhatsNewAsync();

    private async void ShowWhatsNewOnce(object sender, RoutedEventArgs e)
    {
        Nav.Loaded -= ShowWhatsNewOnce;

        try
        {
            await ShowWhatsNewAsync();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another dialog was already open; the Updated bar's What's new button still shows it.
        }
    }

    private Task ShowWhatsNewAsync() => Dialogs.ShowChangelogAsync(
        Nav.XamlRoot, "What's new", $"Cinema Guard is now version {CinemaGuard.Core.CoreInfo.Version}.", ViewModel.WhatsNew);

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.OverallProgress) or nameof(MainViewModel.IsRunning))
            _taskbar.Set(ViewModel.OverallProgress, ViewModel.IsRunning);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = args.IsSettingsSelected
            ? typeof(SettingsPage)
            : (args.SelectedItem as NavigationViewItem)?.Tag switch
            {
                "filter" => typeof(FilterPage),
                "models" => typeof(ModelsPage),
                "tools" => typeof(ToolsPage),
                _ => typeof(QueuePage),
            };

        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
    }

    private void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is "extension")
            Process.Start(new ProcessStartInfo(ExtensionUrl) { UseShellExecute = true });
    }
}
