namespace CinemaGuard.Core.Pipeline;

/// <summary>
/// One file's overall percentage. Each step weighs its expected time in seconds; finished steps count in
/// full and a running step by its own fraction. The percentage never goes back, even when a step is added
/// or dropped once the pipeline knows more, and it stops short of 100 until the pipeline says it is done.
/// </summary>
public sealed class FileProgress
{
    private readonly object _gate = new();
    private readonly Dictionary<string, double> _weights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _fractions = new(StringComparer.Ordinal);
    private double _percent;

    public double Percent
    {
        get { lock (_gate) return _percent; }
    }

    /// <summary>The expected seconds of every planned step together.</summary>
    public double TotalWeight
    {
        get { lock (_gate) return _weights.Values.Sum(); }
    }

    public bool Has(string step)
    {
        lock (_gate) return _weights.ContainsKey(step);
    }

    /// <summary>Adds a step or changes its weight; a weight of 0 or less drops it.</summary>
    public void Plan(string step, double weight)
    {
        lock (_gate)
        {
            if (weight > 0) _weights[step] = weight;
            else _weights.Remove(step);

            Update();
        }
    }

    public void Drop(string step) => Plan(step, 0);

    /// <summary>How far a planned step is, 0 to 1. Unplanned steps are ignored.</summary>
    public void Report(string step, double fraction)
    {
        lock (_gate)
        {
            if (!_weights.ContainsKey(step)) return;
            _fractions[step] = Math.Clamp(fraction, 0, 1);
            Update();
        }
    }

    public void Complete(string step) => Report(step, 1);

    private void Update()
    {
        var total = _weights.Values.Sum();
        if (total <= 0) return;

        var done = _weights.Sum(pair => pair.Value * _fractions.GetValueOrDefault(pair.Key));
        _percent = Math.Max(_percent, Math.Min(99.5, done / total * 100));
    }
}

/// <summary>Built-in expected times (seconds), used until <see cref="RunHistory"/> has runs of alike files.</summary>
public static class StepEstimates
{
    /// <summary>Decoding one audio track to 16 kHz mono runs at a few hundred times real time.</summary>
    public static double ExtractAudio(double durationSeconds) => 3 + Math.Max(60, durationSeconds) / 250;

    /// <summary>The encode copies the video, so it is bound by the disk; a muted track is also re-encoded.</summary>
    public static double Encode(long inputBytes, double durationSeconds, bool reencodesAudio) =>
        3 + inputBytes / (150.0 * 1024 * 1024) + (reencodesAudio ? Math.Max(60, durationSeconds) / 300 : 0);

    public const double CheckOutput = 3;
}
