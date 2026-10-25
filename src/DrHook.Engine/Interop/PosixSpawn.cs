// POSIX process spawn with isolated, redirected stdio — the substrate half of ADR-011 D2
// (validated by probe 59 / finding 75). dbgshim's CreateProcessForLaunch takes no stdio handles,
// so a launched debuggee inherits the debugger process's fds — on an MCP stdio server that means
// the debuggee's Console.WriteLine corrupts the JSON-RPC channel. Here DrHook OWNS the spawn:
// posix_spawnp the target with stdin redirected to /dev/null and stdout/stderr dup2'd to
// DrHook-owned pipes, HELD before it runs; the caller arms RegisterForRuntimeStartup, then
// Continue releases it.
//
// How the target is held, and the libc ABI the spawn uses, differ per platform, so each has its own
// implementation: DarwinSpawn (POSIX_SPAWN_START_SUSPENDED, an Apple extension) and LinuxSpawn
// (glibc has no suspended-spawn flag; the child stops itself before exec). This file holds the part
// that is the same on both: the pipes, the file actions and the posix_spawnp call, over spawn
// attribute/file-action storage the platform implementation declares exactly.
// Windows uses CreateProcess(CREATE_SUSPENDED) + STARTUPINFO redirection (not yet wired).

using System.Runtime.InteropServices;

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class PosixSpawn
{
    private const int SIGKILL = 9;                             // same on Darwin and Linux
    private const int O_RDONLY = 0;                            // <fcntl.h>, same on Darwin and Linux

    [DllImport("libc", SetLastError = true)] private static extern int pipe(int* fds);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, byte* buf, nint count);
    [DllImport("libc", SetLastError = true)] internal static extern int kill(int pid, int sig);
    [DllImport("libc", SetLastError = true)] private static extern int posix_spawnp(int* pid, byte* file, void* fileActions, void* attrp, byte** argv, byte** envp);
    [DllImport("libc")] private static extern int posix_spawnattr_init(void* attr);
    [DllImport("libc")] private static extern int posix_spawnattr_setflags(void* attr, short flags);
    [DllImport("libc")] private static extern int posix_spawnattr_destroy(void* attr);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_init(void* fa);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_adddup2(void* fa, int fd, int newfd);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_addclose(void* fa, int fd);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_addchdir_np(void* fa, byte* path);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_addopen(void* fa, int fd, byte* path, int oflag, int mode);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_destroy(void* fa);

    /// <summary>Spawn <paramref name="file"/> (PATH-searched like execvp) HELD before it runs, with the
    /// child's stdin → /dev/null (never the debugger's fd 0 / MCP JSON-RPC channel), stdout → a fresh pipe
    /// and stderr → a second pipe. Returns 0 with <paramref name="pid"/> and the parent-side read fds on
    /// success; a negative/errno-style code on failure (read fds = -1). The caller must arm
    /// RegisterForRuntimeStartup, then <see cref="Continue"/> to let the child run, and owns draining +
    /// closing the returned read fds.</summary>
    internal static int SpawnSuspendedRedirected(string file, string[] argv, string? cwd,
        IReadOnlyDictionary<string, string>? env, out int pid, out int stdoutReadFd, out int stderrReadFd)
    {
        if (OperatingSystem.IsMacOS())
            return DarwinSpawn.SpawnSuspendedRedirected(file, argv, cwd, env, out pid, out stdoutReadFd, out stderrReadFd);
        if (OperatingSystem.IsLinux())
            return LinuxSpawn.SpawnSuspendedRedirected(file, argv, cwd, env, out pid, out stdoutReadFd, out stderrReadFd);
        throw new PlatformNotSupportedException(
            "PosixSpawn is POSIX-only. The Windows launch path uses dbgshim CreateProcessForLaunch (ADR-007 Phase 9).");
    }

    /// <summary>SIGCONT — release a held child once the debugger callback is armed. The signal NUMBER
    /// differs per platform (Darwin 19, Linux 18), so each implementation sends its own.</summary>
    internal static void Continue(int pid)
    {
        if (OperatingSystem.IsMacOS()) { DarwinSpawn.Continue(pid); return; }
        if (OperatingSystem.IsLinux()) { LinuxSpawn.Continue(pid); return; }
        throw new PlatformNotSupportedException("PosixSpawn is POSIX-only.");
    }

    /// <summary>SIGKILL — best-effort forced termination for failure-cleanup paths. Kills a stopped
    /// child too.</summary>
    internal static void Kill(int pid) => kill(pid, SIGKILL);

    internal static int Close(int fd) => close(fd);

    internal static nint Read(int fd, byte* buf, nint count) => read(fd, buf, count);

    /// <summary>The spawn itself, shared by both platforms. <paramref name="attr"/> and
    /// <paramref name="fileActions"/> point to UNINITIALIZED storage of the platform's exact
    /// <c>posix_spawnattr_t</c> / <c>posix_spawn_file_actions_t</c> type; this initializes, uses and
    /// destroys them. <paramref name="spawnFlags"/> goes to <c>posix_spawnattr_setflags</c>.</summary>
    internal static int SpawnRedirected(void* attr, void* fileActions, short spawnFlags,
        string file, string[] argv, string? cwd, IReadOnlyDictionary<string, string>? env,
        out int pid, out int stdoutReadFd, out int stderrReadFd)
    {
        pid = 0; stdoutReadFd = -1; stderrReadFd = -1;

        int* outp = stackalloc int[2];
        int* errp = stackalloc int[2];
        if (pipe(outp) != 0) return -5;
        if (pipe(errp) != 0) { close(outp[0]); close(outp[1]); return -5; }
        int oR = outp[0], oW = outp[1], eR = errp[0], eW = errp[1];

        posix_spawnattr_init(attr);
        posix_spawnattr_setflags(attr, spawnFlags);
        posix_spawn_file_actions_init(fileActions);
        // Child stdin -> /dev/null: a launched debuggee must NEVER inherit the debugger's stdin. On an
        // MCP stdio server fd 0 is the JSON-RPC REQUEST channel, so an inherited fd 0 lets the child
        // share — and break — the protocol input, disconnecting the server. D2 isolated fd 1/2 (output
        // corrupting the channel); fd 0 is the input half of that same isolation. The debuggee is
        // non-interactive here — real interactive stdin is the D4 PTY concern (ADR-011), not this.
        byte* devNull = Utf8("/dev/null");
        posix_spawn_file_actions_addopen(fileActions, 0, devNull, O_RDONLY, 0);
        posix_spawn_file_actions_adddup2(fileActions, oW, 1);   // child stdout -> stdout pipe
        posix_spawn_file_actions_adddup2(fileActions, eW, 2);   // child stderr -> stderr pipe
        posix_spawn_file_actions_addclose(fileActions, oR);     // child doesn't keep the read ends
        posix_spawn_file_actions_addclose(fileActions, eR);
        posix_spawn_file_actions_addclose(fileActions, oW);     // originals (dup2'd copies on 1/2 survive)
        posix_spawn_file_actions_addclose(fileActions, eW);

        byte* cwdPtr = null;
        if (!string.IsNullOrEmpty(cwd)) { cwdPtr = Utf8(cwd); posix_spawn_file_actions_addchdir_np(fileActions, cwdPtr); }

        byte** cArgv = MarshalArray(argv);
        byte** cEnvp = MarshalArray(BuildChildEnv(env));
        byte* cFile = Utf8(file);

        int childPid;
        int rc = posix_spawnp(&childPid, cFile, fileActions, attr, cArgv, cEnvp);

        posix_spawn_file_actions_destroy(fileActions);
        posix_spawnattr_destroy(attr);
        FreeArray(cArgv);
        FreeArray(cEnvp);
        Marshal.FreeCoTaskMem((nint)cFile);
        Marshal.FreeCoTaskMem((nint)devNull);
        if (cwdPtr != null) Marshal.FreeCoTaskMem((nint)cwdPtr);

        // Parent drops the write ends so read() returns EOF when the child closes them (exit).
        close(oW);
        close(eW);

        if (rc != 0) { close(oR); close(eR); return rc > 0 ? -rc : rc; }

        pid = childPid;
        stdoutReadFd = oR;
        stderrReadFd = eR;
        return 0;
    }

    private static byte* Utf8(string s) => (byte*)Marshal.StringToCoTaskMemUTF8(s);

    private static byte** MarshalArray(string[] items)
    {
        byte** arr = (byte**)Marshal.AllocCoTaskMem((items.Length + 1) * sizeof(nint));
        for (int i = 0; i < items.Length; i++) arr[i] = Utf8(items[i]);
        arr[items.Length] = null;
        return arr;
    }

    private static void FreeArray(byte** arr)
    {
        for (int i = 0; arr[i] != null; i++) Marshal.FreeCoTaskMem((nint)arr[i]);
        Marshal.FreeCoTaskMem((nint)arr);
    }

    /// <summary>The child's environment as a <c>KEY=VALUE</c> array for <c>posix_spawn</c>'s envp:
    /// the parent's environment with <paramref name="overrides"/> applied on top (inherit-plus-override).
    /// <paramref name="overrides"/> null/empty reproduces the inherit-only behaviour. POSIX env keys are
    /// case-sensitive, so the merge uses ordinal comparison.</summary>
    private static string[] BuildChildEnv(IReadOnlyDictionary<string, string>? overrides)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            map[(string)e.Key] = e.Value?.ToString() ?? string.Empty;
        if (overrides is not null)
            foreach (KeyValuePair<string, string> kv in overrides)
                map[kv.Key] = kv.Value;   // set or replace — overrides win
        var list = new List<string>(map.Count);
        foreach (KeyValuePair<string, string> kv in map)
            list.Add($"{kv.Key}={kv.Value}");
        return list.ToArray();
    }
}
