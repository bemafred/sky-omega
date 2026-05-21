// Attach/detach lifecycle, ported from PoC probes 05/06 as engine code. Composes the
// validated pieces: DbgShim (native attach flow) -> ICorDebug RCW (consume, source-gen COM)
// -> ManagedCallbackHost (receive, [UnmanagedCallersOnly] vtable) -> DebugActiveProcess ->
// ICorDebugController (Continue/Detach). The StrategyBasedComWrappers is held as a substrate
// singleton (finding 13). Phase 1 validates attach + callback delivery + clean teardown;
// the continue-loop and stepping are Phase 2 (ADR-006).

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using SkyOmega.DrHook.Engine.Interop;

namespace SkyOmega.DrHook.Engine;

/// <summary>An attached managed-debugging session over a target .NET process. Dispose detaches
/// and releases all native resources.</summary>
public sealed class DebugSession : IDisposable
{
    private static readonly ComWrappers Wrappers = new StrategyBasedComWrappers();

    private readonly DbgShim _dbgShim;
    private readonly ManagedCallbackHost _callback;
    private readonly ICorDebug _cordbg;
    private readonly ICorDebugController _controller;
    private nint _pUnknown;
    private nint _pProcess;
    private bool _detached;
    private bool _disposed;

    private DebugSession(int processId, DbgShim dbgShim, ManagedCallbackHost callback,
                         ICorDebug cordbg, ICorDebugController controller, nint pUnknown, nint pProcess)
    {
        ProcessId = processId;
        _dbgShim = dbgShim;
        _callback = callback;
        _cordbg = cordbg;
        _controller = controller;
        _pUnknown = pUnknown;
        _pProcess = pProcess;
    }

    /// <summary>OS process id of the attached target.</summary>
    public int ProcessId { get; }

    /// <summary>Attach the native ICorDebug engine to a running .NET process and register the
    /// managed callback. On macOS/ARM64 this needs no debug entitlement (finding 13).</summary>
    /// <exception cref="DebugEngineException">An ICorDebug step failed.</exception>
    public static DebugSession Attach(int processId, IDebugEventSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        DbgShim dbgShim = DbgShim.Load();
        ManagedCallbackHost? callback = null;
        nint pUnknown = 0;
        try
        {
            ThrowIfFailed(dbgShim.CreateCordbForProcess(processId, out pUnknown), "CreateDebuggingInterfaceFromVersion");

            var cordbg = (ICorDebug)Wrappers.GetOrCreateObjectForComInstance(pUnknown, CreateObjectFlags.None);
            ThrowIfFailed(cordbg.Initialize(), "ICorDebug.Initialize");

            callback = new ManagedCallbackHost(sink);
            ThrowIfFailed(cordbg.SetManagedHandler(callback.NativePointer), "ICorDebug.SetManagedHandler");

            ThrowIfFailed(cordbg.DebugActiveProcess((uint)processId, 0, out nint pProcess), "ICorDebug.DebugActiveProcess");
            var controller = (ICorDebugController)Wrappers.GetOrCreateObjectForComInstance(pProcess, CreateObjectFlags.None);

            return new DebugSession(processId, dbgShim, callback, cordbg, controller, pUnknown, pProcess);
        }
        catch
        {
            callback?.Dispose();
            if (pUnknown != 0) Marshal.Release(pUnknown);
            dbgShim.Dispose();
            throw;
        }
    }

    /// <summary>Detach the debugger; the target resumes running without it. Idempotent.</summary>
    public void Detach()
    {
        if (_detached) return;
        _controller.Detach();
        _detached = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Detach();
        _cordbg.Terminate();

        if (_pProcess != 0) { Marshal.Release(_pProcess); _pProcess = 0; }
        if (_pUnknown != 0) { Marshal.Release(_pUnknown); _pUnknown = 0; }

        _callback.Dispose();
        _dbgShim.Dispose();
        GC.KeepAlive(_cordbg);
        GC.KeepAlive(_controller);
    }

    private static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
            throw new DebugEngineException(operation, hr);
    }
}

/// <summary>Raised when an ICorDebug operation returns a failure HRESULT.</summary>
public sealed class DebugEngineException : Exception
{
    public DebugEngineException(string operation, int hresult)
        : base($"{operation} failed (HRESULT 0x{hresult:X8}).")
        => HResult = hresult;
}
