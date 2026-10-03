// Parent-pid lookup for a target by PID — the substrate's view of the OS parent-child link.
// macOS: libproc proc_pidinfo(PROC_PIDTBSDINFO) → struct proc_bsdinfo.pbi_ppid. Linux: field 4 of
// /proc/<pid>/stat. Windows: no zombie/reaping model, so the question the caller asks (finding 87,
// the dual-reaper race) does not arise — returns null.
//
// Layout basis (verified 2026-10-03 against the macOS 26 SDK <sys/proc_info.h> by compiling a probe):
// PROC_PIDTBSDINFO = 3, sizeof(struct proc_bsdinfo) = 136, offsetof(pbi_ppid) = 16 (after the four
// uint32 fields pbi_flags / pbi_status / pbi_xstatus / pbi_pid).

using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class ProcessParentage
{
    private const int PROC_PIDTBSDINFO = 3;
    private const int ProcBsdInfoSize = 136;
    private const int PbiPpidOffset = 16;

    /// <summary>The OS parent pid of <paramref name="pid"/>, or null when it cannot be read (the
    /// target exited, the platform has no parent-reaping model, or the read failed). Best-effort by
    /// design: callers use it for a diagnostic decision, never for control flow that must succeed.</summary>
    public static int? ParentOf(int pid)
    {
        if (OperatingSystem.IsMacOS())
        {
            byte* info = stackalloc byte[ProcBsdInfoSize];
            int read = proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, info, ProcBsdInfoSize);
            return read == ProcBsdInfoSize ? (int)*(uint*)(info + PbiPpidOffset) : null;
        }
        if (OperatingSystem.IsLinux())
        {
            // /proc/<pid>/stat: "pid (comm) state ppid ..." — comm may contain spaces and ')', so
            // parse from the LAST ')'.
            try
            {
                string stat = File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat");
                string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                return int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ppid) ? ppid : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
        return null;
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "proc_pidinfo")]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, byte* buffer, int buffersize);
}
