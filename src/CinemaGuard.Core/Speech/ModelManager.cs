using System.Text.Json;
using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Settings;
using CinemaGuard.Core.Tools;

namespace CinemaGuard.Core.Speech;

/// <summary>A download in progress (or finished) for one model or engine, shown on its row.</summary>
public sealed class DownloadJob
{
    private readonly CancellationTokenSource _cancellation = new();

    internal DownloadJob(string key, string title, long total)
    {
        Key = key;
        Title = title;
        Total = Math.Max(1, total);
    }

    public string Key { get; }
    public string Title { get; }
    public long Total { get; internal set; }
    public long Done { get; internal set; }
    public string Status { get; internal set; } = "Waiting";
    public string? Error { get; internal set; }
    public bool IsFinished { get; internal set; }
    public bool Succeeded => IsFinished && Error is null && !Cancelled;
    public bool Cancelled { get; internal set; }

    /// <summary>
    /// Phonon fetches one archive before unpacking it, so nothing on disk grows while it downloads.
    /// The row then shows a moving bar without a fake percentage.
    /// </summary>
    public bool IsIndeterminate { get; internal set; }

    public Task Completion { get; internal set; } = Task.CompletedTask;

    public double Percent => Math.Clamp(Done * 100.0 / Total, 0, 100);

    internal CancellationToken Token => _cancellation.Token;

    public void Cancel() => _cancellation.Cancel();
}

