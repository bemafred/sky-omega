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

    /// <summary>Invoked when a <see cref="BreakpointPolicy"/> fires its log action (a logpoint hit)
    /// or when a condition faults (<see cref="LogRecord.IsFault"/>). The engine produces the record;
    /// the host chooses the destination — typically a bounded ring buffer drained by a tool, with a
    /// file tee for high-volume runs (finding 35). Default no-op so existing sinks compile without
    /// change; logpoint-aware sinks override. May be called from the thread that invoked
    /// <see cref="DebugSession.WaitForPolicyStop"/> (not the callback worker), so implementations
    /// must be thread-safe if they also handle <see cref="OnEvent"/>.</summary>
    void OnLog(LogRecord record) { }
}
