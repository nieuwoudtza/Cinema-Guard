using Cinema_Guard.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cinema_Guard.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; } = App.Services.GetRequiredService<SettingsViewModel>();

    public SettingsPage()
    {
        InitializeComponent();
    }

    private void EditFilter_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(FilterPage));

    private void OpenTools_Click(object sender, RoutedEventArgs e) => App.Shell?.NavigateTo("tools");

    private void ChangeModel_Click(object sender, RoutedEventArgs e) => App.Shell?.NavigateTo("models");

    private async void CommandLine_Click(object sender, RoutedEventArgs e) =>
        await Dialogs.ShowCommandLineAsync(XamlRoot, CinemaGuard.Core.Pipeline.LaunchArguments.HelpText);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var (ok, message) = ViewModel.Save();

        SaveStatus.Text = message;
        SaveStatus.Foreground = ok
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 129, 141));
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = await Dialogs.ConfirmAsync(
            XamlRoot,
            "Reset the settings?",
            "This restores the recommended defaults, including the filter word lists.",
            "Reset");

        if (!confirmed) return;

        ViewModel.ResetToDefaults();

        // Bindings to the settings object are one-time, so reload the page with the new values.
        Frame.Navigate(typeof(SettingsPage));
        Frame.BackStack.Clear();
    }
}
