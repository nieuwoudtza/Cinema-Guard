using Cinema_Guard.Controls;
using Cinema_Guard.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Cinema_Guard.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; } = App.Services.GetRequiredService<SettingsViewModel>();

    public SettingsPage()
    {
        InitializeComponent();
    }

    private void FocusSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplySearch(sender.Text);

    /// <summary>Shows the rows whose section, header, description or choices hold every typed word; hides a section with none.</summary>
    private void ApplySearch(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var anyShown = false;
        TextBlock? section = null;

        foreach (var child in SettingsList.Children)
        {
            if (child is TextBlock header) { section = header; continue; }
            if (child is not Border card || section is null) continue;

            var rows = card.Child is SettingRow single ? [single] : ((Panel)card.Child).Children.OfType<SettingRow>().ToList();
            var cardShown = false;

            foreach (var row in rows)
            {
                var text = section.Text + " " + RowText(row);
                var show = words.All(word => text.Contains(word, StringComparison.CurrentCultureIgnoreCase));
                row.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                row.Highlight(words);
                cardShown |= show;
            }

            section.Visibility = card.Visibility = cardShown ? Visibility.Visible : Visibility.Collapsed;
            SearchHighlight.Apply(section, words);
            anyShown |= cardShown;
        }

        NoMatches.Text = $"No settings match \u201C{query.Trim()}\u201D.";
        NoMatches.Visibility = anyShown ? Visibility.Collapsed : Visibility.Visible;
        SettingsScroller.ChangeView(null, 0, null, true);
    }

    private static string RowText(SettingRow row)
    {
        var text = row.Header + " " + row.Description;

        // The options of a drop-down, so "Dual" or "Recycle Bin" finds its row.
        if (row.Body is ComboBox box)
            text += " " + string.Join(" ", box.Items.Select(item => item is ComboBoxItem option ? option.Content : item));

        return text;
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
