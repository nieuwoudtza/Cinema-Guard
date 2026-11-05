using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CinemaGuard.Core.Logging;
using Microsoft.Win32;

namespace Cinema_Guard.Services;

/// <summary>
/// Moves a window to the virtual desktop the user is on. Windows has no call that names the current
/// desktop; Explorer keeps it in the registry. Without it the window stays put, and activating it
/// switches the user to its desktop instead.
/// </summary>
public static class VirtualDesktops
{
    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer";

    public static void MoveToCurrent(IntPtr window)
    {
        try
        {
            var manager = (IVirtualDesktopManager)new VirtualDesktopManager();
            if (manager.IsWindowOnCurrentVirtualDesktop(window, out var onCurrent) != 0 || onCurrent) return;
            if (CurrentDesktop() is not { } current) return;

            var result = manager.MoveWindowToDesktop(window, ref current);
            if (result != 0) Log.Warn($"Could not move the window to this desktop (0x{result:X8}).");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not move the window to this desktop: {ex.Message}");
        }
    }

    /// <summary>Windows 11 keeps it under Explorer, Windows 10 per logon session.</summary>
    private static Guid? CurrentDesktop()
    {
        foreach (var key in new[] { $@"{Explorer}\VirtualDesktops", $@"{Explorer}\SessionInfo\{Process.GetCurrentProcess().SessionId}\VirtualDesktops" })
        {
            if (Registry.CurrentUser.OpenSubKey(key)?.GetValue("CurrentVirtualDesktop") is byte[] { Length: 16 } bytes)
                return new Guid(bytes);
        }

        return null;
    }

    [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
    private class VirtualDesktopManager;

    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr window, [MarshalAs(UnmanagedType.Bool)] out bool onCurrent);
        [PreserveSig] int GetWindowDesktopId(IntPtr window, out Guid desktop);
        [PreserveSig] int MoveWindowToDesktop(IntPtr window, ref Guid desktop);
    }
}
