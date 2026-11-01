using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CinemaGuard.Core.Logging;

namespace Cinema_Guard.Services;

/// <summary>Sleep, hibernate, restart or shut down the PC once the queue has finished (the Queue page's "When finished").</summary>
public static class PowerActions
{
    public static void Sleep()
    {
        Log.Info("The queue finished: putting the PC to sleep.");

        // hibernate: false = sleep; wakeup events stay enabled.
        if (!SetSuspendState(false, false, false))
            Log.Warn($"Sleep failed (error {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>Hibernate; where hibernation is turned off (powercfg /h off), sleep instead of staying on all night.</summary>
    public static void Hibernate()
    {
        if (!IsHibernateAvailable())
        {
            Log.Warn("Hibernation is turned off on this PC: sleeping instead.");
            Sleep();
            return;
        }

        Log.Info("The queue finished: hibernating the PC.");

        if (!SetSuspendState(true, false, false))
        {
            Log.Warn($"Hibernate failed (error {Marshal.GetLastWin32Error()}): sleeping instead.");
            Sleep();
        }
    }

    /// <summary>A normal shutdown: Windows asks running programs to close, so nothing is cut off mid-write.</summary>
    public static void ShutDown(int delaySeconds = 0) => RunShutdown("/s", "shutting down", delaySeconds);

    /// <summary>A normal restart, the same way as <see cref="ShutDown"/>.</summary>
    public static void Restart(int delaySeconds = 0) => RunShutdown("/r", "restarting", delaySeconds);

    private static void RunShutdown(string mode, string what, int delaySeconds)
    {
        Log.Info($"The queue finished: {what} the PC in {delaySeconds} s.");

        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", $"{mode} /t {delaySeconds} /c \"Cinema Guard finished the queue.\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"{what} failed: {ex.Message}");
        }
    }

    private static bool IsHibernateAvailable()
    {
        try
        {
            return GetPwrCapabilities(out var capabilities) && capabilities.SystemS4 && capabilities.HiberFilePresent;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the power capabilities: {ex.Message}");
            return false;
        }
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetPwrCapabilities(out SystemPowerCapabilities capabilities);

    /// <summary>SYSTEM_POWER_CAPABILITIES; only the first fields are read, the rest keep the size right.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerCapabilities
    {
        [MarshalAs(UnmanagedType.U1)] public bool PowerButtonPresent;
        [MarshalAs(UnmanagedType.U1)] public bool SleepButtonPresent;
        [MarshalAs(UnmanagedType.U1)] public bool LidPresent;
        [MarshalAs(UnmanagedType.U1)] public bool SystemS1;
        [MarshalAs(UnmanagedType.U1)] public bool SystemS2;
        [MarshalAs(UnmanagedType.U1)] public bool SystemS3;
        [MarshalAs(UnmanagedType.U1)] public bool SystemS4;
        [MarshalAs(UnmanagedType.U1)] public bool SystemS5;
        [MarshalAs(UnmanagedType.U1)] public bool HiberFilePresent;
        [MarshalAs(UnmanagedType.U1)] public bool FullWake;
        [MarshalAs(UnmanagedType.U1)] public bool VideoDimPresent;
        [MarshalAs(UnmanagedType.U1)] public bool ApmPresent;
        [MarshalAs(UnmanagedType.U1)] public bool UpsPresent;
        [MarshalAs(UnmanagedType.U1)] public bool ThermalControl;
        [MarshalAs(UnmanagedType.U1)] public bool ProcessorThrottle;
        public byte ProcessorMinThrottle;
        public byte ProcessorMaxThrottle;
        [MarshalAs(UnmanagedType.U1)] public bool FastSystemS4;
        [MarshalAs(UnmanagedType.U1)] public bool Hiberboot;
        [MarshalAs(UnmanagedType.U1)] public bool WakeAlarmPresent;
        [MarshalAs(UnmanagedType.U1)] public bool AoAc;
        [MarshalAs(UnmanagedType.U1)] public bool DiskSpinDown;
        public byte HiberFileType;
        [MarshalAs(UnmanagedType.U1)] public bool AoAcConnectivitySupported;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Spare3;
        [MarshalAs(UnmanagedType.U1)] public bool SystemBatteriesPresent;
        [MarshalAs(UnmanagedType.U1)] public bool BatteriesAreShortTerm;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public BatteryReportingScale[] BatteryScale;
        public int AcOnLineWake;
        public int SoftLidWake;
        public int RtcWake;
        public int MinDeviceWakeState;
        public int DefaultLowLatencyWake;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryReportingScale
    {
        public uint Granularity;
        public uint Capacity;
    }
}
