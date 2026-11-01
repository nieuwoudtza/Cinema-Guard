using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Cinema_Guard.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Cinema_Guard.Views;

/// <summary>Content dialogs built in code so the pages stay small and there is no extra XAML surface.</summary>
public static class Dialogs
{
    private static readonly DataTemplate PeekTemplate = (DataTemplate)XamlReader.Load("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Grid ColumnDefinitions="130,44,*" ColumnSpacing="10" Padding="0,4">
                <TextBlock Text="{Binding Word}" FontWeight="SemiBold" VerticalAlignment="Center" />
                <TextBlock Grid.Column="1" Text="{Binding Count}" HorizontalAlignment="Right" VerticalAlignment="Center" />
                <TextBlock Grid.Column="2" Text="{Binding Times}" Style="{StaticResource CaptionTextBlockStyle}"
                           Foreground="{ThemeResource TextFillColorSecondaryBrush}" TextWrapping="Wrap" VerticalAlignment="Center" />
            </Grid>
        </DataTemplate>
        """);

    private static readonly DataTemplate SpanTemplate = (DataTemplate)XamlReader.Load("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <StackPanel Spacing="3" Padding="0,6">
                <StackPanel Orientation="Horizontal" Spacing="8">
                    <TextBlock Text="{Binding Range}" FontWeight="SemiBold" />
                    <TextBlock Text="{Binding Language}" Style="{StaticResource CaptionTextBlockStyle}"
                               Foreground="{ThemeResource AccentTextFillColorPrimaryBrush}" VerticalAlignment="Center" />
                    <TextBlock Text="{Binding Segments}" Style="{StaticResource CaptionTextBlockStyle}"
                               Foreground="{ThemeResource TextFillColorSecondaryBrush}" VerticalAlignment="Center" />
                </StackPanel>
                <TextBlock Text="{Binding Text}" TextWrapping="Wrap" />
                <TextBlock Text="{Binding EnglishText}" Style="{StaticResource CaptionTextBlockStyle}"
                           Foreground="{ThemeResource TextFillColorSecondaryBrush}" TextWrapping="Wrap" />
            </StackPanel>
        </DataTemplate>
        """);

    public static async Task ShowPeekAsync(XamlRoot root, PeekContent content)
    {
        var body = new StackPanel { Spacing = 10, MinWidth = 520 };

        body.Children.Add(new TextBlock
        {
            Text = content.Subtitle,
            Style = (Style)Application.Current.Resources["MutedStyle"],
            TextWrapping = TextWrapping.Wrap,
        });

        if (content.Rows.Count == 0)
        {
            body.Children.Add(new TextBlock { Text = "No filtered words were found." });
        }
        else
        {
            var list = new ListView
            {
                ItemsSource = content.Rows,
                ItemTemplate = PeekTemplate,
                SelectionMode = ListViewSelectionMode.None,
                MaxHeight = 420,
            };

            body.Children.Add(list);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"Peek — {content.Header}",
            Content = new ScrollViewer { Content = body, MaxHeight = 520 },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        await dialog.ShowAsync();
    }

    public static async Task ShowLanguageReportAsync(XamlRoot root, string title, IReadOnlyList<ForeignSpanDto> spans)
    {
        var body = new StackPanel { Spacing = 8, MinWidth = 560 };

        if (spans.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "No non-English dialogue was detected. (The multilingual pass adds this report when it is enabled.)",
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            body.Children.Add(new ListView
            {
                ItemsSource = spans,
                ItemTemplate = SpanTemplate,
                SelectionMode = ListViewSelectionMode.None,
                MaxHeight = 460,
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"Language report — {title}",
            Content = new ScrollViewer { Content = body, MaxHeight = 560 },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        await dialog.ShowAsync();
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string primary = "Yes", string closeText = "Cancel")
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>The command-line options (the same text as --help), selectable, with a Copy button that leaves the dialog open.</summary>
    public static async Task ShowCommandLineAsync(XamlRoot root, string text)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Command-line options",
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = text,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 12,
                    IsTextSelectionEnabled = true,
                },
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Auto,
                MaxHeight = 520,
            },
            PrimaryButtonText = "Copy",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        // ContentDialog's own MaxWidth (548) would wrap the 100-column text.
        dialog.Resources["ContentDialogMaxWidth"] = 820.0;

        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            dialog.PrimaryButtonText = "Copied";
        };

        await dialog.ShowAsync();
    }

