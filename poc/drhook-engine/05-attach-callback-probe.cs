#!/usr/bin/env -S dotnet
#:property AllowUnsafeBlocks=true
//
// DrHook.Engine PoC — Probe 05: DebugActiveProcess + first callback + Detach (integration)
// ======================================================================================
//
// Hypothesis (findings 03/05/09):
//   After SetManagedHandler (probe 04), ICorDebug.DebugActiveProcess attaches to a live
//   .NET process; the runtime fires managed callbacks into our [GeneratedComClass] V-table
//   on a runtime-owned thread; observing the first callback validates SLOT DISPATCH (the
//   right C# method runs for the slot the runtime calls). Then we Detach cleanly.
//
// This is the FIRST invasive probe — it attaches and controls the target (probes 02-04
// only enumerated / created interfaces / registered a callback, none touched execution).
//
// Safety:
//   - The target is a DISPOSABLE sleeper the operator spawns and `kill -9`s in cleanup,
//     so a wrong Detach can at worst freeze a throwaway process.
//   - Detach is attempted in a finally so the target is released on any path.
//   - The stub does NOT call Continue: the runtime fires ONE catch-up callback, records
//     it, returns S_OK, and stops waiting for Continue. Observing that one event is enough
//     to validate slot dispatch. The main thread then Detaches from the stopped state —
//     whether Detach-from-stopped is allowed is itself an empirical question this answers.
//
// Open question this settles: macOS attach entitlement. Probe 02 found no wall for
//   enumerate + create-interface, but DebugActiveProcess actually attaches (ptrace /
//   task_for_pid class). A plain `dotnet run` has no debug entitlement — if attach is
//   refused, the failing HRESULT at DebugActiveProcess (exit 13) is a SUBSTANTIVE FINDING
//   (DrHook would need a codesigned debug entitlement, as vsdbg has), not a probe defect.
//
// Type mapping per finding 08 (PAL-verified): DWORD/BOOL -> uint/int; pointers -> nint.
//
// Falsification ladder (exit codes): 2 usage; 3 dbgshim; 4 EnumerateCLRs; 5 version;
//   6 create-interface; 8 RCW; 9 Initialize; 11 callback CCW; 12 SetManagedHandler;
//   13 DebugActiveProcess (may be the macOS entitlement wall — a finding); 14 attached
//   but no callback fired within timeout; 10 Terminate.
//
// Usage:  dotnet 05-attach-callback-probe.cs <pid>   (DBGSHIM_PATH override supported)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;

return Probe05.Run(args);

// ---- ICorDebug — slots 3..8 (DebugActiveProcess at slot 8) ----
// SetUnmanagedHandler (6) and CreateProcess (7) are uncalled slot-fillers that push
// DebugActiveProcess to its correct slot 8. Never invoked.
[GeneratedComInterface]
[Guid("3d6f5f61-7538-11d3-8d5b-00104b35e7ef")]
internal partial interface ICorDebug
{
    [PreserveSig] int Initialize();
    [PreserveSig] int Terminate();
    [PreserveSig] int SetManagedHandler(nint pCallback);
    [PreserveSig] int SetUnmanagedHandler(nint pCallback);                       // slot 6 filler
    [PreserveSig] int CreateProcess(nint lpApplicationName, nint lpCommandLine,  // slot 7 filler
        nint lpProcessAttributes, nint lpThreadAttributes, int bInheritHandles,
        uint dwCreationFlags, nint lpEnvironment, nint lpCurrentDirectory,
        nint lpStartupInfo, nint lpProcessInformation, int debuggingFlags, out nint ppProcess);
    [PreserveSig] int DebugActiveProcess(uint id, int win32Attach, out nint ppProcess);
}

// ---- ICorDebugController — slots 3..9 (Continue at 4, Detach at 9) ----
[GeneratedComInterface]
[Guid("3d6f5f62-7538-11d3-8d5b-00104b35e7ef")]
internal partial interface ICorDebugController
{
    [PreserveSig] int Stop(uint dwTimeoutIgnored);
    [PreserveSig] int Continue(int fIsOutOfBand);
    [PreserveSig] int IsRunning(out int pbRunning);
    [PreserveSig] int HasQueuedCallbacks(nint pThread, out int pbQueued);
    [PreserveSig] int EnumerateThreads(out nint ppThreads);
    [PreserveSig] int SetAllThreadsDebugState(int state, nint pExceptThisThread);
    [PreserveSig] int Detach();
}

// ---- The four managed-callback interfaces (38 methods, exact IDL order; finding 03/04) ----
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

[GeneratedComInterface]
[Guid("264EA0FC-2591-49AA-868E-835E6515323F")]
internal partial interface ICorDebugManagedCallback3
{
    [PreserveSig] int CustomNotification(nint pThread, nint pAppDomain);
}

[GeneratedComInterface]
[Guid("322911AE-16A5-49BA-84A3-ED69678138A3")]
internal partial interface ICorDebugManagedCallback4
{
    [PreserveSig] int BeforeGarbageCollection(nint pProcess);
    [PreserveSig] int AfterGarbageCollection(nint pProcess);
    [PreserveSig] int DataBreakpoint(nint pProcess, nint pThread, nint pContext, uint contextSize);
}

