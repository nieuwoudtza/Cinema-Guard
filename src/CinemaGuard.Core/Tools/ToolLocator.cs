using System.Text.RegularExpressions;
using CinemaGuard.Core.Settings;

namespace CinemaGuard.Core.Tools;

public static partial class PathGuard
{
    /// <summary>Prefix for Win32 file APIs so paths past 260 characters work even when the OS setting is off.</summary>
    public static string Extended(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return Path.IsPathFullyQualified(path) ? @"\\?\" + path : path;
    }

    public static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static bool IsWritableDirectory(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".cg-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static long GetFreeSpace(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root)) return long.MaxValue;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : long.MaxValue;
        }
        catch
        {
            // UNC or a drive we cannot query: assume there is room and let the write fail loudly.
            return long.MaxValue;
        }
    }

    public static void EnsureDirectory(string directory)
    {
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
    }
}

public sealed record ToolStatus(
    string Key,
    string DisplayName,
    bool Found,
    bool Required,
    string? Path,
    string? Version,
    string? Problem)
{
    public bool Ready => Found && Problem is null;
}

/// <param name="Problem">Why <paramref name="Path"/> cannot be used; null when it is ready.</param>
public sealed record FfmpegChoice(string? Path, string? Version, string? Problem);

public sealed record ToolSnapshot(
    IReadOnlyList<ToolStatus> Tools,
    bool FfmpegReady,
    bool DockerEngineReady,
    bool DockerImagePresent,
    bool PhononAvailable,
    PhononRuntimeMode EffectiveRuntime,
    DateTimeOffset CheckedAt,
    bool DockerEngineRunning = false);

/// <summary>Finds ffmpeg, ffprobe, docker, fermion and subloader-cli, and reports what is usable.</summary>
public sealed partial class ToolLocator(IProcessRunner runner)
{
    public static string DefaultSubloaderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Subloader", "subloader-cli.exe");

    public static string DefaultDockerDesktopPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe");

    /// <summary>The ffmpeg that ships with the old app is 2021-era; never pick it up.</summary>
    private static bool RejectOldAppPath(string? path) =>
        path is not null && path.Contains("Cinema Guard (Old)", StringComparison.OrdinalIgnoreCase);

    public string? ResolveFfmpeg(AppSettings settings) =>
        Resolve(settings.FfmpegPath, "ffmpeg.exe") ?? FindLocalFfmpeg() ?? FindOnPath("ffmpeg") ?? FindBesideFfprobe();

    /// <summary>The copy the Tools page installed, or one beside the app; both beat an old PATH install.</summary>
    private static string? FindLocalFfmpeg()
    {
        if (File.Exists(ToolDownloader.FfmpegPath)) return ToolDownloader.FfmpegPath;

        var beside = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe");
        return File.Exists(beside) ? beside : null;
    }

    /// <summary>
    /// The FFmpeg to run, checked to be 7.1 or newer: the one set in Settings, else the first new enough of the
    /// Tools page's copy, one beside the app and every copy on PATH, so an old build early on PATH does not hide a
    /// newer one. With none new enough, the first copy found and why it cannot be used.
    /// </summary>
    public async Task<FfmpegChoice> FindSupportedFfmpegAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var configured = Resolve(settings.FfmpegPath, "ffmpeg.exe");
        IEnumerable<string?> candidates = configured is not null
            ? [configured]
            : [FindLocalFfmpeg(), .. FindAllOnPath("ffmpeg"), .. FindAllOnPath("ffprobe").Select(BesideProbe)];

        FfmpegChoice? first = null;

        foreach (var path in candidates.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var choice = await CheckFfmpegAsync(path, cancellationToken);
            if (choice.Problem is null) return choice;
            first ??= choice;
        }

