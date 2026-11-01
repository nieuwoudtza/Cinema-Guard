using System;
using System.Runtime.InteropServices;
using CinemaGuard.Core.Pipeline;
using CinemaGuard.Core.Speech;
using Velopack;

namespace Cinema_Guard;

/// <summary>Replaces the XAML-generated Main so Velopack runs before any window or headless work.</summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // `Cinema Guard.exe --engine-host`: the Whisper/Parakeet helper process. No window, no Velopack, no XAML.
        if (args.Length > 0 && args[0] == EngineHostServer.Argument)
        {
            Environment.Exit(EngineHostServer.Run());
            return;
        }

        var launch = LaunchArguments.Parse(args);

        // `--help`: print the options and exit, before Velopack could install an update under the caller.
        if (launch.Help)
        {
            PrintHelp();
            Environment.Exit(0);
            return;
        }

        // Install, update and uninstall hooks run here and exit the process; a normal launch returns at once.
        // A downloaded update is installed on startup too, which would end a headless run with no result,
        // or replace the folder another headless run is working from; those launches leave it for later.
        var deferUpdate = launch.Headless || HeadlessRuns.AnyActive();
        VelopackApp.Build().SetAutoApplyOnStartup(!deferUpdate).Run();

        XamlGeneratedProgram.XamlGeneratedMain();
    }

    /// <summary>
    /// The app is a window program, so it has no console of its own: the text goes to a redirected
    /// stdout as is, else to the console it was started from, else (Explorer, a shortcut) a message box.
    /// </summary>
    private static void PrintHelp()
    {
        var text = LaunchArguments.HelpText;

        if (Console.IsOutputRedirected || AttachConsole(AttachParentProcess))
        {
            // A blank line first: cmd has already printed its next prompt by the time this arrives.
            Console.Out.Write(Environment.NewLine + text + Environment.NewLine);
            Console.Out.Flush();
            return;
        }

        MessageBox(IntPtr.Zero, text, "Cinema Guard", 0x40 /* MB_ICONINFORMATION */);
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