// ---- The managed callback. Records the first event (slot-dispatch validation) and
//      signals the main thread. Does NOT call Continue — the runtime fires one catch-up
//      callback then stops; observing it is enough. Every method must exist (complete
//      V-table) or the runtime crashes when it calls a missing slot. ----
[GeneratedComClass]
internal partial class AttachCallback :
    ICorDebugManagedCallback, ICorDebugManagedCallback2,
    ICorDebugManagedCallback3, ICorDebugManagedCallback4
{
    readonly object _lock = new();
    public string? FirstEvent;
    public int Count;
    public readonly ManualResetEventSlim Fired = new(false);

    int R(string name)
    {
        lock (_lock) { FirstEvent ??= name; Count++; }
        Fired.Set();
        return 0; // S_OK
    }

    // ICorDebugManagedCallback
    public int Breakpoint(nint a, nint t, nint b) => R("Breakpoint");
    public int StepComplete(nint a, nint t, nint s, int reason) => R("StepComplete");
    public int Break(nint a, nint t) => R("Break");
    public int Exception(nint a, nint t, int unhandled) => R("Exception");
    public int EvalComplete(nint a, nint t, nint e) => R("EvalComplete");
    public int EvalException(nint a, nint t, nint e) => R("EvalException");
    public int CreateProcess(nint p) => R("CreateProcess");
    public int ExitProcess(nint p) => R("ExitProcess");
    public int CreateThread(nint a, nint t) => R("CreateThread");
    public int ExitThread(nint a, nint t) => R("ExitThread");
    public int LoadModule(nint a, nint m) => R("LoadModule");
    public int UnloadModule(nint a, nint m) => R("UnloadModule");
    public int LoadClass(nint a, nint c) => R("LoadClass");
    public int UnloadClass(nint a, nint c) => R("UnloadClass");
    public int DebuggerError(nint p, int hr, uint code) => R("DebuggerError");
    public int LogMessage(nint a, nint t, int lvl, nint sw, nint msg) => R("LogMessage");
    public int LogSwitch(nint a, nint t, int lvl, uint reason, nint sw, nint parent) => R("LogSwitch");
    public int CreateAppDomain(nint p, nint a) => R("CreateAppDomain");
    public int ExitAppDomain(nint p, nint a) => R("ExitAppDomain");
    public int LoadAssembly(nint a, nint asm) => R("LoadAssembly");
    public int UnloadAssembly(nint a, nint asm) => R("UnloadAssembly");
    public int ControlCTrap(nint p) => R("ControlCTrap");
    public int NameChange(nint a, nint t) => R("NameChange");
    public int UpdateModuleSymbols(nint a, nint m, nint stream) => R("UpdateModuleSymbols");
    public int EditAndContinueRemap(nint a, nint t, nint f, int accurate) => R("EditAndContinueRemap");
    public int BreakpointSetError(nint a, nint t, nint b, uint err) => R("BreakpointSetError");

    // ICorDebugManagedCallback2
    public int FunctionRemapOpportunity(nint a, nint t, nint oldF, nint newF, uint off) => R("FunctionRemapOpportunity");
    public int CreateConnection(nint p, uint id, nint name) => R("CreateConnection");
    public int ChangeConnection(nint p, uint id) => R("ChangeConnection");
    public int DestroyConnection(nint p, uint id) => R("DestroyConnection");
    public int Exception(nint a, nint t, nint frame, uint off, int evt, uint flags) => R("Exception2");
    public int ExceptionUnwind(nint a, nint t, int evt, uint flags) => R("ExceptionUnwind");
    public int FunctionRemapComplete(nint a, nint t, nint f) => R("FunctionRemapComplete");
    public int MDANotification(nint ctrl, nint t, nint mda) => R("MDANotification");

    // ICorDebugManagedCallback3
    public int CustomNotification(nint t, nint a) => R("CustomNotification");

    // ICorDebugManagedCallback4
    public int BeforeGarbageCollection(nint p) => R("BeforeGarbageCollection");
    public int AfterGarbageCollection(nint p) => R("AfterGarbageCollection");
    public int DataBreakpoint(nint p, nint t, nint ctx, uint size) => R("DataBreakpoint");
}

