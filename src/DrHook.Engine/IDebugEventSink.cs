namespace SkyOmega.DrHook.Engine;

/// <summary>
/// Receives managed debug events delivered by the runtime (via mscordbi) to the engine's
/// <c>ICorDebugManagedCallback</c> vtable. Phase 1 surfaces events by name for lifecycle
/// observation; Phase 2 enriches this with typed events and the controller needed to drive
/// the continue-loop (ADR-006 Open Question 4). Implementations must not throw — the call
/// originates on mscordbi's native event thread across the [UnmanagedCallersOnly] boundary.
/// </summary>
public interface IDebugEventSink
{
    /// <summary>Invoked once per managed debug callback, identified by its ICorDebug method
    /// name (e.g. "CreateProcess", "LoadModule", "Breakpoint").</summary>
    void OnEvent(string name);
}
