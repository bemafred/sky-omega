// In-process layer tests for the callback vtable — the testability discipline's PRIMARY
// surface (docs/limits/drhook-testability.md): the test itself plays the role of the native
// caller (mscordbi), invoking the hand-rolled vtable directly in-process. No debuggee
// process, no dbgshim, no external tool — deterministic and CI-safe. This exercises the
// engine's newest/hardest code: vtable layout, QueryInterface multi-interface dispatch, and
// GCHandle-based instance recovery (the mechanism probe 06's static thunks didn't have).

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Interop;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed unsafe class ManagedCallbackHostTests
{
    private const int S_OK = 0;
    private const int E_NOINTERFACE = unchecked((int)0x80004002);

    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_Callback = new("3d6f5f60-7538-11d3-8d5b-00104b35e7ef");
    private static readonly Guid IID_Callback2 = new("250E5EEA-DB5C-4C76-B6F3-8C46F12E3203");
    private static readonly Guid IID_Callback3 = new("264EA0FC-2591-49AA-868E-835E6515323F");
    private static readonly Guid IID_Callback4 = new("322911AE-16A5-49BA-84A3-ED69678138A3");
    private static readonly Guid IID_Bogus = new("11111111-2222-3333-4444-555555555555");

    private sealed class RecordingSink : IManagedCallbackSink
    {
        public List<string> Events { get; } = new();
        public List<CallbackKind> Kinds { get; } = new();
        public List<nint> Threads { get; } = new();
        public List<int> Details { get; } = new();
        public List<nint> Breakpoints { get; } = new();
        public void OnCallback(CallbackKind kind, string name, nint appDomain, nint thread, int detail, nint breakpoint = 0)
        {
            Events.Add(name);
            Kinds.Add(kind);
            Threads.Add(thread);
            Details.Add(detail);
            Breakpoints.Add(breakpoint);
        }
    }

    private static nint Slot(nint subObject, int index) => ((nint*)*(nint*)subObject)[index];

    [Fact]
    public void QueryInterface_HandsOutASubObjectForEachSupportedIid()
    {
        var sink = new RecordingSink();
        using var host = new ManagedCallbackHost(sink);
        nint p = host.NativePointer;
        var qi = (delegate* unmanaged[Cdecl]<nint, Guid*, nint*, int>)Slot(p, 0);

        foreach (Guid iid in new[] { IID_IUnknown, IID_Callback, IID_Callback2, IID_Callback3, IID_Callback4 })
        {
            Guid id = iid;
            nint ppv;
            int hr = qi(p, &id, &ppv);
            Assert.Equal(S_OK, hr);
            Assert.True(ppv != 0, $"QI for {iid} returned a null interface pointer");
        }
    }

    [Fact]
    public void QueryInterface_RejectsUnsupportedIid()
    {
        var sink = new RecordingSink();
        using var host = new ManagedCallbackHost(sink);
        nint p = host.NativePointer;
        var qi = (delegate* unmanaged[Cdecl]<nint, Guid*, nint*, int>)Slot(p, 0);

        Guid bogus = IID_Bogus;
        nint ppv;
        int hr = qi(p, &bogus, &ppv);

        Assert.Equal(E_NOINTERFACE, hr);
        Assert.True(ppv == 0);
    }

    [Fact]
    public void CreateProcessThunk_ForwardsToTheSink()
    {
        var sink = new RecordingSink();
        using var host = new ManagedCallbackHost(sink);
        nint p = host.NativePointer;

        // ICorDebugManagedCallback V-table: IUnknown(0-2), Breakpoint(3)..EvalException(8), CreateProcess(9).
        var createProcess = (delegate* unmanaged[Cdecl]<nint, nint, int>)Slot(p, 9);
        int hr = createProcess(p, 0);

        Assert.Equal(S_OK, hr);
        Assert.Equal(new[] { "CreateProcess" }, sink.Events.ToArray());
        Assert.Equal(new[] { CallbackKind.Informational }, sink.Kinds.ToArray());
    }

    [Fact]
    public void BreakpointThunk_ClassifiesAsStopping_AndForwardsThreadPointer()
    {
        var sink = new RecordingSink();
        using var host = new ManagedCallbackHost(sink);
        nint p = host.NativePointer;

        // ICorDebugManagedCallback slot 3 = Breakpoint(pThis, pAppDomain, pThread, pBreakpoint).
        var breakpoint = (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int>)Slot(p, 3);
        int hr = breakpoint(p, 0xAA, 0xBB, 0xCC);

        Assert.Equal(S_OK, hr);
        Assert.Equal(new[] { "Breakpoint" }, sink.Events.ToArray());
        Assert.Equal(new[] { CallbackKind.BreakpointHit }, sink.Kinds.ToArray()); // not auto-continued downstream
        Assert.Equal(new nint[] { 0xBB }, sink.Threads.ToArray());                // pThread forwarded for stepping
    }

    [Fact]
    public void Callback2Method_DispatchesThroughTheV2SubObject()
    {
        var sink = new RecordingSink();
        using var host = new ManagedCallbackHost(sink);
        nint p = host.NativePointer;

        var qi = (delegate* unmanaged[Cdecl]<nint, Guid*, nint*, int>)Slot(p, 0);
        Guid id = IID_Callback2;
        nint pV2;
        Assert.Equal(S_OK, qi(p, &id, &pV2));

        // ICorDebugManagedCallback2: IUnknown(0-2), FunctionRemapOpportunity(3).
        var fro = (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, uint, int>)Slot(pV2, 3);
        int hr = fro(pV2, 0, 0, 0, 0, 0);

        Assert.Equal(S_OK, hr);
        Assert.Equal(new[] { "FunctionRemapOpportunity" }, sink.Events.ToArray());
    }

    [Fact]
    public void Exception2Thunk_ClassifiesAsStopping_AndForwardsCallbackType()
    {
        var sink = new RecordingSink();
        using var host = new ManagedCallbackHost(sink);
        nint p = host.NativePointer;

        var qi = (delegate* unmanaged[Cdecl]<nint, Guid*, nint*, int>)Slot(p, 0);
        Guid id = IID_Callback2;
        nint pV2;
        Assert.Equal(S_OK, qi(p, &id, &pV2));

        // ICorDebugManagedCallback2: IUnknown(0-2), FunctionRemapOpportunity(3), CreateConnection(4),
        // ChangeConnection(5), DestroyConnection(6), Exception(7) — the rich exception callback.
        // Signature: Exception(pThis, pAppDomain, pThread, pFrame, nOffset, dwEventType, dwFlags).
        var exception = (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, uint, int, uint, int>)Slot(pV2, 7);
        const int DEBUG_EXCEPTION_FIRST_CHANCE = 1;
        int hr = exception(pV2, 0xAA, 0xBB, 0, 0, DEBUG_EXCEPTION_FIRST_CHANCE, 0);

        Assert.Equal(S_OK, hr);
        Assert.Equal(new[] { "Exception" }, sink.Events.ToArray());
        Assert.Equal(new[] { CallbackKind.Exception }, sink.Kinds.ToArray());          // stopping, not auto-continued
        Assert.Equal(new nint[] { 0xBB }, sink.Threads.ToArray());                      // pThread forwarded
        Assert.Equal(new[] { DEBUG_EXCEPTION_FIRST_CHANCE }, sink.Details.ToArray());   // CorDebugExceptionCallbackType forwarded
    }

    [Fact]
    public void InstanceDispatch_RoutesEachCallbackToItsOwnHostSink()
    {
        var sinkA = new RecordingSink();
        var sinkB = new RecordingSink();
        using var hostA = new ManagedCallbackHost(sinkA);
        using var hostB = new ManagedCallbackHost(sinkB);

        var cpA = (delegate* unmanaged[Cdecl]<nint, nint, int>)Slot(hostA.NativePointer, 9);
        var cpB = (delegate* unmanaged[Cdecl]<nint, nint, int>)Slot(hostB.NativePointer, 9);

        cpA(hostA.NativePointer, 0);
        cpB(hostB.NativePointer, 0);
        cpB(hostB.NativePointer, 0);

        // GCHandle recovery must route to the right instance, not a shared static.
        Assert.Equal(new[] { "CreateProcess" }, sinkA.Events.ToArray());
        Assert.Equal(new[] { "CreateProcess", "CreateProcess" }, sinkB.Events.ToArray());
    }
}
