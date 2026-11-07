// Whether a process has ended, by its PID and its start time: a PID alone stops being an identity once the process
// is reaped and the PID reused.
//
// A process that has exited but has not been reaped is a zombie, and kill(pid, 0) succeeds on a zombie. That is why
// Process.HasExited reports a zombie as running for any process this one did not Process.Start, which is how the
// engine holds its target (GetProcessById). Observed 2026-11-07 (probe 106): in a session stuck as in finding 105,
// mscordbi's PAL never reaps the dead launched target, and HasExited said false while waitid saw the child exited.
//
// macOS: proc_pidinfo(PROC_PIDTBSDINFO) → struct proc_bsdinfo: pbi_status at offset 4 (SZOMB = 5), pbi_start_tvsec at
// 120, pbi_start_tvusec at 128 (sizeof 136; verified 2026-11-06 against the macOS 27 SDK <sys/proc_info.h> by
// compiling a probe, as ProcessParentage's offsets were).
// Linux: /proc/<pid>/stat field 3 (state: Z zombie, X dead) and field 22 (starttime, clock ticks after boot).
// Windows: no zombies, and the Process handle identifies the process and signals at its exit, so this answers null.

using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class ProcessLifetime
{
    private const int PROC_PIDTBSDINFO = 3;
    private const int ProcBsdInfoSize = 136;
    private const int PbiStatusOffset = 4;
    private const int PbiStartSecOffset = 120;
    private const int PbiStartMicroSecOffset = 128;
    private const uint SZOMB = 5;
    private const int ESRCH = 3;

    /// <summary>The start time of the running process <paramref name="pid"/>, in the platform's own unit; null when no
    /// running process has that PID, the record cannot be read, or the platform has no such record (Windows).</summary>
    public static long? StartOf(int pid) => Read(pid) is { Exists: true, Zombie: false } record ? record.Start : null;

    /// <summary>True when the process <paramref name="pid"/> that started at <paramref name="start"/> has ended: the PID
    /// has no process, its process is a zombie, or the process under the PID started at another time. False when it is
    /// running. Null when that cannot be read (another user's process, an unexpected error) or on Windows.</summary>
    public static bool? HasEnded(int pid, long start) => Read(pid) switch
    {
        null => null,
        { Exists: false } => true,
        { Zombie: true } => true,
        { Start: var s } => s != start,
    };

    private readonly record struct Record(bool Exists, bool Zombie, long Start);

    private static Record? Read(int pid)
    {
        if (OperatingSystem.IsMacOS())
        {
            byte* info = stackalloc byte[ProcBsdInfoSize];
            int read = LibProc.proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, info, ProcBsdInfoSize);
            if (read != ProcBsdInfoSize)
                return read <= 0 && Marshal.GetLastPInvokeError() == ESRCH ? new Record(false, false, 0) : null;
            long start = *(long*)(info + PbiStartSecOffset) * 1_000_000 + *(long*)(info + PbiStartMicroSecOffset);
            return new Record(true, *(uint*)(info + PbiStatusOffset) == SZOMB, start);
        }
        if (OperatingSystem.IsLinux())
        {
            string stat;
            try { stat = File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat"); }
            catch (FileNotFoundException) { return new Record(false, false, 0); }
            catch (DirectoryNotFoundException) { return new Record(false, false, 0); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            // "pid (comm) state ppid ...": comm may contain spaces and ')', so the fields start after the last ')'.
            // fields[0] is field 3 (state), fields[19] is field 22 (starttime).
            string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            if (fields.Length < 20 || !long.TryParse(fields[19], NumberStyles.Integer, CultureInfo.InvariantCulture, out long start))
                return null;
            return new Record(true, fields[0] is "Z" or "X", start);
        }
        return null;
    }
}
