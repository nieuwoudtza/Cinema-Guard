using System.Diagnostics;
using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Speech;
using CinemaGuard.Core.Tools;

namespace CinemaGuard.Core.Phonon;

public static class PhononModels
{
    public const string English = "phonon-2.5-en";
    public const string Multilingual = "phonon-2.5";
}

public interface IPhononRunner
{
    string Name { get; }

    Task<PhononResult> TranscribeAsync(
        string wavPath,
        string model,
        string? hotwords,
        Action<string>? onLog,
        CancellationToken cancellationToken);
}

/// <summary>
/// `fermion transcribe &lt;model&gt; &lt;wav&gt; --json` on the CPU, from the private runtime (or a fermion.exe
/// set in Settings). Offline: a model that is not downloaded fails here rather than downloading mid-queue.
/// </summary>
public sealed class LocalPhononRunner(PhononLauncher launcher, IProcessRunner runner) : IPhononRunner
{
    public string Name => "Phonon CPU";

    public async Task<PhononResult> TranscribeAsync(
        string wavPath, string model, string? hotwords, Action<string>? onLog, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "transcribe", model, wavPath, "--json", "--verbose" };
        if (!string.IsNullOrWhiteSpace(hotwords))
        {
            arguments.Add("--hotwords");
            arguments.Add(hotwords);
        }

        onLog?.Invoke($"{Name}: {model} on {Path.GetFileName(wavPath)}");
        var result = await runner.RunAsync(launcher.Spec(arguments, offline: true),
            onStandardOutput: onLog, cancellationToken: cancellationToken);

        if (result.Cancelled) throw new OperationCanceledException(cancellationToken);
        if (!result.Success) throw new PhononException($"Phonon exited {result.ExitCode}: {Tail(result.StdErr)}");

        return PhononResult.TryParse(result.StdOut) ?? throw new PhononException("Phonon printed no JSON.");
    }

    internal static string Tail(string text, int length = 600)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(no output)";
        var trimmed = text.Trim();
        return trimmed.Length <= length ? trimmed : "..." + trimmed[^length..];
    }
}

/// <summary>The published Fermion CUDA image, fed a 16 kHz mono wav and printing JSON.</summary>
public sealed class DockerPhononRunner(string dockerPath, string image, IProcessRunner runner) : IPhononRunner
{
    public string Name => "Phonon GPU";

    public async Task<PhononResult> TranscribeAsync(
        string wavPath, string model, string? hotwords, Action<string>? onLog, CancellationToken cancellationToken)
    {
        var workDirectory = Path.GetDirectoryName(wavPath) ?? throw new PhononException("The work folder is missing.");
        var fileName = Path.GetFileName(wavPath);
        var containerName = "cg-" + Guid.NewGuid().ToString("N")[..12];

        var arguments = new List<string>
        {
            "run", "--rm", "--name", containerName,
            "--gpus", "all",
            "-v", "phonon-cache:/home/phonon/.cache",
            "-v", $"{workDirectory}:/work:ro",
            "--entrypoint", "python3",
            image,
            "/opt/phonon/entrypoint.py", "transcribe", $"/work/{fileName}",
            "--model", model, "--json", "--verbose",
        };

        if (!string.IsNullOrWhiteSpace(hotwords))
        {
            arguments.Add("--hotwords");
            arguments.Add(hotwords);
        }

        onLog?.Invoke($"{Name}: {model} via Docker");

        try
        {
            var result = await runner.RunAsync(new ProcessSpec(dockerPath, arguments),
                onStandardOutput: onLog, cancellationToken: cancellationToken);

            if (result.Cancelled)
            {
                await KillContainerAsync(containerName);
                throw new OperationCanceledException(cancellationToken);
            }

            if (!result.Success)
                throw new PhononException($"Docker/Phonon exited {result.ExitCode}: {LocalPhononRunner.Tail(result.StdErr)}");

            return PhononResult.TryParse(result.StdOut) ?? throw new PhononException("Phonon printed no JSON.");
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested) await KillContainerAsync(containerName);
        }
    }

    private async Task KillContainerAsync(string containerName)
    {
        try
        {
            await runner.RunAsync(new ProcessSpec(dockerPath, ["rm", "-f", containerName]), cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not remove the cancelled container {containerName}: {ex.Message}");
        }
    }
}

public sealed record PhononOutcome(PhononResult Result, string RunnerName, bool FallbackUsed, string? Note);

/// <summary>
/// Chooses the runtime (Docker GPU first when Phonon's GPU switch is on, the private CPU runtime as the
/// fallback), starts Docker Desktop when asked, and serialises decodes across app instances so two runs
/// never fight over the GPU.
/// </summary>
public sealed class PhononService(IProcessRunner runner, ToolLocator locator)
{
    private static readonly Semaphore CrossProcessSemaphore = CreateCrossProcessSemaphore();

