using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CinemaGuard.Core.Logging;
using CinemaGuard.Core.Phonon;

namespace CinemaGuard.Core.Speech;

/// <summary>One transcription for the engine host: which engine, which files, which native runtime.</summary>
public sealed class HostRequest
{
    public string Engine { get; set; } = "whisper";
    public string Model { get; set; } = string.Empty;

    /// <summary>"cpu", "cpu-noavx", "cuda13", "cuda12", "vulkan" (Whisper) or "cpu", "cuda" (Parakeet).</summary>
    public string Runtime { get; set; } = "cpu";

    /// <summary>The folder holding the GPU pack's native libraries.</summary>
    public string? RuntimeFolder { get; set; }

    public string Wav { get; set; } = string.Empty;
    public string? Language { get; set; }
    public string? Prompt { get; set; }
    public string? HeadsPreset { get; set; }
    public int Threads { get; set; }

    /// <summary>The key a host process is started for: native runtimes cannot be swapped inside one process.</summary>
    [JsonIgnore]
    public string ProcessKey => $"{Engine}:{Runtime}:{RuntimeFolder}";
}

public sealed class HostMessage
{
    public string Type { get; set; } = "log";
    public double Progress { get; set; }
    public string? Text { get; set; }
    public PhononResult? Result { get; set; }
}

internal static class HostJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// The engine host side: `Cinema Guard.exe --engine-host` reads one JSON request per line on stdin and
/// answers with progress/log lines and a result (or error) line on stdout. The model stays loaded
/// between requests, so a queue loads it once instead of once per file and per channel.
/// </summary>
public static class EngineHostServer
{
    public const string Argument = "--engine-host";

    public static int Run()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        var gate = new object();

        void Send(HostMessage message)
        {
            var line = JsonSerializer.Serialize(message, HostJson.Options);
            lock (gate) stdout.WriteLine(line);
        }

        WhisperHostEngine? whisper = null;
        ParakeetHostEngine? parakeet = null;

        try
        {
            Send(new HostMessage { Type = "ready" });

            while (stdin.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var request = JsonSerializer.Deserialize<HostRequest>(line, HostJson.Options)
                                  ?? throw new InvalidDataException("Empty request.");

                    var stopwatch = Stopwatch.StartNew();
                    PhononResult result;

                    if (request.Engine == "parakeet")
                    {
                        parakeet ??= new ParakeetHostEngine(request, text => Send(new HostMessage { Type = "log", Text = text }));
                        result = parakeet.Transcribe(request, value => Send(new HostMessage { Type = "progress", Progress = value }));
                    }
                    else
                    {
                        whisper ??= new WhisperHostEngine(request, text => Send(new HostMessage { Type = "log", Text = text }));
                        result = whisper.Transcribe(request, value => Send(new HostMessage { Type = "progress", Progress = value }));
                    }

                    Send(new HostMessage { Type = "log", Text = $"Transcribed in {stopwatch.Elapsed.TotalSeconds:0.0} s" });
                    Send(new HostMessage { Type = "result", Result = result });
                }
                catch (Exception ex)
                {
                    Send(new HostMessage { Type = "error", Text = ex.Message });
                }
            }

            return 0;
        }
        finally
        {
            whisper?.Dispose();
            parakeet?.Dispose();
        }
    }
}

