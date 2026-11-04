using System.Text;
using System.Text.RegularExpressions;

namespace CinemaGuard.Core.Subtitles;

/// <summary>
/// Strips tags, SDH descriptions and stray punctuation from cue text before matching,
/// keeping the text a human reads. Ported from the old app's Tool.FormatSubtitles, without
/// its 100-attempt exception and with '-' kept so entries like FUCKED-UP still match.
/// </summary>
public static partial class SubtitleFormatter
{
    private static readonly Regex TagRegex = new(@"<[^>]*>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BraceRegex = new(@"\{[^}]*\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BracketRegex = new(@"\[[^\]]*\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string NormaliseSpace(string value) =>
        value.Replace('\u00A0', ' ').Replace('\u200B', ' ').Replace('\uFEFF', ' ');

    public static string FormatLine(string line) => FormatLine(line, keepMasks: false);

    /// <param name="keepMasks">
    /// Keep the masking characters (f***) and turn [bleep] markers into a masked word, so the text still
    /// shows where a censored word was spoken.
    /// </param>
    public static string FormatLine(string line, bool keepMasks)
    {
        if (string.IsNullOrEmpty(line)) return string.Empty;

        var value = NormaliseSpace(line);
        if (keepMasks) value = SwearMatcher.MarkBleeps(value);
        value = TagRegex.Replace(value, string.Empty);
        value = BraceRegex.Replace(value, string.Empty);
        value = BracketRegex.Replace(value, string.Empty);

        // Unbalanced brackets: keep what a viewer would see on screen.
        var open = value.IndexOf('[');
        if (open >= 0 && value.IndexOf(']', open) < 0) value = value[..open];

        var close = value.IndexOf(']');
        if (close >= 0) value = value[(close + 1)..];

        value = value.Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u201C', '"').Replace('\u201D', '"');

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (IsAllowed(character) || keepMasks && character is '*' or '#' or '@') builder.Append(character);
        }

        return WhitespaceRegex.Replace(builder.ToString(), " ").Trim();
    }

    public static string FormatCue(string text, bool keepMasks = false) =>
        string.Join(" ", text.Replace("\r\n", "\n").Split('\n').Select(line => FormatLine(line, keepMasks))).Trim();

    public static List<string> FormatLines(IEnumerable<string> lines) => lines.Select(FormatLine).ToList();

    private static bool IsAllowed(char character) =>
        char.IsLetterOrDigit(character) || character is ' ' or '\t' or ',' or '.' or '?' or '\'' or '"' or '!' or '$' or '%' or '-';
}
