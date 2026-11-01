using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CinemaGuard.Core.Tools;

namespace Cinema_Guard.Services;

/// <summary>The SendTo shortcut: created and removed through the Windows shell, no COM interop in-process.</summary>
public static class ShellIntegration
{
    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "SendTo", "Cinema Guard.lnk");

    public static string ExecutablePath => Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "Cinema Guard.exe");

    public static async Task<(bool Ok, string Message)> CreateShortcutAsync(IProcessRunner runner)
    {
        var shortcut = ShortcutPath.Replace("'", "''");
        var executable = ExecutablePath.Replace("'", "''");
        var directory = AppContext.BaseDirectory.Replace("'", "''");

        var script =
            "$shell = New-Object -ComObject WScript.Shell; " +
            $"$shortcut = $shell.CreateShortcut('{shortcut}'); " +
            $"$shortcut.TargetPath = '{executable}'; " +
            $"$shortcut.WorkingDirectory = '{directory}'; " +
            $"$shortcut.IconLocation = '{executable}'; " +
            "$shortcut.Description = 'Open media in Cinema Guard'; " +
            "$shortcut.Save()";

        var result = await runner.RunAsync(new ProcessSpec("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script]));

        return result.Success
            ? (true, "SendTo shortcut created.")
            : (false, result.StdErr.Trim() is { Length: > 0 } error ? error : "Could not create the shortcut.");
    }

    public static (bool Ok, string Message) RemoveShortcut()
    {
        try
        {
            if (!File.Exists(ShortcutPath)) return (false, "There is no shortcut to remove.");

            File.Delete(ShortcutPath);
            return (true, "SendTo shortcut removed.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
