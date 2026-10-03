// Native libdbgshim interop — the bridge to ICorDebug. libdbgshim is a native
// runtime-substrate asset (ADR-009 clarification), not a managed dependency; it is loaded
// via NativeLibrary and called through cdecl function pointers. Ported from PoC probes
// 02/06. The attach flow: locate the target's runtime module (finding 88 — the BCL module list, NOT
// dbgshim's EnumerateCLRs); CreateVersionStringFromModule converts that path to the opaque version
// token; CreateDebuggingInterfaceFromVersionEx(CorDebugVersion_4_0) yields IUnknown.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SkyOmega.DrHook.Engine.Interop;

internal sealed unsafe class DbgShim : IDisposable
{
    private const int CorDebugVersion_4_0 = 4;
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int E_INSUFFICIENT_BUFFER = unchecked((int)0x8007007A);

    private nint _lib;  // not readonly — Dispose zeros it after Free (ENG-DBG-D)
    private readonly delegate* unmanaged[Cdecl]<uint, char*, char*, uint, uint*, int> _createVersionStringFromModule;
    private readonly delegate* unmanaged[Cdecl]<int, char*, nint*, int> _createDebuggingInterfaceFromVersionEx;
    // Launch path (RegisterForRuntimeStartup flow).
    private readonly delegate* unmanaged[Cdecl]<char*, int, nint, char*, uint*, nint*, int> _createProcessForLaunch;
    private readonly delegate* unmanaged[Cdecl]<nint, int> _resumeProcess;
    private readonly delegate* unmanaged[Cdecl]<nint, int> _closeResumeHandle;
    private readonly delegate* unmanaged[Cdecl]<uint, nint, nint, nint*, int> _registerForRuntimeStartup;
    private readonly delegate* unmanaged[Cdecl]<nint, int> _unregisterForRuntimeStartup;

    private DbgShim(nint lib)
    {
        _lib = lib;
        _createVersionStringFromModule = (delegate* unmanaged[Cdecl]<uint, char*, char*, uint, uint*, int>)NativeLibrary.GetExport(lib, "CreateVersionStringFromModule");
        _createDebuggingInterfaceFromVersionEx = (delegate* unmanaged[Cdecl]<int, char*, nint*, int>)NativeLibrary.GetExport(lib, "CreateDebuggingInterfaceFromVersionEx");
        _createProcessForLaunch = (delegate* unmanaged[Cdecl]<char*, int, nint, char*, uint*, nint*, int>)NativeLibrary.GetExport(lib, "CreateProcessForLaunch");
        _resumeProcess = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(lib, "ResumeProcess");
        _closeResumeHandle = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(lib, "CloseResumeHandle");
        _registerForRuntimeStartup = (delegate* unmanaged[Cdecl]<uint, nint, nint, nint*, int>)NativeLibrary.GetExport(lib, "RegisterForRuntimeStartup");
        _unregisterForRuntimeStartup = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(lib, "UnregisterForRuntimeStartup");
    }

    /// <summary>Locate and load libdbgshim. Throws if it cannot be found or loaded.</summary>
    public static DbgShim Load()
    {
        string? path = Resolve(out string searched);
        if (path is null)
            throw new DllNotFoundException(
                "libdbgshim not found. It left the .NET runtime install at .NET 7+; set DBGSHIM_PATH " +
                "or restore the Microsoft.Diagnostics.DbgShim native-asset NuGet. Searched:\n" + searched);
        return new DbgShim(NativeLibrary.Load(path));
    }

