#!/usr/bin/env -S dotnet
#:property AllowUnsafeBlocks=true
//
// DrHook.Engine PoC — Probe 04: ICorDebugManagedCallback V-table via [GeneratedComClass]
// =====================================================================================
//
// Hypothesis (findings 03 + 05 + probe-04 planning note):
//   A managed [GeneratedComClass] implementing the full ICorDebugManagedCallback (+2/3/4)
//   surface can be exposed to native code via StrategyBasedComWrappers
//   .GetOrCreateComInterfaceForObject, and ICorDebug.SetManagedHandler accepts it
//   (QI succeeds for the required callback IIDs). This is the EXPOSE direction —
//   probe 03 proved the CONSUME direction (RCW from native IUnknown).
//
// The boss fight: the four callback interfaces total 38 methods. Because we IMPLEMENT
// them (the runtime calls us), every method must be declared in EXACT IDL order — a
// missing or misordered slot would crash when that callback fires. Probe 04 does NOT
// attach (no DebugActiveProcess), so no callback fires here; it validates that
// SetManagedHandler accepts the managed-implemented V-table. The build also validates
// that all 38 signatures are expressible in source-gen COM. Slot-dispatch correctness
// (does calling slot N invoke the right method) is probe 05's job, when callbacks fire.
//
// Interface-pointer params -> nint (no marshalling on the callback path, finding 05).
// BOOL/LONG/HRESULT/enums -> int; DWORD/ULONG/ULONG32/CONNID -> uint. [PreserveSig] int.
//
// Falsification ladder (exit codes): 2 usage; 3 dbgshim; 4 EnumerateCLRs;
//   5 version; 6 create-interface; 8 ComWrappers RCW; 9 Initialize;
//   11 callback CCW (GetOrCreateComInterfaceForObject); 12 SetManagedHandler; 10 Terminate.
//
// Usage:  dotnet 04-managedcallback-vtable-probe.cs <pid>   (DBGSHIM_PATH override supported)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;

return Probe04.Run(args);

// ---- ICorDebug — prefix through SetManagedHandler (slots 3,4,5) ----
[GeneratedComInterface]
[Guid("3d6f5f61-7538-11d3-8d5b-00104b35e7ef")]
internal partial interface ICorDebug
{
    [PreserveSig] int Initialize();
    [PreserveSig] int Terminate();
    [PreserveSig] int SetManagedHandler(nint pCallback);
}

// ---- ICorDebugManagedCallback (IID 3d6f5f60-...) — 26 methods, exact IDL order ----
[GeneratedComInterface]
[Guid("3d6f5f60-7538-11d3-8d5b-00104b35e7ef")]
internal partial interface ICorDebugManagedCallback
{
    [PreserveSig] int Breakpoint(nint pAppDomain, nint pThread, nint pBreakpoint);
    [PreserveSig] int StepComplete(nint pAppDomain, nint pThread, nint pStepper, int reason);
    [PreserveSig] int Break(nint pAppDomain, nint thread);
    [PreserveSig] int Exception(nint pAppDomain, nint pThread, int unhandled);
    [PreserveSig] int EvalComplete(nint pAppDomain, nint pThread, nint pEval);
    [PreserveSig] int EvalException(nint pAppDomain, nint pThread, nint pEval);
    [PreserveSig] int CreateProcess(nint pProcess);
    [PreserveSig] int ExitProcess(nint pProcess);
    [PreserveSig] int CreateThread(nint pAppDomain, nint thread);
    [PreserveSig] int ExitThread(nint pAppDomain, nint thread);
    [PreserveSig] int LoadModule(nint pAppDomain, nint pModule);
    [PreserveSig] int UnloadModule(nint pAppDomain, nint pModule);
    [PreserveSig] int LoadClass(nint pAppDomain, nint c);
    [PreserveSig] int UnloadClass(nint pAppDomain, nint c);
    [PreserveSig] int DebuggerError(nint pProcess, int errorHR, uint errorCode);
    [PreserveSig] int LogMessage(nint pAppDomain, nint pThread, int lLevel, nint pLogSwitchName, nint pMessage);
    [PreserveSig] int LogSwitch(nint pAppDomain, nint pThread, int lLevel, uint ulReason, nint pLogSwitchName, nint pParentName);
    [PreserveSig] int CreateAppDomain(nint pProcess, nint pAppDomain);
    [PreserveSig] int ExitAppDomain(nint pProcess, nint pAppDomain);
    [PreserveSig] int LoadAssembly(nint pAppDomain, nint pAssembly);
    [PreserveSig] int UnloadAssembly(nint pAppDomain, nint pAssembly);
    [PreserveSig] int ControlCTrap(nint pProcess);
    [PreserveSig] int NameChange(nint pAppDomain, nint pThread);
    [PreserveSig] int UpdateModuleSymbols(nint pAppDomain, nint pModule, nint pSymbolStream);
    [PreserveSig] int EditAndContinueRemap(nint pAppDomain, nint pThread, nint pFunction, int fAccurate);
    [PreserveSig] int BreakpointSetError(nint pAppDomain, nint pThread, nint pBreakpoint, uint dwError);
}

