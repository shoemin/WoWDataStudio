using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WoWData.Core.HotReload;

/// <summary>
/// 0.13.1: the program file of a running process, also when the process cannot be opened.
/// <para>The game started by WoWDebug.exe (the project's launcher, which also loads wowcode.dll) refuses every
/// <c>OpenProcess</c>, even with only PROCESS_QUERY_LIMITED_INFORMATION (ERROR_ACCESS_DENIED): its security descriptor
/// does not give the user that right. <c>Process.MainModule</c>, <c>QueryFullProcessImageName</c> and WMI's
/// ExecutablePath all come back empty, so 0.13.0 never found that game ("The game is not running from ..."). The
/// fallback asks the system's process list instead (NtQuerySystemInformation, SystemProcessIdInformation), which needs
/// no handle and works for 32-bit processes seen from the 64-bit app; it returns the NT path
/// (<c>\Device\HarddiskVolume4\...\WoW.exe</c>), which is turned back into a drive path.</para>
/// </summary>
public static class ProcessImage
{
    /// <summary>The program file of process <paramref name="pid"/> as a normal path (C:\...\WoW.exe), or null when the
    /// process is gone or the path cannot be read.</summary>
    public static string? PathOf(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return ByHandle(pid) ?? (NtPath(pid) is { } nt ? FromNtPath(nt, DriveDevices()) : null);
    }

    /// <summary>The usual way: QueryFullProcessImageName with the limited query right (works for elevated and 32-bit
    /// processes; refused for a process whose security descriptor does not allow it).</summary>
    public static string? ByHandle(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>The NT path of a process's program from the system's process list (no process handle needed).</summary>
    public static unsafe string? NtPath(int pid)
    {
        const int SystemProcessIdInformation = 88;
        const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
        ushort max = 1024;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var buf = new char[max / 2];
            fixed (char* p = buf)
            {
                var info = new SystemProcessIdInfo { ProcessId = (IntPtr)pid, Length = 0, MaximumLength = max, Buffer = (IntPtr)p };
                int st = NtQuerySystemInformation(SystemProcessIdInformation, ref info, Marshal.SizeOf<SystemProcessIdInfo>(), out _);
                if (st == 0) return info.Length == 0 ? null : new string(p, 0, info.Length / 2);
                if (st != StatusInfoLengthMismatch || info.MaximumLength <= max) return null;
                max = info.MaximumLength;
            }
        }
        return null;
    }

    /// <summary>The drive letters and the NT devices behind them ("C:" -> "\Device\HarddiskVolume4"), from QueryDosDevice.</summary>
    public static IReadOnlyList<(string Drive, string Device)> DriveDevices()
    {
        var list = new List<(string, string)>();
        var sb = new StringBuilder(1024);
        for (char c = 'A'; c <= 'Z'; c++)
        {
            var drive = c + ":";
            sb.Clear();
            if (QueryDosDevice(drive, sb, (uint)sb.Capacity) == 0) continue;
            var target = sb.ToString();
            int nul = target.IndexOf('\0');
            if (nul >= 0) target = target[..nul];
            if (target.Length > 0) list.Add((drive, target));
        }
        return list;
    }

    /// <summary>Turns an NT path into a drive path with the given drive map: \Device\HarddiskVolume4\Users\x -> C:\Users\x;
    /// \??\C:\x -> C:\x; \Device\Mup\server\share\x (a network share) -> \\server\share\x. Null when no drive matches.</summary>
    public static string? FromNtPath(string ntPath, IEnumerable<(string Drive, string Device)> drives)
    {
        if (string.IsNullOrEmpty(ntPath)) return null;
        if (ntPath.StartsWith(@"\??\", StringComparison.Ordinal)) return ntPath[4..];
        if (ntPath.StartsWith(@"\Device\Mup\", StringComparison.OrdinalIgnoreCase)) return @"\\" + ntPath[@"\Device\Mup\".Length..];
        if (ntPath.Length >= 2 && ntPath[1] == ':') return ntPath;   // already a drive path
        foreach (var (drive, device) in drives)
        {
            if (string.IsNullOrEmpty(device)) continue;
            // "\Device\HarddiskVolume1" must not match "\Device\HarddiskVolume10\..."
            if (ntPath.StartsWith(device, StringComparison.OrdinalIgnoreCase) && (ntPath.Length == device.Length || ntPath[device.Length] == '\\'))
                return drive + (ntPath.Length == device.Length ? @"\" : ntPath[device.Length..]);
        }
        return null;
    }

    /// <summary>A folder in a form two paths of the same folder share: full, without a trailing separator, with long names
    /// (a program started through an 8.3 short path reports C:\Users\x\WOWCUS~1\...).</summary>
    public static string NormalizeDir(string dir)
    {
        var full = Path.GetFullPath(dir).TrimEnd('\\', '/');
        if (full.Length == 2 && full[1] == ':') full += @"\";
        if (OperatingSystem.IsWindows() && full.Contains('~'))
        {
            var sb = new StringBuilder(1024);
            uint n = GetLongPathName(full, sb, (uint)sb.Capacity);
            if (n > 0 && n < sb.Capacity) full = sb.ToString(0, (int)n).TrimEnd('\\', '/');
        }
        return full;
    }

    /// <summary>When the process started (null when the process cannot be opened).</summary>
    public static DateTime? StartTime(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemProcessIdInfo
    {
        public IntPtr ProcessId;
        public ushort Length;           // UNICODE_STRING ImageName
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, ref SystemProcessIdInfo info, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(string device, StringBuilder target, uint max);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint max);
}