    /// <summary>
    /// Counts down and returns true when the time runs out or the user presses the action button; false
    /// on Cancel. For "When finished": a sleep or shutdown nobody can stop would be a nasty surprise.
    /// </summary>
    public static async Task<bool> CountdownAsync(XamlRoot root, string title, Func<int, string> message, int seconds, string actionText)
    {
        var text = new TextBlock { Text = message(seconds), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        var proceed = false;

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = text,
            PrimaryButtonText = actionText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        var left = seconds;
        var timer = dialog.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) =>
        {
            left--;
            text.Text = message(Math.Max(0, left));
            if (left > 0) return;

            timer.Stop();
            proceed = true;
            dialog.Hide();
        };

        timer.Start();

        try
        {
            var result = await dialog.ShowAsync();
            return proceed || result == ContentDialogResult.Primary;
        }
        finally
        {
            timer.Stop();
        }
    }

    /// <summary>Every model or engine delete goes through here. Cancel is the default button.</summary>
    public static async Task<bool> ConfirmDeleteAsync(XamlRoot root, string name, long bytes, string? extra = null)
    {
        var body = new StackPanel { Spacing = 8, MaxWidth = 440 };
        body.Children.Add(new TextBlock
        {
            Text = $"This frees {ModelsViewModel.Size(bytes)} on this PC. You can download it again at any time.",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = extra ?? "Films you already processed are not affected, and their saved transcripts are kept.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["MutedStyle"],
        });

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"Delete {name}?",
            Content = body,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// The first-run guide (and the Models page's "How do I choose?"): three plain steps and one line per
    /// family. Returns true when the user wants the recommended model downloaded.
    /// </summary>
    public static async Task<bool> SetupGuideAsync(XamlRoot root, bool recommendedInstalled, long recommendedBytes)
    {
        var muted = (Style)Application.Current.Resources["MutedStyle"];
        var body = new StackPanel { Spacing = 14, MaxWidth = 480 };

        body.Children.Add(new TextBlock
        {
            Text = "Cinema Guard listens to each film with a speech model, finds the words on your filter and mutes them.",
            TextWrapping = TextWrapping.Wrap,
        });

        var steps = new StackPanel { Spacing = 10 };
        steps.Children.Add(Step("1", "Pick a speech model", "The recommended one suits almost everyone. You can change it later on the Models page."));
        steps.Children.Add(Step("2", "Download it once",
            recommendedInstalled
                ? "The recommended model is already downloaded, so this step is done."
                : $"About {ModelsViewModel.Size(recommendedBytes)}, one time. It stays on this PC; you can delete it on the Models page."));
        steps.Children.Add(Step("3", "Add films and press Censor", "On the Queue page, drop in films or folders. Cinema Guard writes a muted copy next to each one."));
        body.Children.Add(steps);

        var choices = new StackPanel { Spacing = 4 };
        choices.Children.Add(new TextBlock { Text = "Which model should I pick?", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var line in new[]
                 {
                     "Recommended (Phonon 2.5 pair): catches the most words in English films, with a second pass for other languages.",
                     "Parakeet: faster. English, or 25 European languages.",
                     "Whisper: the most languages, and fast on a graphics card.",
                     "Your own server: for a GPU machine on your network or an online service.",
                 })
        {
            choices.Children.Add(new TextBlock { Text = "•  " + line, TextWrapping = TextWrapping.Wrap, Style = muted });
        }

        body.Children.Add(choices);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Set up Cinema Guard in three steps",
            Content = new ScrollViewer { Content = body, MaxHeight = 560 },
            PrimaryButtonText = recommendedInstalled ? "Got it" : "Download the recommended model",
            CloseButtonText = recommendedInstalled ? string.Empty : "Not now",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        return !recommendedInstalled && result == ContentDialogResult.Primary;

        static Grid Step(string number, string title, string text)
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var badge = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = number,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"],
                },
            };

            var texts = new StackPanel { Spacing = 2 };
            texts.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            texts.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["MutedStyle"] });

            grid.Children.Add(badge);
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);
            return grid;
        }
    }

    public sealed record ServerSettings(string Url, string Model, string? ApiKey, int MaxUploadMb, bool Use);

    /// <summary>Address, model, key and upload cap for "Your own server", with a connection test.</summary>
    public static async Task<ServerSettings?> ServerSetupAsync(XamlRoot root, CinemaGuard.Core.Speech.RemoteSpec current, bool hasSavedKey)
    {
        var url = new TextBox { Header = "Server address", Text = current.Url, PlaceholderText = "http://192.168.1.20:8000" };
        var model = new TextBox
        {
            Header = "Model name",
            Text = current.Model,
            PlaceholderText = "phonon-2.5-en for fermion serve, whisper-1 for OpenAI",
        };
        var key = new PasswordBox
        {
            Header = "API key (only for online services)",
            PlaceholderText = hasSavedKey ? "Saved. Leave empty to keep it" : "Optional",
        };
        var cap = new NumberBox
        {
            Header = "Split uploads larger than (MB)",
            Value = current.MaxUploadMb,
            Minimum = 0,
            Maximum = 4096,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            PlaceholderText = "0",
        };
        var capNote = new TextBlock
        {
            Text = "0 sends each film in one piece (right for fermion serve). Use 25 for OpenAI.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["MutedStyle"],
        };

        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["MutedStyle"] };
        var test = new Button { Content = "Test connection" };

        test.Click += async (_, _) =>
        {
            test.IsEnabled = false;
            result.Text = "Testing…";

            try
            {
                using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
                var apiKey = string.IsNullOrEmpty(key.Password) ? current.ApiKey : key.Password;
                result.Text = await CinemaGuard.Core.Speech.RemoteTranscriber.TestAsync(
                    new CinemaGuard.Core.Speech.RemoteSpec(url.Text, model.Text, apiKey, 0, null), timeout.Token);
            }
            catch (Exception ex)
            {
                result.Text = "Could not connect: " + ex.Message;
            }
            finally
            {
                test.IsEnabled = true;
            }
        };

        var intro = new TextBlock
        {
            Text = "Any server with an OpenAI-style /v1/audio/transcriptions address that returns word timestamps works: " +
                   "Phonon's \"fermion serve\" in Docker on a GPU machine, or a service such as OpenAI or Groq.",
            TextWrapping = TextWrapping.Wrap,
        };

        var testRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        testRow.Children.Add(test);
        testRow.Children.Add(result);

        var body = new StackPanel { Spacing = 12, MaxWidth = 480 };
        foreach (var element in new UIElement[] { intro, url, model, key, cap, capNote, testRow }) body.Children.Add(element);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Your own server",
            Content = new ScrollViewer { Content = body, MaxHeight = 600 },
            PrimaryButtonText = "Save and use",
            SecondaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var answer = await dialog.ShowAsync();
        if (answer == ContentDialogResult.None) return null;

        return new ServerSettings(
            url.Text.Trim(),
            model.Text.Trim(),
            string.IsNullOrEmpty(key.Password) ? null : key.Password,
            double.IsNaN(cap.Value) ? 0 : (int)Math.Round(cap.Value),
            answer == ContentDialogResult.Primary);
    }

    public static async Task MessageAsync(XamlRoot root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        await dialog.ShowAsync();
    }
}
