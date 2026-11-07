// Whether a process has ended, by its PID and its start time: a PID alone stops being an identity once the process
// is reaped and the PID reused. And how it ended (WaitForChildEnd, WaitForEnd; see their section below).
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
using Microsoft.Win32.SafeHandles;

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

    // How a process ended. POSIX: waitid(P_PID, pid, WEXITED | WNOWAIT) blocks until the child ends and reads its
    // record without reaping it, so whoever reaps it still can (mscordbi's PAL reaps a child of this process, finding
    // 87). Verified 2026-11-07 by a C probe on macOS 27 arm64 and Linux arm64: an exit with code 3, a SIGTERM and an
    // abort read as CLD_EXITED/3, CLD_KILLED/15 and CLD_KILLED/6 (CLD_DUMPED/6 on Linux, which dumped core); a later
    // waitpid reaped each with the same status; after the reap waitid gave ECHILD.
    // siginfo_t, from the headers (compiled probes, 2026-11-07): macOS 27 SDK, 104 bytes, si_code at 8, si_status at
    // 20, WNOWAIT 0x20; glibc 2.43 on arm64 and x64, 128 bytes, si_code at 8, si_status at 24, WNOWAIT 0x01000000.
    // P_PID 1, WEXITED 4, CLD_EXITED 1, CLD_KILLED 2, CLD_DUMPED 3 on both.
    // Windows: a handle with SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, waited on, then GetExitCodeProcess. Any
    // process DrHook can open, not only its children; the process record lasts as long as the handle.

    private const int P_PID = 1;
    private const int WEXITED = 4;
    private const int CLD_EXITED = 1, CLD_KILLED = 2, CLD_DUMPED = 3;
    private const int SiginfoBufferSize = 128;
    private const int SiCodeOffset = 8;
    private const int EINTR = 4, ECHILD = 10;

    /// <summary>Blocks until the child <paramref name="pid"/> of this process ends, and returns how it ended, leaving it
    /// unreaped for its reaper. Unknown when another reaper collected it first, or waitid fails. Unix only.</summary>
    public static TargetExit WaitForChildEnd(int pid)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("ProcessLifetime.WaitForChildEnd is Unix-only.");
        int noWait = OperatingSystem.IsMacOS() ? 0x20 : 0x01000000;
        int statusOffset = OperatingSystem.IsMacOS() ? 20 : 24;
        byte* info = stackalloc byte[SiginfoBufferSize];
        while (true)
        {
            new Span<byte>(info, SiginfoBufferSize).Clear();
            if (waitid(P_PID, (uint)pid, info, WEXITED | noWait) == 0)
            {
                int code = *(int*)(info + SiCodeOffset);
                int status = *(int*)(info + statusOffset);
                return code switch
                {
                    CLD_EXITED => TargetExit.Exited(status),
                    CLD_KILLED => TargetExit.Killed(status, coreDumped: false),
                    CLD_DUMPED => TargetExit.Killed(status, coreDumped: true),
                    _ => TargetExit.Unknown($"waitid reported si_code {code.ToString(CultureInfo.InvariantCulture)}"),
                };
            }
            int errno = Marshal.GetLastPInvokeError();
            if (errno == EINTR) continue;
            return errno == ECHILD
                ? TargetExit.Unknown("another reaper collected its exit status before DrHook read it")
                : TargetExit.Unknown($"waitid failed with errno {errno.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private const uint SYNCHRONIZE = 0x00100000, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint INFINITE = 0xFFFFFFFF, WAIT_OBJECT_0 = 0;

    /// <summary>A handle to <paramref name="pid"/> that can wait for its end and read its exit code; invalid when it
    /// cannot be opened (then <see cref="Marshal.GetLastPInvokeError"/> says why). Windows only.</summary>
    public static SafeProcessHandle OpenForEnd(int pid) =>
        OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, inheritHandle: false, (uint)pid);

    /// <summary>Blocks until the process of <paramref name="process"/> ends and returns its exit code, then closes the
    /// handle. Windows only.</summary>
    public static TargetExit WaitForEnd(SafeProcessHandle process)
    {
        using (process)
        {
            if (WaitForSingleObject(process, INFINITE) != WAIT_OBJECT_0)
                return TargetExit.Unknown($"WaitForSingleObject failed with error {Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)}");
            if (!GetExitCodeProcess(process, out uint code))
                return TargetExit.Unknown($"GetExitCodeProcess failed with error {Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)}");
            return TargetExit.Exited(unchecked((int)code));
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int waitid(int idtype, uint id, byte* infop, int options);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
