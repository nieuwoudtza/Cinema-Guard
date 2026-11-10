using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CinemaGuard.Core.Logging;

namespace Cinema_Guard.Services;

/// <summary>
/// Keeps the SendTo shortcut working like the old app: the first instance owns a named pipe,
/// later launches hand their paths over and exit.
/// </summary>
public sealed class InstanceCoordinator : IDisposable
{
    /// <summary>The window's mutex and pipe name; one window per Windows session.</summary>
    public const string WindowKey = "CinemaGuard2.Instance";

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cancellation = new();

    public bool IsFirstInstance { get; }

    /// <summary>Another launch handed over: its paths, none when the app was simply opened again.</summary>
    public event Action<IReadOnlyList<string>>? LaunchReceived;

    public InstanceCoordinator(string key)
    {
        _mutex = new Mutex(initiallyOwned: true, @"Local\" + key, out var created);
        IsFirstInstance = created;
        _pipeName = key + ".pipe";

        if (IsFirstInstance) _ = Task.Run(ServerLoopAsync);
    }

    private async Task ServerLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                using var server = new System.IO.Pipes.NamedPipeServerStream(
                    _pipeName,
                    System.IO.Pipes.PipeDirection.In,
                    1,
                    System.IO.Pipes.PipeTransmissionMode.Byte,
                    System.IO.Pipes.PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_cancellation.Token);

                using var reader = new StreamReader(server);
                var content = await reader.ReadToEndAsync();

                var paths = content
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();

                // Even with no paths: the window must come forward, or opening the app again looks
                // like it did nothing while this copy runs on (minimised, or on another desktop).
                Log.Info($"Another launch handed over {paths.Count} path(s).");
                LaunchReceived?.Invoke(paths);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Instance pipe error: {ex.Message}");
                await Task.Delay(500);
            }
        }
    }

    public void SendToRunningInstance(IEnumerable<string> paths) => TrySend(_pipeName, paths);

    /// <summary>
    /// Hands <paramref name="lines"/> (paths, and options such as --review) to the open window, from a process
    /// that is not one itself (a -start run). False when no window is open or it did not answer.
    /// </summary>
    public static bool SendToWindow(string key, IEnumerable<string> lines)
    {
        if (!Mutex.TryOpenExisting(@"Local\" + key, out var mutex)) return false;

        mutex.Dispose();
        return TrySend(key + ".pipe", lines);
    }

    private static bool TrySend(string pipeName, IEnumerable<string> lines)
    {
        try
        {
            // This launch is what the user just clicked, so it may take the foreground; pass that on,
            // else Windows only flashes the running copy's taskbar button.
            AllowSetForegroundWindow(AnyProcess);

            using var client = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.Out);
            client.Connect(3000);

            using var writer = new StreamWriter(client);
            writer.Write(string.Join('\n', lines));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not hand the paths to the running instance: {ex.Message}");
            return false;
        }
    }

    private const int AnyProcess = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    public void Dispose()
    {
        _cancellation.Cancel();
        try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
        _mutex.Dispose();
    }
}
