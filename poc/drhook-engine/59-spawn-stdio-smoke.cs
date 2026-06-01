#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
#:property AllowUnsafeBlocks=true
//
// DrHook.Engine probe 59 — own-spawn stdio isolation + RegisterForRuntimeStartup ==============
//
// ADR-011 D2. drhook_launch's debuggee inherits the MCP server's stdin/stdout/stderr, because
// dbgshim's CreateProcessForLaunch takes no stdio handles (DbgShim.cs:237) — so a debugged
// Console.WriteLine corrupts the MCP JSON-RPC stdout channel. Proposed fix (Option B): DrHook
// OWNS process creation — posix_spawn the target SUSPENDED with stdout/stderr dup2'd to
// DrHook-owned pipes, then RegisterForRuntimeStartup, then SIGCONT — instead of dbgshim's
// CreateProcessForLaunch + ResumeProcess.
//
// Two independent unknowns, isolated into two spawns (dont-compound-unknowns):
//   PART 1 (control, NO debugger): does dup2-to-pipe isolate the child's stdout/stderr from this
//           process's stdout? Spawn suspended + redirected, SIGCONT, run free, expect marker IN pipe.
//   PART 2 (debugger): does RegisterForRuntimeStartup fire for a process WE spawned suspended
//           (not CreateProcessForLaunch)? Spawn suspended + redirected, register, SIGCONT, expect
//           the startup callback with a valid ICorDebug. (The target parks at the runtime->debugger
//           handoff — we are NOT completing the attach here; firing + valid cordbg is the signal.)
//
// Falsification: 2 usage; 3 build target; 4 libdbgshim not found; 5 pipe(); 6 posix_spawn;
//   7 suspend did not hold (target ran before SIGCONT); 8 RegisterForRuntimeStartup error;
//   9 startup callback never fired after SIGCONT; 10 callback fired but no ICorDebug / hr<0;
//   11 control: target stdout NOT captured in pipe (redirection failed); 0 PASS.
//
// Usage:  dotnet run --no-cache 59-spawn-stdio-smoke.cs 59-spawn-target.cs   (macOS-arm64)

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

return Spawn59.Run(args);

static unsafe class Spawn59
{
    const int POSIX_SPAWN_START_SUSPENDED = 0x0080;   // <spawn.h> (Darwin)
    const int SIGCONT = 19;                            // Darwin signal numbers
    const int SIGKILL = 9;
    const string Marker = "PROBE59_TARGET_STDOUT";

