namespace CinemaGuard.Core.Subtitles;

public sealed class Cue
{
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public string Text { get; set; } = string.Empty;

    public double StartSeconds
    {
        get => Start.TotalSeconds;
        set => Start = TimeSpan.FromSeconds(value);
    }

    public double EndSeconds
    {
        get => End.TotalSeconds;
        set => End = TimeSpan.FromSeconds(value);
    }

    public Cue Clone() => new() { Start = Start, End = End, Text = Text };

    public override string ToString() => $"{Start:hh\\:mm\\:ss\\.fff} --> {End:hh\\:mm\\:ss\\.fff} {Text}";
}

public enum SubtitleFormat { Srt, Vtt, Ass }

public sealed record SubtitleDocument(SubtitleFormat Format, List<Cue> Cues, string RawText);
