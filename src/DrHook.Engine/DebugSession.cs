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
    private readonly CallbackPump _pump;
    private readonly ManagedCallbackHost _callback;
    private readonly ICorDebug _cordbg;
    private readonly ICorDebugController _controller;
    private nint _pUnknown;
    private nint _pProcess;
    private bool _detached;
    private bool _disposed;

    private DebugSession(int processId, DbgShim dbgShim, CallbackPump pump, ManagedCallbackHost callback,
                         ICorDebug cordbg, ICorDebugController controller, nint pUnknown, nint pProcess)
    {
        ProcessId = processId;
        _dbgShim = dbgShim;
        _pump = pump;
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
        CallbackPump? pump = null;
        ManagedCallbackHost? callback = null;
        nint pUnknown = 0;
        try
        {
            ThrowIfFailed(dbgShim.CreateCordbForProcess(processId, out pUnknown), "CreateDebuggingInterfaceFromVersion");

            var cordbg = (ICorDebug)Wrappers.GetOrCreateObjectForComInstance(pUnknown, CreateObjectFlags.None);
            ThrowIfFailed(cordbg.Initialize(), "ICorDebug.Initialize");

            pump = new CallbackPump(sink);
            callback = new ManagedCallbackHost(pump);
            ThrowIfFailed(cordbg.SetManagedHandler(callback.NativePointer), "ICorDebug.SetManagedHandler");

            ThrowIfFailed(cordbg.DebugActiveProcess((uint)processId, 0, out nint pProcess), "ICorDebug.DebugActiveProcess");
            var controller = (ICorDebugController)Wrappers.GetOrCreateObjectForComInstance(pProcess, CreateObjectFlags.None);

            // Drive the continue-loop now that the process controller exists. Callbacks
            // enqueued since SetManagedHandler (if any) drain immediately.
            pump.Start(() => controller.Continue(0));

            return new DebugSession(processId, dbgShim, pump, callback, cordbg, controller, pUnknown, pProcess);
        }
        catch
        {
            pump?.Dispose();
            callback?.Dispose();
            if (pUnknown != 0) Marshal.Release(pUnknown);
            dbgShim.Dispose();
            throw;
        }
    }

    /// <summary>Block until the debuggee next stops (breakpoint, step complete, or
    /// <c>Debugger.Break</c>), up to <paramref name="timeout"/>. Returns null on timeout (still
    /// running); a <see cref="StopReason.ProcessExited"/> result means the session is over.
    /// While stopped the debuggee is frozen — inspect, then <see cref="Resume"/>.</summary>
    public StopInfo? WaitForStop(TimeSpan timeout) => _pump.WaitForStop(timeout);

    /// <summary>Resume a stopped debuggee so it runs to the next stop or exit.</summary>
    public void Resume() => _pump.Resume();

    /// <summary>Names of the modules loaded in the target (process → app domains → assemblies →
    /// modules). Valid only while the debuggee is stopped (after <see cref="WaitForStop"/>) —
    /// ICorDebug enumeration requires the process to be synchronized.</summary>
    public IReadOnlyList<string> EnumerateModules() => RuntimeNavigation.ModuleNames(_pProcess);

    /// <summary>Detach the debugger; the target resumes running without it. Idempotent.</summary>
    public void Detach()
    {
        if (_detached) return;
        _controller.Detach();
        _detached = true;
    }

    /// <summary>Synchronize the target before Detach so mscordbi's RC event thread is not
    /// mid-flush of queued callbacks when Detach tears down the shim (finding 14). Stop blocks
    /// until the process is synchronized; best-effort (a failing HRESULT falls through to
    /// Detach rather than throwing on the dispose path).</summary>
    private void Quiesce() => _controller.Stop(0);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop our worker first: it drives controller.Continue, so it must be joined before
        // we touch the controller for the quiescent detach.
        _pump.Dispose();

        // Quiescent detach (ADR-006 Phase 2 increment 2; finding 14 / docs/limits/drhook-clean-detach.md).
        // Detach must not race mscordbi's RC event thread flushing queued callbacks — that
        // segfaults the shim mid-flush under load (probe 07). Stop() synchronizes the process:
        // it blocks until any in-flight dispatch completes and the debuggee is halted, so no
        // flush is in progress when Detach tears down the shim. Detach from the synchronized
        // state is the probe-05-validated safe path.
        Quiesce();
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
