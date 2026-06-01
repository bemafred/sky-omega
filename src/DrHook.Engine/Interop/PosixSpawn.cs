// POSIX process spawn with isolated, redirected stdio — the substrate half of ADR-011 D2
// (validated by probe 59 / finding 75). dbgshim's CreateProcessForLaunch takes no stdio handles,
// so a launched debuggee inherits the debugger process's fds — on an MCP stdio server that means
// the debuggee's Console.WriteLine corrupts the JSON-RPC channel. Here DrHook OWNS the spawn:
// posix_spawnp the target SUSPENDED with stdout/stderr dup2'd to DrHook-owned pipes; the caller
// arms RegisterForRuntimeStartup, then SIGCONT (Continue) to release it. POSIX-specific;
// Windows uses CreateProcess(CREATE_SUSPENDED) + STARTUPINFO redirection (not yet wired).

using System.Runtime.InteropServices;

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class PosixSpawn
{
    private const int POSIX_SPAWN_START_SUSPENDED = 0x0080;   // <spawn.h> (Darwin)
    private const int SIGCONT = 19;                            // Darwin signal numbers
    private const int SIGKILL = 9;

    [DllImport("libc", SetLastError = true)] private static extern int pipe(int* fds);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, byte* buf, nint count);
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int sig);
    [DllImport("libc", SetLastError = true)] private static extern int posix_spawnp(int* pid, byte* file, nint* fileActions, nint* attrp, byte** argv, byte** envp);
    [DllImport("libc")] private static extern int posix_spawnattr_init(nint* attr);
    [DllImport("libc")] private static extern int posix_spawnattr_setflags(nint* attr, short flags);
    [DllImport("libc")] private static extern int posix_spawnattr_destroy(nint* attr);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_init(nint* fa);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_adddup2(nint* fa, int fd, int newfd);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_addclose(nint* fa, int fd);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_addchdir_np(nint* fa, byte* path);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_destroy(nint* fa);

    /// <summary>SIGCONT — release a START_SUSPENDED child once the debugger callback is armed.</summary>
    internal static void Continue(int pid) => kill(pid, SIGCONT);

    /// <summary>SIGKILL — best-effort forced termination for failure-cleanup paths.</summary>
    internal static void Kill(int pid) => kill(pid, SIGKILL);

    internal static int Close(int fd) => close(fd);

    internal static nint Read(int fd, byte* buf, nint count) => read(fd, buf, count);

    /// <summary>Spawn <paramref name="file"/> (PATH-searched like execvp) SUSPENDED, with the child's
    /// stdout → a fresh pipe and stderr → a second pipe. Returns 0 with <paramref name="pid"/> and the
    /// parent-side read fds on success; a negative/errno-style code on failure (read fds = -1). The
    /// caller must arm RegisterForRuntimeStartup, then <see cref="Continue"/> to let the child run, and
    /// owns draining + closing the returned read fds.</summary>
    internal static int SpawnSuspendedRedirected(string file, string[] argv, string? cwd,
        out int pid, out int stdoutReadFd, out int stderrReadFd)
    {
        pid = 0; stdoutReadFd = -1; stderrReadFd = -1;

        int* outp = stackalloc int[2];
        int* errp = stackalloc int[2];
        if (pipe(outp) != 0) return -5;
        if (pipe(errp) != 0) { close(outp[0]); close(outp[1]); return -5; }
        int oR = outp[0], oW = outp[1], eR = errp[0], eW = errp[1];

        nint attr = 0, fa = 0;
        posix_spawnattr_init(&attr);
        posix_spawnattr_setflags(&attr, unchecked((short)POSIX_SPAWN_START_SUSPENDED));
        posix_spawn_file_actions_init(&fa);
        posix_spawn_file_actions_adddup2(&fa, oW, 1);   // child stdout -> stdout pipe
        posix_spawn_file_actions_adddup2(&fa, eW, 2);   // child stderr -> stderr pipe
        posix_spawn_file_actions_addclose(&fa, oR);     // child doesn't keep the read ends
        posix_spawn_file_actions_addclose(&fa, eR);
        posix_spawn_file_actions_addclose(&fa, oW);     // originals (dup2'd copies on 1/2 survive)
        posix_spawn_file_actions_addclose(&fa, eW);

        byte* cwdPtr = null;
        if (!string.IsNullOrEmpty(cwd)) { cwdPtr = Utf8(cwd); posix_spawn_file_actions_addchdir_np(&fa, cwdPtr); }

        byte** cArgv = MarshalArray(argv);
        byte** cEnvp = MarshalArray(CurrentEnv());
        byte* cFile = Utf8(file);

        int childPid;
        int rc = posix_spawnp(&childPid, cFile, &fa, &attr, cArgv, cEnvp);

        posix_spawn_file_actions_destroy(&fa);
        posix_spawnattr_destroy(&attr);
        FreeArray(cArgv);
        FreeArray(cEnvp);
        Marshal.FreeCoTaskMem((nint)cFile);
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

    private static string[] CurrentEnv()
    {
        var list = new List<string>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            list.Add($"{e.Key}={e.Value}");
        return list.ToArray();
    }
}