// ---- ICorDebugManagedCallback2 (IID 250E5EEA-...) — 8 methods ----
[GeneratedComInterface]
[Guid("250E5EEA-DB5C-4C76-B6F3-8C46F12E3203")]
internal partial interface ICorDebugManagedCallback2
{
    [PreserveSig] int FunctionRemapOpportunity(nint pAppDomain, nint pThread, nint pOldFunction, nint pNewFunction, uint oldILOffset);
    [PreserveSig] int CreateConnection(nint pProcess, uint dwConnectionId, nint pConnName);
    [PreserveSig] int ChangeConnection(nint pProcess, uint dwConnectionId);
    [PreserveSig] int DestroyConnection(nint pProcess, uint dwConnectionId);
    [PreserveSig] int Exception(nint pAppDomain, nint pThread, nint pFrame, uint nOffset, int dwEventType, uint dwFlags);
    [PreserveSig] int ExceptionUnwind(nint pAppDomain, nint pThread, int dwEventType, uint dwFlags);
    [PreserveSig] int FunctionRemapComplete(nint pAppDomain, nint pThread, nint pFunction);
    [PreserveSig] int MDANotification(nint pController, nint pThread, nint pMDA);
}

// ---- ICorDebugManagedCallback3 (IID 264EA0FC-...) — 1 method ----
[GeneratedComInterface]
[Guid("264EA0FC-2591-49AA-868E-835E6515323F")]
internal partial interface ICorDebugManagedCallback3
{
    [PreserveSig] int CustomNotification(nint pThread, nint pAppDomain);
}

// ---- ICorDebugManagedCallback4 (IID 322911AE-...) — 3 methods ----
[GeneratedComInterface]
[Guid("322911AE-16A5-49BA-84A3-ED69678138A3")]
internal partial interface ICorDebugManagedCallback4
{
    [PreserveSig] int BeforeGarbageCollection(nint pProcess);
    [PreserveSig] int AfterGarbageCollection(nint pProcess);
    [PreserveSig] int DataBreakpoint(nint pProcess, nint pThread, nint pContext, uint contextSize);
}

