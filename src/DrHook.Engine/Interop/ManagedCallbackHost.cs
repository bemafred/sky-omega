// The RECEIVE direction: a hand-rolled native vtable mscordbi calls back into.
//
// Validated by PoC probes 05/06 (findings 12/13): a [GeneratedComClass] ComWrappers CCW
// registers via SetManagedHandler but the runtime NEVER delivers callbacks to it; a raw
// [UnmanagedCallersOnly] function-pointer vtable DOES receive delivery. So the callback is
// hand-rolled, not ComWrappers. This generalizes probe 06's static thunks to INSTANCE
// dispatch: a native COM object holds, per interface, { vtable-ptr, gchandle }; every thunk
// recovers its ManagedCallbackHost from the GCHandle (at pThis + sizeof(nint)) and forwards
// to the IManagedCallbackSink, tagging the three STOPPING callbacks (Breakpoint, StepComplete,
// Break) with their CallbackKind + the thread/appDomain pointers so the pump can suppress the
// auto-Continue. The 38 callback methods are declared in exact IDL order — a misordered slot
// crashes when the runtime calls it.
//
// SUBSTRATE RULE 1 — O(1)-stack thunks (ENG-STK-3, finding 55):
// Every [UnmanagedCallersOnly] thunk below runs on mscordbi's RC event thread, whose stack
// budget we do NOT own. The contract is: thunks must do O(1) stack work — recover the host
// via HostOf, dispatch to _sink.OnCallback (which BlockingCollection.Adds and returns), and
// return S_OK. NO stackalloc, NO recursion, NO synchronous user code. Future growth of any
// thunk requires re-validating against mscordbi's actual stack budget. Phase 8 will add an
// IL-size unit test asserting each thunk's IL stays under a threshold.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SkyOmega.DrHook.Engine.Interop;

internal sealed unsafe class ManagedCallbackHost : IDisposable
{
    private const int S_OK = 0;
    private const int E_NOINTERFACE = unchecked((int)0x80004002);

    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_Callback = new("3d6f5f60-7538-11d3-8d5b-00104b35e7ef");
    private static readonly Guid IID_Callback2 = new("250E5EEA-DB5C-4C76-B6F3-8C46F12E3203");
    private static readonly Guid IID_Callback3 = new("264EA0FC-2591-49AA-868E-835E6515323F");
    private static readonly Guid IID_Callback4 = new("322911AE-16A5-49BA-84A3-ED69678138A3");

    private readonly IManagedCallbackSink _sink;
    private GCHandle _self;
    private nint _block;                   // 4 sub-objects, each { vtable-ptr, gchandle }
    private nint _v1, _v2, _v3, _v4;       // the four interface vtables
    private int _refCount;

    public ManagedCallbackHost(IManagedCallbackSink sink)
    {
        _sink = sink;
        Build();
    }

    /// <summary>The ICorDebugManagedCallback (sub-object 0) pointer to hand to SetManagedHandler.</summary>
    public nint NativePointer => _block;

    private uint AddRefImpl() => (uint)Interlocked.Increment(ref _refCount);
    private uint ReleaseImpl() => (uint)Interlocked.Decrement(ref _refCount); // teardown is Dispose, not refcount-driven

    private static ManagedCallbackHost? HostOf(nint pThis)
        => GCHandle.FromIntPtr(*(nint*)(pThis + sizeof(nint))).Target as ManagedCallbackHost;

    private static int Fire(nint pThis, string name)
        => Fire(pThis, CallbackKind.Informational, name, 0, 0, 0);

    private static int Fire(nint pThis, CallbackKind kind, string name, nint appDomain, nint thread, int detail = 0, nint breakpoint = 0)
    {
        HostOf(pThis)?._sink.OnCallback(kind, name, appDomain, thread, detail, breakpoint);
        return S_OK;
    }

