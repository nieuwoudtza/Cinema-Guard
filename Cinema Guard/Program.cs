using System;
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

        // Install, update and uninstall hooks run here and exit the process; a normal launch returns at once.
        VelopackApp.Build().Run();

        XamlGeneratedProgram.XamlGeneratedMain();
    }
}
