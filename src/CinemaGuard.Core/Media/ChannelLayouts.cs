namespace CinemaGuard.Core.Media;

/// <summary>Channel layouts and the channels that can carry dialogue (from the old app's Info.cs).</summary>
public static class ChannelLayouts
{
    public static IReadOnlyDictionary<string, string[]> Layouts { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["mono"] = ["FC"],
        ["stereo"] = ["FL", "FR"],
        ["2.1"] = ["FL", "FR", "LFE"],
        ["3.0"] = ["FL", "FR", "FC"],
        ["3.0(back)"] = ["FL", "FR", "BC"],
        ["4.0"] = ["FL", "FR", "FC", "BC"],
        ["quad"] = ["FL", "FR", "BL", "BR"],
        ["quad(side)"] = ["FL", "FR", "SL", "SR"],
        ["3.1"] = ["FL", "FR", "FC", "LFE"],
        ["5.0"] = ["FL", "FR", "FC", "BL", "BR"],
        ["5.0(side)"] = ["FL", "FR", "FC", "SL", "SR"],
        ["4.1"] = ["FL", "FR", "FC", "LFE", "BC"],
        ["5.1"] = ["FL", "FR", "FC", "LFE", "BL", "BR"],
        ["5.1(side)"] = ["FL", "FR", "FC", "LFE", "SL", "SR"],
        ["6.0"] = ["FL", "FR", "FC", "BC", "SL", "SR"],
        ["6.0(front)"] = ["FL", "FR", "FLC", "FRC", "SL", "SR"],
        ["hexagonal"] = ["FL", "FR", "FC", "BL", "BR", "BC"],
        ["6.1"] = ["FL", "FR", "FC", "LFE", "BC", "SL", "SR"],
        ["6.1(back)"] = ["FL", "FR", "FC", "LFE", "BL", "BR", "BC"],
        ["6.1(front)"] = ["FL", "FR", "LFE", "FLC", "FRC", "SL", "SR"],
        ["7.0"] = ["FL", "FR", "FC", "BL", "BR", "SL", "SR"],
        ["7.0(front)"] = ["FL", "FR", "FC", "FLC", "FRC", "SL", "SR"],
        ["7.1"] = ["FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR"],
        ["7.1(wide)"] = ["FL", "FR", "FC", "LFE", "BL", "BR", "FLC", "FRC"],
        ["7.1(wide-side)"] = ["FL", "FR", "FC", "LFE", "FLC", "FRC", "SL", "SR"],
        ["octagonal"] = ["FL", "FR", "FC", "BL", "BR", "BC", "SL", "SR"],
        ["hexadecagonal"] = ["FL", "FR", "FC", "LFE", "BL", "BR", "FLC", "FRC", "BC", "SL", "SR", "TC", "TFL", "TFC", "TFR", "TBL", "TBC", "TBR", "WL", "WR"],
        ["downmix"] = ["DL", "DR"],
        ["22.2"] = ["FL", "FR", "FC", "LFE", "BL", "BR", "FLC", "FRC", "BC", "SL", "SR", "TC", "TFL", "TFC", "TFR", "TBL", "TBC", "TBR", "LFE2", "TSL", "TSR", "BFC", "BFL", "BFR"],
    };

    public static IReadOnlyList<string> DialogueChannels { get; } = ["FC", "FL", "FR", "SL", "SR", "BL", "BR"];

    public static string[]? ChannelsFor(string? layout) =>
        layout is not null && Layouts.TryGetValue(layout.Trim(), out var channels) ? channels : null;

    /// <summary>Vocals mode needs a centre or surround channel to isolate dialogue from.</summary>
    public static bool CanSplitVocals(string? layout)
    {
        var channels = ChannelsFor(layout);
        return channels is not null &&
               (channels.Contains("FC") || channels.Contains("SL") || channels.Contains("SR"));
    }

    public static bool HasFrontCenter(string? layout) => ChannelsFor(layout)?.Contains("FC") == true;

    /// <summary>A track's channels by name, or numbered c0, c1… when its layout has no names.</summary>
    public static IReadOnlyList<string> ChannelsOf(MediaStreamInfo stream) =>
        ChannelsFor(stream.ChannelLayout) ?? Enumerable.Range(0, stream.Channels ?? 0).Select(i => $"c{i}").ToArray();

    /// <summary>"Centre (FC)", "Channel 3" for a numbered one.</summary>
    public static string Describe(string channel)
    {
        var name = channel switch
        {
            "FL" => "Front left",
            "FR" => "Front right",
            "FC" => "Centre",
            "LFE" => "Subwoofer",
            "BL" => "Back left",
            "BR" => "Back right",
            "BC" => "Back centre",
            "SL" => "Side left",
            "SR" => "Side right",
            "FLC" => "Front left of centre",
            "FRC" => "Front right of centre",
            "DL" => "Downmix left",
            "DR" => "Downmix right",
            _ => null,
        };

        if (name is not null) return $"{name} ({channel})";
        return channel.Length > 1 && channel[0] == 'c' && int.TryParse(channel[1..], out var number) ? $"Channel {number + 1}" : channel;
    }
}
