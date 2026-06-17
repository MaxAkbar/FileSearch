using System;
using System.Runtime.InteropServices;

namespace FileSearch.Core.Indexing;

public interface IIndexerRuntimeCondition
{
    bool IsOnBattery { get; }

    bool IsUserIdle(TimeSpan idleThreshold);
}

internal sealed class WindowsIndexerRuntimeCondition : IIndexerRuntimeCondition
{
    public bool IsOnBattery
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return false;

            return GetSystemPowerStatus(out var status) &&
                status.AcLineStatus == 0;
        }
    }

    public bool IsUserIdle(TimeSpan idleThreshold)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        var info = new LastInputInfo
        {
            CbSize = (uint)Marshal.SizeOf<LastInputInfo>(),
        };
        if (!GetLastInputInfo(ref info))
            return false;

        var idleMilliseconds = unchecked((uint)Environment.TickCount - info.DwTime);
        return TimeSpan.FromMilliseconds(idleMilliseconds) >= idleThreshold;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint CbSize;
        public uint DwTime;
    }
}
