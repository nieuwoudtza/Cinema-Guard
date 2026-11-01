using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Phonon;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Tools;

namespace CinemaGuard.Core.Speech;

/// <summary>One transcription pass: a catalogue model (with its variant and device) or the server.</summary>
public sealed record SpeechPass(SpeechModelInfo? Model, ModelVariant? Variant, bool UseGpu, RemoteSpec? Remote)
{
    /// <summary>Stored in the transcript cache. Phonon keeps its bare alias, so caches from before the Models page still match.</summary>
    public string CacheKey => Remote?.CacheKey ?? (Model!.Family == SpeechFamily.Phonon ? Model.Id : $"{Model.Id}/{Variant!.Id}");

    public string DisplayName => Remote is not null ? "your server" : Model!.Name;
}

/// <summary>What the saved choice runs: one pass, or the Phonon 2.5 pair's two.</summary>
public sealed record SpeechPlan(IReadOnlyList<SpeechPass> Passes)
{
    public SpeechPass Primary => Passes[0];
    public SpeechPass? Multilingual => Passes.Count > 1 ? Passes[1] : null;
}

public sealed record SpeechOutcome(PhononResult Result, string EngineName, string? Note);

/// <summary>
/// The one place the pipeline asks for a transcript. It turns the saved choice into passes, says
/// what is missing before anything runs, and sends each pass to Phonon (CPU or Docker), the engine
/// host (Whisper, Parakeet) or the server. The engine host lives for one queue run.
/// </summary>
public sealed class SpeechService(ModelManager models, PhononService phonon, ToolLocator locator)
{
    private EngineHostClient? _host;

    public ModelManager Models => models;

    public static SpeechPlan Plan(AppSettings settings)
    {
        if (settings.SpeechModel == SpeechCatalog.RemoteId)
            return new SpeechPlan([new SpeechPass(null, null, false, Remote(settings))]);

        if (settings.SpeechModel == SpeechCatalog.PairId || SpeechCatalog.Find(settings.SpeechModel) is null)
            return new SpeechPlan([Pass(SpeechCatalog.PhononEnglish, settings), Pass(SpeechCatalog.PhononMultilingual, settings)]);

        return new SpeechPlan([Pass(SpeechCatalog.Find(settings.SpeechModel)!, settings)]);
    }

    public static SpeechPass Pass(SpeechModelInfo model, AppSettings settings)
    {
        var variant = model.Variant(settings.SpeechVariants.GetValueOrDefault(model.Id));
        var useGpu = model.Family == SpeechFamily.Phonon && settings.PhononRuntime != PhononRuntimeMode.Local;

        return new SpeechPass(model, variant, useGpu, null);
    }

    public static RemoteSpec Remote(AppSettings settings) =>
        new(settings.RemoteUrl, settings.RemoteModel, RemoteTranscriber.Unprotect(settings.RemoteApiKeyProtected), settings.RemoteMaxUploadMb, settings.PhononHotwords);

    /// <summary>"Phonon 2.5 English + Phonon 2.5 · CPU" for the Settings row and the Queue chip.</summary>
    public string Describe(AppSettings settings, bool shortForm = false)
    {
        if (settings.SpeechModel == SpeechCatalog.RemoteId)
            return string.IsNullOrWhiteSpace(settings.RemoteUrl) ? "Your server (not set up)" : $"Server · {HostOf(settings.RemoteUrl)}";

        var plan = Plan(settings);
        var name = plan.Multilingual is not null
            ? shortForm ? "Phonon 2.5 pair" : "Phonon 2.5 English + Phonon 2.5"
            : plan.Primary.Model!.Name;

        return $"{name} · {DeviceName(plan.Primary, settings)}";
    }

    public string DeviceName(SpeechPass pass, AppSettings settings)
    {
        if (pass.Remote is not null) return "server";

        return pass.Model!.Family switch
        {
            SpeechFamily.Phonon => pass.UseGpu && locator.ResolveDocker(settings) is not null ? "GPU (Docker)" : "CPU",
            SpeechFamily.Whisper => HardwareInfo.WhisperRuntimeName(ActiveWhisperRuntime(settings)),
            _ => "CPU",
        };
    }

    private WhisperRuntime ActiveWhisperRuntime(AppSettings settings)
    {
        var hardware = settings.Hardware ?? models.Hardware;
        var pack = EnginePacks.WhisperFor(hardware);
        if (pack is not null && models.IsInstalled(pack)) return EnginePacks.RuntimeOf(pack);
        return hardware.Avx2 ? WhisperRuntime.Cpu : WhisperRuntime.CpuNoAvx;
    }

    /// <summary>Why this pass cannot run right now (in words for the queue row), or null when it can.</summary>
    public string? Problem(SpeechPass pass, AppSettings settings)
    {
        if (pass.Remote is { } remote)
            return string.IsNullOrWhiteSpace(remote.Url) ? "No server address is set. Open Models and set up Your own server." : null;

        var model = pass.Model!;
        var variant = pass.Variant!;

        if (model.Family == SpeechFamily.Phonon)
        {
            var local = models.IsInstalled(model, variant) && PhononLauncher.Resolve(models) is not null;
            var docker = pass.UseGpu && locator.ResolveDocker(settings) is not null;
            return local || docker ? null : $"{model.Name} isn't downloaded. Open Models to download it.";
        }

        return models.IsInstalled(model, variant) ? null : $"{model.Name} isn't downloaded. Open Models to download it.";
    }

