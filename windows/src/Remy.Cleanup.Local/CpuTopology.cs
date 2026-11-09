using System.Runtime.InteropServices;

namespace Remy.Cleanup.Local;

/// <summary>Physical core count via GetLogicalProcessorInformationEx(RelationProcessorCore).</summary>
public static class CpuTopology
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    public static int PhysicalCoreCount()
    {
        try
        {
            uint len = 0;
            GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref len);
            if (len == 0) return Environment.ProcessorCount;
            var buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(0, buf, ref len)) return Environment.ProcessorCount;
                int count = 0; uint off = 0;
                while (off < len)
                {
                    uint size = (uint)Marshal.ReadInt32(buf, (int)off + 4);
                    if (size == 0) break;
                    count++; off += size;
                }
                return count > 0 ? count : Environment.ProcessorCount;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return Environment.ProcessorCount; }
    }

    /// <summary>max(1, min(8, physicalCores, activeProcessors)).</summary>
    public static int S1ThreadCount() => Math.Max(1, Math.Min(8, Math.Min(PhysicalCoreCount(), Environment.ProcessorCount)));
}
