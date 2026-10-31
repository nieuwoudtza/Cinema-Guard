namespace CinemaGuard.Core.Intervals;

public enum HitSource { English, Multilingual, Subtitle, LegacyOneWord, Masked }

/// <summary>One place a filtered word is heard, on the media timeline (before the audio offset).</summary>
public sealed record Hit(double Start, double End, string Label, HitSource Source, bool WholeCue)
{
    public double Duration => End - Start;
}

public readonly record struct TimeRange(double Start, double End)
{
    public double Duration => End - Start;
    public bool Overlaps(TimeRange other) => other.Start < End && Start < other.End;
    public override string ToString() => $"{Start:0.000}-{End:0.000}";
}