/// <summary>
/// Knows what is on disk, downloads models and engines (resuming, verified), and deletes them.
/// A model's download first fetches the engines it needs; each engine is its own job, so two models
/// that need the same engine share one download.
/// </summary>
public sealed class ModelManager(SettingsService settings, IProcessRunner runner)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DownloadJob> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _phononLock = new(1, 1);
    private long? _diskUsage;

    /// <summary>Raised from worker threads whenever a job moves or anything is installed or deleted.</summary>
    public event Action? Changed;

    public static string ModelKey(SpeechModelInfo model, ModelVariant variant) => $"model:{model.Id}/{variant.Id}";
    public static string PackKey(EnginePack pack) => $"pack:{pack.Id}";

    private AppSettings S => settings.Current;
    public HardwareInfo Hardware => S.Hardware ?? new HardwareInfo { Avx2 = true, LogicalCores = Environment.ProcessorCount };

    // ------------------------------------------------------------------ state

    public bool IsInstalled(SpeechModelInfo model, ModelVariant variant)
    {
        if (model.Phonon is { } phonon)
        {
            var folder = SpeechPaths.PhononModelFolder(phonon);
            return Directory.Exists(folder) && Directory.EnumerateFiles(folder).Take(2).Count() == 2;
        }

        var root = SpeechPaths.ModelFolder(model, variant);
        return variant.Files.All(file =>
        {
            var path = Path.Combine(root, file.RelativePath);
            return File.Exists(path) && (file.Size <= 0 || new FileInfo(path).Length == file.Size);
        });
    }

    public bool IsInstalled(EnginePack pack)
    {
        if (pack.Kind == PackKind.PhononRuntime && ExternalFermion is not null) return true;
        return File.Exists(pack.MarkerPath);
    }

    /// <summary>A fermion.exe the user pointed Settings at; it replaces the private Phonon engine.</summary>
    public string? ExternalFermion =>
        !string.IsNullOrWhiteSpace(S.LocalFermionPath) && File.Exists(S.LocalFermionPath) ? S.LocalFermionPath : null;

    public DownloadJob? Job(string key)
    {
        lock (_gate) return _jobs.TryGetValue(key, out var job) && !job.IsFinished ? job : null;
    }

    /// <summary>The last job for the key, finished or not (for showing a failure on the row).</summary>
    public DownloadJob? LastJob(string key)
    {
        lock (_gate) return _jobs.GetValueOrDefault(key);
    }

    public bool IsBusy
    {
        get { lock (_gate) return _jobs.Values.Any(job => !job.IsFinished); }
    }

    /// <summary>The engines a model needs on this PC: Phonon's runtime or Whisper's GPU pack.</summary>
    public IReadOnlyList<EnginePack> PacksFor(SpeechModelInfo model, bool useGpu)
    {
        var packs = new List<EnginePack>();

        switch (model.Family)
        {
            case SpeechFamily.Phonon:
                packs.Add(EnginePacks.PhononRuntime);
                break;
            case SpeechFamily.Whisper:
                if (EnginePacks.WhisperFor(Hardware) is { } whisper) packs.Add(whisper);
                break;

            // Parakeet always runs on the CPU: its GPU pack was slower than the CPU on the test
            // machine and costs 3.6 GB, so it is no longer downloaded for a model.
        }

        return packs;
    }

    public long MissingBytes(SpeechModelInfo model, ModelVariant variant, bool useGpu)
    {
        long total = 0;
        foreach (var pack in PacksFor(model, useGpu).Where(p => !IsInstalled(p))) total += pack.DownloadSize;
        if (!IsInstalled(model, variant)) total += variant.DiskSize;
        return total;
    }

    // ------------------------------------------------------------------ downloads

    /// <summary>Starts (or joins) the download of a model and every engine it is missing.</summary>
    public DownloadJob Download(SpeechModelInfo model, ModelVariant variant, bool useGpu)
    {
        var key = ModelKey(model, variant);
        var packs = PacksFor(model, useGpu).Where(p => !IsInstalled(p)).ToList();

        lock (_gate)
        {
            if (_jobs.TryGetValue(key, out var running) && !running.IsFinished) return running;

            var job = new DownloadJob(key, model.Name, Math.Max(1, MissingBytes(model, variant, useGpu)));
            _jobs[key] = job;

            var packJobs = packs.Select(StartPackLocked).ToList();
            job.Completion = Task.Run(() => RunModelJobAsync(job, model, variant, packJobs));
            return job;
        }
    }

    public DownloadJob DownloadPack(EnginePack pack)
    {
        lock (_gate) return StartPackLocked(pack);
    }

    private DownloadJob StartPackLocked(EnginePack pack)
    {
        var key = PackKey(pack);
        if (_jobs.TryGetValue(key, out var running) && !running.IsFinished) return running;

        var job = new DownloadJob(key, pack.Name, pack.DownloadSize);
        _jobs[key] = job;
        job.Completion = Task.Run(() => RunPackJobAsync(job, pack));
        return job;
    }

    public void Cancel(string key)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(key, out var job)) job.Cancel();
        }
    }

    private async Task RunModelJobAsync(DownloadJob job, SpeechModelInfo model, ModelVariant variant, List<DownloadJob> packJobs)
    {
        try
        {
            long packBytes = 0;

            foreach (var packJob in packJobs)
            {
                job.Status = packJob.Title.StartsWith("Phonon", StringComparison.Ordinal)
                    ? "Downloading the Phonon engine"
                    : $"Downloading the {packJob.Title}";
                Raise();

                // Mirror the engine's progress onto the model row while it runs.
                while (!packJob.Completion.IsCompleted)
                {
                    job.Done = packBytes + packJob.Done;
                    Raise();
                    await Task.WhenAny(packJob.Completion, Task.Delay(500, job.Token));
                    job.Token.ThrowIfCancellationRequested();
                }

                if (!packJob.Succeeded)
                    throw new DownloadException(packJob.Error ?? $"The {packJob.Title} download was cancelled.");

                packBytes += packJob.Total;
            }

            job.Done = packBytes;

            if (!IsInstalled(model, variant))
            {
                if (model.Phonon is { } phonon)
                    await DownloadPhononModelAsync(job, phonon, packBytes);
                else
                    await DownloadFilesAsync(job, model, variant, packBytes);
            }

            job.Done = job.Total;
            job.Status = "Downloaded";
            Log.Info($"Downloaded {model.Name} ({variant.Label}).");
        }
        catch (OperationCanceledException)
        {
            job.Cancelled = true;
            job.Status = "Cancelled";
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            job.Status = "Failed";
            Log.Error($"Download of {model.Name} failed", ex);
        }
        finally
        {
            job.IsFinished = true;
            InvalidateDiskUsage();
            Raise();
        }
    }

    private async Task DownloadFilesAsync(DownloadJob job, SpeechModelInfo model, ModelVariant variant, long offset)
    {
        var folder = SpeechPaths.ModelFolder(model, variant);
        long done = offset;

        foreach (var file in variant.Files)
        {
            job.Status = $"Downloading {model.Name}";
            var target = Path.Combine(folder, file.RelativePath);
            var start = done;

            await Downloader.DownloadAsync(file.Url, target, file.Size, file.Sha256, bytes =>
            {
                job.Done = start + bytes;
                Raise();
            }, job.Token);

            done = start + Math.Max(file.Size, new FileInfo(target).Length);
        }
    }

    /// <summary>
    /// Fermion fetches and verifies Phonon models itself (--download-only). Its Hugging Face staging copy
    /// is removed afterwards: only the unpacked model is needed, which halves the space.
    /// </summary>
    private async Task DownloadPhononModelAsync(DownloadJob job, PhononSpec phonon, long offset)
    {
        // Only one Fermion download at a time: say so instead of showing a frozen 0%.
        job.Status = "Waiting for the other model to finish";
        job.Done = Math.Max(job.Done, offset);
        job.IsIndeterminate = true;
        Raise();

        await _phononLock.WaitAsync(job.Token);

        try
        {
            job.Status = "Downloading the model";
            Raise();

            var launcher = PhononLauncher.Resolve(this) ?? throw new DownloadException("The Phonon engine is not installed.");
            var before = FolderSize(SpeechPaths.PhononCache);
            var expected = Math.Max(1, job.Total - offset);

            using var poll = new CancellationTokenSource();
            var watcher = Task.Run(async () =>
            {
                while (!poll.IsCancellationRequested)
                {
                    try { await Task.Delay(250, poll.Token); } catch (OperationCanceledException) { break; }

                    // Fermion stages the download in the same cache; its growth is the best live estimate.
                    var grown = Math.Max(0, FolderSize(SpeechPaths.PhononCache) - before);
                    job.Done = Math.Max(job.Done, offset + Math.Min(expected * 99 / 100, grown));
                    Raise();
                }
            });

            var spec = launcher.Spec(["transcribe", phonon.Alias, "--download-only"], offline: false);
            var result = await runner.RunAsync(spec, cancellationToken: job.Token);

            poll.Cancel();
            await watcher;

            if (result.Cancelled) throw new OperationCanceledException(job.Token);
            if (!result.Success)
                throw new DownloadException($"Phonon could not download {phonon.Alias}: {LastLine(result.StdErr)}");

            PrunePhononStaging(phonon);
        }
        finally
        {
            _phononLock.Release();
        }
    }

    private static void PrunePhononStaging(PhononSpec phonon)
    {
        var repo = phonon.CacheFolder.Replace("__", "--");

        foreach (var path in new[]
                 {
                     Path.Combine(SpeechPaths.PhononCache, "models--" + repo),
                     Path.Combine(SpeechPaths.PhononCache, ".locks", "models--" + repo),
                     Path.Combine(SpeechPaths.PhononCache, "blobs"),
                     Path.Combine(SpeechPaths.PhononCache, ".xet"),
                 })
        {
            TryDeleteDirectory(path);
        }
    }

    /// <summary>Where engine archives land while an install unpacks them.</summary>
    public static string DownloadsFolder => Path.Combine(SpeechPaths.Engines, "downloads");

    /// <summary>
    /// Deletes archives left behind by installs that are no longer running. A running pack keeps every
    /// archive because it may still need to unpack it; after that a retry can fetch the file again.
    /// </summary>
    public void SweepDownloads(TimeSpan maxAge)
    {
        if (IsBusy) return;

        var folder = DownloadsFolder;
        if (!Directory.Exists(folder)) return;

        var cutoff = DateTime.UtcNow - maxAge;

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc >= cutoff) continue;

                info.Delete();
                Log.Info($"Removed the leftover download {info.Name}.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not delete the leftover download {Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    private async Task RunPackJobAsync(DownloadJob job, EnginePack pack)
    {
        try
        {
            if (pack.Kind == PackKind.PhononRuntime)
                await PhononRuntimeInstaller.InstallAsync(pack, runner, job, Raise);
            else
                await InstallArchivePackAsync(job, pack);

            job.Done = job.Total;
            job.Status = "Installed";
            Log.Info($"Installed {pack.Name} ({pack.Description}).");
        }
        catch (OperationCanceledException)
        {
            job.Cancelled = true;
            job.Status = "Cancelled";
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            job.Status = "Failed";
            Log.Error($"Install of {pack.Name} failed", ex);
        }
        finally
        {
            job.IsFinished = true;
            InvalidateDiskUsage();
            Raise();

            // A successful install no longer needs its archives; a failed one keeps them for the retry.
            if (job.Succeeded) SweepDownloads(TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Downloads every archive (kept until the pack is complete, so a retry resumes), unpacks the wanted
    /// files into "&lt;folder&gt;.partial", and only then swaps it in. A half-installed pack never loads.
    /// </summary>
    private async Task InstallArchivePackAsync(DownloadJob job, EnginePack pack)
    {
        var downloads = Path.Combine(SpeechPaths.Engines, "downloads");
        var staging = pack.Folder + ".partial";
        TryDeleteDirectory(staging);
        Directory.CreateDirectory(staging);

        long done = 0;
        var archives = new List<string>();

        foreach (var source in pack.Sources)
        {
            job.Token.ThrowIfCancellationRequested();
            var start = done;
            var target = Path.Combine(staging, source.Target);

            // cuBLAS and friends are the same files in the Whisper and Parakeet packs: copy, don't re-download.
            if (TryCopyFromInstalledPacks(pack, source, target))
            {
                done += source.Size;
                job.Done = done;
                Raise();
                continue;
            }

            var name = Path.GetFileName(new Uri(source.Url).LocalPath);
            var archive = Path.Combine(downloads, name);
            job.Status = $"Downloading {name}";
            Raise();

            await Downloader.DownloadAsync(source.Url, archive, source.Size, source.Sha256, bytes =>
            {
                job.Done = start + bytes;
                Raise();
            }, job.Token);

            job.Status = $"Unpacking {name}";
            Raise();

            var extracted = await Task.Run(() => Downloader.Extract(archive, source, target, job.Token), job.Token);
            if (extracted == 0) throw new DownloadException($"{name} did not contain the expected files.");

            archives.Add(archive);
            done = start + source.Size;
        }

        await File.WriteAllTextAsync(Path.Combine(staging, Path.GetFileName(pack.MarkerPath)),
            JsonSerializer.Serialize(new { pack.Id, installed = DateTimeOffset.Now }), job.Token);

        TryDeleteDirectory(pack.Folder);
        Directory.CreateDirectory(Path.GetDirectoryName(pack.Folder)!);
        Directory.Move(staging, pack.Folder);

        // The archives are only kept while another pack's download still needs them.
        foreach (var archive in archives.Where(a => !SharedByRunningJob(a, job.Key))) TryDeleteFile(archive);
    }

    private bool TryCopyFromInstalledPacks(EnginePack pack, PackSource source, string target)
    {
        if (source.Names is not { Count: > 0 } names) return false;

        foreach (var other in EnginePacks.All.Where(p => p.Id != pack.Id && p.Kind != PackKind.PhononRuntime && IsInstalled(p)))
        {
            var found = names.Select(n => Path.Combine(other.Folder, n)).ToList();
            if (!found.All(File.Exists)) continue;

            Directory.CreateDirectory(target);
            foreach (var file in found) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            return true;
        }

        return false;
    }

    private bool SharedByRunningJob(string archive, string exceptKey)
    {
        var name = Path.GetFileName(archive);
        lock (_gate)
        {
            return _jobs.Values
                .Where(j => !j.IsFinished && j.Key != exceptKey && j.Key.StartsWith("pack:", StringComparison.Ordinal))
                .Select(j => EnginePacks.Find(j.Key[5..]))
                .Any(p => p?.Sources.Any(s => s.Url.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)) == true);
        }
    }

    // ------------------------------------------------------------------ deletion

    public void Delete(SpeechModelInfo model, ModelVariant variant)
    {
        if (Job(ModelKey(model, variant)) is not null) throw new InvalidOperationException("Cancel the download first.");

        if (model.Phonon is { } phonon)
        {
            TryDeleteDirectory(Path.Combine(SpeechPaths.PhononCache, "speech", phonon.CacheFolder), throwOnError: true);
            PrunePhononStaging(phonon);
        }
        else if (model.Family == SpeechFamily.Parakeet)
        {
            TryDeleteDirectory(SpeechPaths.ModelFolder(model, variant), throwOnError: true);

            var modelFolder = Path.Combine(SpeechPaths.ParakeetModels, model.Id);
            if (Directory.Exists(modelFolder) && !Directory.EnumerateFileSystemEntries(modelFolder).Any()) TryDeleteDirectory(modelFolder);
        }
        else
        {
            foreach (var file in variant.Files)
            {
                var path = Path.Combine(SpeechPaths.ModelFolder(model, variant), file.RelativePath);
                if (File.Exists(path)) File.Delete(path);
                TryDeleteFile(path + ".part");
            }
        }

        Log.Info($"Deleted {model.Name} ({variant.Label}).");
        InvalidateDiskUsage();
        Raise();
    }

    public void Delete(EnginePack pack)
    {
        if (Job(PackKey(pack)) is not null) throw new InvalidOperationException("Cancel the download first.");

        TryDeleteDirectory(pack.Folder, throwOnError: true);
        TryDeleteDirectory(pack.Folder + ".partial");
        Log.Info($"Deleted {pack.Name} ({pack.Description}).");
        InvalidateDiskUsage();
        Raise();
    }

    /// <summary>Models still on disk that need this engine (so deleting the last one can offer to remove it).</summary>
    public IReadOnlyList<SpeechModelInfo> ModelsUsing(EnginePack pack) =>
        SpeechCatalog.Models
            .Where(m => m.Variants.Any(v => IsInstalled(m, v)))
            .Where(m => pack.Kind switch
            {
                PackKind.PhononRuntime => m.Family == SpeechFamily.Phonon,
                PackKind.WhisperGpu => m.Family == SpeechFamily.Whisper,
                _ => m.Family == SpeechFamily.Parakeet,
            })
            .ToList();

    // ------------------------------------------------------------------ sizes

    public long InstalledSize(SpeechModelInfo model, ModelVariant variant)
    {
        if (model.Phonon is { } phonon) return FolderSize(Path.Combine(SpeechPaths.PhononCache, "speech", phonon.CacheFolder));
        if (model.Family == SpeechFamily.Parakeet) return FolderSize(SpeechPaths.ModelFolder(model, variant));

        return variant.Files.Sum(f =>
        {
            var path = Path.Combine(SpeechPaths.ModelFolder(model, variant), f.RelativePath);
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        });
    }

    public long InstalledSize(EnginePack pack) => FolderSize(pack.Folder);

    /// <summary>Everything under models\ and engines\, cached until something changes.</summary>
    public long DiskUsage()
    {
        if (_diskUsage is { } cached) return cached;
        var total = FolderSize(SpeechPaths.Models) + FolderSize(SpeechPaths.Engines);
        _diskUsage = total;
        return total;
    }

    private void InvalidateDiskUsage() => _diskUsage = null;

    public static long FolderSize(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return 0;
            return new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Sum(f => f.Length);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private void Raise()
    {
        try { Changed?.Invoke(); } catch (Exception ex) { Log.Warn($"Model listener failed: {ex.Message}"); }
    }

    internal static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "(no output)" : lines[^1];
    }

    internal static void TryDeleteDirectory(string path, bool throwOnError = false)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (!throwOnError)
        {
            Log.Warn($"Could not delete {path}: {ex.Message}");
        }
        catch (Exception ex)
        {
            throw new IOException($"Could not delete {Path.GetFileName(path)}: {ex.Message} Close anything using it and try again.", ex);
        }
    }

    internal static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not delete {path}: {ex.Message}");
        }
    }
}