    private void Build()
    {
        _v1 = (nint)Alloc(29); // IUnknown(3) + ICorDebugManagedCallback(26)
        _v2 = (nint)Alloc(11); // IUnknown(3) + ...Callback2(8)
        _v3 = (nint)Alloc(4);  // IUnknown(3) + ...Callback3(1)
        _v4 = (nint)Alloc(6);  // IUnknown(3) + ...Callback4(3)

        foreach (nint v in stackalloc nint[] { _v1, _v2, _v3, _v4 })
        {
            nint* p = (nint*)v;
            p[0] = (nint)(delegate* unmanaged[Cdecl]<nint, Guid*, nint*, int>)&QueryInterface;
            p[1] = (nint)(delegate* unmanaged[Cdecl]<nint, uint>)&AddRef;
            p[2] = (nint)(delegate* unmanaged[Cdecl]<nint, uint>)&Release;
        }

        nint* v1 = (nint*)_v1;
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
        v1[k] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, uint, int>)&BreakpointSetError;

        nint* v2 = (nint*)_v2;
        k = 3;
        v2[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, uint, int>)&FunctionRemapOpportunity;
        v2[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, uint, nint, int>)&CreateConnection;
        v2[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, uint, int>)&ChangeConnection;
        v2[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, uint, int>)&DestroyConnection;
        v2[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, uint, int, uint, int>)&Exception2;
        v2[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int, uint, int>)&ExceptionUnwind;
        v2[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int>)&FunctionRemapComplete;
        v2[k] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int>)&MDANotification;

        ((nint*)_v3)[3] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&CustomNotification;

        nint* v4 = (nint*)_v4;
        k = 3;
        v4[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&BeforeGarbageCollection;
        v4[k++] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&AfterGarbageCollection;
        v4[k] = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, uint, int>)&DataBreakpoint;

        _self = GCHandle.Alloc(this);
        nint gch = GCHandle.ToIntPtr(_self);
        nint* block = (nint*)Alloc(8); // 4 sub-objects x { vtable, gchandle }
        block[0] = _v1; block[1] = gch;
        block[2] = _v2; block[3] = gch;
        block[4] = _v3; block[5] = gch;
        block[6] = _v4; block[7] = gch;
        _block = (nint)block;
    }

    private static void* Alloc(int slots) => NativeMemory.AllocZeroed((nuint)slots, (nuint)sizeof(nint));

    public void Dispose()
    {
        if (_block != 0) { NativeMemory.Free((void*)_block); _block = 0; }
        if (_v1 != 0) { NativeMemory.Free((void*)_v1); _v1 = 0; }
        if (_v2 != 0) { NativeMemory.Free((void*)_v2); _v2 = 0; }
        if (_v3 != 0) { NativeMemory.Free((void*)_v3); _v3 = 0; }
        if (_v4 != 0) { NativeMemory.Free((void*)_v4); _v4 = 0; }
        if (_self.IsAllocated) _self.Free();
    }

    // ============================ IUnknown ============================
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int QueryInterface(nint pThis, Guid* riid, nint* ppv)
    {
        ManagedCallbackHost? host = HostOf(pThis);
        if (host is null) { *ppv = 0; return E_NOINTERFACE; }
        Guid iid = *riid;
        int index =
            (iid == IID_IUnknown || iid == IID_Callback) ? 0 :
            (iid == IID_Callback2) ? 1 :
            (iid == IID_Callback3) ? 2 :
            (iid == IID_Callback4) ? 3 : -1;
        if (index < 0) { *ppv = 0; return E_NOINTERFACE; }
        *ppv = host._block + index * 2 * sizeof(nint);
        host.AddRefImpl();
        return S_OK;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint AddRef(nint pThis) => HostOf(pThis)?.AddRefImpl() ?? 1;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint Release(nint pThis) => HostOf(pThis)?.ReleaseImpl() ?? 0;

    // ============================ ICorDebugManagedCallback (26) ============================
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int Breakpoint(nint p, nint a, nint t, nint b) => Fire(p, CallbackKind.BreakpointHit, "Breakpoint", a, t, detail: 0, breakpoint: b);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int StepComplete(nint p, nint a, nint t, nint s, int r) => Fire(p, CallbackKind.StepComplete, "StepComplete", a, t);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int Break(nint p, nint a, nint t) => Fire(p, CallbackKind.Break, "Break", a, t);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int Exception1(nint p, nint a, nint t, int u) => Fire(p, "Exception");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int EvalComplete(nint p, nint a, nint t, nint e) => Fire(p, CallbackKind.EvalComplete, "EvalComplete", a, t);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int EvalException(nint p, nint a, nint t, nint e) => Fire(p, CallbackKind.EvalException, "EvalException", a, t);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int CreateProcess(nint p, nint proc) => Fire(p, "CreateProcess");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int ExitProcess(nint p, nint proc) => Fire(p, "ExitProcess");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int CreateThread(nint p, nint a, nint t) => Fire(p, "CreateThread");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int ExitThread(nint p, nint a, nint t) => Fire(p, "ExitThread");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int LoadModule(nint p, nint a, nint m) => Fire(p, CallbackKind.Informational, "LoadModule", a, 0, 0, breakpoint: m); // carry the ICorDebugModule for the Layer 2 entry-module hold-gate
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int UnloadModule(nint p, nint a, nint m) => Fire(p, "UnloadModule");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int LoadClass(nint p, nint a, nint c) => Fire(p, "LoadClass");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int UnloadClass(nint p, nint a, nint c) => Fire(p, "UnloadClass");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int DebuggerError(nint p, nint proc, int hr, uint code) => Fire(p, "DebuggerError");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int LogMessage(nint p, nint a, nint t, int lvl, nint sw, nint msg) => Fire(p, "LogMessage");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int LogSwitch(nint p, nint a, nint t, int lvl, uint reason, nint sw, nint parent) => Fire(p, "LogSwitch");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int CreateAppDomain(nint p, nint proc, nint a) => Fire(p, "CreateAppDomain");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int ExitAppDomain(nint p, nint proc, nint a) => Fire(p, "ExitAppDomain");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int LoadAssembly(nint p, nint a, nint asm) => Fire(p, "LoadAssembly");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int UnloadAssembly(nint p, nint a, nint asm) => Fire(p, "UnloadAssembly");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int ControlCTrap(nint p, nint proc) => Fire(p, "ControlCTrap");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int NameChange(nint p, nint a, nint t) => Fire(p, "NameChange");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int UpdateModuleSymbols(nint p, nint a, nint m, nint s) => Fire(p, "UpdateModuleSymbols");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int EditAndContinueRemap(nint p, nint a, nint t, nint f, int acc) => Fire(p, "EditAndContinueRemap");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int BreakpointSetError(nint p, nint a, nint t, nint b, uint err) => Fire(p, "BreakpointSetError");

    // ============================ ICorDebugManagedCallback2 (8) ============================
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int FunctionRemapOpportunity(nint p, nint a, nint t, nint oF, nint nF, uint off) => Fire(p, "FunctionRemapOpportunity");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int CreateConnection(nint p, nint proc, uint id, nint name) => Fire(p, "CreateConnection");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int ChangeConnection(nint p, nint proc, uint id) => Fire(p, "ChangeConnection");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int DestroyConnection(nint p, nint proc, uint id) => Fire(p, "DestroyConnection");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int Exception2(nint p, nint a, nint t, nint f, uint off, int evt, uint flags) => Fire(p, CallbackKind.Exception, "Exception", a, t, evt);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int ExceptionUnwind(nint p, nint a, nint t, int evt, uint flags) => Fire(p, "ExceptionUnwind");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int FunctionRemapComplete(nint p, nint a, nint t, nint f) => Fire(p, "FunctionRemapComplete");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int MDANotification(nint p, nint ctrl, nint t, nint mda) => Fire(p, "MDANotification");

    // ============================ ICorDebugManagedCallback3 (1) + 4 (3) ============================
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int CustomNotification(nint p, nint t, nint a) => Fire(p, "CustomNotification");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int BeforeGarbageCollection(nint p, nint proc) => Fire(p, "BeforeGarbageCollection");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int AfterGarbageCollection(nint p, nint proc) => Fire(p, "AfterGarbageCollection");
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] private static int DataBreakpoint(nint p, nint proc, nint t, nint ctx, uint size) => Fire(p, "DataBreakpoint");
}
