using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace PcCareAgent;

public static class SystemInfo
{
    public static string OsDescription
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key?.GetValue("ProductName") is string productName)
                {
                    var display = key.GetValue("DisplayVersion") as string
                               ?? key.GetValue("ReleaseId") as string ?? "";
                    return $"{productName} {display} ({Environment.OSVersion.Version})";
                }
            }
            catch { /* non-Windows or restricted */ }
            return Environment.OSVersion.VersionString;
        }
    }

    public static DeviceSnapshot Snapshot(string agentVersion)
    {
        var disks = new List<DiskInfo>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                disks.Add(new DiskInfo(d.Name, Gb(d.TotalSize), Gb(d.TotalFreeSpace)));
            }
        }
        catch { /* access issues — report what we have */ }

        return new DeviceSnapshot(
            Environment.MachineName,
            OsDescription,
            agentVersion,
            Environment.ProcessorCount,
            TotalRamMb(),
            UptimeHours(),
            disks);
    }

    private static double Gb(long bytes) => Math.Round(bytes / 1024.0 / 1024 / 1024, 1);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    private static long TotalRamMb()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref m)) return (long)(m.ullTotalPhys / (1024 * 1024));
            }
        }
        catch { }
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    private static double UptimeHours()
    {
        try { if (OperatingSystem.IsWindows()) return Math.Round(GetTickCount64() / 1000.0 / 3600, 1); }
        catch { }
        return 0;
    }
}

public sealed record DeviceSnapshot(
    [property: JsonPropertyName("hostname")] string Hostname,
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("agentVersion")] string AgentVersion,
    [property: JsonPropertyName("cpuCount")] int CpuCount,
    [property: JsonPropertyName("ramTotalMb")] long RamTotalMb,
    [property: JsonPropertyName("uptimeHours")] double UptimeHours,
    [property: JsonPropertyName("disks")] List<DiskInfo> Disks);

public sealed record DiskInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("totalGb")] double TotalGb,
    [property: JsonPropertyName("freeGb")] double FreeGb);
