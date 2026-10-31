using System;
using System.Runtime.InteropServices;
using CinemaGuard.Core.Logging;

namespace Cinema_Guard.Services;

public enum TaskbarProgressState
{
    NoProgress = 0,
    Indeterminate = 1,
    Normal = 2,
    Error = 4,
    Paused = 8,
}

/// <summary>Taskbar progress for the running queue (the old app had this too).</summary>
public sealed class TaskbarProgress
{
    private readonly ITaskbarList3? _taskbar;
    private readonly IntPtr _window;

    public TaskbarProgress(IntPtr window)
    {
        _window = window;

        try
        {
            _taskbar = (ITaskbarList3)new TaskbarList();
            _taskbar.HrInit();
        }
        catch (Exception ex)
        {
            Log.Warn($"Taskbar progress is unavailable: {ex.Message}");
            _taskbar = null;
        }
    }

    public void Set(double percent, bool running)
    {
        if (_taskbar is null) return;

        try
        {
            if (!running)
            {
                _taskbar.SetProgressState(_window, TaskbarProgressState.NoProgress);
                return;
            }

            _taskbar.SetProgressState(_window, TaskbarProgressState.Normal);
            _taskbar.SetProgressValue(_window, (ulong)Math.Clamp(percent, 0, 100), 100);
        }
        catch
        {
            // The taskbar object can go away; never fail the queue because of it.
        }
    }
}

[ComImport]
[Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskbarList3
{
    void HrInit();
    void AddTab(IntPtr hwnd);
    void DeleteTab(IntPtr hwnd);
    void ActivateTab(IntPtr hwnd);
    void SetActiveAlt(IntPtr hwnd);
    void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
    void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
    void SetProgressState(IntPtr hwnd, TaskbarProgressState state);
}

[ComImport]
[Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
[ClassInterface(ClassInterfaceType.None)]
internal class TaskbarList;
