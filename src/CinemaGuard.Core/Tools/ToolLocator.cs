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

public sealed record ToolSnapshot(
    IReadOnlyList<ToolStatus> Tools,
    bool FfmpegReady,
    bool DockerEngineReady,
    bool DockerImagePresent,
    bool PhononAvailable,
    PhononRuntimeMode EffectiveRuntime,
    DateTimeOffset CheckedAt);

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

    public string? ResolveFfprobe(AppSettings settings)
    {
        var explicitProbe = Resolve(settings.FfprobePath, "ffprobe.exe");
        if (explicitProbe is not null) return explicitProbe;

        var ffmpeg = ResolveFfmpeg(settings);
        if (ffmpeg is not null)
        {
            var beside = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? string.Empty, "ffprobe.exe");
            if (File.Exists(beside) && !RejectOldAppPath(beside)) return beside;
        }

        return FindOnPath("ffprobe");
    }

    private string? FindBesideFfprobe()
    {
        var probe = FindOnPath("ffprobe");
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

    public static string? FindOnPath(string fileName)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';');

        foreach (var directory in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? fileName : fileName + extension.ToLowerInvariant());
                if (File.Exists(candidate) && !RejectOldAppPath(candidate)) return candidate;
            }
        }

        return null;
    }

    public async Task<ToolSnapshot> CheckAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var tools = new List<ToolStatus>();

        var ffmpeg = ResolveFfmpeg(settings);
        var ffmpegStatus = await CheckVersionAsync("ffmpeg", "FFmpeg", ffmpeg, ["-version"], required: true, cancellationToken);
        tools.Add(ffmpegStatus with { Problem = CheckFfmpegVersion(ffmpegStatus) });

        var ffprobe = ResolveFfprobe(settings);
        tools.Add(await CheckVersionAsync("ffprobe", "FFprobe", ffprobe, ["-version"], required: true, cancellationToken));

        var docker = ResolveDocker(settings);
        var dockerStatus = await CheckDockerAsync(docker, settings, cancellationToken);
        tools.Add(dockerStatus.Status);

        var fermion = ResolveFermion(settings);
        if (fermion is not null)
        {
            tools.Add(await CheckVersionAsync("fermion", "Phonon engine (your fermion.exe)", fermion, ["--version"], required: false, cancellationToken));
        }
        else
        {
            var installed = File.Exists(Speech.EnginePacks.PhononRuntime.MarkerPath) && File.Exists(Speech.SpeechPaths.PhononPython);
            tools.Add(new ToolStatus("fermion", "Phonon engine", installed, false,
                installed ? Speech.SpeechPaths.PhononRuntime : null,
                installed ? $"fermion {Speech.EnginePacks.FermionVersion}" : null,
                installed ? null : "Not downloaded (Models page)"));
        }

        var subloader = ResolveSubloader(settings);
        tools.Add(await CheckVersionAsync("subloader", "Subloader", subloader, ["--version"], required: false, cancellationToken));

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
            DateTimeOffset.Now);
    }

    private async Task<(ToolStatus Status, bool EngineReady, bool ImagePresent)> CheckDockerAsync(
        string? docker, AppSettings settings, CancellationToken cancellationToken)
    {
        if (docker is null)
            return (new ToolStatus("docker", "Docker", false, false, null, null, "Not found"), false, false);

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

    private static string? CheckFfmpegVersion(ToolStatus status)
    {
        if (!status.Found) return status.Problem;

        var match = VersionRegex().Match(status.Version ?? string.Empty);
        if (!match.Success) return null;

        var parts = match.Value.Split('.');
        if (!int.TryParse(parts[0], out var major)) return null;

        if (major < 7) return $"FFmpeg {match.Value} is too old: 7.1 or newer is required";

        if (major == 7)
        {
            var minor = parts.Length > 1 && int.TryParse(parts[1], out var value) ? value : 0;
            if (minor < 1) return "FFmpeg 7.0 is too old: 7.1 or newer is required";
        }

        return null;
    }

    [GeneratedRegex(@"[0-9]+(?:\.[0-9]+)+")]
    private static partial Regex VersionRegex();
}