    /// <summary>Run the attach flow for <paramref name="pid"/> and produce an <c>IUnknown*</c> that QIs to
    /// ICorDebug. Returns the final HRESULT; on success <paramref name="pUnknown"/> is non-zero (the caller owns
    /// the reference).
    ///
    /// FINDING 88 — why not dbgshim's <c>EnumerateCLRs</c>: to detect single-file apps it opens the on-disk file
    /// of EVERY module mapped into the target and parses its Mach-O symbol table, and its reader dereferences a
    /// missing <c>LC_SYMTAB</c> unchecked (guarded only by a debug-build assert). On macOS 27 / Apple M5 every
    /// Metal process maps a GPU-driver shader library (<c>AGXMetalG17X.bundle/…/ds.binary.metallib.g17s-a0</c>)
    /// that is a valid Mach-O with no <c>LC_SYMTAB</c> — so attaching to any Metal GUI app SEGV'd the DEBUGGER
    /// host (dbgshim 9.0 and 10.0 alike). The BCL module list (<see cref="System.Diagnostics.Process.Modules"/>)
    /// finds the runtime module without parsing anything, and <c>CreateVersionStringFromModule</c> parses only
    /// the one file it is handed.
    ///
    /// Resolution: the shared-framework runtime (<c>libcoreclr.dylib</c> / <c>libcoreclr.so</c> / <c>coreclr.dll</c>)
    /// among the target's MAPPED FILES (<see cref="MappedFiles"/>), polled for <see cref="RuntimeModuleWait"/>
    /// (finding 04: a freshly started target may not have loaded it yet). Only when no runtime module appears
    /// within that window is the target treated as a SINGLE-FILE app (runtime embedded in its host) and its main
    /// executable used. The order is load-bearing: on Unix <c>CreateVersionStringFromModule</c> does NOT validate
    /// the module (it calls <c>GetTargetCLRMetrics</c> without the ClrInfo out-param, which is what gates the
    /// single-file check) — it returns a version string for ANY mapped file, and a framework-dependent app host
    /// handed over before its runtime loads yields a bogus token (<c>0x80131C3C</c> at the next step, a clean
    /// failure, never a crash).</summary>
    public int CreateCordbForProcess(int pid, out nint pUnknown)
    {
        pUnknown = 0;

        string? runtimeModule = null;
        DateTime deadline = DateTime.UtcNow + RuntimeModuleWait;
        while ((runtimeModule = MappedFiles.FindByFileName(pid, RuntimeModuleName)) is null && DateTime.UtcNow < deadline)
            Thread.Sleep(100);
        runtimeModule ??= SingleFileHostOf(pid);
        if (runtimeModule is null)
            return E_FAIL;

        string? version = CreateVersionString((uint)pid, runtimeModule);
        if (version is null)
            return E_FAIL;

        nint cordb;
        int hr;
        fixed (char* pVersion = version)
            hr = _createDebuggingInterfaceFromVersionEx(CorDebugVersion_4_0, pVersion, &cordb);
        if (hr < 0 || cordb == 0)
            return hr < 0 ? hr : E_FAIL;

        pUnknown = cordb;
        return 0;
    }

    /// <summary>Upper bound on waiting for the target's runtime module to be mapped (finding 04's 100 ms x 30).</summary>
    private static readonly TimeSpan RuntimeModuleWait = TimeSpan.FromSeconds(3);

    private static readonly string RuntimeModuleName =
        OperatingSystem.IsWindows() ? "coreclr.dll" :
        OperatingSystem.IsMacOS() ? "libcoreclr.dylib" :
                                    "libcoreclr.so";

