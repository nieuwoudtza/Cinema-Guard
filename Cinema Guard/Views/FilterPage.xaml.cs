using System;
using System.Collections.Generic;
using System.Linq;
using CinemaGuard.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Cinema_Guard.Views;

public sealed partial class FilterPage : Page
{
    private readonly AppSettings _settings = App.Settings.Current;
    private readonly List<FamilyRow> _fullRows = [];
    private readonly List<FamilyRow> _singleRows = [];

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
            _fullRows.Add(new FamilyRow(FullWordsPanel, _settings.WordsFull, root, ShowCount));
            _singleRows.Add(new FamilyRow(SingleWordsPanel, _settings.WordsSingle, root, ShowCount));
        }

        FullCustom.Text = string.Join(Environment.NewLine, _settings.WordsFull.Where(word => !familyWords.Contains(word)));
        SingleCustom.Text = string.Join(Environment.NewLine, _settings.WordsSingle.Where(word => !familyWords.Contains(word)));

        ShowCount();
    }

    private void ShowCount() => Status.Text = $"{_settings.WordsFull.Count + _settings.WordsSingle.Count} word(s) active";

    /// <summary>
    /// One word family: its checkbox toggles every spelling (half-ticked when only some are on), and the
    /// spellings button opens the family so single spellings can be turned off, such as one that is
    /// fine in your house while the rest are not.
    /// </summary>
    private sealed class FamilyRow
    {
        private readonly List<string> _words;
        private readonly string _root;
        private readonly Action _changed;
        private readonly CheckBox _family;
        private readonly TextBlock? _countText;
        private readonly List<(CheckBox Box, string Word)> _spellings = [];

        public FamilyRow(StackPanel panel, List<string> words, string root, Action changed)
        {
            _words = words;
            _root = root;
            _changed = changed;

            var family = FilterCatalog.Associated(root);

            _family = new CheckBox { Content = FilterCatalog.DisplayName(root), Tag = root, MinWidth = 0 };
            ToolTipService.SetToolTip(_family, FamilyToolTip(root));

            // Click, not Checked/Unchecked: a half-ticked family turns fully on, which the box's own cycle would not do.
            _family.Click += (_, _) =>
            {
                FilterCatalog.EnableFamily(_words, _root, FilterCatalog.FamilyState(_words, _root) != true);
                Refresh();
            };

            var header = new Grid { ColumnSpacing = 4 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(_family);
            panel.Children.Add(header);

            if (family.Count < 2)
            {
                Refresh();
                return;
            }

            var chevron = new FontIcon { Glyph = "", FontSize = 10 };
            _countText = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center };

            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(_countText);
            content.Children.Add(chevron);

            var expand = new Button
            {
                Content = content,
                Style = (Style)Application.Current.Resources["TransparentButtonStyle"],
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };

            AutomationProperties.SetName(expand, $"Spellings of {FilterCatalog.DisplayName(root)}");
            ToolTipService.SetToolTip(expand, "Choose single spellings");

            VariableSizedWrapGrid? details = null;

            expand.Click += (_, _) =>
            {
                // Built on first open: the hundreds of spellings would otherwise slow every visit to the page.
                if (details is null)
                {
                    details = Spellings(family);
                    panel.Children.Insert(panel.Children.IndexOf(header) + 1, details);
                    Refresh();
                }

                var open = details.Visibility == Visibility.Collapsed;
                details.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                chevron.Glyph = open ? "" : "";
            };

            Grid.SetColumn(expand, 1);
            header.Children.Add(expand);

            Refresh();
        }

        private VariableSizedWrapGrid Spellings(IReadOnlyList<string> family)
        {
            var details = new VariableSizedWrapGrid
            {
                Orientation = Orientation.Horizontal,
                ItemWidth = 170,
                Margin = new Thickness(28, 0, 0, 8),
                Visibility = Visibility.Collapsed,
            };

            foreach (var word in family)
            {
                var box = new CheckBox { Content = FilterCatalog.DisplayName(word), MinWidth = 0 };
                box.Click += (_, _) =>
                {
                    FilterCatalog.EnableWord(_words, word, box.IsChecked == true);
                    Refresh();
                };

                details.Children.Add(box);
                _spellings.Add((box, word));
            }

            return details;
        }

        /// <summary>Selects or clears the whole family (Select all / none).</summary>
        public void Set(bool enable)
        {
            FilterCatalog.EnableFamily(_words, _root, enable);
            Refresh();
        }

        public bool IsFullyOn => FilterCatalog.FamilyState(_words, _root) == true;

        private void Refresh()
        {
            var family = FilterCatalog.Associated(_root);
            _family.IsChecked = FilterCatalog.FamilyState(_words, _root);

            foreach (var (box, word) in _spellings) box.IsChecked = _words.Contains(word);
            if (_countText is not null) _countText.Text = $"{family.Count(_words.Contains)} of {family.Count}";

            _changed();
        }
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

    private void ToggleAllFull_Click(object sender, RoutedEventArgs e) => ToggleAll(_fullRows);

    private void ToggleAllSingle_Click(object sender, RoutedEventArgs e) => ToggleAll(_singleRows);

    private static void ToggleAll(List<FamilyRow> rows)
    {
        var allChecked = rows.All(row => row.IsFullyOn);

        foreach (var row in rows) row.Set(!allChecked);
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
