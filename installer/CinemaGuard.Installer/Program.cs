using System;
using System.IO;
using System.Windows.Forms;

namespace CinemaGuard.Installer;

internal static class Program
{
    /// <summary>`setup --elevated-install "folder" "result file"`: the administrator half of an install.</summary>
    public const string ElevatedSwitch = "--elevated-install";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == ElevatedSwitch) return InstallAsAdministrator(args[1], args[2]);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm());
        return 0;
    }

    /// <summary>
    /// Only the file copy runs as administrator. Shortcuts and the first launch stay with the person who
    /// started setup, so they land in their profile and the app does not start elevated.
    /// </summary>
    private static int InstallAsAdministrator(string folder, string resultFile)
    {
        try
        {
            Installer.Install(folder);
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(resultFile, ex.Message); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return 1;
        }
    }
}