    // A single-file app's runtime lives in its main executable. Null if the target is gone.
    private static string? SingleFileHostOf(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.MainModule?.FileName;
        }
        catch (ArgumentException) { return null; }                         // target exited
        catch (InvalidOperationException) { return null; }                 // target exited
        catch (System.ComponentModel.Win32Exception) { return null; }      // main module unreadable
    }

    private string? CreateVersionString(uint pid, string modulePath)
    {
        uint cch = 100;
        fixed (char* pModule = modulePath)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                char[] buf = new char[cch];
                uint needed = 0;
                int hr;
                fixed (char* pBuf = buf)
                    hr = _createVersionStringFromModule(pid, pModule, pBuf, cch, &needed);
                if (hr >= 0)
                {
                    int nul = Array.IndexOf(buf, '\0');
                    return new string(buf, 0, nul < 0 ? buf.Length : nul);
                }
                if (hr == E_INSUFFICIENT_BUFFER && needed > cch)
                {
                    cch = needed;
                    continue;
                }
                return null;
            }
        }
        return null;
    }

    private static string? Resolve(out string searched)
    {
        List<string> tried = new();

        // 1. Explicit override — DBGSHIM_PATH for testing a custom build. No consumer relies
        //    on this for default operation; it's the "ssh into the engine room" knob.
        string? env = Environment.GetEnvironmentVariable("DBGSHIM_PATH");
        if (!string.IsNullOrEmpty(env))
        {
            tried.Add(env + "  (DBGSHIM_PATH)");
            if (File.Exists(env)) { searched = string.Join('\n', tried); return env; }
        }

        string libName =
            OperatingSystem.IsWindows() ? "dbgshim.dll" :
            OperatingSystem.IsMacOS() ? "libdbgshim.dylib" :
                                        "libdbgshim.so";
        string rid = RuntimeInformation.RuntimeIdentifier;

        // 2. Bundled via per-RID Microsoft.Diagnostics.DbgShim.<rid> package — the package's
        //    native asset deploys to bin/<config>/<tfm>/runtimes/<rid>/native/. PRIMARY path for
        //    production and dev once the engine is referenced as a PackageReference.
        string runtimesNative = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", libName);
        tried.Add(runtimesNative + "  (app base — runtimes/<rid>/native)");
        if (File.Exists(runtimesNative)) { searched = string.Join('\n', tried); return runtimesNative; }

        // 3. Bundled directly at AppContext.BaseDirectory — rare layout (some packaging
        //    strategies flatten native assets next to the assembly), kept as a safety net.
        string flat = Path.Combine(AppContext.BaseDirectory, libName);
        tried.Add(flat + "  (app base — flat)");
        if (File.Exists(flat)) { searched = string.Join('\n', tried); return flat; }

        // 4. Pre-.NET-7 runtimes shipped it in the runtime directory (defunct on .NET 7+).
        string runtimeDir = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), libName);
        tried.Add(runtimeDir + "  (runtime dir)");
        if (File.Exists(runtimeDir)) { searched = string.Join('\n', tried); return runtimeDir; }

        // 5. NuGet cache — found if a Microsoft.Diagnostics.DbgShim.<rid> happens to be
        //    restored locally but not deployed (e.g. a naked `dotnet build` against an older
        //    csproj that didn't bundle). Fallback for legacy / external scenarios.
        string? cached = FindInNuGetCache(libName, tried);
        if (cached is not null) { searched = string.Join('\n', tried); return cached; }

        searched = string.Join('\n', tried);
        return null;
    }

    private static string? FindInNuGetCache(string libName, List<string> tried)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string pkgRoot = Path.Combine(home, ".nuget", "packages", $"microsoft.diagnostics.dbgshim.{rid}");
        tried.Add(pkgRoot + "/*/runtimes/" + rid + "/native/  (nuget cache)");
        if (!Directory.Exists(pkgRoot))
            return null;

        string? best = null;
        foreach (string versionDir in Directory.EnumerateDirectories(pkgRoot))
        {
            string candidate = Path.Combine(versionDir, "runtimes", rid, "native", libName);
            if (File.Exists(candidate) && (best is null || string.CompareOrdinal(versionDir, best) > 0))
                best = candidate;
        }
        return best;
    }

    /// <summary>Launch a process under debug control using dbgshim's RegisterForRuntimeStartup flow:
    /// (1) <c>CreateProcessForLaunch</c> spawns the process SUSPENDED so it can't run before we
    /// register; (2) <c>RegisterForRuntimeStartup</c> installs the static callback that delivers an
    /// <c>ICorDebug</c> <c>IUnknown*</c> once the runtime has initialized; (3) <c>ResumeProcess</c>
    /// + <c>CloseResumeHandle</c> let the process run; (4) we wait on the startup event. On success
    /// <paramref name="pid"/> + <paramref name="pUnknown"/> are non-zero; the caller still calls
    /// <c>DebugActiveProcess</c> on the cordbg to complete the attach (same as the Attach path from
    /// <see cref="CreateCordbForProcess"/>'s output onward).</summary>
    public int LaunchWithDebugger(string commandLine, string? workingDirectory, TimeSpan startupTimeout,
        out uint pid, out nint pUnknown)
    {
        pid = 0;
        pUnknown = 0;

        nint resumeHandle = 0;
        uint launchedPid = 0;
        int hr;
        fixed (char* pCmd = commandLine)
        fixed (char* pCwd = workingDirectory)
        {
            // CreateProcessForLaunch(cmdLine, suspend=TRUE, env=null/inherit, cwd, &pid, &resumeHandle)
            hr = _createProcessForLaunch(pCmd, 1, 0, pCwd, &launchedPid, &resumeHandle);
            if (hr < 0) return hr;
        }
        pid = launchedPid;

        var ctx = new StartupContext();
        GCHandle handle = GCHandle.Alloc(ctx);
        nint pContext = GCHandle.ToIntPtr(handle);

        nint unregisterToken = 0;
        try
        {
            nint pCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, void>)&StartupCallbackThunk;
            hr = _registerForRuntimeStartup(launchedPid, pCallback, pContext, &unregisterToken);
            if (hr < 0)
            {
                _closeResumeHandle(resumeHandle);
                return hr;
            }

            // The process is still suspended — release it now that the callback is armed.
            _resumeProcess(resumeHandle);
            _closeResumeHandle(resumeHandle);

            if (!ctx.Signaled.Wait(startupTimeout))
                return E_FAIL; // runtime didn't initialize within the budget

            if (ctx.HResult < 0) return ctx.HResult;
            pUnknown = ctx.PCordb;
            return 0;
        }
        finally
        {
            if (unregisterToken != 0) _unregisterForRuntimeStartup(unregisterToken);
            handle.Free();
            ctx.Signaled.Dispose();
        }
    }

    /// <summary>POSIX launch with ISOLATED stdio (ADR-011 D2 / finding 75). Unlike
    /// <see cref="LaunchWithDebugger"/> — which uses dbgshim's <c>CreateProcessForLaunch</c> and so
    /// makes the child inherit the debugger process's stdin/stdout/stderr — this posix_spawnp's the
    /// target SUSPENDED with stdout/stderr redirected to DrHook-owned pipes, arms
    /// <c>RegisterForRuntimeStartup</c>, then <c>SIGCONT</c>s it. The launched debuggee's console
    /// output therefore cannot reach the parent's fds, which under an MCP stdio server are the
    /// JSON-RPC channel. On success <paramref name="pid"/> + <paramref name="pUnknown"/> are set and
    /// the caller owns draining/closing <paramref name="stdoutFd"/> + <paramref name="stderrFd"/>;
    /// the caller still <c>DebugActiveProcess</c>es to complete the attach (same as the other paths).</summary>
    public int LaunchWithDebuggerPosix(string program, IReadOnlyList<string> args, string? workingDirectory,
        IReadOnlyDictionary<string, string>? env, TimeSpan startupTimeout, out uint pid, out nint pUnknown, out int stdoutFd, out int stderrFd)
    {
        pid = 0;
        pUnknown = 0;
        stdoutFd = -1;
        stderrFd = -1;

        string[] argv = new string[args.Count + 1];
        argv[0] = program;
        for (int i = 0; i < args.Count; i++) argv[i + 1] = args[i];

        int spawnRc = PosixSpawn.SpawnSuspendedRedirected(program, argv, workingDirectory, env, out int childPid, out int oFd, out int eFd);
        if (spawnRc != 0) return E_FAIL;
        pid = (uint)childPid;

        var ctx = new StartupContext();
        GCHandle handle = GCHandle.Alloc(ctx);
        nint pContext = GCHandle.ToIntPtr(handle);
        nint unregisterToken = 0;
        try
        {
            nint pCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, void>)&StartupCallbackThunk;
            int hr = _registerForRuntimeStartup((uint)childPid, pCallback, pContext, &unregisterToken);
            if (hr < 0) { PosixSpawn.Kill(childPid); PosixSpawn.Close(oFd); PosixSpawn.Close(eFd); return hr; }

            // Callback armed — release the suspended child. START_SUSPENDED guaranteed the CLR could
            // not have initialized before now, so the runtime-startup callback is not missed.
            PosixSpawn.Continue(childPid);

            if (!ctx.Signaled.Wait(startupTimeout)) { PosixSpawn.Kill(childPid); PosixSpawn.Close(oFd); PosixSpawn.Close(eFd); return E_FAIL; }
            if (ctx.HResult < 0) { PosixSpawn.Kill(childPid); PosixSpawn.Close(oFd); PosixSpawn.Close(eFd); return ctx.HResult; }

            pUnknown = ctx.PCordb;
            stdoutFd = oFd;
            stderrFd = eFd;
            return 0;
        }
        finally
        {
            if (unregisterToken != 0) _unregisterForRuntimeStartup(unregisterToken);
            handle.Free();
            ctx.Signaled.Dispose();
        }
    }

    /// <summary>The startup callback parameter — a <c>GCHandle.ToIntPtr</c> of one of these is passed
    /// to <c>RegisterForRuntimeStartup</c>, and the static thunk publishes the result here. Reference
    /// type so the GCHandle keeps it pinned for the dbgshim's native thread to write into.</summary>
    private sealed class StartupContext
    {
        public nint PCordb;
        public int HResult;
        public readonly ManualResetEventSlim Signaled = new(false);
    }

    // SUBSTRATE RULE 1 — O(1)-stack thunk (ENG-STK-3, finding 55):
    // Runs on libdbgshim's internal startup thread, whose stack budget we do NOT own. Must
    // stay O(1) stack: GCHandle.FromIntPtr + field writes + Signaled.Set. NO stackalloc,
    // NO recursion, NO synchronous user code. Phase 8 IL-size test guards against drift.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void StartupCallbackThunk(nint pCordb, nint parameter, int hr)
    {
        // dbgshim fires this from its internal thread once the runtime has initialized. Recover
        // the context via the GCHandle and signal the waiter.
        if (parameter == 0) return;
        GCHandle h = GCHandle.FromIntPtr(parameter);
        if (h.Target is StartupContext ctx)
        {
            ctx.PCordb = pCordb;
            ctx.HResult = hr;
            ctx.Signaled.Set();
        }
    }

    public void Dispose()
    {
        // Zero _lib after Free so a concurrent Dispose can't double-dlclose
        // (T7 in finding 54; macOS dlclose-on-already-freed is undefined).
        if (_lib != 0)
        {
            NativeLibrary.Free(_lib);
            _lib = 0;
        }
    }
}
