using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Cinema_Guard.Controls;

/// <summary>Marks the words of a search inside a TextBlock, the way a browser's find does.</summary>
public static class SearchHighlight
{
    public static void Apply(TextBlock block, string[] words)
    {
        block.TextHighlighters.Clear();

        var text = block.Text;
        if (words.Length == 0 || string.IsNullOrEmpty(text)) return;

        var highlighter = new TextHighlighter
        {
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"],
        };

        foreach (var word in words)
        {
            for (var start = text.IndexOf(word, StringComparison.CurrentCultureIgnoreCase);
                 start >= 0;
                 start = text.IndexOf(word, start + word.Length, StringComparison.CurrentCultureIgnoreCase))
            {
                highlighter.Ranges.Add(new TextRange { StartIndex = start, Length = word.Length });
            }
        }

        if (highlighter.Ranges.Count > 0)
            block.TextHighlighters.Add(highlighter);
    }
}
