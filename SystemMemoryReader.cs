using System.Runtime.InteropServices;

namespace LlmMemoryWidget;

public sealed class SystemMemorySnapshot
{
    public ulong TotalPhysicalBytes { get; init; }

    public ulong AvailablePhysicalBytes { get; init; }

    public double TotalPhysicalGb => TotalPhysicalBytes / 1024d / 1024d / 1024d;

    public double AvailablePhysicalGb => AvailablePhysicalBytes / 1024d / 1024d / 1024d;

    public double UsedPhysicalGb => Math.Max(0, TotalPhysicalGb - AvailablePhysicalGb);
}

public static class SystemMemoryReader
{
    public static SystemMemorySnapshot Read()
    {
        var status = new MemoryStatusEx
        {
            dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new InvalidOperationException(
                $"GlobalMemoryStatusEx failed. Win32 error: {Marshal.GetLastWin32Error()}");
        }

        return new SystemMemorySnapshot
        {
            TotalPhysicalBytes = status.ullTotalPhys,
            AvailablePhysicalBytes = status.ullAvailPhys
        };
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
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
}
