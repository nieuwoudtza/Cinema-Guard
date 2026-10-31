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
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cancellation = new();

    public bool IsFirstInstance { get; }

    public event Action<IReadOnlyList<string>>? PathsReceived;

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

                if (paths.Count > 0)
                {
                    Log.Info($"Received {paths.Count} path(s) from another instance.");
                    PathsReceived?.Invoke(paths);
                }
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

    public void SendToRunningInstance(IEnumerable<string> paths)
    {
        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(".", _pipeName, System.IO.Pipes.PipeDirection.Out);
            client.Connect(3000);

            using var writer = new StreamWriter(client);
            writer.Write(string.Join('\n', paths));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not hand the paths to the running instance: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
        _mutex.Dispose();
    }
}