/// <summary>
/// The app side of the engine host. Starts the host on first use, restarts it when the native runtime
/// changes, and kills it on cancel (which also frees the GPU). A Windows job object ties its life to
/// the app, so it never outlives a crash.
/// </summary>
public sealed class EngineHostClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Process? _process;
    private string? _processKey;
    private readonly string _hostPath;
    private readonly IReadOnlyList<string> _hostArguments;

    /// <param name="hostPath">The executable that understands <see cref="EngineHostServer.Argument"/>; the app itself by default.</param>
    public EngineHostClient(string? hostPath = null, IReadOnlyList<string>? hostArguments = null)
    {
        _hostPath = hostPath ?? Environment.ProcessPath ?? throw new InvalidOperationException("The app's own path is unknown.");
        _hostArguments = hostArguments ?? [EngineHostServer.Argument];
    }

    public async Task<PhononResult> TranscribeAsync(HostRequest request, Action<double>? progress, Action<string>? log, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            if (_process is null || _process.HasExited || _processKey != request.ProcessKey)
            {
                Stop();
                Start(request);
            }

            var process = _process!;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, HostJson.Options).AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);

            await using var registration = cancellationToken.Register(Stop);

            while (true)
            {
                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (line is null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var exitCode = SafeExitCode(process);
                    Stop();
                    throw new PhononException($"The speech engine stopped unexpectedly (exit code {exitCode}). {LastError()}".Trim());
                }

                HostMessage? message;
                try { message = JsonSerializer.Deserialize<HostMessage>(line, HostJson.Options); }
                catch (JsonException) { log?.Invoke(line); continue; }

                switch (message?.Type)
                {
                    case "progress":
                        progress?.Invoke(message.Progress);
                        break;
                    case "log":
                        if (message.Text is not null) { log?.Invoke(message.Text); Log.Info($"Engine: {message.Text}"); }
                        break;
                    case "result":
                        return message.Result ?? new PhononResult();
                    case "error":
                        throw new PhononException(message.Text ?? "The speech engine failed.");
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private readonly StringBuilder _stderr = new();

    private void Start(HostRequest request)
    {
        var startInfo = new ProcessStartInfo(_hostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };

        foreach (var argument in _hostArguments) startInfo.ArgumentList.Add(argument);

        // The GPU pack's CUDA libraries resolve from PATH as well as from next to the loader.
        if (!string.IsNullOrEmpty(request.RuntimeFolder))
            startInfo.Environment["PATH"] = request.RuntimeFolder + ";" + Environment.GetEnvironmentVariable("PATH");

        lock (_stderr) _stderr.Clear();

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderr)
            {
                _stderr.AppendLine(e.Data);
                if (_stderr.Length > 8000) _stderr.Remove(0, _stderr.Length - 8000);
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        JobObject.Assign(process);

        // The host says "ready" once it is up; anything else first is a broken start.
        var first = process.StandardOutput.ReadLine();
        if (first is null || !first.Contains("\"ready\"", StringComparison.Ordinal))
        {
            try { process.Kill(true); } catch { /* gone */ }
            throw new PhononException($"The speech engine did not start. {LastError()}".Trim());
        }

        _process = process;
        _processKey = request.ProcessKey;
        Log.Info($"Engine host started for {request.ProcessKey} (pid {process.Id}).");
    }

    private string LastError()
    {
        lock (_stderr)
        {
            var text = _stderr.ToString().Trim();
            return text.Length <= 400 ? text : "..." + text[^400..];
        }
    }

    private static int SafeExitCode(Process process)
    {
        try { process.WaitForExit(2000); return process.HasExited ? process.ExitCode : -1; }
        catch { return -1; }
    }

    /// <summary>Ends the host (and frees its memory and VRAM). The next request starts a new one.</summary>
    public void Stop()
    {
        var process = _process;
        _process = null;
        _processKey = null;
        if (process is null) return;

        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone.
        }

        process.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Kills child processes when the app exits, even if it crashes.</summary>
internal static class JobObject
{
    private static readonly Lazy<IntPtr> Handle = new(Create);

    public static void Assign(Process process)
    {
        try
        {
            if (Handle.Value != IntPtr.Zero) AssignProcessToJobObject(Handle.Value, process.Handle);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not tie the engine to the app's lifetime: {ex.Message}");
        }
    }

    private static IntPtr Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;

        var info = new JobObjectExtendedLimitInformation { BasicLimitInformation = { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(length);

        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            SetInformationJobObject(job, 9, pointer, (uint)length); // JobObjectExtendedLimitInformation
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
