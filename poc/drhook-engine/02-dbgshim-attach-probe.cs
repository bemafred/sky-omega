#!/usr/bin/env -S dotnet
#:property AllowUnsafeBlocks=true
//
// DrHook.Engine PoC — Probe 02: dbgshim attach + QueryInterface(ICorDebug)
// ==========================================================================
//
// Hypothesis (finding 04 + 05):
//   A BCL + P/Invoke client can attach to a running .NET process via dbgshim
//   and obtain a pointer that QueryInterfaces to ICorDebug, then release
//   cleanly — with neither netcoredbg nor Microsoft.Diagnostics.NETCore.Client.
//
// Scope discipline (EEE):
//   Probe 02 uses ONLY P/Invoke + Marshal.QueryInterface / Marshal.Release on
//   the raw IntPtr. NO source-generated COM yet — that is probe 03's new
//   variable, tested alongside the Initialize/Terminate lifecycle. Keeping the
//   two apart isolates the dbgshim attach flow as the single thing under test
//   here. Probe 02 does NOT call DebugActiveProcess — it never attaches the
//   debugger, so it is non-invasive to the target (safe against any live
//   dotnet process; the target is neither suspended nor controlled).
//
// Corrected attach flow (finding 04 — netcoredbg reference corrected finding 02):
//   1. EnumerateCLRs(pid)  WITH RETRY (100ms x 30; check AreAllHandlesValid)
//        -> pStringArray[0] is a coreclr MODULE PATH, not a version string
//   2. CreateVersionStringFromModule(pid, modulePath, buf, ...) -> version string
//   3. CreateDebuggingInterfaceFromVersionEx(CorDebugVersion_4_0=4, version, &pUnk)
//   4. pUnk->QueryInterface(IID_ICorDebug) -> confirms the pointer is ICorDebug
//   5. Release(pCordb), Release(pUnk), CloseCLREnumeration
//
// All strings are UTF-16 (LPWSTR is 16-bit on the Unix PAL — finding 04).
//
// Usage:  dotnet 02-dbgshim-attach-probe.cs <pid-of-running-dotnet-process>
//   or set DBGSHIM_PATH to override library discovery.
//
// Exit codes: 0 pass; 2 usage; 3 dbgshim-not-found; 4 EnumerateCLRs;
//             5 CreateVersionStringFromModule; 6 CreateDebuggingInterface;
//             7 QueryInterface(ICorDebug).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

return Probe02.Run(args);

static unsafe class Probe02
{
    // cordebug.idl CorDebugInterfaceVersion: CorDebugVersion_4_0 = 4 (finding 03/04)
    const int CorDebugVersion_4_0 = 4;

    // IID_ICorDebug (cordebug.idl, finding 03)
    static readonly Guid IID_ICorDebug = new("3d6f5f61-7538-11d3-8d5b-00104b35e7ef");

    // PAL INVALID_HANDLE_VALUE == (void*)-1
    static readonly nint INVALID_HANDLE_VALUE = -1;

    // HRESULTs we branch on
    const int E_INVALIDARG = unchecked((int)0x80070057);
    const int E_FAIL = unchecked((int)0x80004005);
    const int E_INSUFFICIENT_BUFFER = unchecked((int)0x8007007A);

