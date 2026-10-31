using System.Text.Json;
using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Tools;

namespace CinemaGuard.Core.Speech;

/// <summary>
/// How to start Fermion: the private runtime's python.exe (no launcher exe, so the folder can move),
/// or a fermion.exe the user set in Settings. Every run gets the app's model folder as its cache.
/// </summary>
public sealed class PhononLauncher(string fileName, IReadOnlyList<string> prefix)
{
    private const string Bootstrap = "import sys; from fermion.cli import main; sys.exit(main())";

    public string FileName { get; } = fileName;

    public static PhononLauncher? Resolve(ModelManager models)
    {
        if (models.ExternalFermion is { } fermion) return new PhononLauncher(fermion, []);
        if (models.IsInstalled(EnginePacks.PhononRuntime) && File.Exists(SpeechPaths.PhononPython))
            return new PhononLauncher(SpeechPaths.PhononPython, ["-c", Bootstrap]);
        return null;
    }

    /// <param name="offline">Transcription never downloads: a missing model fails instead of fetching it.</param>
    public ProcessSpec Spec(IEnumerable<string> arguments, bool offline)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FERMION_CACHE_DIR"] = SpeechPaths.PhononCache,
            ["PYTHONUTF8"] = "1",
            ["PYTHONNOUSERSITE"] = "1",
            ["HF_HUB_DISABLE_TELEMETRY"] = "1",
            ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1",
        };

        if (offline) environment["HF_HUB_OFFLINE"] = "1";

        return new ProcessSpec(FileName, [.. prefix, .. arguments]) { Environment = environment };
    }
}

/// <summary>
/// Installs Phonon's private runtime: Python's embeddable zip, pip, CPU torch and fermion-research,
/// all pinned, into engines\phonon. Nothing outside that folder is touched.
/// </summary>
internal static class PhononRuntimeInstaller
{
    public static async Task InstallAsync(EnginePack pack, IProcessRunner runner, DownloadJob job, Action raise)
    {
        var staging = pack.Folder + ".partial";
        ModelManager.TryDeleteDirectory(staging);
        Directory.CreateDirectory(staging);

        var token = job.Token;
        var pythonSource = pack.Sources[0];
        var pipSource = pack.Sources[1];

        // Progress is the size of the folder against the finished runtime: pip prints nothing countable.
        using var poll = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            while (!poll.IsCancellationRequested)
            {
                try { await Task.Delay(1000, poll.Token); } catch (OperationCanceledException) { break; }
                var size = ModelManager.FolderSize(staging);
                job.Done = Math.Max(job.Done, Math.Min(job.Total * 98 / 100, size * job.Total / pack.DiskSize));
                raise();
            }
        });

        try
        {
            job.Status = "Downloading Python";
            raise();

            var downloads = Path.Combine(SpeechPaths.Engines, "downloads");
            var zip = Path.Combine(downloads, Path.GetFileName(new Uri(pythonSource.Url).LocalPath));
            await Downloader.DownloadAsync(pythonSource.Url, zip, pythonSource.Size, pythonSource.Sha256, null, token);

            var pythonFolder = Path.Combine(staging, pythonSource.Target);
            Downloader.Extract(zip, pythonSource, pythonFolder, token);
            EnableSitePackages(pythonFolder);

            var getPip = Path.Combine(staging, pipSource.Target);
            await Downloader.DownloadAsync(pipSource.Url, getPip, 0, null, null, token);

            var python = Path.Combine(pythonFolder, "python.exe");

            job.Status = "Setting up pip";
            raise();
            await RunAsync(runner, python, [getPip, "--no-warn-script-location"], token, "pip");

            job.Status = "Downloading PyTorch (about 250 MB)";
            raise();
            await RunAsync(runner, python,
                ["-m", "pip", "install", "--no-warn-script-location", $"torch=={EnginePacks.TorchVersion}",
                 "--index-url", "https://download.pytorch.org/whl/cpu"], token, "PyTorch");

            job.Status = "Downloading the Phonon engine";
            raise();
            await RunAsync(runner, python,
                ["-m", "pip", "install", "--no-warn-script-location", $"fermion-research=={EnginePacks.FermionVersion}"], token, "the Phonon engine");

            job.Status = "Checking the engine";
            raise();
            await RunAsync(runner, python, ["-c", "import fermion.cli"], token, "the engine check");

            ModelManager.TryDeleteFile(getPip);
            ModelManager.TryDeleteFile(zip);

            await File.WriteAllTextAsync(Path.Combine(staging, Path.GetFileName(pack.MarkerPath)),
                JsonSerializer.Serialize(new { pack.Id, python = EnginePacks.PythonVersion, torch = EnginePacks.TorchVersion, fermion = EnginePacks.FermionVersion, installed = DateTimeOffset.Now }),
                token);
        }
        finally
        {
            poll.Cancel();
            await watcher;
        }

        ModelManager.TryDeleteDirectory(pack.Folder, throwOnError: true);
        Directory.Move(staging, pack.Folder);
    }

    /// <summary>The embeddable build ignores site-packages until its ._pth file says otherwise.</summary>
    private static void EnableSitePackages(string pythonFolder)
    {
        var pth = Directory.EnumerateFiles(pythonFolder, "python3*._pth").FirstOrDefault()
                  ?? throw new DownloadException("The Python download is missing its ._pth file.");

        var lines = File.ReadAllLines(pth)
            .Select(line => line.TrimStart().StartsWith("#", StringComparison.Ordinal) && line.Contains("import site", StringComparison.Ordinal) ? "import site" : line)
            .ToList();

        if (!lines.Contains(@"Lib\site-packages")) lines.Add(@"Lib\site-packages");
        File.WriteAllLines(pth, lines);
    }

    private static async Task RunAsync(IProcessRunner runner, string python, IReadOnlyList<string> arguments, CancellationToken token, string what)
    {
        var spec = new ProcessSpec(python, arguments)
        {
            Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PYTHONNOUSERSITE"] = "1",
                ["PYTHONUTF8"] = "1",
                ["PIP_DISABLE_PIP_VERSION_CHECK"] = "1",
                ["PIP_NO_CACHE_DIR"] = "1",
                ["PIP_NO_INPUT"] = "1",
            },
        };

        var result = await runner.RunAsync(spec, cancellationToken: token);
        if (result.Cancelled) throw new OperationCanceledException(token);

        if (!result.Success)
        {
            Log.Warn($"Phonon engine setup ({what}) failed:\n{result.StdErr}\n{result.StdOut}");
            throw new DownloadException($"Installing {what} failed: {ModelManager.LastLine(result.StdErr.Length > 0 ? result.StdErr : result.StdOut)}");
        }
    }
}
