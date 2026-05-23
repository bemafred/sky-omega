#!/usr/bin/env -S dotnet
#:property AllowUnsafeBlocks=true
//
// DrHook.Engine PoC — Probe 06: hand-rolled [UnmanagedCallersOnly] callback vtable
// ================================================================================
//
// Decisive test for finding 12's hypothesis. Probe 05 attached cleanly but the runtime
// never delivered a managed callback to our [GeneratedComClass] ComWrappers CCW. netcoredbg
// (native C++ callback) gets delivery; we don't. The split:
//   A2 — the ComWrappers *object* CCW dispatch is the problem; a raw [UnmanagedCallersOnly]
//        function-pointer vtable (no ComWrappers object) DOES receive delivery -> BCL-only
//        survives, engine hand-rolls the vtable.
//   A1 — the native->managed transition itself fails on mscordbi's event thread; even
//        [UnmanagedCallersOnly] gets nothing -> engine needs a native callback shim.
//
// This probe builds the ICorDebugManagedCallback (+2/3/4) vtable BY HAND: four native
// vtables of [UnmanagedCallersOnly] static-method function pointers, a COM object whose
// first fields are those vtable pointers, and a QueryInterface that hands out the right
// sub-object per IID. No [GeneratedComClass]. Same attach flow as probe 05; ICorDebug /
// ICorDebugController are still consumed via source-gen RCW (the consume direction works).
//
// If SetManagedHandler succeeds, our QI thunk ran on OUR thread (structural validity).
// The DELIVERY test is whether the CreateProcess thunk fires on mscordbi's event thread.
//
// Type mapping per finding 08; interface pointers -> nint; HRESULT/BOOL/enums -> int;
// DWORD/ULONG/ULONG32/CONNID -> uint. Calling convention: Cdecl (AAPCS64 on arm64), matching
// the platform C++ vtable ABI mscordbi uses.
//
// Falsification ladder: 2 usage; 3 dbgshim; 4 EnumerateCLRs; 5 version; 6 create-interface;
//   8 RCW; 9 Initialize; 12 SetManagedHandler; 13 DebugActiveProcess; 14 attached-but-no-
//   dispatch (=> A1); 0 PASS (callback fired => A2). 10 Terminate.
//
// Usage:  dotnet 06-unmanagedcallersonly-vtable-probe.cs <pid>   (DBGSHIM_PATH override)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;

return Probe06.Run(args);

// ---- consume side: ICorDebug (to slot 8) + ICorDebugController (Continue@4, Detach@9) ----
[GeneratedComInterface]
[Guid("3d6f5f61-7538-11d3-8d5b-00104b35e7ef")]
internal partial interface ICorDebug
{
    [PreserveSig] int Initialize();
    [PreserveSig] int Terminate();
    [PreserveSig] int SetManagedHandler(nint pCallback);
    [PreserveSig] int SetUnmanagedHandler(nint pCallback);
    [PreserveSig] int CreateProcess(nint a1, nint a2, nint a3, nint a4, int a5, uint a6,
        nint a7, nint a8, nint a9, nint a10, int a11, out nint ppProcess);
    [PreserveSig] int DebugActiveProcess(uint id, int win32Attach, out nint ppProcess);
}

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

static unsafe class Probe06
{
    const int CorDebugVersion_4_0 = 4;
    static readonly nint INVALID_HANDLE_VALUE = -1;
    const int E_INVALIDARG = unchecked((int)0x80070057);
    const int E_FAIL = unchecked((int)0x80004005);
    const int E_NOINTERFACE = unchecked((int)0x80004002);
    const int E_INSUFFICIENT_BUFFER = unchecked((int)0x8007007A);
    const int S_OK = 0;

    // ---- recording state (static — the thunks are static [UnmanagedCallersOnly]) ----
    static readonly object s_lock = new();
    static string? s_firstEvent;
    static int s_count;
    static readonly ManualResetEventSlim s_fired = new(false);
    static nint s_block; // base of the hand-rolled COM object (4 vtable-ptr slots)

    static int Record(string name)
    {
        lock (s_lock) { s_firstEvent ??= name; s_count++; }
        s_fired.Set();
        return S_OK;
    }