    /// <param name="local">The CPU runtime, or null when Phonon is not installed locally (Docker only).</param>
    public async Task<PhononOutcome> TranscribeAsync(
        string wavPath, string model, AppSettings settings, PhononLauncher? local, Action<string>? onLog, CancellationToken cancellationToken)
    {
        var docker = locator.ResolveDocker(settings);
        var preferDocker = settings.PhononRuntime != PhononRuntimeMode.Local;

        Exception? dockerError = null;

        if (preferDocker && docker is not null)
        {
            try
            {
                if (!await IsEngineReadyAsync(docker, cancellationToken))
                {
                    if (!settings.StartDockerDesktop)
                        throw new PhononException("Docker Desktop is not running.");

                    await StartDockerDesktopAsync(docker, onLog, cancellationToken);
                    if (!await IsEngineReadyAsync(docker, cancellationToken))
                        throw new PhononException("Docker Desktop did not start in time.");
                }

                if (!await ImageExistsAsync(docker, settings.DockerImage, cancellationToken))
                    throw new PhononException($"The Docker image {settings.DockerImage} is not downloaded.");

                var result = await RunSerialisedAsync(
                    new DockerPhononRunner(docker, settings.DockerImage, runner), wavPath, model, settings.PhononHotwords, onLog, cancellationToken);

                return new PhononOutcome(result, "GPU (Docker)", false, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A cancelled docker probe reports "not running"; never fall back to the CPU for that.
                cancellationToken.ThrowIfCancellationRequested();

                dockerError = ex;
                Log.Warn($"GPU transcription unavailable, trying the CPU: {ex.Message}");
                onLog?.Invoke($"GPU unavailable ({ex.Message.Trim()}); switching to the CPU.");
            }
        }

        if (local is null)
        {
            throw new PhononException(dockerError is null
                ? "The Phonon engine is not downloaded. Open Models and download the model you picked."
                : $"GPU transcription failed ({dockerError.Message}) and Phonon is not downloaded for the CPU. Open Models to download it.");
        }

        var cpu = await RunSerialisedAsync(
            new LocalPhononRunner(local, runner), wavPath, model, settings.PhononHotwords, onLog, cancellationToken);

        return new PhononOutcome(cpu, "CPU", dockerError is not null, dockerError?.Message);
    }

    private static async Task<PhononResult> RunSerialisedAsync(
        IPhononRunner transcriptionRunner,
        string wavPath,
        string model,
        string? hotwords,
        Action<string>? onLog,
        CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            // Wait on the cancellation handle too, so Cancel does not hang behind another instance's decode.
            var signalled = await Task.Run(
                () => WaitHandle.WaitAny([CrossProcessSemaphore, cancellationToken.WaitHandle], TimeSpan.FromMinutes(45)),
                CancellationToken.None);

            acquired = signalled == 0;

            if (!acquired && cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            if (!acquired) onLog?.Invoke("Another Cinema Guard instance is transcribing; continuing anyway.");

            return await transcriptionRunner.TranscribeAsync(wavPath, model, hotwords, onLog, cancellationToken);
        }
        finally
        {
            if (acquired)
            {
                try { CrossProcessSemaphore.Release(); } catch (SemaphoreFullException) { /* already released */ }
            }
        }
    }

    public async Task<bool> IsEngineReadyAsync(string docker, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessSpec(docker, ["version", "--format", "{{.Server.Version}}"]),
            cancellationToken: cancellationToken);
        return result.Success && result.StdOut.Trim().Length > 0;
    }

    public async Task<bool> ImageExistsAsync(string docker, string image, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessSpec(docker, ["image", "inspect", image, "--format", "{{.Id}}"]),
            cancellationToken: cancellationToken);
        return result.Success;
    }

    private async Task StartDockerDesktopAsync(string docker, Action<string>? onLog, CancellationToken cancellationToken)
    {
        var desktop = ToolLocator.DefaultDockerDesktopPath;
        if (!File.Exists(desktop))
        {
            onLog?.Invoke("Docker Desktop could not be found.");
            return;
        }

        onLog?.Invoke("Starting Docker Desktop...");
        try
        {
            Process.Start(new ProcessStartInfo(desktop) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not start Docker Desktop: {ex.Message}");
            return;
        }

        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            if (await IsEngineReadyAsync(docker, cancellationToken)) return;
        }
    }

    private static Semaphore CreateCrossProcessSemaphore()
    {
        try
        {
            return new Semaphore(1, 1, @"Local\CinemaGuard.Phonon");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not create the cross-process Phonon semaphore: {ex.Message}");
            return new Semaphore(1, 1);
        }
    }
}
