// Linux (glibc) suspended spawn. glibc's posix_spawn has no START_SUSPENDED flag, so the child holds
// ITSELF: the spawn runs `/bin/sh -c 'kill -STOP $$; exec "$0" "$@"' <program> <args...>`. The shell
// stops itself before exec; we wait until /proc reports it stopped (state T), the caller arms
// RegisterForRuntimeStartup, then SIGCONT lets the shell exec the target. exec keeps the pid (and the
// process start time dbgshim keys its startup semaphores on), so the registration made against the
// stopped shell is the target's, and the target runs no instruction of its own before the callback is
// armed — the same guarantee DarwinSpawn gets from POSIX_SPAWN_START_SUSPENDED.
//
// ABI basis — glibc 2.43 <spawn.h>, <bits/types/__sigset_t.h>, <bits/types/struct_sched_param.h>,
// read on zbook (Ubuntu 26.04.1 LTS, x86_64) 2026-10-25; sizes confirmed there with sizeof():
// posix_spawnattr_t = 336 bytes, posix_spawn_file_actions_t = 80 bytes. sigset_t is
// unsigned long[1024 / (8 * sizeof(unsigned long))] = 16 x 8 = 128 bytes on LP64. SIGCONT = 18
// (<bits/signum-arch.h>; 19 is SIGSTOP on Linux).
//
// musl defines these types differently and has not been measured, so a musl system is refused
// rather than spawned with a guessed layout.

using System.Globalization;
using System.Runtime.InteropServices;

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class LinuxSpawn
{
    private const int SIGCONT = 18;

    /// <summary>The shell's self-hold: stop, and once continued, become the target (same pid).</summary>
    private const string HoldThenExec = "kill -STOP $$; exec \"$0\" \"$@\"";

    /// <summary>Upper bound on waiting for the spawned shell to reach its self-stop.</summary>
    private static readonly TimeSpan HoldWait = TimeSpan.FromSeconds(5);

    /// <summary>glibc <c>posix_spawnattr_t</c>:
    /// <code>
    /// typedef struct {
    ///   short int __flags; pid_t __pgrp; sigset_t __sd; sigset_t __ss;
    ///   struct sched_param __sp; int __policy; int __cgroup; int __pad[15];
    /// } posix_spawnattr_t;
    /// </code></summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpawnAttr
    {
        public short Flags;
        public int ProcessGroup;
        public fixed ulong DefaultSignals[16];     // sigset_t __sd
        public fixed ulong SignalMask[16];         // sigset_t __ss
        public int SchedPriority;                  // struct sched_param { int sched_priority; }
        public int Policy;
        public int Cgroup;
        public fixed int Pad[15];
    }

    /// <summary>glibc <c>posix_spawn_file_actions_t</c>:
    /// <code>
    /// typedef struct {
    ///   int __allocated; int __used; struct __spawn_action *__actions; int __pad[16];
    /// } posix_spawn_file_actions_t;
    /// </code></summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpawnFileActions
    {
        public int Allocated;
        public int Used;
        public nint Actions;
        public fixed int Pad[16];
    }

    internal static int SpawnSuspendedRedirected(string file, string[] argv, string? cwd,
        IReadOnlyDictionary<string, string>? env, out int pid, out int stdoutReadFd, out int stderrReadFd)
    {
        if (LinuxLibc.IsMusl)
            throw new PlatformNotSupportedException(
                "DrHook launch on musl libc is not supported yet: musl's posix_spawn types have not been " +
                "measured, and the spawn will not run on a guessed layout. Attach works on musl.");

        // /bin/sh -c '<hold>' <program> <args...> — the script sees the program as $0 and its args as $@.
        string[] shellArgv = new string[argv.Length + 3];
        shellArgv[0] = "/bin/sh";
        shellArgv[1] = "-c";
        shellArgv[2] = HoldThenExec;
        shellArgv[3] = file;
        for (int i = 1; i < argv.Length; i++) shellArgv[i + 3] = argv[i];

        SpawnAttr attr;
        SpawnFileActions fileActions;
        int rc = PosixSpawn.SpawnRedirected(&attr, &fileActions, spawnFlags: 0,
            "/bin/sh", shellArgv, cwd, env, out pid, out stdoutReadFd, out stderrReadFd);
        if (rc != 0) return rc;

        if (!WaitUntilStopped(pid, HoldWait))
        {
            PosixSpawn.Kill(pid);
            PosixSpawn.Close(stdoutReadFd);
            PosixSpawn.Close(stderrReadFd);
            pid = 0; stdoutReadFd = -1; stderrReadFd = -1;
            return -6;
        }
        return 0;
    }

    internal static void Continue(int pid) => PosixSpawn.kill(pid, SIGCONT);

    /// <summary>Poll <c>/proc/&lt;pid&gt;/stat</c> until the process is in state <c>T</c> (stopped by a
    /// signal). False if it exits first or the wait runs out.</summary>
    private static bool WaitUntilStopped(int pid, TimeSpan timeout)
    {
        string statPath = $"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat";
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            char state;
            try
            {
                // "pid (comm) state ..." — comm may contain spaces and ')', so read after the LAST ')'.
                string stat = File.ReadAllText(statPath);
                state = stat[stat.LastIndexOf(')') + 2];
            }
            catch (IOException) { return false; }                  // gone
            catch (UnauthorizedAccessException) { return false; }
            if (state == 'T') return true;
            if (state is 'Z' or 'X') return false;                  // exited before reaching the hold
            Thread.Sleep(5);
        }
        return false;
    }
}
