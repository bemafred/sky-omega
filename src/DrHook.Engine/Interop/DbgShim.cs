// Native libdbgshim interop — the bridge to ICorDebug. libdbgshim is a native
// runtime-substrate asset (ADR-009 clarification), not a managed dependency; it is loaded
// via NativeLibrary and called through cdecl function pointers. Ported from PoC probes
// 02/06. The corrected attach flow (finding 04): EnumerateCLRs-with-retry returns coreclr
// MODULE PATHS (not versions); CreateVersionStringFromModule converts a path to the opaque
// version token; CreateDebuggingInterfaceFromVersionEx(CorDebugVersion_4_0) yields IUnknown.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SkyOmega.DrHook.Engine.Interop;

internal sealed unsafe class DbgShim : IDisposable
{
    private const int CorDebugVersion_4_0 = 4;
    private static readonly nint InvalidHandleValue = -1;
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int E_INSUFFICIENT_BUFFER = unchecked((int)0x8007007A);

    private nint _lib;  // not readonly — Dispose zeros it after Free (ENG-DBG-D)
    private readonly delegate* unmanaged[Cdecl]<uint, nint*, nint*, uint*, int> _enumerateCLRs;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, uint, int> _closeCLREnumeration;
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
        _enumerateCLRs = (delegate* unmanaged[Cdecl]<uint, nint*, nint*, uint*, int>)NativeLibrary.GetExport(lib, "EnumerateCLRs");
        _closeCLREnumeration = (delegate* unmanaged[Cdecl]<nint, nint, uint, int>)NativeLibrary.GetExport(lib, "CloseCLREnumeration");
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

    /// <summary>Run the corrected attach flow for <paramref name="pid"/> and produce an
    /// <c>IUnknown*</c> that QIs to ICorDebug. Returns the final HRESULT; on success
    /// <paramref name="pUnknown"/> is non-zero (the caller owns the reference).</summary>
    public int CreateCordbForProcess(int pid, out nint pUnknown)
    {
        pUnknown = 0;

        nint handleArray = 0, stringArray = 0;
        uint count = 0;
        int hr = EnumerateWithRetry((uint)pid, &handleArray, &stringArray, &count);
        if (hr < 0 || count == 0)
            return hr < 0 ? hr : E_FAIL;

        string modulePath = Marshal.PtrToStringUni(((nint*)stringArray)[0]) ?? string.Empty;
        _closeCLREnumeration(handleArray, stringArray, count);
        if (modulePath.Length == 0)
            return E_FAIL;

        string? version = CreateVersionString((uint)pid, modulePath);
        if (version is null)
            return E_FAIL;

        nint cordb;
        fixed (char* pVersion = version)
            hr = _createDebuggingInterfaceFromVersionEx(CorDebugVersion_4_0, pVersion, &cordb);
        if (hr < 0 || cordb == 0)
            return hr < 0 ? hr : E_FAIL;

        pUnknown = cordb;
        return 0;
    }

    // finding 04: EnumerateCLRs needs retry — the coreclr-mid-load race returns
    // INVALID_HANDLE_VALUE handles; a freshly-started target may not have loaded coreclr yet.
    private int EnumerateWithRetry(uint pid, nint* pHandleArray, nint* pStringArray, uint* pCount)
    {
        const int maxTries = 30; // 100ms x 30 = 3s
        int hr = 0;
        for (int tries = 0; tries < maxTries; tries++)
        {
            hr = _enumerateCLRs(pid, pHandleArray, pStringArray, pCount);
            if (hr >= 0 && *pHandleArray != 0 && *pCount > 0)
            {
                if (AllHandlesValid(*pHandleArray, *pCount))
                    return hr;
                _closeCLREnumeration(*pHandleArray, *pStringArray, *pCount);
                *pHandleArray = 0; *pStringArray = 0; *pCount = 0;
            }
            if (hr == E_INVALIDARG || hr == E_FAIL)
                return hr;
            Thread.Sleep(100);
        }
        return hr;
    }

    private static bool AllHandlesValid(nint handleArray, uint count)
    {
        for (uint i = 0; i < count; i++)
            if (((nint*)handleArray)[i] == InvalidHandleValue)
                return false;
        return true;
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
