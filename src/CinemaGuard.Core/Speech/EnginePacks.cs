namespace CinemaGuard.Core.Speech;

/// <summary>Where every model and engine lives. One root, so "Open folder" and the disk total cover everything.</summary>
public static class SpeechPaths
{
    private static string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinemaGuard");

    /// <summary>Tests point this at a temporary folder.</summary>
    public static string Root
    {
        get => _root;
        set => _root = value;
    }

    public static string Models => Path.Combine(Root, "models");
    public static string Engines => Path.Combine(Root, "engines");

    /// <summary>FERMION_CACHE_DIR for every Phonon run, so Phonon models sit with the others.</summary>
    public static string PhononCache => Path.Combine(Models, "phonon");
    public static string WhisperModels => Path.Combine(Models, "whisper");
    public static string ParakeetModels => Path.Combine(Models, "parakeet");

    public static string PhononRuntime => Path.Combine(Engines, "phonon");
    public static string PhononPython => Path.Combine(PhononRuntime, "python", "python.exe");

    /// <summary>Whisper.net looks for runtimes\&lt;kind&gt;\win-x64 next to <c>RuntimeOptions.LibraryPath</c>.</summary>
    public static string WhisperRuntimes => Path.Combine(Engines, "whisper");

    public static string ModelFolder(SpeechModelInfo model, ModelVariant variant) => model.Family switch
    {
        SpeechFamily.Whisper => WhisperModels,
        SpeechFamily.Parakeet => Path.Combine(ParakeetModels, model.Id, variant.Id),
        _ => PhononCache,
    };

    public static string PhononModelFolder(PhononSpec spec) => Path.Combine(PhononCache, "speech", spec.CacheFolder, spec.ModelFolder);
}

public enum PackFormat { File, Zip, TarBz2 }