        return first ?? new FfmpegChoice(null, null, "Not found");
    }

    private async Task<FfmpegChoice> CheckFfmpegAsync(string path, CancellationToken cancellationToken)
    {
        var key = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}";
        if (_ffmpegChecks.TryGetValue(key, out var known)) return known;

        var result = await runner.RunAsync(new ProcessSpec(path, ["-hide_banner", "-version"]), cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var output = result.StdOut.Length > 0 ? result.StdOut : result.StdErr;
        var (version, problem) = string.IsNullOrWhiteSpace(output)
            ? (null, "Found but did not run")
            : ReadFfmpegVersion(output);

        var choice = new FfmpegChoice(path, version, problem);
        // A copy that did not run may work next time (a slow disk, a virus scan); only an answer is kept.
        if (version is not null) _ffmpegChecks[key] = choice;
        return choice;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FfmpegChoice> _ffmpegChecks = new();

    /// <summary>
    /// The version <c>ffmpeg -version</c> reports, and why it is too old (null when it is 7.1 or newer). A release
    /// names its number; a git build (2023-10-12-git-…, N-112345-g…) names a date or commit instead, so its
    /// libavutil version decides: 7.1 shipped libavutil 59.39. A build that shows neither is not trusted.
    /// </summary>
    public static (string? Version, string? Problem) ReadFfmpegVersion(string output)
    {
        var firstLine = output.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? string.Empty;
        var name = FfmpegNameRegex().Match(firstLine) is { Success: true } named ? named.Groups[1].Value : firstLine;

        if (FfmpegReleaseRegex().Match(firstLine) is { Success: true } release)
        {
            var number = release.Groups[1].Value;
            var parts = number.Split('.').Select(int.Parse).ToArray();
            var tooOld = parts[0] < 7 || (parts[0] == 7 && parts[1] < 1);
            return (number, tooOld ? $"FFmpeg {number} is too old: 7.1 or newer is required" : null);
        }

        if (LibavutilRegex().Match(output) is { Success: true } avutil)
        {
            var major = int.Parse(avutil.Groups[1].Value);
            var minor = int.Parse(avutil.Groups[2].Value);
            var tooOld = major < 59 || (major == 59 && minor < 39);
            return (name, tooOld ? $"FFmpeg {name} is older than 7.1, which is required" : null);
        }

        return (name.Length > 0 ? name : null, "Cannot tell which FFmpeg version this is: 7.1 or newer is required");
    }

    [GeneratedRegex(@"^ffmpeg version (\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex FfmpegNameRegex();

    /// <summary>A release number at the start of the version (7.1.2-full_build, n7.1, 4.4.2-0ubuntu0.22.04.1).</summary>
    [GeneratedRegex(@"^ffmpeg version n?([0-9]+\.[0-9]+(?:\.[0-9]+)*)", RegexOptions.IgnoreCase)]
    private static partial Regex FfmpegReleaseRegex();

    [GeneratedRegex(@"^\s*libavutil\s+([0-9]+)\.\s*([0-9]+)\.", RegexOptions.Multiline)]
    private static partial Regex LibavutilRegex();

    public string? ResolveFfprobe(AppSettings settings) => ResolveFfprobe(settings, ResolveFfmpeg(settings));

    /// <summary>The FFprobe set in Settings, else the one beside <paramref name="ffmpeg"/>, else the first on PATH.</summary>
    public string? ResolveFfprobe(AppSettings settings, string? ffmpeg)
    {
        var explicitProbe = Resolve(settings.FfprobePath, "ffprobe.exe");
        if (explicitProbe is not null) return explicitProbe;

        if (ffmpeg is not null)
        {
            var beside = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? string.Empty, "ffprobe.exe");
            if (File.Exists(beside) && !RejectOldAppPath(beside)) return beside;
        }

        return FindOnPath("ffprobe");
    }

    private static string? FindBesideFfprobe() => BesideProbe(FindOnPath("ffprobe"));

    private static string? BesideProbe(string? probe)
    {
        if (probe is null) return null;
        var beside = Path.Combine(Path.GetDirectoryName(probe) ?? string.Empty, "ffmpeg.exe");
        return File.Exists(beside) ? beside : null;
    }

    public string? ResolveDocker(AppSettings settings) => Resolve(settings.DockerPath, "docker.exe") ?? FindOnPath("docker");

    /// <summary>A fermion.exe set in Settings. Without one, Phonon runs on the engine from the Models page.</summary>
    public string? ResolveFermion(AppSettings settings) => Resolve(settings.LocalFermionPath, "fermion.exe");

    public string? ResolveSubloader(AppSettings settings)
    {
        var configured = Resolve(settings.SubloaderPath, "subloader-cli.exe");
        if (configured is not null) return configured;
        if (File.Exists(DefaultSubloaderPath)) return DefaultSubloaderPath;
        return FindOnPath("subloader-cli");
    }

    public string? Resolve(string? configured, string fileName)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;

        var trimmed = configured.Trim().Trim('"');
        if (File.Exists(trimmed) && !RejectOldAppPath(trimmed)) return trimmed;

        // The user may have pointed at a folder.
        if (Directory.Exists(trimmed))
        {
            var candidate = Path.Combine(trimmed, fileName);
            if (File.Exists(candidate) && !RejectOldAppPath(candidate)) return candidate;
        }

        return null;
    }

    public static string? FindOnPath(string fileName) => FindAllOnPath(fileName).FirstOrDefault();

    /// <summary>Every copy on PATH, in PATH order.</summary>
    public static IEnumerable<string> FindAllOnPath(string fileName)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var directory in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? fileName : fileName + extension.ToLowerInvariant());
                if (File.Exists(candidate) && !RejectOldAppPath(candidate)) yield return candidate;
            }
        }
    }

    /// <summary>The tools <see cref="CheckAsync"/> reports, by key and in order, for a list shown before the checks finish.</summary>
    public static IReadOnlyList<(string Key, string DisplayName)> CheckedTools { get; } =
    [
        ("ffmpeg", "FFmpeg"),
        ("ffprobe", "FFprobe"),
        ("docker", "Docker (GPU)"),
        ("fermion", "Phonon engine"),
        ("subloader", "Subloader"),
    ];

    /// <param name="onTool">Told about each tool the moment its own check finishes; the checks run side by side.</param>
    public async Task<ToolSnapshot> CheckAsync(AppSettings settings, IProgress<ToolStatus>? onTool = null, CancellationToken cancellationToken = default)
    {
        async Task<T> Check<T>(Func<Task<T>> check, Func<T, ToolStatus> status)
        {
            var result = await Task.Run(check, cancellationToken);
            onTool?.Report(status(result));
            return result;
        }

        // The same choice a run makes, so this page never calls ready a copy the run would refuse.
        var ffmpegChoice = Task.Run(() => FindSupportedFfmpegAsync(settings, cancellationToken), cancellationToken);

        var ffmpegTask = Check(async () =>
        {
            var choice = await ffmpegChoice;
            return new ToolStatus("ffmpeg", "FFmpeg", choice.Path is not null, true, choice.Path, choice.Version, choice.Problem);
        }, status => status);

        var ffprobeTask = Check(
            async () => await CheckVersionAsync("ffprobe", "FFprobe", ResolveFfprobe(settings, (await ffmpegChoice).Path), ["-version"], required: true, cancellationToken),
            status => status);

        var dockerTask = Check(() => CheckDockerAsync(ResolveDocker(settings), settings, cancellationToken), result => result.Status);

        var fermionTask = Check(() =>
        {
            var fermion = ResolveFermion(settings);
            if (fermion is not null)
                return CheckVersionAsync("fermion", "Phonon engine (your fermion.exe)", fermion, ["--version"], required: false, cancellationToken);

            var installed = File.Exists(Speech.EnginePacks.PhononRuntime.MarkerPath) && File.Exists(Speech.SpeechPaths.PhononPython);
            return Task.FromResult(new ToolStatus("fermion", "Phonon engine", installed, false,
                installed ? Speech.SpeechPaths.PhononRuntime : null,
                installed ? $"fermion {Speech.EnginePacks.FermionVersion}" : null,
                installed ? null : "Not downloaded (Models page)"));
        }, status => status);

        var subloaderTask = Check(
            () => CheckVersionAsync("subloader", "Subloader", ResolveSubloader(settings), ["--version"], required: false, cancellationToken),
            status => status);

        var dockerStatus = await dockerTask;
        List<ToolStatus> tools = [await ffmpegTask, await ffprobeTask, dockerStatus.Status, await fermionTask, await subloaderTask];

        var dockerReady = dockerStatus.Status.Ready && dockerStatus.EngineReady;
        var imageReady = dockerStatus.ImagePresent;
        var fermionReady = tools.First(t => t.Key == "fermion").Ready;

        var effective = settings.PhononRuntime switch
        {
            PhononRuntimeMode.Docker => PhononRuntimeMode.Docker,
            PhononRuntimeMode.Local => PhononRuntimeMode.Local,
            _ => dockerReady && imageReady ? PhononRuntimeMode.Docker : PhononRuntimeMode.Local,
        };

        return new ToolSnapshot(
            tools,
            tools.First(t => t.Key == "ffmpeg").Ready,
            dockerReady,
            imageReady,
            (dockerReady && imageReady) || fermionReady,
            effective,
            DateTimeOffset.Now,
            dockerStatus.EngineReady);
    }

    private async Task<(ToolStatus Status, bool EngineReady, bool ImagePresent)> CheckDockerAsync(
        string? docker, AppSettings settings, CancellationToken cancellationToken)
    {
        if (docker is null)
            return (new ToolStatus("docker", "Docker (GPU)", false, false, null, null, "Not found"), false, false);

        var version = await runner.RunAsync(new ProcessSpec(docker, ["version", "--format", "{{.Server.Version}}"]),
            cancellationToken: cancellationToken);

        var engineReady = version.Success && version.StdOut.Trim().Length > 0;
        var imagePresent = false;
        string? problem = engineReady ? null : "Docker Desktop is not running";

        if (engineReady)
        {
            var image = await runner.RunAsync(new ProcessSpec(docker, ["image", "inspect", settings.DockerImage, "--format", "{{.Id}}"]),
                cancellationToken: cancellationToken);
            imagePresent = image.Success;
            if (!imagePresent) problem = "Image not downloaded yet";
        }

        return (new ToolStatus("docker", "Docker (GPU)", true, false, docker,
            engineReady ? version.StdOut.Trim() : null, problem), engineReady, imagePresent);
    }

    private async Task<ToolStatus> CheckVersionAsync(
        string key, string displayName, string? path, string[] arguments, bool required, CancellationToken cancellationToken)
    {
        if (path is null || !File.Exists(path))
            return new ToolStatus(key, displayName, false, required, null, null, "Not found");

        var result = await runner.RunAsync(new ProcessSpec(path, arguments), cancellationToken: cancellationToken);
        var firstLine = (result.StdOut.Length > 0 ? result.StdOut : result.StdErr)
            .Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();

        if (result.ExitCode != 0 && string.IsNullOrEmpty(firstLine))
            return new ToolStatus(key, displayName, true, required, path, null, "Found but did not run");

        var version = VersionRegex().Match(firstLine ?? string.Empty).Value;
        return new ToolStatus(key, displayName, true, required, path,
            string.IsNullOrEmpty(version) ? firstLine : version, null);
    }

    [GeneratedRegex(@"[0-9]+(?:\.[0-9]+)+")]
    private static partial Regex VersionRegex();
}
