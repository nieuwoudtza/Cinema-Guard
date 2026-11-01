using System.Text.RegularExpressions;
using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Tools;

namespace CinemaGuard.Core.Phonon;

/// <summary>Image names ("ghcr.io/fermionresearch/phonon-cuda:1.0.11") and how a saved one follows app updates.</summary>
public static class DockerImages
{
    public static (string Repository, string Tag) Split(string image)
    {
        var trimmed = image.Trim();
        var slash = trimmed.LastIndexOf('/');
        var colon = trimmed.LastIndexOf(':');
        return colon > slash ? (trimmed[..colon], trimmed[(colon + 1)..]) : (trimmed, "latest");
    }

    /// <summary>
    /// The image to use. Empty, or an older build of the recommended image, becomes the recommended one:
    /// each app version is tested with one image, so a setting left on the old one follows the update.
    /// Any other image (a newer build, another repository) is the user's own choice and stays.
    /// </summary>
    public static string Follow(string? configured, string recommended)
    {
        if (string.IsNullOrWhiteSpace(configured)) return recommended;

        var (repository, tag) = Split(configured);
        var (recommendedRepository, recommendedTag) = Split(recommended);

        return string.Equals(repository, recommendedRepository, StringComparison.OrdinalIgnoreCase) && IsOlder(tag, recommendedTag)
            ? recommended
            : configured.Trim();
    }

    /// <summary>True when both tags are version numbers and the first is the lower one.</summary>
    public static bool IsOlder(string tag, string than) =>
        Version.TryParse(tag, out var version) && Version.TryParse(than, out var other) && version < other;
}

/// <summary>
/// Reads `docker pull` output. Without a terminal Docker prints one line per layer change and no byte
/// counts, so progress is the layers downloaded and unpacked against all the layers it listed.
/// </summary>
public sealed partial class DockerPullProgress
{
    private readonly Dictionary<string, int> _layers = new(StringComparer.Ordinal);

    public int Layers => _layers.Count;
    public int Downloaded => _layers.Values.Count(state => state >= 1);
    public bool IsIndeterminate => _layers.Count == 0;

    /// <summary>0 to 100: a layer counts half once downloaded and fully once unpacked (or already on disk).</summary>
    public double Percent => _layers.Count == 0 ? 0 : _layers.Values.Sum() * 50.0 / _layers.Count;

    /// <summary>Feeds one line; true when it changed the progress.</summary>
    public bool Feed(string line)
    {
        var match = LayerLine().Match(line.Trim());
        if (!match.Success) return false;

        var layer = match.Groups[1].Value;
        var state = match.Groups[2].Value.Trim() switch
        {
            "Already exists" or "Pull complete" => 2,
            "Download complete" or "Verifying Checksum" or "Extracting" => 1,
            _ => 0,
        };

        var previous = _layers.GetValueOrDefault(layer, -1);
        if (state <= previous) return false;

        _layers[layer] = state;
        return true;
    }

    [GeneratedRegex(@"^([0-9a-f]{12}): (.+)$")]
    private static partial Regex LayerLine();
}

/// <summary>
/// Downloads Phonon's CUDA image (`docker pull`), starting Docker Desktop first when it is down. One
/// download at a time for the whole app, so it carries on when the user leaves the Tools page.
/// </summary>
public sealed class DockerImagePuller(IProcessRunner runner, ToolLocator locator, PhononService phonon)
{
    private CancellationTokenSource? _cancellation;

    public event Action? Changed;

    public bool IsPulling { get; private set; }
    public double Percent { get; private set; }
    public bool IsIndeterminate { get; private set; } = true;
    public string Status { get; private set; } = string.Empty;

    public void Cancel()
    {
        try { _cancellation?.Cancel(); }
        catch (ObjectDisposedException) { /* finished in the meantime */ }
    }

    public async Task<(bool Ok, string Message)> PullAsync(AppSettings settings)
    {
        if (IsPulling) return (false, "The GPU image is already downloading.");

        var docker = locator.ResolveDocker(settings);
        if (docker is null) return (false, "Docker Desktop is not installed.");

        var image = settings.DockerImage;
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        IsPulling = true;
        Percent = 0;
        Report("Checking Docker Desktop…", indeterminate: true);

        try
        {
            if (!await phonon.EnsureEngineAsync(docker, line => Report(line, indeterminate: true), token))
            {
                token.ThrowIfCancellationRequested();
                return (false, "Docker Desktop did not start. Start it from the Start menu, then try again.");
            }

            Report("Downloading the GPU image…", indeterminate: true);

            var progress = new DockerPullProgress();
            var result = await runner.RunAsync(
                new ProcessSpec(docker, ["pull", image]),
                onStandardOutput: line =>
                {
                    if (!progress.Feed(line) || progress.IsIndeterminate) return;
                    Percent = progress.Percent;
                    Report($"Downloading the GPU image: {progress.Downloaded} of {progress.Layers} parts", indeterminate: false);
                },
                cancellationToken: token);

            if (result.Cancelled) return (false, "The GPU image download was cancelled.");

            if (!result.Success)
            {
                Log.Warn($"docker pull {image} failed: {result.StdErr}");
                return (false, $"The GPU image could not be downloaded: {LocalPhononRunner.Tail(result.StdErr, 300)}");
            }

            Log.Info($"Downloaded the Docker image {image}.");
            await RemoveSupersededAsync(docker, image);
            return (true, "The GPU image is downloaded. Phonon now runs on the graphics card.");
        }
        catch (OperationCanceledException)
        {
            return (false, "The GPU image download was cancelled.");
        }
        finally
        {
            IsPulling = false;
            var cancellation = _cancellation;
            _cancellation = null;
            cancellation?.Dispose();
            Report(string.Empty, indeterminate: true);
        }
    }

    /// <summary>
    /// After an update's image is in: older builds of the same image are several GB each and nothing
    /// uses them any more. One a container still holds is left alone (docker refuses, which is logged).
    /// </summary>
    private async Task RemoveSupersededAsync(string docker, string image)
    {
        var (repository, tag) = DockerImages.Split(image);

        var list = await runner.RunAsync(new ProcessSpec(docker, ["image", "ls", repository, "--format", "{{.Tag}}"]));
        if (!list.Success) return;

        var older = list.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(old => DockerImages.IsOlder(old, tag))
            .Distinct()
            .ToList();

        foreach (var old in older)
        {
            Report($"Removing the previous GPU image ({old})…", indeterminate: true);

            var removed = await runner.RunAsync(new ProcessSpec(docker, ["image", "rm", $"{repository}:{old}"]));
            if (removed.Success) Log.Info($"Removed the superseded Docker image {repository}:{old}.");
            else Log.Warn($"Could not remove {repository}:{old}: {LocalPhononRunner.Tail(removed.StdErr, 200)}");
        }
    }

    private void Report(string status, bool indeterminate)
    {
        Status = status;
        IsIndeterminate = indeterminate;

        try { Changed?.Invoke(); } catch (Exception ex) { Log.Warn($"Docker pull listener failed: {ex.Message}"); }
    }
}
