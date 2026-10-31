using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using CinemaGuard.Core.Logging;
using Microsoft.Win32;

namespace CinemaGuard.Core.Speech;

/// <summary>The Whisper.net native runtime to load, best first.</summary>
public enum WhisperRuntime { Cuda13, Cuda12, Vulkan, Cpu, CpuNoAvx }

/// <summary>
/// What this PC can run, found on first start and on Re-detect, and saved in the settings.
/// Every GPU option on the Models page is decided from this, never from guesses at run time.
/// </summary>
public sealed class HardwareInfo
{
    public string? NvidiaName { get; set; }
    public long NvidiaMemoryBytes { get; set; }

    /// <summary>The newest CUDA version the driver supports, as cuDriverGetVersion reports it (13000 = 13.0).</summary>
    public int CudaDriverVersion { get; set; }

    public int ComputeMajor { get; set; }
    public int ComputeMinor { get; set; }

    /// <summary>Every display adapter Windows lists (any vendor), for the Vulkan path and the summary.</summary>
    public List<string> Adapters { get; set; } = [];

    public bool HasVulkan { get; set; }
    public bool Avx2 { get; set; }
    public int LogicalCores { get; set; }
    public DateTimeOffset DetectedAt { get; set; }

    public bool HasNvidia => NvidiaName is not null && CudaDriverVersion > 0;

    /// <summary>CUDA 13 dropped GPUs older than Turing (compute 7.5); those keep CUDA 12.</summary>
    public bool CanRunCuda13 => HasNvidia && CudaDriverVersion >= 13000 && (ComputeMajor > 7 || (ComputeMajor == 7 && ComputeMinor >= 5));

    public bool CanRunCuda12 => HasNvidia && CudaDriverVersion >= 12040;

    public int CudaMajor => CanRunCuda13 ? 13 : CanRunCuda12 ? 12 : 0;

    /// <summary>A real GPU other than the basic display driver, for Vulkan.</summary>
    public bool HasVulkanGpu => HasVulkan && Adapters.Any(a => !a.Contains("Basic", StringComparison.OrdinalIgnoreCase) &&
                                                             !a.Contains("Remote", StringComparison.OrdinalIgnoreCase) &&
                                                             !a.Contains("Virtual", StringComparison.OrdinalIgnoreCase));

    public WhisperRuntime BestWhisperRuntime =>
        CanRunCuda13 ? WhisperRuntime.Cuda13 :
        CanRunCuda12 ? WhisperRuntime.Cuda12 :
        HasVulkanGpu ? WhisperRuntime.Vulkan :
        Avx2 ? WhisperRuntime.Cpu : WhisperRuntime.CpuNoAvx;

    public bool WhisperUsesGpu => BestWhisperRuntime is WhisperRuntime.Cuda13 or WhisperRuntime.Cuda12 or WhisperRuntime.Vulkan;

    /// <summary>Parakeet's GPU build is CUDA-only.</summary>
    public bool CanRunParakeetGpu => CudaMajor >= 12;

    public string GpuName => NvidiaName ?? Adapters.FirstOrDefault(a => !a.Contains("Basic", StringComparison.OrdinalIgnoreCase)) ?? "No graphics card found";

    public string Summary
    {
        get
        {
            var parts = new List<string>();

            if (HasNvidia)
            {
                parts.Add(NvidiaMemoryBytes > 0 ? $"{NvidiaName}, {Math.Round(NvidiaMemoryBytes / 1073741824.0)} GB" : NvidiaName!);
                parts.Add($"driver supports CUDA {CudaDriverVersion / 1000}.{CudaDriverVersion % 1000 / 10}");
            }
            else
            {
                parts.Add(GpuName);
                if (HasVulkanGpu) parts.Add("Vulkan");
            }

            parts.Add(Avx2 ? "AVX2" : "no AVX2");
            if (LogicalCores > 0) parts.Add($"{LogicalCores} CPU threads");
            return string.Join(" · ", parts);
        }
    }

