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
using System.Threading;
using System.Threading.Tasks;
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
    // macOS deviates for a stopped child: waitid(WEXITED | WNOWAIT) on a child held by POSIX_SPAWN_START_SUSPENDED (how a
    // launch holds it) returns CLD_STOPPED at once and on every call, though WSTOPPED was not asked for; after SIGCONT it
    // blocks until the exit (C probe, 2026-11-07). Linux follows POSIX: a SIGSTOPped child (how its launch holds it) is
    // ignored, and waitid returns at the exit (C probe on Linux arm64). So macOS waits with kqueue: EVFILT_PROC with
    // NOTE_EXIT | NOTE_EXITSTATUS blocks through a hold and through a SIGSTOP/SIGCONT, returns at the exit with the wait
    // status in data (exit 3: 0x300; SIGKILL: 0x9), and does not reap. A registration on a child that has already exited
    // fails with ESRCH; then waitid reads the zombie, which is not stopped. struct kevent (macOS 27 SDK, compiled probe):
    // 32 bytes, ident at 0, filter (int16) at 8, flags (uint16) at 10, fflags (uint32) at 12, data (intptr) at 16, udata
    // at 24; EVFILT_PROC -5, EV_ADD 0x1, EV_ONESHOT 0x10, EV_ERROR 0x4000, NOTE_EXIT 0x80000000, NOTE_EXITSTATUS 0x04000000.
    // Windows: a handle with SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, waited on, then GetExitCodeProcess. Any
    // process DrHook can open, not only its children; the process record lasts as long as the handle.

    private const int P_PID = 1;
    private const int WEXITED = 4;
    private const int CLD_EXITED = 1, CLD_KILLED = 2, CLD_DUMPED = 3;
    private const int SiginfoBufferSize = 128;
    private const int SiCodeOffset = 8;
    private const int EINTR = 4, ECHILD = 10;

    /// <summary>Blocks until the child <paramref name="pid"/> of this process ends, and returns how it ended, leaving it
    /// unreaped for its reaper. A hold or a stop is not an end. Unknown when another reaper collected it first, or the
    /// wait fails. Unix only.</summary>
    public static TargetExit WaitForChildEnd(int pid)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("ProcessLifetime.WaitForChildEnd is Unix-only.");
        if (OperatingSystem.IsMacOS())
        {
            int queue = kqueue();
            if (queue < 0)
                return TargetExit.Unknown($"kqueue failed with errno {Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)}");
            try
            {
                Kevent change = new() { Ident = (nuint)pid, Filter = EVFILT_PROC, Flags = EV_ADD | EV_ONESHOT, Fflags = NOTE_EXIT | NOTE_EXITSTATUS };
                if (kevent(queue, &change, 1, null, 0, null) == 0)
                {
                    Kevent ended;
                    int received;
                    while ((received = kevent(queue, null, 0, &ended, 1, null)) < 0 && Marshal.GetLastPInvokeError() == EINTR) { }
                    if (received != 1)
                        return TargetExit.Unknown($"kevent failed with errno {Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)}");
                    if ((ended.Flags & EV_ERROR) != 0)
                        return TargetExit.Unknown($"kevent reported error {ended.Data.ToString(CultureInfo.InvariantCulture)}");
                    // The wait status, as waitpid gives it: an exit when the low 7 bits are 0, else the killing signal.
                    int status = (int)ended.Data;
                    return (status & 0x7F) == 0
                        ? TargetExit.Exited((status >> 8) & 0xFF)
                        : TargetExit.Killed(status & 0x7F, coreDumped: (status & 0x80) != 0);
                }
                if (Marshal.GetLastPInvokeError() != ESRCH)
                    return TargetExit.Unknown($"kevent registration failed with errno {Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)}");
                // ESRCH: the child has already exited. A zombie is not stopped, so waitid below reads its record.
            }
            finally
            {
                PosixSpawn.Close(queue);
            }
        }
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

    /// <summary>Start reading how <paramref name="processId"/> ends, before the debugger attaches. The read blocks on a
    /// background thread until the target ends, so it lives as long as the target, as the detach reaper does; it is
    /// a blocked system call and dies with this process.
    /// POSIX: waitid with WNOWAIT reads the child's record without reaping it. mscordbi's PAL reaps a child of this
    /// process when it sees the exit (finding 87), before ExitProcess is delivered, so the record must be read by a
    /// wait already in place; WNOWAIT leaves the reaping to the PAL. A target that is not this process's child is
    /// unknown: only its parent can read its end.
    /// Windows: a handle of DrHook's own (the launch's CreateProcess handle is closed after launch), waited on, then
    /// GetExitCodeProcess. That works for any process DrHook can open, attached ones included.
    /// A launch starts it at the spawn, while the child is still held (DbgShim); an attach, before it attaches.</summary>
    public static Task<TargetExit> ObserveEnd(int processId)
    {
        Func<TargetExit> read;
        if (OperatingSystem.IsWindows())
        {
            SafeProcessHandle handle = OpenForEnd(processId);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                return Task.FromResult(TargetExit.Unknown(
                    $"DrHook could not open it to read its exit code (error {error.ToString(CultureInfo.InvariantCulture)})"));
            }
            read = () => WaitForEnd(handle);
        }
        else if (ProcessParentage.ParentOf(processId) == Environment.ProcessId)
        {
            read = () => WaitForChildEnd(processId);
        }
        else
        {
            return Task.FromResult(TargetExit.Unknown("it is not DrHook's child, so only its own parent can read how it ended"));
        }

        var ended = new TaskCompletionSource<TargetExit>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try { ended.SetResult(read()); }
            catch (Exception ex) { ended.SetResult(TargetExit.Unknown($"reading it failed: {ex.GetType().Name}: {ex.Message}")); }
        }, maxStackSize: 256 * 1024)
        {
            IsBackground = true,
            Name = $"drhook-exit-{processId.ToString(CultureInfo.InvariantCulture)}",
        }.Start();
        return ended.Task;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int waitid(int idtype, uint id, byte* infop, int options);

    [StructLayout(LayoutKind.Sequential)]
    private struct Kevent
    {
        public nuint Ident;
        public short Filter;
        public ushort Flags;
        public uint Fflags;
        public nint Data;
        public nint Udata;
    }
    private const short EVFILT_PROC = -5;
    private const ushort EV_ADD = 0x1, EV_ONESHOT = 0x10, EV_ERROR = 0x4000;
    private const uint NOTE_EXIT = 0x80000000, NOTE_EXITSTATUS = 0x04000000;

    [DllImport("libc", SetLastError = true)]
    private static extern int kqueue();

    [DllImport("libc", SetLastError = true)]
    private static extern int kevent(int kq, Kevent* changelist, int nchanges, Kevent* eventlist, int nevents, void* timeout);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
