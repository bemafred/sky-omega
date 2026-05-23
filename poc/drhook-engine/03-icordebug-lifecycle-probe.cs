#!/usr/bin/env -S dotnet
#:property AllowUnsafeBlocks=true
//
// DrHook.Engine PoC — Probe 03: ICorDebug Initialize/Terminate via source-generated COM
// =====================================================================================
//
// Hypothesis (findings 03 + 05):
//   Source-generated COM ([GeneratedComInterface] + StrategyBasedComWrappers) can wrap
//   dbgshim's IUnknown* as a typed ICorDebug, and the Initialize() -> Terminate()
//   lifecycle behaves per the cordebug.idl contract — WITHOUT attaching to any process.
//   Finding 03: Initialize + Terminate with no debuggee is a valid lifecycle (the
//   "Terminate forbidden until ExitProcess fires for all attached processes" constraint
//   doesn't apply when nothing is attached).
//
// New variable vs probe 02:
//   Probe 02 used raw Marshal.QueryInterface to confirm the pointer is ICorDebug.
//   Probe 03 swaps that for source-generated COM (the mechanism probe 04's callback
//   V-table needs) and adds the lifecycle calls. The attach flow up to the IUnknown*
//   is identical to probe 02 — that part is already validated, so any failure here
//   isolates to source-gen COM or the lifecycle.
//
// This probe also falsifies a tooling question: does [GeneratedComInterface] source
// generation work in a .NET 10 file-based app? If not, that's a finding (engine probes
// past 02 need a real .csproj for source-gen COM).
//
// Falsification ladder (exit codes): 2 usage; 3 dbgshim-not-found; 4 EnumerateCLRs;
//   5 CreateVersionStringFromModule; 6 CreateDebuggingInterfaceFromVersionEx;
//   8 ComWrappers wrap/cast; 9 Initialize; 10 Terminate.
//   (7 — probe 02's manual-QI step — is replaced here by ComWrappers.)
//
// Usage:  dotnet 03-icordebug-lifecycle-probe.cs <pid>   (DBGSHIM_PATH override supported)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;

return Probe03.Run(args);

// ICorDebug — minimal source-generated view (cordebug.idl, finding 03).
// The generator lays out the V-table in declaration order after IUnknown's three slots,
// so methods MUST be declared in IDL order: Initialize (slot 3) then Terminate (slot 4).
// Declaring a prefix of the interface is valid — we only call these two.
// [PreserveSig] keeps the HRESULT explicit (finding 05): debugger HRESULTs include
// informational success codes (S_FALSE, CORDBG_S_*) that must not throw.
[GeneratedComInterface]
[Guid("3d6f5f61-7538-11d3-8d5b-00104b35e7ef")]
internal partial interface ICorDebug
{
    [PreserveSig] int Initialize();
    [PreserveSig] int Terminate();
}

static unsafe class Probe03
{
    const int CorDebugVersion_4_0 = 4;
    static readonly nint INVALID_HANDLE_VALUE = -1;
    const int E_INVALIDARG = unchecked((int)0x80070057);
    const int E_FAIL = unchecked((int)0x80004005);
    const int E_INSUFFICIENT_BUFFER = unchecked((int)0x8007007A);

