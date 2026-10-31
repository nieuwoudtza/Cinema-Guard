using System;
using System.ComponentModel;
using System.Threading;
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
    private readonly TaskbarProgress _taskbar;
    private readonly CancellationTokenSource _updateCheck = new();

    public MainViewModel ViewModel => App.MainViewModel!;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);

        AppWindow.Resize(new SizeInt32(1240, 860));

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(area.X + Math.Max(0, (area.Width - 1240) / 2), area.Y + Math.Max(0, (area.Height - 860) / 2)));

        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        _taskbar = new TaskbarProgress(WinRT.Interop.WindowNative.GetWindowHandle(this));

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
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

        _ = ViewModel.CheckForUpdatesAsync(_updateCheck.Token);
    }

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

    private void RestartToUpdate_Click(object sender, RoutedEventArgs e) => ViewModel.RestartToUpdate();

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
}
