// Native libdbgshim interop — the bridge to ICorDebug. libdbgshim is a native
// runtime-substrate asset (ADR-009 clarification), not a managed dependency; it is loaded
// via NativeLibrary and called through cdecl function pointers. Ported from PoC probes
// 02/06. The corrected attach flow (finding 04): EnumerateCLRs-with-retry returns coreclr
// MODULE PATHS (not versions); CreateVersionStringFromModule converts a path to the opaque
// version token; CreateDebuggingInterfaceFromVersionEx(CorDebugVersion_4_0) yields IUnknown.

using System.Globalization;
using System.Runtime.InteropServices;

namespace SkyOmega.DrHook.Engine.Interop;

internal sealed unsafe class DbgShim : IDisposable
{
    private const int CorDebugVersion_4_0 = 4;
    private static readonly nint InvalidHandleValue = -1;
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int E_INSUFFICIENT_BUFFER = unchecked((int)0x8007007A);

    private readonly nint _lib;
    private readonly delegate* unmanaged[Cdecl]<uint, nint*, nint*, uint*, int> _enumerateCLRs;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, uint, int> _closeCLREnumeration;
    private readonly delegate* unmanaged[Cdecl]<uint, char*, char*, uint, uint*, int> _createVersionStringFromModule;
    private readonly delegate* unmanaged[Cdecl]<int, char*, nint*, int> _createDebuggingInterfaceFromVersionEx;

    private DbgShim(nint lib)
    {
        _lib = lib;
        _enumerateCLRs = (delegate* unmanaged[Cdecl]<uint, nint*, nint*, uint*, int>)NativeLibrary.GetExport(lib, "EnumerateCLRs");
        _closeCLREnumeration = (delegate* unmanaged[Cdecl]<nint, nint, uint, int>)NativeLibrary.GetExport(lib, "CloseCLREnumeration");
        _createVersionStringFromModule = (delegate* unmanaged[Cdecl]<uint, char*, char*, uint, uint*, int>)NativeLibrary.GetExport(lib, "CreateVersionStringFromModule");
        _createDebuggingInterfaceFromVersionEx = (delegate* unmanaged[Cdecl]<int, char*, nint*, int>)NativeLibrary.GetExport(lib, "CreateDebuggingInterfaceFromVersionEx");
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

        // Bundled alongside the engine (once the native asset is packaged).
        string bundled = Path.Combine(AppContext.BaseDirectory, libName);
        tried.Add(bundled + "  (app base)");
        if (File.Exists(bundled)) { searched = string.Join('\n', tried); return bundled; }

        // Pre-.NET-7 runtimes shipped it in the runtime directory.
        string runtimeDir = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), libName);
        tried.Add(runtimeDir + "  (runtime dir)");
        if (File.Exists(runtimeDir)) { searched = string.Join('\n', tried); return runtimeDir; }

        // NuGet cache: microsoft.diagnostics.dbgshim.<rid>/<version>/runtimes/<rid>/native/.
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

    public void Dispose()
    {
        if (_lib != 0)
            NativeLibrary.Free(_lib);
    }
}