    [DllImport("libc", SetLastError = true)] static extern int pipe(int* fds);
    [DllImport("libc", SetLastError = true)] static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] static extern nint read(int fd, byte* buf, nint count);
    [DllImport("libc", SetLastError = true)] static extern int kill(int pid, int sig);
    [DllImport("libc", SetLastError = true)] static extern int posix_spawn(int* pid, byte* path, nint* fileActions, nint* attrp, byte** argv, byte** envp);
    [DllImport("libc")] static extern int posix_spawnattr_init(nint* attr);
    [DllImport("libc")] static extern int posix_spawnattr_setflags(nint* attr, short flags);
    [DllImport("libc")] static extern int posix_spawnattr_destroy(nint* attr);
    [DllImport("libc")] static extern int posix_spawn_file_actions_init(nint* fa);
    [DllImport("libc")] static extern int posix_spawn_file_actions_adddup2(nint* fa, int fd, int newfd);
    [DllImport("libc")] static extern int posix_spawn_file_actions_addclose(nint* fa, int fd);
    [DllImport("libc")] static extern int posix_spawn_file_actions_destroy(nint* fa);

    sealed class StartupCtx { public nint PCordb; public int HResult; public readonly ManualResetEventSlim Signaled = new(false); }
    static GCHandle s_ctxHandle;

    // RegisterForRuntimeStartup callback. O(1)-stack: runs on dbgshim's internal thread (ENG-STK-3).
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void StartupThunk(nint pCordb, nint parameter, int hr)
    {
        if (parameter == 0) return;
        GCHandle h = GCHandle.FromIntPtr(parameter);
        if (h.Target is StartupCtx ctx) { ctx.PCordb = pCordb; ctx.HResult = hr; ctx.Signaled.Set(); }
    }

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet run --no-cache 59-spawn-stdio-smoke.cs 59-spawn-target.cs");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"rid        : {RuntimeInformation.RuntimeIdentifier}");

        string? dll = BuildTarget(args[0]);
        if (dll is null) { Console.Error.WriteLine("FAIL(3): could not build target"); return 3; }
        Console.WriteLine($"target dll : {dll}");
        (string exe, string[] argv) = ResolveRun(dll);
        Console.WriteLine($"exec       : {exe}{(argv.Length > 1 ? " " + string.Join(' ', argv[1..]) : "")}");

        string? dbgshimPath = ResolveDbgShim();
        if (dbgshimPath is null) { Console.Error.WriteLine("FAIL(4): libdbgshim not found"); return 4; }
        Console.WriteLine($"dbgshim    : {dbgshimPath}");
        nint lib = NativeLibrary.Load(dbgshimPath);
        var registerForRuntimeStartup = (delegate* unmanaged[Cdecl]<uint, nint, nint, nint*, int>)NativeLibrary.GetExport(lib, "RegisterForRuntimeStartup");
        var unregisterForRuntimeStartup = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(lib, "UnregisterForRuntimeStartup");

        // ── PART 1 — redirection control (no debugger) ─────────────────────────────────────
        Console.WriteLine("part 1     : redirection control — spawn suspended+redirected, run FREE, expect marker in pipe.");
        int pid1 = SpawnSuspended(exe, argv, out int rfd1);
        if (pid1 == -5) { Console.Error.WriteLine("FAIL(5): pipe()"); return 5; }
        if (pid1 == -6) { Console.Error.WriteLine("FAIL(6): posix_spawn (control)"); return 6; }
        var sb1 = new StringBuilder();
        var done1 = new ManualResetEventSlim(false);
        new Thread(() => DrainThread(rfd1, sb1, done1)) { IsBackground = true }.Start();
        kill(pid1, SIGCONT);
        done1.Wait(TimeSpan.FromSeconds(8));
        string o1 = Text(sb1);
        if (!o1.Contains(Marker))
        {
            Console.Error.WriteLine($"FAIL(11): control target stdout NOT in pipe — redirection failed (pipe=\"{o1.Replace("\n", "\\n").Trim()}\")");
            try { kill(pid1, SIGKILL); } catch { }
            return 11;
        }
        try { kill(pid1, SIGKILL); } catch { }
        close(rfd1);
        Console.WriteLine($"redirect ok: control marker captured in pipe, isolated from this stdout (pipe=\"{o1.Replace("\n", "\\n").Trim()}\")");

        // ── PART 2 — RegisterForRuntimeStartup on our own suspended spawn ───────────────────
        Console.WriteLine("part 2     : debugger — spawn suspended+redirected, RegisterForRuntimeStartup, SIGCONT, expect callback.");
        int pid2 = SpawnSuspended(exe, argv, out int rfd2);
        if (pid2 == -5) { Console.Error.WriteLine("FAIL(5): pipe()"); return 5; }
        if (pid2 == -6) { Console.Error.WriteLine("FAIL(6): posix_spawn (debugger)"); return 6; }
        var sb2 = new StringBuilder();
        var done2 = new ManualResetEventSlim(false);
        new Thread(() => DrainThread(rfd2, sb2, done2)) { IsBackground = true }.Start();

        Thread.Sleep(700);   // suspend checkpoint — target must NOT have run yet
        if (Text(sb2).Contains(Marker))
        {
            Console.Error.WriteLine("FAIL(7): target produced output BEFORE SIGCONT — suspend did not hold");
            try { kill(pid2, SIGKILL); } catch { }
            return 7;
        }
        Console.WriteLine($"spawned    : pid={pid2} (START_SUSPENDED, no output before SIGCONT)");

        var ctx = new StartupCtx();
        s_ctxHandle = GCHandle.Alloc(ctx);
        nint token = 0;
        nint pCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, void>)&StartupThunk;
        int hrReg = registerForRuntimeStartup((uint)pid2, pCallback, GCHandle.ToIntPtr(s_ctxHandle), &token);
        if (hrReg < 0) { Console.Error.WriteLine($"FAIL(8): RegisterForRuntimeStartup hr=0x{hrReg:X8}"); try { kill(pid2, SIGKILL); } catch { } return 8; }
        Console.WriteLine("registered : RegisterForRuntimeStartup armed (process still suspended)");

        kill(pid2, SIGCONT);
        Console.WriteLine("resumed    : SIGCONT sent");

        bool fired = ctx.Signaled.Wait(TimeSpan.FromSeconds(20));
        if (token != 0) unregisterForRuntimeStartup(token);
        if (!fired) { Console.Error.WriteLine("FAIL(9): startup callback never fired after SIGCONT"); try { kill(pid2, SIGKILL); } catch { } return 9; }
        Console.WriteLine($"callback   : fired (pCordb=0x{ctx.PCordb:X}, hr=0x{ctx.HResult:X8})");
        if (ctx.PCordb == 0 || ctx.HResult < 0) { Console.Error.WriteLine("FAIL(10): callback fired but no ICorDebug / hr<0"); try { kill(pid2, SIGKILL); } catch { } return 10; }

        try { kill(pid2, SIGKILL); } catch { }
        if (s_ctxHandle.IsAllocated) s_ctxHandle.Free();
        close(rfd2);

        Console.WriteLine("PASS(0): own-spawn SUSPENDED isolates stdout to a DrHook pipe AND RegisterForRuntimeStartup fires — Option B viable");
        return 0;
    }

    // posix_spawn the target SUSPENDED with child fd 1,2 -> a fresh pipe's write end. Returns the
    // child pid and (out) the parent's read end; -5 = pipe() failed, -6 = posix_spawn failed.
    static int SpawnSuspended(string exe, string[] argvManaged, out int readFd)
    {
        readFd = -1;
        int* pfds = stackalloc int[2];
        if (pipe(pfds) != 0) return -5;
        int rfd = pfds[0], wfd = pfds[1];

        nint attr = 0, fa = 0;
        posix_spawnattr_init(&attr);
        posix_spawnattr_setflags(&attr, unchecked((short)POSIX_SPAWN_START_SUSPENDED));
        posix_spawn_file_actions_init(&fa);
        posix_spawn_file_actions_adddup2(&fa, wfd, 1);
        posix_spawn_file_actions_adddup2(&fa, wfd, 2);
        posix_spawn_file_actions_addclose(&fa, rfd);
        posix_spawn_file_actions_addclose(&fa, wfd);

        byte** argv = MarshalArgv(argvManaged);
        byte** envp = MarshalArgv(CurrentEnv());
        byte* path = Utf8(exe);

        int pid;
        int rc = posix_spawn(&pid, path, &fa, &attr, argv, envp);
        posix_spawn_file_actions_destroy(&fa);
        posix_spawnattr_destroy(&attr);
        if (rc != 0) { close(rfd); close(wfd); return -6; }

        close(wfd);           // parent drops the write end -> read() hits EOF when the child exits
        readFd = rfd;
        return pid;
    }

    // Resolve how to launch the target by full path (posix_spawn does NOT search PATH). Prefer the
    // build's native apphost (X for X.dll); else the dotnet muxer via DOTNET_HOST_PATH + 'exec'.
    static (string exe, string[] argv) ResolveRun(string dll)
    {
        string apphost = Path.ChangeExtension(dll, null);   // strip .dll -> native apphost path
        if (File.Exists(apphost)) return (apphost, new[] { apphost });
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? (Environment.ProcessPath is { } p && Path.GetFileNameWithoutExtension(p).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? p : "dotnet");
        return (host, new[] { host, "exec", dll });
    }

    static void DrainThread(int readFd, StringBuilder sb, ManualResetEventSlim done)
    {
        byte[] buf = new byte[4096];
        fixed (byte* p = buf)
            for (nint n; (n = read(readFd, p, buf.Length)) > 0;)
                lock (sb) sb.Append(Encoding.UTF8.GetString(buf, 0, (int)n));
        done.Set();
    }

    static string Text(StringBuilder sb) { lock (sb) return sb.ToString(); }

    static byte* Utf8(string s) => (byte*)Marshal.StringToCoTaskMemUTF8(s);

    static byte** MarshalArgv(string[] items)
    {
        byte** arr = (byte**)Marshal.AllocCoTaskMem((items.Length + 1) * sizeof(nint));
        for (int i = 0; i < items.Length; i++) arr[i] = Utf8(items[i]);
        arr[items.Length] = null;
        return arr;
    }

    static string[] CurrentEnv()
    {
        var list = new System.Collections.Generic.List<string>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            list.Add($"{e.Key}={e.Value}");
        return list.ToArray();
    }

    static string? BuildTarget(string csPath)
    {
        var psi = new ProcessStartInfo("dotnet", $"build \"{csPath}\" -c Debug")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        string outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) { Console.Error.WriteLine(outp); return null; }
        foreach (var line in outp.Split('\n'))
        {
            int arrow = line.IndexOf("-> ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                string path = line[(arrow + 3)..].Trim();
                if (path.EndsWith(".dll", StringComparison.Ordinal) && File.Exists(path)) return path;
            }
        }
        return null;
    }

    static string? ResolveDbgShim()
    {
        string? env = Environment.GetEnvironmentVariable("DBGSHIM_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        string lib = OperatingSystem.IsWindows() ? "dbgshim.dll" : OperatingSystem.IsMacOS() ? "libdbgshim.dylib" : "libdbgshim.so";
        string rid = RuntimeInformation.RuntimeIdentifier;
        string baseNative = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", lib);
        if (File.Exists(baseNative)) return baseNative;
        string flat = Path.Combine(AppContext.BaseDirectory, lib);
        if (File.Exists(flat)) return flat;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string pkgRoot = Path.Combine(home, ".nuget", "packages", $"microsoft.diagnostics.dbgshim.{rid}");
        if (Directory.Exists(pkgRoot))
        {
            string? best = null;
            foreach (string v in Directory.EnumerateDirectories(pkgRoot))
            {
                string cand = Path.Combine(v, "runtimes", rid, "native", lib);
                if (File.Exists(cand) && (best is null || string.CompareOrdinal(v, best) > 0)) best = cand;
            }
            return best;
        }
        return null;
    }
}