static unsafe class Probe05
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
            Console.Error.WriteLine("Usage: dotnet 05-attach-callback-probe.cs <pid>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"os-arch    : {RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"target pid : {pid}");

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
        if (hr < 0 || count == 0) { Console.Error.WriteLine($"FALSIFIED (EnumerateCLRs): hr=0x{hr:X8}"); NativeLibrary.Free(lib); return 4; }
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
        catch (Exception ex) { Console.Error.WriteLine($"FALSIFIED (RCW): {ex.Message}"); Marshal.Release(pUnknown); closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib); return 8; }

        int hi = iCorDebug.Initialize();
        if (hi < 0) { Console.Error.WriteLine($"FALSIFIED (Initialize): 0x{hi:X8}"); Marshal.Release(pUnknown); closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib); return 9; }
        Console.WriteLine("Initialize : S_OK");

        var stub = new AttachCallback();
        nint pCallback;
        try { pCallback = cw.GetOrCreateComInterfaceForObject(stub, CreateComInterfaceFlags.None); }
        catch (Exception ex) { Console.Error.WriteLine($"FALSIFIED (callback CCW): {ex.Message}"); iCorDebug.Terminate(); Marshal.Release(pUnknown); closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib); return 11; }

        int hs = iCorDebug.SetManagedHandler(pCallback);
        if (hs < 0) { Console.Error.WriteLine($"FALSIFIED (SetManagedHandler): 0x{hs:X8}"); Marshal.Release(pCallback); iCorDebug.Terminate(); Marshal.Release(pUnknown); closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib); GC.KeepAlive(stub); return 12; }
        Console.WriteLine("SetHandler : S_OK");

        // ---- THE INVASIVE STEP: attach ----
        int hrAttach = iCorDebug.DebugActiveProcess((uint)pid, /*win32Attach FALSE*/ 0, out nint pProcess);
        Console.WriteLine($"DebugActiveProcess: hr=0x{hrAttach:X8}");
        if (hrAttach < 0 || pProcess == 0)
        {
            Console.Error.WriteLine("FALSIFIED (DebugActiveProcess) — attach refused.");
            Console.Error.WriteLine("  NOTE: on macOS this may be the debug-entitlement wall (ptrace/task_for_pid");
            Console.Error.WriteLine("  needs a codesigned debugger entitlement, as vsdbg has). A SUBSTANTIVE FINDING,");
            Console.Error.WriteLine("  not a probe defect. Compare: probe 02 (enumerate+create-interface) needed none.");
            WriteFixture(pid, modulePath, version, hrAttach, false, null, 0, 0, 0);
            Marshal.Release(pCallback); iCorDebug.Terminate(); Marshal.Release(pUnknown);
            closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib); GC.KeepAlive(stub);
            return 13;
        }
        Console.WriteLine($"attached   : ICorDebugProcess 0x{pProcess:X}");

        var controller = (ICorDebugController)cw.GetOrCreateObjectForComInstance(pProcess, CreateObjectFlags.None);

        // FINDING (run 1+2): after DebugActiveProcess the process is RUNNING, not stopped —
        // an explicit Continue here returns CORDBG_E_SUPERFLOUS_CONTINUE (0x8013132F). And
        // modern CoreCLR does NOT replay catch-up Create/Load events on attach the way
        // desktop .NET Framework did, so a PARKED target generates no callbacks. The target
        // must produce a LIVE managed event post-attach (thread start / module load /
        // exception); each such event is a synchronized stop. The stub records the first one
        // (no Continue) and the main thread Detaches from that stopped state.

        bool fired;
        int hrDetach;
        try
        {
            fired = stub.Fired.Wait(TimeSpan.FromSeconds(15));
            if (fired)
                Console.WriteLine($"callback   : FIRED  first='{stub.FirstEvent}'  count={stub.Count}  <-- SLOT DISPATCH VALIDATED");
            else
                Console.WriteLine("callback   : none within 15s (attached but no dispatch — see finding 10)");
        }
        finally
        {
            // Always release the target. Detach from the stopped-after-callback state;
            // whether that's allowed is an empirical question this records.
            hrDetach = controller.Detach();
            Console.WriteLine($"Detach     : hr=0x{hrDetach:X8}");
        }

        int hrTerm = iCorDebug.Terminate();
        Console.WriteLine($"Terminate  : hr=0x{hrTerm:X8}");

        WriteFixture(pid, modulePath, version, hrAttach, fired, stub.FirstEvent, stub.Count, hrDetach, hrTerm);

        Marshal.Release(pCallback);
        Marshal.Release(pUnknown);
        closeCLREnumeration(handleArray, stringArray, count);
        NativeLibrary.Free(lib);
        GC.KeepAlive(stub); GC.KeepAlive(controller); GC.KeepAlive(iCorDebug);

        if (!fired) { Console.Error.WriteLine("Result: attached but no callback dispatched."); return 14; }
        if (hrTerm < 0) return 10;
        Console.WriteLine("PROBE 05 PASSED");
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

    static void WriteFixture(int pid, string modulePath, string version, int hrAttach, bool fired, string? firstEvent, int count, int hrDetach, int hrTerm)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"05-attach-callback-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 05 fixture — DebugActiveProcess + first callback + Detach\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"coreclr-path     = {modulePath}\n" +
            $"version-string   = {version}\n" +
            $"debugactiveprocess-hr = 0x{hrAttach:X8}\n" +
            $"callback-fired   = {fired}\n" +
            $"first-event      = {firstEvent ?? "(none)"}\n" +
            $"callback-count   = {count}\n" +
            $"detach-hr        = 0x{hrDetach:X8}\n" +
            $"terminate-hr     = 0x{hrTerm:X8}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