// ---- The managed-implemented callback. Every method returns S_OK (0). ----
// Probe 04 doesn't attach, so none of these fire here — but the full surface must
// exist for the CCW V-table to be complete (probe 05 fires them). A counter records
// any unexpected dispatch (it should stay 0 in probe 04).
[GeneratedComClass]
internal partial class StubCallback :
    ICorDebugManagedCallback, ICorDebugManagedCallback2,
    ICorDebugManagedCallback3, ICorDebugManagedCallback4
{
    public int Dispatches; // expected 0 in probe 04 (no attach, no callbacks)

    // ICorDebugManagedCallback
    public int Breakpoint(nint a, nint t, nint b) { Dispatches++; return 0; }
    public int StepComplete(nint a, nint t, nint s, int reason) { Dispatches++; return 0; }
    public int Break(nint a, nint t) { Dispatches++; return 0; }
    public int Exception(nint a, nint t, int unhandled) { Dispatches++; return 0; }
    public int EvalComplete(nint a, nint t, nint e) { Dispatches++; return 0; }
    public int EvalException(nint a, nint t, nint e) { Dispatches++; return 0; }
    public int CreateProcess(nint p) { Dispatches++; return 0; }
    public int ExitProcess(nint p) { Dispatches++; return 0; }
    public int CreateThread(nint a, nint t) { Dispatches++; return 0; }
    public int ExitThread(nint a, nint t) { Dispatches++; return 0; }
    public int LoadModule(nint a, nint m) { Dispatches++; return 0; }
    public int UnloadModule(nint a, nint m) { Dispatches++; return 0; }
    public int LoadClass(nint a, nint c) { Dispatches++; return 0; }
    public int UnloadClass(nint a, nint c) { Dispatches++; return 0; }
    public int DebuggerError(nint p, int hr, uint code) { Dispatches++; return 0; }
    public int LogMessage(nint a, nint t, int lvl, nint sw, nint msg) { Dispatches++; return 0; }
    public int LogSwitch(nint a, nint t, int lvl, uint reason, nint sw, nint parent) { Dispatches++; return 0; }
    public int CreateAppDomain(nint p, nint a) { Dispatches++; return 0; }
    public int ExitAppDomain(nint p, nint a) { Dispatches++; return 0; }
    public int LoadAssembly(nint a, nint asm) { Dispatches++; return 0; }
    public int UnloadAssembly(nint a, nint asm) { Dispatches++; return 0; }
    public int ControlCTrap(nint p) { Dispatches++; return 0; }
    public int NameChange(nint a, nint t) { Dispatches++; return 0; }
    public int UpdateModuleSymbols(nint a, nint m, nint stream) { Dispatches++; return 0; }
    public int EditAndContinueRemap(nint a, nint t, nint f, int accurate) { Dispatches++; return 0; }
    public int BreakpointSetError(nint a, nint t, nint b, uint err) { Dispatches++; return 0; }

    // ICorDebugManagedCallback2
    public int FunctionRemapOpportunity(nint a, nint t, nint oldF, nint newF, uint off) { Dispatches++; return 0; }
    public int CreateConnection(nint p, uint id, nint name) { Dispatches++; return 0; }
    public int ChangeConnection(nint p, uint id) { Dispatches++; return 0; }
    public int DestroyConnection(nint p, uint id) { Dispatches++; return 0; }
    public int Exception(nint a, nint t, nint frame, uint off, int evt, uint flags) { Dispatches++; return 0; }
    public int ExceptionUnwind(nint a, nint t, int evt, uint flags) { Dispatches++; return 0; }
    public int FunctionRemapComplete(nint a, nint t, nint f) { Dispatches++; return 0; }
    public int MDANotification(nint ctrl, nint t, nint mda) { Dispatches++; return 0; }

    // ICorDebugManagedCallback3
    public int CustomNotification(nint t, nint a) { Dispatches++; return 0; }

    // ICorDebugManagedCallback4
    public int BeforeGarbageCollection(nint p) { Dispatches++; return 0; }
    public int AfterGarbageCollection(nint p) { Dispatches++; return 0; }
    public int DataBreakpoint(nint p, nint t, nint ctx, uint size) { Dispatches++; return 0; }
}

