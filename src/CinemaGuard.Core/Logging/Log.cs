using System.Diagnostics;
using System.Text;

namespace CinemaGuard.Core.Logging;

/// <summary>
/// Process-wide file log. Every failure in the app ends up here with its stack trace;
/// there are no silent swallows anywhere else.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinemaGuard", "logs");
    private static string? _file;

    public static string Directory => _directory;

    /// <summary>Raised on every line, for the in-app log view. Handlers must be quick.</summary>
    public static event Action<string>? LineWritten;

    public static void Init(string? directory = null)
    {
        if (!string.IsNullOrWhiteSpace(directory)) _directory = directory;
        System.IO.Directory.CreateDirectory(_directory);
        _file = Path.Combine(_directory, $"cg-{DateTime.Now:yyyyMMdd}.log");
        Info($"--- Cinema Guard {CoreInfo.Version} started ---");
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Debug(string message) => Write("DEBUG", message, null);
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (exception is not null) line += Environment.NewLine + exception;

        lock (Gate)
        {
            try
            {
                _file ??= Path.Combine(_directory, $"cg-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // A logger that cannot write must not take the app down.
            }
        }

        try { LineWritten?.Invoke(line); } catch { /* listener problem */ }
        System.Diagnostics.Debug.WriteLine(line);
    }
}
