// Which files a target process has mapped — the substrate's own module lookup, used to find the target's
// runtime (libcoreclr) WITHOUT dbgshim's EnumerateCLRs (finding 88: it parses every mapped file's Mach-O symbol
// table and SEGVs the debugger on one without LC_SYMTAB). Nothing here opens or parses a mapped file.
//
//   macOS:   proc_pidinfo(PROC_PIDREGIONPATHINFO) region walk — no task port, works unentitled for a same-user
//            target. (BCL Process.Modules on macOS returns ONLY the main executable — observed 2026-10-03.)
//   Linux:   /proc/<pid>/maps.
//   Windows: Process.Modules (the full loaded-module list there).
//
// Layout basis (verified 2026-10-03 against the macOS 27 SDK <sys/proc_info.h> by compiling a probe):
// PROC_PIDREGIONPATHINFO = 8, sizeof(struct proc_regionwithpathinfo) = 1272, prp_prinfo.pri_address @ 80,
// prp_prinfo.pri_size @ 88, prp_vip.vip_path @ 248 (char[MAXPATHLEN = 1024]).

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class MappedFiles
{
    private const int PROC_PIDREGIONPATHINFO = 8;
    private const int RegionWithPathInfoSize = 1272;
    private const int RegionAddressOffset = 80;
    private const int RegionSizeOffset = 88;
    private const int RegionPathOffset = 248;
    private const int MaxPathLength = 1024;

    /// <summary>Upper bound on regions visited per lookup — a typical GUI process maps a few thousand; the walk
    /// also stops when the kernel reports no further region or the cursor fails to advance.</summary>
    private const int MaxRegions = 1 << 16;

    /// <summary>Full path of the first file mapped into <paramref name="pid"/> whose file name equals
    /// <paramref name="fileName"/> (case-insensitive), or null if none is mapped or the target is gone.</summary>
    public static string? FindByFileName(int pid, string fileName)
    {
        if (OperatingSystem.IsMacOS()) return FindInRegionsMacOS(pid, fileName);
        if (OperatingSystem.IsLinux()) return FindInProcMaps(pid, fileName);
        try
        {
            using Process process = Process.GetProcessById(pid);
            foreach (ProcessModule module in process.Modules)
                using (module)
                    if (string.Equals(module.ModuleName, fileName, StringComparison.OrdinalIgnoreCase))
                        return module.FileName;
        }
        catch (ArgumentException) { /* target exited */ }
        catch (InvalidOperationException) { /* target exited */ }
        catch (System.ComponentModel.Win32Exception) { /* module list unreadable */ }
        return null;
    }

    private static string? FindInRegionsMacOS(int pid, string fileName)
    {
        byte* info = stackalloc byte[RegionWithPathInfoSize];
        ulong cursor = 0;
        for (int i = 0; i < MaxRegions; i++)
        {
            if (LibProc.proc_pidinfo(pid, PROC_PIDREGIONPATHINFO, cursor, info, RegionWithPathInfoSize) < RegionWithPathInfoSize)
                return null;
            ulong address = *(ulong*)(info + RegionAddressOffset);
            ulong size = *(ulong*)(info + RegionSizeOffset);
            byte* path = info + RegionPathOffset;
            int length = new ReadOnlySpan<byte>(path, MaxPathLength).IndexOf((byte)0);
            if (length > 0)
            {
                string mapped = Encoding.UTF8.GetString(path, length);
                if (string.Equals(Path.GetFileName(mapped), fileName, StringComparison.OrdinalIgnoreCase))
                    return mapped;
            }
            ulong next = address + size;
            if (next <= cursor) return null;
            cursor = next;
        }
        return null;
    }

    private static string? FindInProcMaps(int pid, string fileName)
    {
        try
        {
            // Each line: "address perms offset dev inode   pathname" — the path is everything from the 6th field.
            foreach (string line in File.ReadLines($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/maps"))
            {
                int slash = line.IndexOf('/');
                if (slash < 0) continue;
                string mapped = line[slash..];
                if (string.Equals(Path.GetFileName(mapped), fileName, StringComparison.OrdinalIgnoreCase))
                    return mapped;
            }
        }
        catch (IOException) { /* target exited */ }
        catch (UnauthorizedAccessException) { /* not ours to read */ }
        return null;
    }

}
