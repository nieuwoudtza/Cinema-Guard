using CinemaGuard.Core.Logging;

namespace CinemaGuard.Core.Tools;

public static class RecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    /// <summary>
    /// Whether the shell may show its error dialog when a file cannot be recycled. A headless run turns
    /// this off: nobody is there to close the dialog, and the run would wait on it forever.
    /// </summary>
    public static bool ShowErrorDialogs { get; set; } = true;

    /// <summary>Sends a file to the Recycle Bin. Never a permanent delete; never throws.</summary>
    public static bool Send(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        try
        {
            var operation = new ShFileOpStruct
            {
                wFunc = FoDelete,
                pFrom = Path.GetFullPath(path) + '\0', // the list ends with a second null, which the marshaller adds
                fFlags = (ushort)(FofSilent | FofNoConfirmation | FofAllowUndo | (ShowErrorDialogs ? 0 : FofNoErrorUi)),
            };

            var result = SHFileOperation(ref operation);

            if (result != 0 || operation.fAnyOperationsAborted || File.Exists(path))
            {
                Log.Warn($"Could not recycle {path}: shell error 0x{result:X}");
                return false;
            }

            Log.Info($"Sent to the Recycle Bin: {path}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not recycle {path}: {ex.Message}");
            return false;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct operation);

    /// <summary>Deletes a file for good, skipping the Recycle Bin. Never throws.</summary>
    public static bool DeletePermanently(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        try
        {
            File.Delete(path);
            Log.Info($"Deleted permanently: {path}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not delete {path}: {ex.Message}");
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