    public static int Run(string[] args)
    {
        if (args.Length < 1 ||
            !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
        {
            Console.Error.WriteLine("Usage: dotnet 02-dbgshim-attach-probe.cs <pid-of-running-dotnet-process>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"os-arch    : {RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"target pid : {pid}");

        // ---- locate + load libdbgshim ----
        string? dbgshimPath = ResolveDbgShim(out string searched);
        if (dbgshimPath is null)
        {
            Console.Error.WriteLine($"FALSIFIED (discovery): dbgshim not found. Searched:\n{searched}");
            return 3;
        }
        Console.WriteLine($"dbgshim    : {dbgshimPath}");

        nint lib = NativeLibrary.Load(dbgshimPath);

        var enumerateCLRs =
            (delegate* unmanaged[Cdecl]<uint, nint*, nint*, uint*, int>)
            NativeLibrary.GetExport(lib, "EnumerateCLRs");
        var closeCLREnumeration =
            (delegate* unmanaged[Cdecl]<nint, nint, uint, int>)
            NativeLibrary.GetExport(lib, "CloseCLREnumeration");
        var createVersionStringFromModule =
            (delegate* unmanaged[Cdecl]<uint, char*, char*, uint, uint*, int>)
            NativeLibrary.GetExport(lib, "CreateVersionStringFromModule");
        var createDebuggingInterfaceFromVersionEx =
            (delegate* unmanaged[Cdecl]<int, char*, nint*, int>)
            NativeLibrary.GetExport(lib, "CreateDebuggingInterfaceFromVersionEx");

        // ---- step 1: EnumerateCLRs with retry (finding 04) ----
        nint handleArray = 0, stringArray = 0;
        uint count = 0;
        int retries = 0;
        int hr = EnumerateWithRetry(enumerateCLRs, closeCLREnumeration,
                                    (uint)pid, &handleArray, &stringArray, &count, ref retries);
        if (hr < 0 || count == 0)
        {
            Console.Error.WriteLine($"FALSIFIED (EnumerateCLRs): hr=0x{hr:X8} count={count} retries={retries}");
            NativeLibrary.Free(lib);
            return 4;
        }
        Console.WriteLine($"clr-count  : {count}  (retries={retries})");
        if (count > 1)
            Console.WriteLine("  note: count > 1 — multiple CLRs in target; using index 0");

        // module path = stringArray[0] (a coreclr module path, NOT a version string)
        nint firstModulePtr = ((nint*)stringArray)[0];
        string modulePath = Marshal.PtrToStringUni(firstModulePtr) ?? "";
        Console.WriteLine($"coreclr    : {modulePath}");

        // ---- step 2: CreateVersionStringFromModule ----
        string? version = GetVersionString(createVersionStringFromModule, (uint)pid, modulePath);
        if (version is null)
        {
            Console.Error.WriteLine("FALSIFIED (CreateVersionStringFromModule)");
            closeCLREnumeration(handleArray, stringArray, count);
            NativeLibrary.Free(lib);
            return 5;
        }
        Console.WriteLine($"version    : {version}");

        // ---- step 3: CreateDebuggingInterfaceFromVersionEx(4, version, &pUnk) ----
        nint pUnknown = 0;
        fixed (char* pVersion = version)
        {
            hr = createDebuggingInterfaceFromVersionEx(CorDebugVersion_4_0, pVersion, &pUnknown);
        }
        if (hr < 0 || pUnknown == 0)
        {
            Console.Error.WriteLine($"FALSIFIED (CreateDebuggingInterfaceFromVersionEx): hr=0x{hr:X8} pUnk=0x{pUnknown:X}");
            closeCLREnumeration(handleArray, stringArray, count);
            NativeLibrary.Free(lib);
            return 6;
        }
        Console.WriteLine($"IUnknown   : 0x{pUnknown:X}");

        // ---- step 4: QueryInterface(IID_ICorDebug) — the confirmation ----
        Guid iid = IID_ICorDebug;
        hr = Marshal.QueryInterface(pUnknown, in iid, out nint pCordb);
        if (hr < 0 || pCordb == 0)
        {
            Console.Error.WriteLine($"FALSIFIED (QueryInterface ICorDebug): hr=0x{hr:X8} pCordb=0x{pCordb:X}");
            Marshal.Release(pUnknown);
            closeCLREnumeration(handleArray, stringArray, count);
            NativeLibrary.Free(lib);
            return 7;
        }
        Console.WriteLine($"ICorDebug  : 0x{pCordb:X}  <-- CONFIRMED (QI returned S_OK)");

        // vtable head — logged for inspection but NOT fixtured (see WriteFixture note)
        nint vtable = *(nint*)pCordb;
        Console.Write("vtable[0..3]: ");
        for (int i = 0; i < 4; i++) Console.Write($"0x{((nint*)vtable)[i]:X} ");
        Console.WriteLine();

        WriteFixture(pid, modulePath, version, count, retries);

        // ---- release everything (balance the refcounts) ----
        int rcCordb = Marshal.Release(pCordb);     // release the QI'd ICorDebug
        int rcUnknown = Marshal.Release(pUnknown); // release the original IUnknown
        closeCLREnumeration(handleArray, stringArray, count);
        NativeLibrary.Free(lib);
        Console.WriteLine($"released   : ICorDebug->{rcCordb}, IUnknown->{rcUnknown}");

        Console.WriteLine("PROBE 02 PASSED");
        return 0;
    }

    // finding 04: EnumerateCLRs needs retry — coreclr-mid-load race returns
    // INVALID_HANDLE_VALUE handles; the target may not have loaded coreclr yet.
    static int EnumerateWithRetry(
        delegate* unmanaged[Cdecl]<uint, nint*, nint*, uint*, int> enumerate,
        delegate* unmanaged[Cdecl]<nint, nint, uint, int> close,
        uint pid, nint* pHandleArray, nint* pStringArray, uint* pCount, ref int retries)
    {
        const int maxTries = 30; // 100ms x 30 = 3s (netcoredbg default)
        int hr = 0;
        for (int tries = 0; tries < maxTries; tries++)
        {
            retries = tries;
            hr = enumerate(pid, pHandleArray, pStringArray, pCount);
            if (hr >= 0 && *pHandleArray != 0 && *pCount > 0)
            {
                if (AllHandlesValid(*pHandleArray, *pCount))
                    return hr;
                // race: coreclr caught mid-load. clean up and retry.
                close(*pHandleArray, *pStringArray, *pCount);
                *pHandleArray = 0; *pStringArray = 0; *pCount = 0;
            }
            // no point retrying on bad args / no such process
            if (hr == E_INVALIDARG || hr == E_FAIL)
                return hr;
            Thread.Sleep(100);
        }
        return hr;
    }

    static bool AllHandlesValid(nint handleArray, uint count)
    {
        for (uint i = 0; i < count; i++)
            if (((nint*)handleArray)[i] == INVALID_HANDLE_VALUE)
                return false;
        return true;
    }

    static string? GetVersionString(
        delegate* unmanaged[Cdecl]<uint, char*, char*, uint, uint*, int> createVersion,
        uint pid, string modulePath)
    {
        uint cch = 100; // netcoredbg uses a fixed 100-WCHAR buffer; we grow once if needed
        fixed (char* pModule = modulePath)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                char[] buf = new char[cch];
                uint needed = 0;
                int hr;
                fixed (char* pBuf = buf)
                {
                    hr = createVersion(pid, pModule, pBuf, cch, &needed);
                }
                if (hr >= 0)
                {
                    int nul = Array.IndexOf(buf, '\0');
                    return new string(buf, 0, nul < 0 ? buf.Length : nul);
                }
                if (hr == E_INSUFFICIENT_BUFFER && needed > cch)
                {
                    cch = needed; // grow and retry once
                    continue;
                }
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

        // dbgshim ships with the runtime; look in this process's runtime directory.
        string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        string candidate = Path.Combine(runtimeDir, libName);
        tried.Add(candidate + "  (runtime dir)");
        if (File.Exists(candidate)) { searched = string.Join("\n", tried); return candidate; }

        searched = string.Join("\n", tried) +
                   "\n(hint: set DBGSHIM_PATH, or `find $(dirname $(which dotnet)) -name '" + libName + "'`)";
        return null;
    }

    // Fixture design realization (deviation from finding 02's "capture vtable bytes"):
    // ICorDebug vtable pointers are ASLR-randomized — they change every run, so they
    // are NOT a reproducible fixture. The reproducible, replayable record is the
    // PROTOCOL-OBSERVABLE behavior: version-string format, CLR count, retry count,
    // module-path shape. That is what we fixture. Vtable pointers go to stdout only.
    static void WriteFixture(int pid, string modulePath, string version, uint count, int retries)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"02-dbgshim-attach-{rid}-{ts}.txt");

        string body =
            "# DrHook.Engine probe 02 fixture — dbgshim attach + QI(ICorDebug)\n" +
            "# Protocol-observable behavior (vtable pointers omitted: ASLR-randomized, not reproducible)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"clr-count        = {count}\n" +
            $"enumerate-retries= {retries}\n" +
            $"coreclr-path     = {modulePath}\n" +
            $"version-string   = {version}\n" +
            "qi-icordebug     = S_OK\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
