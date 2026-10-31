using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;

namespace CinemaGuard.Installer;

/// <summary>Everything the setup does besides drawing the window.</summary>
internal static class Installer
{
    /// <summary>Must match --packId in publish.ps1: it names the uninstall key Velopack writes.</summary>
    public const string PackId = "WaveBit.CinemaGuard";
    public const string AppTitle = "Cinema Guard";
    public const string AppExe = "Cinema Guard.exe";

    /// <summary>Windows 10 1809, the oldest build the app's Windows App SDK supports.</summary>
    public const int MinimumBuild = 17763;

    private const int ErrorCancelled = 1223;

    public static string Version
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var plus = version.IndexOf('+');
            return plus < 0 ? version : version.Substring(0, plus);
        }
    }

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppTitle);

    /// <summary>The folder of an earlier install by this user, from Velopack's uninstall key.</summary>
    public static string? ExistingFolder()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{PackId}");
        var folder = key?.GetValue("InstallLocation") as string;
        return !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, "Update.exe")) ? folder : null;
    }

    public static string AppPath(string folder) => Path.Combine(folder, "current", AppExe);

    /// <summary>Null when this PC can run the app, otherwise why not.</summary>
    public static string? Unsupported()
    {
        if (!Environment.Is64BitOperatingSystem)
            return "Cinema Guard needs 64-bit Windows.";

        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (int.TryParse(key?.GetValue("CurrentBuildNumber") as string, out var build) && build < MinimumBuild)
            return "Cinema Guard needs Windows 10 version 1809 or newer. Please run Windows Update first.";

        return null;
    }

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>
    /// True when this user cannot write the folder (Program Files and the like). Probes with a throwaway
    /// folder and file inside the folder, or inside its nearest existing parent, and removes them again.
    /// </summary>
    public static bool NeedsElevation(string folder)
    {
        if (IsElevated) return false;

        var existing = folder;
        while (existing is not null && !Directory.Exists(existing)) existing = Path.GetDirectoryName(existing);
        if (existing is null) return false;

        var probe = Path.Combine(existing, $".cg-write-test-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(probe);
            File.WriteAllText(Path.Combine(probe, "probe"), "");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            // A bad drive or path: let the install report it instead of asking for admin rights.
            return false;
        }
        finally
        {
            try { if (Directory.Exists(probe)) Directory.Delete(probe, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A folder that already holds other files: installing there could mix or remove them.</summary>
    public static bool IsOccupied(string folder) =>
        Directory.Exists(folder) &&
        !File.Exists(Path.Combine(folder, "Update.exe")) &&
        Directory.EnumerateFileSystemEntries(folder).Any();

    public static bool SameFolder(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static bool IsAppRunning() => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExe)).Length > 0;

    /// <summary>Removes an install in another folder, so one copy owns the uninstall entry and updates.</summary>
    public static void UninstallOld(string folder)
    {
        RunAndWait(Path.Combine(folder, "Update.exe"), "--uninstall --silent", elevate: NeedsElevation(folder));
    }

    /// <summary>Unpacks the embedded Velopack setup and installs into the folder. Throws with a readable message.</summary>
    public static void Install(string folder)
    {
        var work = Path.Combine(Path.GetTempPath(), $"CinemaGuardSetup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);

        try
        {
            var setup = Path.Combine(work, "setup.exe");
            var log = Path.Combine(work, "setup.log");

            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.exe")
                ?? throw new InvalidOperationException("This setup file is incomplete. Download it again."))
            using (var file = File.Create(setup))
            {
                payload.CopyTo(file);
            }

            var exitCode = RunAndWait(setup, $"--silent --installto {Quote(folder)} --log {Quote(log)}", elevate: false);

            if (exitCode != 0 || !File.Exists(AppPath(folder)))
                throw new InvalidOperationException($"Setup failed (code {exitCode}).{Environment.NewLine}{Tail(log)}");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Runs this setup again as administrator to install into the folder; returns its error, or null.</summary>
    public static string? InstallElevated(string folder)
    {
        var result = Path.Combine(Path.GetTempPath(), $"CinemaGuardSetup-{Guid.NewGuid():N}.txt");

        try
        {
            var self = Process.GetCurrentProcess().MainModule!.FileName;
            var exitCode = RunAndWait(self, $"{Program.ElevatedSwitch} {Quote(folder)} {Quote(result)}", elevate: true);

            if (exitCode == 0) return null;

            return File.Exists(result) ? File.ReadAllText(result) : $"The administrator setup failed (code {exitCode}).";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return "Administrator permission was not given, so nothing was installed. Choose a folder in your own profile to install without it.";
        }
        finally
        {
            try { File.Delete(result); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void Launch(string folder)
    {
        var app = AppPath(folder);
        Process.Start(new ProcessStartInfo(app) { WorkingDirectory = Path.GetDirectoryName(app), UseShellExecute = true });
    }

    public static string DesktopShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppTitle + ".lnk");

    /// <summary>
    /// Velopack adds the Start menu shortcut, but an administrator install puts it in the administrator's
    /// profile. Making sure here gives the person running setup one as well.
    /// </summary>
    public static string StartMenuShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppTitle + ".lnk");

    public static void CreateShortcut(string shortcutPath, string folder)
    {
        var app = AppPath(folder);
        ShellLink.Create(shortcutPath, app, Path.GetDirectoryName(app)!, $"{AppTitle} - mute swearing in your videos");
    }

    private static int RunAndWait(string exe, string arguments, bool elevate)
    {
        var info = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = elevate,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        if (elevate) info.Verb = "runas";

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(exe)}.");
        process.WaitForExit();
        return process.ExitCode;
    }

    // A trailing backslash would escape the closing quote ("C:\"), so it is doubled.
    private static string Quote(string value) => "\"" + (value.EndsWith("\\") ? value + "\\" : value) + "\"";

    private static string Tail(string log)
    {
        try
        {
            if (!File.Exists(log)) return "";
            var lines = File.ReadAllLines(log);
            return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - 6)));
        }
        catch (IOException)
        {
            return "";
        }
    }
}