    // ---- IIDs ----
    static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    static readonly Guid IID_Callback  = new("3d6f5f60-7538-11d3-8d5b-00104b35e7ef");
    static readonly Guid IID_Callback2 = new("250E5EEA-DB5C-4C76-B6F3-8C46F12E3203");
    static readonly Guid IID_Callback3 = new("264EA0FC-2591-49AA-868E-835E6515323F");
    static readonly Guid IID_Callback4 = new("322911AE-16A5-49BA-84A3-ED69678138A3");

    // ============================ IUnknown thunks (shared by all 4 vtables) ============================
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static int QueryInterface(nint pThis, Guid* riid, nint* ppv)
    {
        Guid iid = *riid;
        nint sub =
            (iid == IID_IUnknown || iid == IID_Callback) ? s_block + 0 * sizeof(nint) :
            (iid == IID_Callback2)                        ? s_block + 1 * sizeof(nint) :
            (iid == IID_Callback3)                        ? s_block + 2 * sizeof(nint) :
            (iid == IID_Callback4)                        ? s_block + 3 * sizeof(nint) : 0;
        if (sub == 0) { *ppv = 0; return E_NOINTERFACE; }
        *ppv = sub;
        return S_OK;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static uint AddRef(nint pThis) => 1;   // probe: object is statically alive, no real refcount
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static uint Release(nint pThis) => 1;

    // ============================ ICorDebugManagedCallback (26) ============================
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int Breakpoint(nint p, nint a, nint t, nint b) => Record("Breakpoint");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int StepComplete(nint p, nint a, nint t, nint s, int r) => Record("StepComplete");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int Break(nint p, nint a, nint t) => Record("Break");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int Exception1(nint p, nint a, nint t, int u) => Record("Exception");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int EvalComplete(nint p, nint a, nint t, nint e) => Record("EvalComplete");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int EvalException(nint p, nint a, nint t, nint e) => Record("EvalException");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int CreateProcess(nint p, nint proc) => Record("CreateProcess");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int ExitProcess(nint p, nint proc) => Record("ExitProcess");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int CreateThread(nint p, nint a, nint t) => Record("CreateThread");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int ExitThread(nint p, nint a, nint t) => Record("ExitThread");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int LoadModule(nint p, nint a, nint m) => Record("LoadModule");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int UnloadModule(nint p, nint a, nint m) => Record("UnloadModule");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int LoadClass(nint p, nint a, nint c) => Record("LoadClass");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int UnloadClass(nint p, nint a, nint c) => Record("UnloadClass");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int DebuggerError(nint p, nint proc, int hr, uint code) => Record("DebuggerError");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int LogMessage(nint p, nint a, nint t, int lvl, nint sw, nint msg) => Record("LogMessage");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int LogSwitch(nint p, nint a, nint t, int lvl, uint reason, nint sw, nint parent) => Record("LogSwitch");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int CreateAppDomain(nint p, nint proc, nint a) => Record("CreateAppDomain");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int ExitAppDomain(nint p, nint proc, nint a) => Record("ExitAppDomain");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int LoadAssembly(nint p, nint a, nint asm) => Record("LoadAssembly");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int UnloadAssembly(nint p, nint a, nint asm) => Record("UnloadAssembly");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int ControlCTrap(nint p, nint proc) => Record("ControlCTrap");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int NameChange(nint p, nint a, nint t) => Record("NameChange");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int UpdateModuleSymbols(nint p, nint a, nint m, nint s) => Record("UpdateModuleSymbols");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int EditAndContinueRemap(nint p, nint a, nint t, nint f, int acc) => Record("EditAndContinueRemap");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int BreakpointSetError(nint p, nint a, nint t, nint b, uint err) => Record("BreakpointSetError");

    // ============================ ICorDebugManagedCallback2 (8) ============================
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int FunctionRemapOpportunity(nint p, nint a, nint t, nint oF, nint nF, uint off) => Record("FunctionRemapOpportunity");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int CreateConnection(nint p, nint proc, uint id, nint name) => Record("CreateConnection");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int ChangeConnection(nint p, nint proc, uint id) => Record("ChangeConnection");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int DestroyConnection(nint p, nint proc, uint id) => Record("DestroyConnection");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int Exception2(nint p, nint a, nint t, nint f, uint off, int evt, uint flags) => Record("Exception2");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int ExceptionUnwind(nint p, nint a, nint t, int evt, uint flags) => Record("ExceptionUnwind");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int FunctionRemapComplete(nint p, nint a, nint t, nint f) => Record("FunctionRemapComplete");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int MDANotification(nint p, nint ctrl, nint t, nint mda) => Record("MDANotification");

    // ============================ ICorDebugManagedCallback3 (1) + 4 (3) ============================
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int CustomNotification(nint p, nint t, nint a) => Record("CustomNotification");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int BeforeGarbageCollection(nint p, nint proc) => Record("BeforeGarbageCollection");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int AfterGarbageCollection(nint p, nint proc) => Record("AfterGarbageCollection");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static int DataBreakpoint(nint p, nint proc, nint t, nint ctx, uint size) => Record("DataBreakpoint");

    // Build the hand-rolled COM object: 4 vtables of function pointers + a block of 4 vtable
    // pointers. Returns the v1 sub-object pointer to hand to SetManagedHandler.
    static nint BuildCallback()
    {
        nint* v1 = Alloc(29);  // IUnknown(3) + 26
        nint* v2 = Alloc(11);  // IUnknown(3) + 8
        nint* v3 = Alloc(4);   // IUnknown(3) + 1
        nint* v4 = Alloc(6);   // IUnknown(3) + 3

        // IUnknown (slots 0-2) shared across all four
        for (int i = 0; i < 4; i++)
        {
            nint* v = i == 0 ? v1 : i == 1 ? v2 : i == 2 ? v3 : v4;
            v[0] = (nint)(delegate* unmanaged[Cdecl]<nint, Guid*, nint*, int>)&QueryInterface;
            v[1] = (nint)(delegate* unmanaged[Cdecl]<nint, uint>)&AddRef;
            v[2] = (nint)(delegate* unmanaged[Cdecl]<nint, uint>)&Release;
        }

        int k = 3;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int>)&Breakpoint;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int, int>)&StepComplete;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&Break;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int, int>)&Exception1;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int>)&EvalComplete;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int>)&EvalException;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&CreateProcess;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&ExitProcess;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&CreateThread;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&ExitThread;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&LoadModule;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&UnloadModule;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&LoadClass;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&UnloadClass;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, uint, int>)&DebuggerError;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int, nint, nint, int>)&LogMessage;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int, uint, nint, nint, int>)&LogSwitch;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&CreateAppDomain;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&ExitAppDomain;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&LoadAssembly;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&UnloadAssembly;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&ControlCTrap;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&NameChange;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int>)&UpdateModuleSymbols;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int, int>)&EditAndContinueRemap;
        v1[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, uint, int>)&BreakpointSetError;

        k = 3;
        v2[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, uint, int>)&FunctionRemapOpportunity;
        v2[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, uint, nint, int>)&CreateConnection;
        v2[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, uint, int>)&ChangeConnection;
        v2[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, uint, int>)&DestroyConnection;
        v2[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, uint, int, uint, int>)&Exception2;
        v2[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int, uint, int>)&ExceptionUnwind;
        v2[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int>)&FunctionRemapComplete;
        v2[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int>)&MDANotification;

        v3[3] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&CustomNotification;

        k = 3;
        v4[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&BeforeGarbageCollection;
        v4[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&AfterGarbageCollection;
        v4[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, uint, int>)&DataBreakpoint;

        nint* block = Alloc(4);
        block[0] = (nint)v1; block[1] = (nint)v2; block[2] = (nint)v3; block[3] = (nint)v4;
        s_block = (nint)block;
        return s_block; // points to v1 (the ICorDebugManagedCallback interface)
    }

    static nint* Alloc(int count)
    {
        nint* p = (nint*)NativeMemory.AllocZeroed((nuint)count, (nuint)sizeof(nint));
        return p;
    }

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
        { Console.Error.WriteLine("Usage: dotnet 06-unmanagedcallersonly-vtable-probe.cs <pid>"); return 2; }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"os-arch    : {RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"target pid : {pid}");

        string? dbgshimPath = ResolveDbgShim(out string searched);
        if (dbgshimPath is null) { Console.Error.WriteLine($"FALSIFIED (discovery):\n{searched}"); return 3; }
        Console.WriteLine($"dbgshim    : {dbgshimPath}");
        nint lib = NativeLibrary.Load(dbgshimPath);

        var enumerateCLRs = (delegate* unmanaged[Cdecl]<uint, nint*, nint*, uint*, int>)NativeLibrary.GetExport(lib, "EnumerateCLRs");
        var closeCLREnumeration = (delegate* unmanaged[Cdecl]<nint, nint, uint, int>)NativeLibrary.GetExport(lib, "CloseCLREnumeration");
        var createVersionStringFromModule = (delegate* unmanaged[Cdecl]<uint, char*, char*, uint, uint*, int>)NativeLibrary.GetExport(lib, "CreateVersionStringFromModule");
        var createDebuggingInterfaceFromVersionEx = (delegate* unmanaged[Cdecl]<int, char*, nint*, int>)NativeLibrary.GetExport(lib, "CreateDebuggingInterfaceFromVersionEx");

        nint handleArray = 0, stringArray = 0; uint count = 0; int retries = 0;
        int hr = EnumerateWithRetry(enumerateCLRs, closeCLREnumeration, (uint)pid, &handleArray, &stringArray, &count, ref retries);
        if (hr < 0 || count == 0) { Console.Error.WriteLine($"FALSIFIED (EnumerateCLRs): 0x{hr:X8}"); NativeLibrary.Free(lib); return 4; }
        string modulePath = Marshal.PtrToStringUni(((nint*)stringArray)[0]) ?? "";
        Console.WriteLine($"coreclr    : {modulePath}");

        string? version = GetVersionString(createVersionStringFromModule, (uint)pid, modulePath);
        if (version is null) { closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib); return 5; }

        nint pUnknown = 0;
        fixed (char* pv = version) hr = createDebuggingInterfaceFromVersionEx(CorDebugVersion_4_0, pv, &pUnknown);
        if (hr < 0 || pUnknown == 0) { Console.Error.WriteLine($"FALSIFIED (CreateDebuggingInterface): 0x{hr:X8}"); closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib); return 6; }

        ComWrappers cw = new StrategyBasedComWrappers();
        ICorDebug iCorDebug;
        try { iCorDebug = (ICorDebug)cw.GetOrCreateObjectForComInstance(pUnknown, CreateObjectFlags.None); }
        catch (Exception ex) { Console.Error.WriteLine($"FALSIFIED (RCW): {ex.Message}"); Marshal.Release(pUnknown); closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib); return 8; }

        int hi = iCorDebug.Initialize();
        if (hi < 0) { Console.Error.WriteLine($"FALSIFIED (Initialize): 0x{hi:X8}"); Marshal.Release(pUnknown); closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib); return 9; }
        Console.WriteLine("Initialize : S_OK");

        nint pCallback = BuildCallback();
        Console.WriteLine($"callback   : hand-rolled [UnmanagedCallersOnly] vtable @ 0x{pCallback:X} (4 IIDs)");

        int hs = iCorDebug.SetManagedHandler(pCallback);
        if (hs < 0) { Console.Error.WriteLine($"FALSIFIED (SetManagedHandler): 0x{hs:X8} — QI thunk path rejected"); iCorDebug.Terminate(); Marshal.Release(pUnknown); closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib); return 12; }
        Console.WriteLine("SetHandler : S_OK  (our QI thunk ran on this thread — vtable structurally valid)");

        int hrAttach = iCorDebug.DebugActiveProcess((uint)pid, 0, out nint pProcess);
        Console.WriteLine($"DebugActiveProcess: hr=0x{hrAttach:X8}");
        if (hrAttach < 0 || pProcess == 0) { Console.Error.WriteLine("FALSIFIED (DebugActiveProcess)"); iCorDebug.Terminate(); Marshal.Release(pUnknown); closeCLREnumeration(handleArray, stringArray, count); NativeLibrary.Free(lib); return 13; }

        var controller = (ICorDebugController)cw.GetOrCreateObjectForComInstance(pProcess, CreateObjectFlags.None);
        int hrDetach = 0;
        bool fired;
        try
        {
            fired = s_fired.Wait(TimeSpan.FromSeconds(15));
            if (fired) Console.WriteLine($"callback   : FIRED  first='{s_firstEvent}'  count={s_count}  <-- A2: [UnmanagedCallersOnly] DELIVERY WORKS");
            else       Console.WriteLine("callback   : none within 15s  <-- A1: managed transition fails even for raw vtable");
        }
        finally { hrDetach = controller.Detach(); Console.WriteLine($"Detach     : hr=0x{hrDetach:X8}"); }

        int hrTerm = iCorDebug.Terminate();
        Console.WriteLine($"Terminate  : hr=0x{hrTerm:X8}");
        WriteFixture(pid, modulePath, version, hrAttach, fired, s_firstEvent, s_count, hrDetach, hrTerm);

        Marshal.Release(pUnknown);
        closeCLREnumeration(handleArray, stringArray, count);
        NativeLibrary.Free(lib);
        GC.KeepAlive(iCorDebug); GC.KeepAlive(controller);

        if (!fired) { Console.Error.WriteLine("Result: A1 — even a raw [UnmanagedCallersOnly] vtable received no callback. Native shim required."); return 14; }
        if (hrTerm < 0) return 10;
        Console.WriteLine("PROBE 06 PASSED — A2: raw managed vtable receives ICorDebug callbacks; BCL-only callback layer is viable.");
        return 0;
    }

    static int EnumerateWithRetry(
        delegate* unmanaged[Cdecl]<uint, nint*, nint*, uint*, int> enumerate,
        delegate* unmanaged[Cdecl]<nint, nint, uint, int> close,
        uint pid, nint* pH, nint* pS, uint* pC, ref int retries)
    {
        const int maxTries = 30; int hr = 0;
        for (int t = 0; t < maxTries; t++)
        {
            retries = t;
            hr = enumerate(pid, pH, pS, pC);
            if (hr >= 0 && *pH != 0 && *pC > 0)
            {
                bool ok = true;
                for (uint i = 0; i < *pC; i++) if (((nint*)*pH)[i] == INVALID_HANDLE_VALUE) { ok = false; break; }
                if (ok) return hr;
                close(*pH, *pS, *pC); *pH = 0; *pS = 0; *pC = 0;
            }
            if (hr == E_INVALIDARG || hr == E_FAIL) return hr;
            Thread.Sleep(100);
        }
        return hr;
    }

    static string? GetVersionString(
        delegate* unmanaged[Cdecl]<uint, char*, char*, uint, uint*, int> createVersion, uint pid, string modulePath)
    {
        uint cch = 100;
        fixed (char* pModule = modulePath)
            for (int attempt = 0; attempt < 2; attempt++)
            {
                char[] buf = new char[cch]; uint needed = 0; int hr;
                fixed (char* pBuf = buf) hr = createVersion(pid, pModule, pBuf, cch, &needed);
                if (hr >= 0) { int nul = Array.IndexOf(buf, '\0'); return new string(buf, 0, nul < 0 ? buf.Length : nul); }
                if (hr == E_INSUFFICIENT_BUFFER && needed > cch) { cch = needed; continue; }
                return null;
            }
        return null;
    }

    static string? ResolveDbgShim(out string searched)
    {
        var tried = new List<string>();
        string? env = Environment.GetEnvironmentVariable("DBGSHIM_PATH");
        if (!string.IsNullOrEmpty(env)) { tried.Add(env + "  (DBGSHIM_PATH)"); if (File.Exists(env)) { searched = string.Join("\n", tried); return env; } }
        string libName = OperatingSystem.IsWindows() ? "dbgshim.dll" : OperatingSystem.IsMacOS() ? "libdbgshim.dylib" : "libdbgshim.so";
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

        searched = string.Join("\n", tried) + "\n(set DBGSHIM_PATH or `dotnet add package Microsoft.Diagnostics.DbgShim." + rid + "` to populate the NuGet cache)";
        return null;
    }

    static void WriteFixture(int pid, string modulePath, string version, int hrAttach, bool fired, string? first, int count, int hrDetach, int hrTerm)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(dir, $"06-unmanagedcallersonly-vtable-{rid}-{ts}.txt"),
            "# DrHook.Engine probe 06 fixture — hand-rolled [UnmanagedCallersOnly] callback vtable\n" +
            $"timestamp        = {DateTime.UtcNow:O}\nruntime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\ntarget-pid       = {pid}\ncoreclr-path     = {modulePath}\nversion-string   = {version}\n" +
            $"setmanagedhandler-hr = (S_OK if reached attach)\ndebugactiveprocess-hr = 0x{hrAttach:X8}\n" +
            $"callback-fired   = {fired}\nfirst-event      = {first ?? "(none)"}\ncallback-count   = {count}\n" +
            $"detach-hr        = 0x{hrDetach:X8}\nterminate-hr     = 0x{hrTerm:X8}\n" +
            $"verdict          = {(fired ? "A2 — raw [UnmanagedCallersOnly] vtable receives delivery; BCL-only viable" : "A1 — managed transition fails on mscordbi thread; native shim required")}\n");
        Console.WriteLine($"fixture    : fixtures/06-unmanagedcallersonly-vtable-{rid}-{ts}.txt");
    }
}