    public static int Run(string[] args)
    {
        if (args.Length < 1 ||
            !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
        {
            Console.Error.WriteLine("Usage: dotnet 03-icordebug-lifecycle-probe.cs <pid>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"os-arch    : {RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"target pid : {pid}");

        // ---- attach flow (identical to probe 02) -> pUnknown ----
        string? dbgshimPath = ResolveDbgShim(out string searched);
        if (dbgshimPath is null) { Console.Error.WriteLine($"FALSIFIED (discovery): dbgshim not found.\n{searched}"); return 3; }
        Console.WriteLine($"dbgshim    : {dbgshimPath}");
        nint lib = NativeLibrary.Load(dbgshimPath);

        var enumerateCLRs = (delegate* unmanaged[Cdecl]<uint, nint*, nint*, uint*, int>)NativeLibrary.GetExport(lib, "EnumerateCLRs");
        var closeCLREnumeration = (delegate* unmanaged[Cdecl]<nint, nint, uint, int>)NativeLibrary.GetExport(lib, "CloseCLREnumeration");
        var createVersionStringFromModule = (delegate* unmanaged[Cdecl]<uint, char*, char*, uint, uint*, int>)NativeLibrary.GetExport(lib, "CreateVersionStringFromModule");
        var createDebuggingInterfaceFromVersionEx = (delegate* unmanaged[Cdecl]<int, char*, nint*, int>)NativeLibrary.GetExport(lib, "CreateDebuggingInterfaceFromVersionEx");

        nint handleArray = 0, stringArray = 0;
        uint count = 0; int retries = 0;
        int hr = EnumerateWithRetry(enumerateCLRs, closeCLREnumeration, (uint)pid, &handleArray, &stringArray, &count, ref retries);
        if (hr < 0 || count == 0) { Console.Error.WriteLine($"FALSIFIED (EnumerateCLRs): hr=0x{hr:X8} count={count}"); NativeLibrary.Free(lib); return 4; }
        string modulePath = Marshal.PtrToStringUni(((nint*)stringArray)[0]) ?? "";
        Console.WriteLine($"coreclr    : {modulePath}");

        string? version = GetVersionString(createVersionStringFromModule, (uint)pid, modulePath);
        if (version is null) { closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib); return 5; }
        Console.WriteLine($"version    : {version}");

        nint pUnknown = 0;
        fixed (char* pVersion = version) hr = createDebuggingInterfaceFromVersionEx(CorDebugVersion_4_0, pVersion, &pUnknown);
        if (hr < 0 || pUnknown == 0) { Console.Error.WriteLine($"FALSIFIED (CreateDebuggingInterfaceFromVersionEx): hr=0x{hr:X8}"); closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib); return 6; }
        Console.WriteLine($"IUnknown   : 0x{pUnknown:X}");

        // ---- NEW: source-generated COM wrap -> typed ICorDebug ----
        ICorDebug iCorDebug;
        try
        {
            ComWrappers cw = new StrategyBasedComWrappers();
            iCorDebug = (ICorDebug)cw.GetOrCreateObjectForComInstance(pUnknown, CreateObjectFlags.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (ComWrappers wrap): {ex.GetType().Name}: {ex.Message}");
            Marshal.Release(pUnknown); closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib);
            return 8;
        }
        Console.WriteLine("ICorDebug  : source-gen RCW created (StrategyBasedComWrappers)");

        // ---- NEW: lifecycle Initialize() -> Terminate() (finding 03) ----
        int hrInit = iCorDebug.Initialize();
        Console.WriteLine($"Initialize : hr=0x{hrInit:X8}");
        if (hrInit < 0)
        {
            Console.Error.WriteLine("FALSIFIED (Initialize)");
            Marshal.Release(pUnknown); closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib);
            return 9;
        }

        int hrTerm = iCorDebug.Terminate();
        Console.WriteLine($"Terminate  : hr=0x{hrTerm:X8}");
        if (hrTerm < 0)
        {
            Console.Error.WriteLine("FALSIFIED (Terminate)");
            Marshal.Release(pUnknown); closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib);
            return 10;
        }

        WriteFixture(pid, modulePath, version, hrInit, hrTerm);

