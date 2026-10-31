using CinemaGuard.Core.Logging;

namespace CinemaGuard.Core.Tools;

public static class RecycleBin
{
    /// <summary>Sends a file to the Recycle Bin. Never a permanent delete; never throws.</summary>
    public static bool Send(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

            Log.Info($"Sent to the Recycle Bin: {path}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not recycle {path}: {ex.Message}");
            return false;
        }
    }
}

/// <summary>Stops the machine sleeping while a queue is running.</summary>
public static class KeepAwake
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    public static void Start()
    {
        try { SetThreadExecutionState(EsContinuous | EsSystemRequired); }
        catch (Exception ex) { Log.Warn($"Could not request keep-awake: {ex.Message}"); }
    }

    public static void Stop()
    {
        try { SetThreadExecutionState(EsContinuous); }
        catch (Exception ex) { Log.Warn($"Could not release keep-awake: {ex.Message}"); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint flags);
}