static unsafe class Probe04
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
            Console.Error.WriteLine("Usage: dotnet 04-managedcallback-vtable-probe.cs <pid>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"os-arch    : {RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"target pid : {pid}");

        // ---- attach flow (probe 02/03) -> pUnknown ----
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

        ComWrappers cw = new StrategyBasedComWrappers();

        ICorDebug iCorDebug;
        try { iCorDebug = (ICorDebug)cw.GetOrCreateObjectForComInstance(pUnknown, CreateObjectFlags.None); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (ComWrappers RCW): {ex.GetType().Name}: {ex.Message}");
            Marshal.Release(pUnknown); closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib);
            return 8;
        }
        Console.WriteLine("ICorDebug  : source-gen RCW created");

        int hrInit = iCorDebug.Initialize();
        Console.WriteLine($"Initialize : hr=0x{hrInit:X8}");
        if (hrInit < 0) { Marshal.Release(pUnknown); closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib); return 9; }

        // ---- NEW: expose the managed callback as a native CCW (the boss fight) ----
        var stub = new StubCallback();
        nint pCallback = 0;
        try { pCallback = cw.GetOrCreateComInterfaceForObject(stub, CreateComInterfaceFlags.None); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (callback CCW): {ex.GetType().Name}: {ex.Message}");
            iCorDebug.Terminate(); Marshal.Release(pUnknown); closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib);
            return 11;
        }
        Console.WriteLine($"callback   : CCW created 0x{pCallback:X} (38-method V-table across 4 IIDs)");

        // ---- NEW: SetManagedHandler — runtime QIs for the callback IIDs ----
        int hrSet = iCorDebug.SetManagedHandler(pCallback);
        Console.WriteLine($"SetHandler : hr=0x{hrSet:X8}");
        if (hrSet < 0)
        {
            Console.Error.WriteLine("FALSIFIED (SetManagedHandler) — runtime rejected the callback V-table (QI for a required IID failed?)");
            Marshal.Release(pCallback); iCorDebug.Terminate(); Marshal.Release(pUnknown);
            closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib); GC.KeepAlive(stub);
            return 12;
        }
        Console.WriteLine($"SetHandler : ACCEPTED  <-- callback V-table registered (dispatches so far: {stub.Dispatches})");

        int hrTerm = iCorDebug.Terminate();
        Console.WriteLine($"Terminate  : hr=0x{hrTerm:X8}");
        if (hrTerm < 0) { Marshal.Release(pCallback); Marshal.Release(pUnknown); closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib); GC.KeepAlive(stub); return 10; }

        WriteFixture(pid, modulePath, version, hrInit, hrSet, hrTerm, stub.Dispatches);

        // ---- cleanup ----
        int rcCb = Marshal.Release(pCallback);  // my CCW ref (runtime released its own at Terminate)
        int rcUnk = Marshal.Release(pUnknown);
        closeCLREnumeration(handleArray, stringArray, count);
        NativeLibrary.Free(lib);
        GC.KeepAlive(stub); GC.KeepAlive(iCorDebug);
        Console.WriteLine($"released   : callback->{rcCb}, IUnknown->{rcUnk}; unexpected dispatches: {stub.Dispatches}");
        Console.WriteLine("PROBE 04 PASSED");
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
                if (hr >= 0) { int nul = Array.IndexOf(buf, '\0'); return new string(buf, 0, nul < 0 ? buf.Length : nul); }
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
        searched = string.Join("\n", tried) +
                   "\n(note: dbgshim is not in the .NET 7+ runtime — set DBGSHIM_PATH to a Microsoft.Diagnostics.DbgShim or VS Code .debugger copy)";
        return null;
    }

    static void WriteFixture(int pid, string modulePath, string version, int hrInit, int hrSet, int hrTerm, int dispatches)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"04-managedcallback-vtable-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 04 fixture — ICorDebugManagedCallback V-table via [GeneratedComClass]\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"coreclr-path     = {modulePath}\n" +
            $"version-string   = {version}\n" +
            "callback-ifaces  = ICorDebugManagedCallback + 2 + 3 + 4 (38 methods)\n" +
            "ccw-create       = OK ([GeneratedComClass] + GetOrCreateComInterfaceForObject)\n" +
            $"initialize-hr    = 0x{hrInit:X8}\n" +
            $"setmanagedhandler-hr = 0x{hrSet:X8}\n" +
            $"terminate-hr     = 0x{hrTerm:X8}\n" +
            $"unexpected-dispatches = {dispatches}  (expected 0 — probe 04 does not attach)\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