    public async Task<SpeechOutcome> TranscribeAsync(
        SpeechPass pass, string wavPath, AppSettings settings, Action<string>? status, CancellationToken cancellationToken)
    {
        if (Problem(pass, settings) is { } problem) throw new PhononException(problem);

        if (pass.Remote is { } remote)
        {
            var result = await RemoteTranscriber.TranscribeAsync(remote, wavPath, status, cancellationToken);
            return new SpeechOutcome(result, "server", null);
        }

        var model = pass.Model!;
        var variant = pass.Variant!;

        if (model.Family == SpeechFamily.Phonon)
        {
            var local = models.IsInstalled(model, variant) ? PhononLauncher.Resolve(models) : null;
            var effective = settings;

            if (!pass.UseGpu && settings.PhononRuntime != PhononRuntimeMode.Local)
            {
                effective = settings.Clone();
                effective.PhononRuntime = PhononRuntimeMode.Local;
            }

            var outcome = await phonon.TranscribeAsync(wavPath, model.Phonon!.Alias, effective, local, status, cancellationToken);
            return new SpeechOutcome(outcome.Result, outcome.RunnerName, outcome.FallbackUsed ? outcome.Note : null);
        }

        var request = BuildHostRequest(model, variant, wavPath, settings);
        _host ??= new EngineHostClient();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var transcript = await _host.TranscribeAsync(request,
            value => status?.Invoke($"{value:0}%"),
            line => Log.Info($"{model.Name}: {line}"),
            cancellationToken);

        Log.Info($"{model.Name} ({variant.Label}, {request.Runtime}) took {stopwatch.Elapsed.TotalSeconds:0.0} s.");
        return new SpeechOutcome(transcript, $"{model.Name} · {request.Runtime}", null);
    }

    public HostRequest BuildHostRequest(SpeechModelInfo model, ModelVariant variant, string wavPath, AppSettings settings)
    {
        var hardware = settings.Hardware ?? models.Hardware;
        var threads = Math.Clamp(hardware.LogicalCores > 0 ? hardware.LogicalCores / 2 : Environment.ProcessorCount / 2, 1, 32);

        if (model.Family == SpeechFamily.Whisper)
        {
            var pack = EnginePacks.WhisperFor(hardware);
            var installed = pack is not null && models.IsInstalled(pack);
            var runtime = installed ? EnginePacks.RuntimeOf(pack!) : hardware.Avx2 ? WhisperRuntime.Cpu : WhisperRuntime.CpuNoAvx;

            return new HostRequest
            {
                Engine = "whisper",
                Model = Path.Combine(SpeechPaths.ModelFolder(model, variant), variant.Files[0].RelativePath),
                Runtime = runtime switch
                {
                    WhisperRuntime.Cuda13 => "cuda13",
                    WhisperRuntime.Cuda12 => "cuda12",
                    WhisperRuntime.Vulkan => "vulkan",
                    WhisperRuntime.CpuNoAvx => "cpu-noavx",
                    _ => "cpu",
                },
                RuntimeFolder = installed ? pack!.Folder : null,
                Wav = wavPath,
                Language = variant.EnglishOnly ? "en" : "auto",
                Prompt = string.IsNullOrWhiteSpace(settings.PhononHotwords) ? null : settings.PhononHotwords.Trim().TrimEnd('.') + ".",
                HeadsPreset = HeadsPreset(model, variant),
                Threads = threads,
            };
        }

        return new HostRequest
        {
            Engine = "parakeet",
            Model = SpeechPaths.ModelFolder(model, variant),
            Runtime = "cpu",
            RuntimeFolder = null,
            Wav = wavPath,
            Threads = threads,
        };
    }

    /// <summary>The DTW alignment heads for each Whisper size (whisper.cpp presets).</summary>
    public static string HeadsPreset(SpeechModelInfo model, ModelVariant variant) => model.Id switch
    {
        "whisper-tiny" => variant.EnglishOnly ? "TinyEn" : "Tiny",
        "whisper-base" => variant.EnglishOnly ? "BaseEn" : "Base",
        "whisper-small" => variant.EnglishOnly ? "SmallEn" : "Small",
        "whisper-medium" => variant.EnglishOnly ? "MediumEn" : "Medium",
        "whisper-large-v1" => "LargeV1",
        "whisper-large-v2" => "LargeV2",
        "whisper-large-v3" => "LargeV3",
        "whisper-large-v3-turbo" => "LargeV3Turbo",
        _ => "None",
    };

    /// <summary>Starts a queue run with no Docker failure remembered from an earlier one.</summary>
    public void BeginSession() => phonon.ResetDockerCheck();

    /// <summary>Ends the engine host after a queue (frees RAM and VRAM). Safe to call any time.</summary>
    public void EndSession()
    {
        _host?.Stop();
        phonon.ResetDockerCheck();
    }

    private static string HostOf(string url)
    {
        try { return new Uri(url.Contains("://") ? url : "http://" + url).Host; }
        catch (UriFormatException) { return url; }
    }

}
