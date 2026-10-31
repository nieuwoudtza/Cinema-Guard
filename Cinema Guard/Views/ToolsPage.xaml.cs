using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Tools;
using Cinema_Guard.Services;
using Cinema_Guard.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Cinema_Guard.Views;

public sealed partial class ToolsPage : Page
{
    public ToolsViewModel ViewModel { get; } = App.Services.GetRequiredService<ToolsViewModel>();

    private readonly IProcessRunner _runner = App.Services.GetRequiredService<IProcessRunner>();

    public ToolsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.RefreshAsync();
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

    private async void Setup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) button.IsEnabled = false;

        try
        {
            var ready = await ViewModel.SetupAsync();

            if (ready)
            {
                Show(InfoBarSeverity.Success, "Cinema Guard is ready", "Add a film to the queue and press Censor.");
                App.Shell?.NavigateTo("queue");
            }
            else
            {
                Show(InfoBarSeverity.Warning, "Setup", ViewModel.SetupStatus);
            }
        }
        finally
        {
            if (sender is Button again) again.IsEnabled = true;
        }
    }

    private void Models_Click(object sender, RoutedEventArgs e) => App.Shell?.NavigateTo("models");

    private async void AddShortcut_Click(object sender, RoutedEventArgs e)
    {
        var (ok, message) = await ShellIntegration.CreateShortcutAsync(_runner);
        Show(ok ? InfoBarSeverity.Success : InfoBarSeverity.Error, "SendTo shortcut", message);
    }

    private void RemoveShortcut_Click(object sender, RoutedEventArgs e)
    {
        var (ok, message) = ShellIntegration.RemoveShortcut();
        Show(ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning, "SendTo shortcut", message);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenPath(Log.Directory);

    private void OpenSettingsFile_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(App.Settings.SettingsPath);
        OpenPath(directory);
    }

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {path}: {ex.Message}");
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationType() };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHelper.GetHandle(App.Shell));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        var (executable, settingsFile) = LocateOldApp(folder.Path);

        if (executable is null || settingsFile is null)
        {
            Show(InfoBarSeverity.Warning, "Import",
                "That folder does not contain both 'Cinema Guard.exe' and 'cg.dat'. Pick the folder the old app runs from, or its bin\\Release folder.");
            return;
        }

        var script = Path.Combine(AppContext.BaseDirectory, "tools", "Import-CgDat.ps1");
        var result = await LegacySettingsImporter.ImportAsync(_runner, script, executable, settingsFile);

        if (result.Settings is null)
        {
            Show(InfoBarSeverity.Error, "Import failed", result.Error ?? "Unknown error.");
            return;
        }

        // Show what will change before replacing the current settings.
        var confirmed = await Dialogs.ConfirmAsync(
            XamlRoot,
            "Import these settings?",
            (result.Summary ?? "The old settings were read.") + "\n\nThis replaces your current settings.",
            "Import");

        if (!confirmed) return;

        App.Settings.SaveAs(result.Settings);
        Show(InfoBarSeverity.Success, "Settings imported", result.Summary ?? "Done.");
    }

    private static (string? Executable, string? SettingsFile) LocateOldApp(string folder)
    {
        string? executable = null;
        string? settings = null;

        foreach (var directory in new[] { folder, Path.Combine(folder, "bin", "Release"), Path.Combine(folder, "bin", "Debug") })
        {
            if (!Directory.Exists(directory)) continue;

            var exe = Path.Combine(directory, "Cinema Guard.exe");
            var dat = Path.Combine(directory, "cg.dat");

            if (executable is null && File.Exists(exe)) executable = exe;
            if (settings is null && File.Exists(dat)) settings = dat;
        }

        return (executable, settings);
    }

    private static PickerLocationId PickerLocationType() => PickerLocationId.ComputerFolder;

    private void Show(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }
}