        // ---- cleanup ----
        // Release my own reference (from CreateDebuggingInterfaceFromVersionEx). The
        // ComWrappers RCW holds its OWN AddRef'd reference, released at GC / process exit.
        // GetOrCreateObjectForComInstance AddRefs the instance; it does not assume
        // ownership of the caller's reference — so releasing here is correct, not a
        // double-release. Deterministic RCW disposal isn't needed for a short-lived probe.
        int rc = Marshal.Release(pUnknown);
        closeCLREnumeration(handleArray, stringArray, count);
        NativeLibrary.Free(lib);
        GC.KeepAlive(iCorDebug); // keep the RCW alive across the calls above
        Console.WriteLine($"released   : my IUnknown ref -> {rc} (ComWrappers RCW ref GC-managed)");
        Console.WriteLine("PROBE 03 PASSED");
        return 0;
    }

    static int EnumerateWithRetry(
        delegate* unmanaged[Cdecl]<uint, nint*, nint*, uint*, int> enumerate,
        delegate* unmanaged[Cdecl]<nint, nint, uint, int> close,
        uint pid, nint* pHandleArray, nint* pStringArray, uint* pCount, ref int retries)
    {
        const int maxTries = 30;
        int hr = 0;
        for (int tries = 0; tries < maxTries; tries++)
        {
            retries = tries;
            hr = enumerate(pid, pHandleArray, pStringArray, pCount);
            if (hr >= 0 && *pHandleArray != 0 && *pCount > 0)
            {
                if (AllHandlesValid(*pHandleArray, *pCount)) return hr;
                close(*pHandleArray, *pStringArray, *pCount);
                *pHandleArray = 0; *pStringArray = 0; *pCount = 0;
            }
            if (hr == E_INVALIDARG || hr == E_FAIL) return hr;
            Thread.Sleep(100);
        }
        return hr;
    }

    static bool AllHandlesValid(nint handleArray, uint count)
    {
        for (uint i = 0; i < count; i++)
            if (((nint*)handleArray)[i] == INVALID_HANDLE_VALUE) return false;
        return true;
    }

    static string? GetVersionString(
        delegate* unmanaged[Cdecl]<uint, char*, char*, uint, uint*, int> createVersion,
        uint pid, string modulePath)
    {
        uint cch = 100;
        fixed (char* pModule = modulePath)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                char[] buf = new char[cch];
                uint needed = 0;
                int hr;
                fixed (char* pBuf = buf) hr = createVersion(pid, pModule, pBuf, cch, &needed);
                if (hr >= 0)
                {
                    int nul = Array.IndexOf(buf, '\0');
                    return new string(buf, 0, nul < 0 ? buf.Length : nul);
                }
                if (hr == E_INSUFFICIENT_BUFFER && needed > cch) { cch = needed; continue; }
                Console.Error.WriteLine($"  CreateVersionStringFromModule hr=0x{hr:X8}");
                return null;
            }
        }
        return null;
    }

    static string? ResolveDbgShim(out string searched)
    {
        var tried = new List<string>();
        string? env = Environment.GetEnvironmentVariable("DBGSHIM_PATH");
        if (!string.IsNullOrEmpty(env))
        {
            tried.Add(env + "  (DBGSHIM_PATH)");
            if (File.Exists(env)) { searched = string.Join("\n", tried); return env; }
        }
        string libName =
            OperatingSystem.IsWindows() ? "dbgshim.dll" :
            OperatingSystem.IsMacOS()   ? "libdbgshim.dylib" :
                                          "libdbgshim.so";
        string candidate = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), libName);
        tried.Add(candidate + "  (runtime dir)");
        if (File.Exists(candidate)) { searched = string.Join("\n", tried); return candidate; }

        string rid = RuntimeInformation.RuntimeIdentifier;
        string pkgRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", $"microsoft.diagnostics.dbgshim.{rid}");
        tried.Add(pkgRoot + "/*/runtimes/" + rid + "/native/  (nuget cache)");
        if (Directory.Exists(pkgRoot))
        {
            string? best = null;
            foreach (string versionDir in Directory.EnumerateDirectories(pkgRoot))
            {
                string c = Path.Combine(versionDir, "runtimes", rid, "native", libName);
                if (File.Exists(c) && (best is null || string.CompareOrdinal(versionDir, best) > 0))
                    best = c;
            }
            if (best is not null) { searched = string.Join("\n", tried); return best; }
        }

        searched = string.Join("\n", tried) +
                   "\n(note: dbgshim left the runtime at .NET 7+; set DBGSHIM_PATH or `dotnet add package Microsoft.Diagnostics.DbgShim." + rid + "` to populate the NuGet cache)";
        return null;
    }

    static void WriteFixture(int pid, string modulePath, string version, int hrInit, int hrTerm)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"03-icordebug-lifecycle-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 03 fixture — ICorDebug Initialize/Terminate via source-gen COM\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"coreclr-path     = {modulePath}\n" +
            $"version-string   = {version}\n" +
            "comwrappers-wrap = OK (StrategyBasedComWrappers + [GeneratedComInterface])\n" +
            $"initialize-hr    = 0x{hrInit:X8}\n" +
            $"terminate-hr     = 0x{hrTerm:X8}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