    public static string WhisperRuntimeName(WhisperRuntime runtime) => runtime switch
    {
        WhisperRuntime.Cuda13 => "GPU (CUDA 13)",
        WhisperRuntime.Cuda12 => "GPU (CUDA 12)",
        WhisperRuntime.Vulkan => "GPU (Vulkan)",
        WhisperRuntime.CpuNoAvx => "CPU (no AVX)",
        _ => "CPU",
    };
}

public static class HardwareDetector
{
    public static HardwareInfo Detect()
    {
        var info = new HardwareInfo
        {
            Avx2 = Avx.IsSupported && Avx2.IsSupported && Fma.IsSupported,
            LogicalCores = Environment.ProcessorCount,
            DetectedAt = DateTimeOffset.Now,
        };

        try { info.Adapters = ReadAdapters(); }
        catch (Exception ex) { Log.Warn($"Could not list the display adapters: {ex.Message}"); }

        info.HasVulkan = File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll"));

        try { ReadCuda(info); }
        catch (Exception ex) { Log.Warn($"Could not query the NVIDIA driver: {ex.Message}"); }

        Log.Info($"Hardware: {info.Summary}");
        return info;
    }

    private static List<string> ReadAdapters()
    {
        var result = new List<string>();
        using var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (display is null) return result;

        foreach (var name in display.GetSubKeyNames())
        {
            if (!int.TryParse(name, out _)) continue;

            try
            {
                using var key = display.OpenSubKey(name);
                if (key?.GetValue("DriverDesc") is string description && !result.Contains(description))
                    result.Add(description);
            }
            catch (System.Security.SecurityException)
            {
                // Some adapter keys are locked down; they are never the GPU we want.
            }
        }

        return result;
    }

    /// <summary>nvcuda.dll ships with every NVIDIA driver; no toolkit is needed to ask it what it supports.</summary>
    private static void ReadCuda(HardwareInfo info)
    {
        if (!NativeLibrary.TryLoad("nvcuda.dll", out var handle)) return;

        try
        {
            var getVersion = Get<CuIntOut>(handle, "cuDriverGetVersion");
            var init = Get<CuInit>(handle, "cuInit");
            var getCount = Get<CuIntOut>(handle, "cuDeviceGetCount");
            var getDevice = Get<CuDeviceGet>(handle, "cuDeviceGet");
            var getName = Get<CuDeviceGetName>(handle, "cuDeviceGetName");
            var getMemory = Get<CuDeviceTotalMem>(handle, "cuDeviceTotalMem_v2");
            var getAttribute = Get<CuDeviceGetAttribute>(handle, "cuDeviceGetAttribute");

            if (getVersion(out var version) != 0 || version <= 0) return;
            if (init(0) != 0 || getCount(out var count) != 0 || count < 1) return;

            // The device with the most memory is the one worth using.
            for (var ordinal = 0; ordinal < count; ordinal++)
            {
                if (getDevice(out var device, ordinal) != 0) continue;

                var buffer = new byte[256];
                var name = getName(buffer, buffer.Length, device) == 0
                    ? System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0').Trim()
                    : "NVIDIA GPU";

                getMemory(out var memory, device);
                getAttribute(out var major, 75, device);
                getAttribute(out var minor, 76, device);

                if ((long)memory <= info.NvidiaMemoryBytes) continue;

                info.NvidiaName = name.StartsWith("NVIDIA", StringComparison.OrdinalIgnoreCase) ? name : "NVIDIA " + name;
                info.NvidiaMemoryBytes = (long)memory;
                info.ComputeMajor = major;
                info.ComputeMinor = minor;
                info.CudaDriverVersion = version;
            }
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    private static T Get<T>(IntPtr library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CuInit(uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CuIntOut(out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CuDeviceGet(out int device, int ordinal);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CuDeviceGetName(byte[] name, int length, int device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CuDeviceTotalMem(out nuint bytes, int device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CuDeviceGetAttribute(out int value, int attribute, int device);
}
