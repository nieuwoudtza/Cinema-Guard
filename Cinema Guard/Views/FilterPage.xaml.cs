using System;
using System.Collections.Generic;
using System.Linq;
using CinemaGuard.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cinema_Guard.Views;

public sealed partial class FilterPage : Page
{
    private readonly AppSettings _settings = App.Settings.Current;
    private readonly List<(CheckBox Box, string Root)> _fullBoxes = [];
    private readonly List<(CheckBox Box, string Root)> _singleBoxes = [];

    public FilterPage()
    {
        InitializeComponent();
        Build();
    }

    private void Build()
    {
        var familyWords = FilterCatalog.Roots.SelectMany(FilterCatalog.Associated).ToHashSet(StringComparer.Ordinal);

        foreach (var root in FilterCatalog.Roots)
        {
            AddFamily(FullWordsPanel, _fullBoxes, _settings.WordsFull, root);
            AddFamily(SingleWordsPanel, _singleBoxes, _settings.WordsSingle, root);
        }

        FullCustom.Text = string.Join(Environment.NewLine, _settings.WordsFull.Where(word => !familyWords.Contains(word)));
        SingleCustom.Text = string.Join(Environment.NewLine, _settings.WordsSingle.Where(word => !familyWords.Contains(word)));

        Status.Text = $"{_settings.WordsFull.Count + _settings.WordsSingle.Count} word(s) active";
    }

    private void AddFamily(StackPanel panel, List<(CheckBox, string)> boxes, List<string> words, string root)
    {
        var box = new CheckBox
        {
            Content = FilterCatalog.DisplayName(root),
            Tag = root,
            MinWidth = 0,
            IsChecked = FilterCatalog.IsFamilyEnabled(words, root),
        };

        ToolTipService.SetToolTip(box, FamilyToolTip(root));

        box.Checked += (_, _) => FilterCatalog.EnableFamily(words, root, true);
        box.Unchecked += (_, _) => FilterCatalog.EnableFamily(words, root, false);

        panel.Children.Add(box);
        boxes.Add((box, root));
    }

    /// <summary>Every spelling the checkbox toggles, masked like the label.</summary>
    private static ToolTip FamilyToolTip(string root)
    {
        var family = FilterCatalog.Associated(root);
        var heading = family.Count == 1 ? "Includes 1 word" : $"Includes {family.Count} words";

        var panel = new StackPanel { Spacing = 4, MaxWidth = 420 };
        panel.Children.Add(new TextBlock { Text = heading, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        panel.Children.Add(new TextBlock
        {
            Text = string.Join(", ", family.Select(FilterCatalog.DisplayName)),
            TextWrapping = TextWrapping.Wrap,
        });

        return new ToolTip { Content = panel, MaxWidth = 460 };
    }

    private void ToggleAllFull_Click(object sender, RoutedEventArgs e) => ToggleAll(_fullBoxes);

    private void ToggleAllSingle_Click(object sender, RoutedEventArgs e) => ToggleAll(_singleBoxes);

    private static void ToggleAll(List<(CheckBox Box, string Root)> boxes)
    {
        var allChecked = boxes.All(entry => entry.Box.IsChecked == true);

        foreach (var (box, _) in boxes) box.IsChecked = !allChecked;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ApplyCustomWords();

        if (_settings.WordsFull.Count == 0 && _settings.WordsSingle.Count == 0)
        {
            Status.Text = "Pick at least one word before saving.";
            return;
        }

        App.Settings.Save();

        // Show the lists as saved (upper case, one space inside a phrase), so the save is visible.
        var familyWords = FilterCatalog.Roots.SelectMany(FilterCatalog.Associated).ToHashSet(StringComparer.Ordinal);
        FullCustom.Text = string.Join(Environment.NewLine, _settings.WordsFull.Where(word => !familyWords.Contains(word)));
        SingleCustom.Text = string.Join(Environment.NewLine, _settings.WordsSingle.Where(word => !familyWords.Contains(word)));

        Status.Text = $"Saved · {_settings.WordsFull.Count + _settings.WordsSingle.Count} word(s) active";
    }

    private void ApplyCustomWords()
    {
        var familyWords = FilterCatalog.Roots.SelectMany(FilterCatalog.Associated).ToHashSet(StringComparer.Ordinal);

        _settings.WordsFull = MergeFamilyWords(_settings.WordsFull, familyWords, FullCustom.Text);
        _settings.WordsSingle = MergeFamilyWords(_settings.WordsSingle, familyWords, SingleCustom.Text);
        _settings.Normalise();
    }

    private static List<string> MergeFamilyWords(IReadOnlyCollection<string> current, HashSet<string> familyWords, string customText)
    {
        var merged = current.Where(familyWords.Contains).ToList();

        foreach (var line in customText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var word = line.ToUpperInvariant();
            if (word.Length > 0 && !merged.Contains(word)) merged.Add(word);
        }

        return merged;
    }
}
