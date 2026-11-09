using System.Runtime.InteropServices;
using System.Text;
using Remy.Platform.Native;

namespace Remy.Platform.Destination;

/// <summary>Process identity and integrity-level helpers.</summary>
internal static class ProcessInfo
{
    const int MediumRid = 0x2000;

    public static string? ImagePath(uint pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return NativeMethods.QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    /// <summary>Integrity RID of a process (0x2000 medium, 0x3000 high, 0x4000 system), or null when it cannot be read.</summary>
    public static int? IntegrityRid(uint pid)
    {
        var proc = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (proc == 0) return null;
        try
        {
            if (!NativeMethods.OpenProcessToken(proc, NativeMethods.TOKEN_QUERY, out var token)) return null;
            try
            {
                NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, 0, 0, out var needed);
                if (needed == 0) return null;
                var buf = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, buf, needed, out _)) return null;
                    var sid = Marshal.ReadIntPtr(buf);   // TOKEN_MANDATORY_LABEL.Label.Sid
                    int count = Marshal.ReadByte(NativeMethods.GetSidSubAuthorityCount(sid));
                    if (count <= 0) return null;
                    return Marshal.ReadInt32(NativeMethods.GetSidSubAuthority(sid, (uint)(count - 1)));
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { NativeMethods.CloseHandle(token); }
        }
        finally { NativeMethods.CloseHandle(proc); }
    }

    static readonly Lazy<int> Own = new(() => IntegrityRid((uint)Environment.ProcessId) ?? MediumRid);

    /// <summary>True when the target runs above our integrity level. Access denied (unreadable) is treated as elevated.</summary>
    public static bool IsElevatedRelativeToUs(uint pid)
    {
        if (pid == (uint)Environment.ProcessId) return false;
        var rid = IntegrityRid(pid);
        return rid == null || rid.Value > Own.Value;
    }

    public static string WindowTitle(nint hwnd)
    {
        var sb = new StringBuilder(512);
        NativeMethods.GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
