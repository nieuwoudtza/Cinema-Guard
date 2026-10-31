using System.Diagnostics;
using System.Text;

namespace CinemaGuard.Core.Tools;

public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    public string CommandLine => Tools.CommandLine.Join(Arguments);
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool Cancelled)
{
    public bool Success => !Cancelled && ExitCode == 0;
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        ProcessSpec spec,
        Action<string>? onStandardOutput = null,
        Action<string>? onStandardError = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The single place external programs are started: argument lists (never concatenated strings),
/// both streams drained asynchronously, and cancellation that kills the whole process tree.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        ProcessSpec spec,
        Action<string>? onStandardOutput = null,
        Action<string>? onStandardError = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return new ProcessResult(-1, string.Empty, "Cancelled", true);

        var output = new StringBuilder();
        var error = new StringBuilder();

        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = spec.WorkingDirectory ?? Environment.CurrentDirectory,
            Arguments = Tools.CommandLine.Join(spec.Arguments),
        };

        if (spec.Environment is not null)
        {
            foreach (var pair in spec.Environment) startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(process.ExitCode);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            output.AppendLine(e.Data);
            SafeInvoke(onStandardOutput, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            error.AppendLine(e.Data);
            SafeInvoke(onStandardError, e.Data);
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.Message, false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone, or access denied: nothing useful to do here.
            }
        });

        var exitCode = await exited.Task.ConfigureAwait(false);

        // Flush the async readers.
        try { process.WaitForExit(); } catch { /* exited */ }

        return new ProcessResult(exitCode, output.ToString(), error.ToString(), cancellationToken.IsCancellationRequested);
    }

    private static void SafeInvoke(Action<string>? callback, string line)
    {
        try { callback?.Invoke(line); } catch { /* a callback must not break the read loop */ }
    }
}

/// <summary>Windows command-line quoting (the rules CreateProcess expects).</summary>
public static class CommandLine
{
    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    public static string Quote(string argument)
    {
        if (argument.Length == 0) return "\"\"";
        if (argument.IndexOfAny([' ', '\t', '"', '\n', '\r']) < 0) return argument;

        var builder = new StringBuilder("\"");
        var backslashes = 0;

        foreach (var character in argument)
        {
            switch (character)
            {
                case '\\':
                    backslashes++;
                    break;
                case '"':
                    builder.Append('\\', backslashes * 2 + 1).Append('"');
                    backslashes = 0;
                    break;
                default:
                    if (backslashes > 0) builder.Append('\\', backslashes);
                    backslashes = 0;
                    builder.Append(character);
                    break;
            }
        }

        if (backslashes > 0) builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }
}