/// <summary>
/// One download inside an engine pack. Archives are unpacked flat into <see cref="Target"/>: only the
/// entries under <see cref="EntryPrefix"/>, or only the file names in <see cref="Names"/>.
/// </summary>
public sealed record PackSource(
    string Url,
    long Size,
    string? Sha256,
    PackFormat Format,
    string Target = "",
    string? EntryPrefix = null,
    IReadOnlyList<string>? Names = null,
    string? NamePrefix = null)
{
    public bool Wants(string entryPath)
    {
        var normalised = entryPath.Replace('\\', '/');
        if (normalised.EndsWith('/')) return false;

        var name = normalised[(normalised.LastIndexOf('/') + 1)..];
        if (Names is not null && Names.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        if (NamePrefix is not null && name.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return true;
        if (EntryPrefix is not null && normalised.StartsWith(EntryPrefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

public enum PackKind { PhononRuntime, WhisperGpu, ParakeetGpu }

/// <summary>
/// A runtime a model family needs besides the model file: downloaded once, shared by every model.
/// <see cref="RelativeFolder"/> is under <see cref="SpeechPaths.Root"/>.
/// </summary>
public sealed record EnginePack(
    string Id,
    PackKind Kind,
    string Name,
    string Description,
    string RelativeFolder,
    long DiskSize,
    IReadOnlyList<PackSource> Sources,
    long ExtraDownload = 0)
{
    /// <summary>The archives plus anything fetched during setup (pip packages for the Phonon engine).</summary>
    public long DownloadSize => Sources.Sum(s => s.Size) + ExtraDownload;

    public string Folder => Path.Combine(SpeechPaths.Root, RelativeFolder);
    public string MarkerPath => Path.Combine(Folder, "cinemaguard-pack.json");
}

public static class EnginePacks
{
    private const string NuGet = "https://api.nuget.org/v3-flatcontainer/";
    private const string Cuda = "https://developer.download.nvidia.com/compute/cuda/redist/";
    private const string Cudnn = "https://developer.download.nvidia.com/compute/cudnn/redist/";
    private const string Sherpa = "https://github.com/k2-fsa/sherpa-onnx/releases/download/v2.0.1/";

    public const string PythonVersion = "3.13.17";
    public const string TorchVersion = "2.15.0";
    public const string FermionVersion = "0.2.15";

    /// <summary>
    /// Phonon's private runtime: Python's embeddable build plus pip, CPU torch and fermion-research,
    /// installed into the app's own folder. It never touches a system Python.
    /// </summary>
    public static EnginePack PhononRuntime { get; } = new(
        "phonon-runtime", PackKind.PhononRuntime, "Phonon engine",
        "Private Python runtime, CPU", @"engines\phonon", 1_041_000_000,
        [
            new PackSource($"https://www.python.org/ftp/python/{PythonVersion}/python-{PythonVersion}-embed-amd64.zip",
                11_441_267, "9e35319bffedab8b36ad44d8f377cb4f1733bcec257a0b6d04d94f01486ddb38", PackFormat.Zip, "python", EntryPrefix: ""),
            // pip, torch and fermion come from their package indexes during setup (ExtraDownload is their rough size).
            new PackSource("https://bootstrap.pypa.io/get-pip.py", 0, null, PackFormat.File, "get-pip.py"),
        ],
        ExtraDownload: 290_000_000);

    public static EnginePack WhisperCuda13 { get; } = new(
        "whisper-cuda13", PackKind.WhisperGpu, "Whisper GPU pack",
        "CUDA 13 libraries", @"engines\whisper\runtimes\cuda\win-x64", 660_000_000,
        [
            new PackSource(NuGet + "whisper.net.runtime.cuda.windows/1.9.1/whisper.net.runtime.cuda.windows.1.9.1.nupkg",
                142_586_522, "3b765c9690c114825af1e9fd48e8f2e6f8b798c314d29e27e09640da479dfceb", PackFormat.Zip, EntryPrefix: "build/win-x64/"),
            new PackSource(Cuda + "cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-13.0.96-archive.zip",
                2_873_447, "a2ed875f9997aa24904fb70cc9db3acd9308433cde99bc8e63ec1271c9da31b4", PackFormat.Zip, Names: ["cudart64_13.dll"]),
            new PackSource(Cuda + "libcublas/windows-x86_64/libcublas-windows-x86_64-13.1.1.3-archive.zip",
                403_660_026, "d6dfb0f7ad09730bafa1fd948bc65472e28c8a6dc30b4a2b32191f2380f9282b", PackFormat.Zip, Names: ["cublas64_13.dll", "cublasLt64_13.dll"]),
        ]);

    public static EnginePack WhisperCuda12 { get; } = new(
        "whisper-cuda12", PackKind.WhisperGpu, "Whisper GPU pack",
        "CUDA 12 libraries", @"engines\whisper\runtimes\cuda12\win-x64", 1_600_000_000,
        [
            new PackSource(NuGet + "whisper.net.runtime.cuda12.windows/1.9.1/whisper.net.runtime.cuda12.windows.1.9.1.nupkg",
                250_058_371, "eb8d4233c202fe948cace50e3fa08fec7beead699db86a17df9820d03180a30c", PackFormat.Zip, EntryPrefix: "build/win-x64/"),
            new PackSource(Cuda + "cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-12.9.79-archive.zip",
                3_521_238, "179e9c43b0735ffe67207b3da556eb5a0c50f3047961882b7657d3b822d34ef8", PackFormat.Zip, Names: ["cudart64_12.dll"]),
            new PackSource(Cuda + "libcublas/windows-x86_64/libcublas-windows-x86_64-12.9.1.4-archive.zip",
                549_755_186, "d534d98b0b453a98914dbf3adf47d7e84b55037abf02f87466439e1dcef581ed", PackFormat.Zip, Names: ["cublas64_12.dll", "cublasLt64_12.dll"]),
        ]);

    public static EnginePack WhisperVulkan { get; } = new(
        "whisper-vulkan", PackKind.WhisperGpu, "Whisper GPU pack",
        "Vulkan library (any graphics card)", @"engines\whisper\runtimes\vulkan\win-x64", 60_000_000,
        [
            new PackSource(NuGet + "whisper.net.runtime.vulkan/1.9.1/whisper.net.runtime.vulkan.1.9.1.nupkg",
                36_867_136, "25fbea771d8aaffc4588a1d6aa1345a7bbc74912a8ada895d1695bbacfc1135a", PackFormat.Zip, EntryPrefix: "build/win-x64/"),
        ]);

    public static EnginePack ParakeetCuda13 { get; } = new(
        "parakeet-cuda13", PackKind.ParakeetGpu, "Parakeet GPU pack",
        "CUDA 13 + cuDNN", @"engines\parakeet-cuda13", 1_750_000_000,
        [
            new PackSource(Sherpa + "sherpa-onnx-v2.0.1-cuda-13.x-cudnn-9.x-onnxruntime1.28.2-win-x64-cuda.tar.bz2",
                480_541_871, null, PackFormat.TarBz2, Names: ["sherpa-onnx-c-api.dll", "onnxruntime.dll", "onnxruntime_providers_cuda.dll", "onnxruntime_providers_shared.dll"]),
            new PackSource(Cuda + "cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-13.0.96-archive.zip",
                2_873_447, "a2ed875f9997aa24904fb70cc9db3acd9308433cde99bc8e63ec1271c9da31b4", PackFormat.Zip, Names: ["cudart64_13.dll"]),
            new PackSource(Cuda + "libcublas/windows-x86_64/libcublas-windows-x86_64-13.1.1.3-archive.zip",
                403_660_026, "d6dfb0f7ad09730bafa1fd948bc65472e28c8a6dc30b4a2b32191f2380f9282b", PackFormat.Zip, Names: ["cublas64_13.dll", "cublasLt64_13.dll"]),
            new PackSource(Cuda + "libcufft/windows-x86_64/libcufft-windows-x86_64-12.0.0.61-archive.zip",
                212_026_245, "4f2695ea50f895618316ee32636f5d0e5e0bc330e74b3e0876bff002af9e1eb3", PackFormat.Zip, NamePrefix: "cufft64_"),
            new PackSource(Cudnn + "cudnn/windows-x86_64/cudnn-windows-x86_64-9.27.0.42_cuda13-archive.zip",
                1_330_803_635, "15595c7b52896262a0e590f02b3b3851c6a2c9046a72047526deaf59a9cbf7d5", PackFormat.Zip, NamePrefix: "cudnn"),
        ]);

    public static EnginePack ParakeetCuda12 { get; } = new(
        "parakeet-cuda12", PackKind.ParakeetGpu, "Parakeet GPU pack",
        "CUDA 12 + cuDNN", @"engines\parakeet-cuda12", 2_300_000_000,
        [
            new PackSource(Sherpa + "sherpa-onnx-v2.0.1-cuda-12.x-cudnn-9.x-onnxruntime1.28.2-win-x64-cuda.tar.bz2",
                597_231_017, null, PackFormat.TarBz2, Names: ["sherpa-onnx-c-api.dll", "onnxruntime.dll", "onnxruntime_providers_cuda.dll", "onnxruntime_providers_shared.dll"]),
            new PackSource(Cuda + "cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-12.9.79-archive.zip",
                3_521_238, "179e9c43b0735ffe67207b3da556eb5a0c50f3047961882b7657d3b822d34ef8", PackFormat.Zip, Names: ["cudart64_12.dll"]),
            new PackSource(Cuda + "libcublas/windows-x86_64/libcublas-windows-x86_64-12.9.1.4-archive.zip",
                549_755_186, "d534d98b0b453a98914dbf3adf47d7e84b55037abf02f87466439e1dcef581ed", PackFormat.Zip, Names: ["cublas64_12.dll", "cublasLt64_12.dll"]),
            new PackSource(Cuda + "libcufft/windows-x86_64/libcufft-windows-x86_64-11.4.1.4-archive.zip",
                198_361_265, "f26f80bb9abff3269c548e1559e8c2b4ba58ccb8acc6095bbc6404fc962d4b80", PackFormat.Zip, NamePrefix: "cufft64_"),
            new PackSource(Cudnn + "cudnn/windows-x86_64/cudnn-windows-x86_64-9.27.0.42_cuda12-archive.zip",
                1_924_314_965, "93949680d6499ad0f17fc1abb131825c9dd41b460844513f608e840e0c8ce72b", PackFormat.Zip, NamePrefix: "cudnn"),
        ]);

    public static IReadOnlyList<EnginePack> All { get; } =
        [PhononRuntime, WhisperCuda13, WhisperCuda12, WhisperVulkan, ParakeetCuda13, ParakeetCuda12];

    /// <summary>
    /// Engine packs Cinema Guard no longer downloads but that someone may still have on disk. They stay
    /// deletable on the Models page so the space can be reclaimed; a Parakeet model never needs them.
    /// </summary>
    public static IReadOnlyList<EnginePack> Legacy { get; } =
        [ParakeetCuda13, ParakeetCuda12];

    public static EnginePack? Find(string? id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>The Whisper GPU pack this PC should use, or null when Whisper runs on the CPU.</summary>
    public static EnginePack? WhisperFor(HardwareInfo hardware) => hardware.BestWhisperRuntime switch
    {
        WhisperRuntime.Cuda13 => WhisperCuda13,
        WhisperRuntime.Cuda12 => WhisperCuda12,
        WhisperRuntime.Vulkan => WhisperVulkan,
        _ => null,
    };

    public static EnginePack? ParakeetFor(HardwareInfo hardware) => hardware.CudaMajor switch
    {
        13 => ParakeetCuda13,
        12 => ParakeetCuda12,
        _ => null,
    };

    public static WhisperRuntime RuntimeOf(EnginePack pack) => pack.Id switch
    {
        "whisper-cuda13" => WhisperRuntime.Cuda13,
        "whisper-cuda12" => WhisperRuntime.Cuda12,
        "whisper-vulkan" => WhisperRuntime.Vulkan,
        _ => WhisperRuntime.Cpu,
    };
}
