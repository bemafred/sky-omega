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
    /// change; logpoint-aware sinks override. Called on the caller thread from inside
    /// <see cref="DebugSession.WaitForStop"/> (not the callback worker), so implementations must be
    /// thread-safe if they also handle <see cref="OnEvent"/>.</summary>
    void OnLog(LogRecord record) { }

    /// <summary>Invoked when the engine detects a substrate-correctness anomaly — the typed
    /// surprise-capture mechanism per ADR-007 Phase 1. Each <see cref="EngineAnomaly.Kind"/> is
    /// a named substrate concept (see <see cref="AnomalyKind"/>); the host buffers them via
    /// <see cref="BoundedAnomalySink"/> (or equivalent) and surfaces through a drain tool.
    /// Default no-op so existing sinks compile without change; diagnostic-aware sinks override.
    /// May be called from the pump worker, the MCP request thread, OR mscordbi's event thread
    /// (per <see cref="EngineAnomaly.Thread"/>), so implementations must be thread-safe.
    /// The contract (per Rule 1, finding 55) is the same as <see cref="OnEvent"/>: the
    /// implementation must do O(1) stack work since some call sites are on threads with stack
    /// budgets we don't own.
    ///
    /// <para><b>MUST NOT throw.</b> <c>OnAnomaly</c> is the substrate's last-resort surface —
    /// it is called from inside the pump worker's outer try/catch (EA-4) when another part of
    /// the substrate (or a user callback) has already thrown. The catch around OnAnomaly itself
    /// is intentionally absent: if this method throws, the pump worker dies with an unhandled
    /// exception, and because the worker is <c>IsBackground=true</c>, the .NET runtime
    /// terminates the process. Implementations that buffer anomalies should swallow their own
    /// failures (e.g. allocation failure → drop the record + count it); throwing here trades
    /// substrate diagnostic state for process death and is never the right answer. WE-OA-1
    /// (finding 60).</para></summary>
    void OnAnomaly(EngineAnomaly anomaly) { }

    /// <summary>Invoked for each chunk of a LAUNCHED debuggee's console output (stdout/stderr),
    /// captured from the DrHook-owned pipe the child's streams were redirected to (ADR-011 D2/D3).
    /// Surface-agnostic: the host buffers it (e.g. <see cref="BoundedConsoleSink"/>, drained by a
    /// tool) and future Mira surfaces consume the same record. Default no-op so existing sinks
    /// compile unchanged. Called on DrHook's console-drain background threads (one per stream), so
    /// implementations must be thread-safe and MUST NOT throw — an unhandled throw would kill the
    /// drain thread, the pipe would stop draining, and the debuggee would block on its next write.</summary>
    void OnConsoleOutput(ConsoleOutputRecord record) { }

    /// <summary>Invoked when the operator states a hypothesis at a debug step — the prediction half of the
    /// (hypothesis, observation) braid (ADR-012 D4 / Phase 3). Emitted from the MCP boundary, where every
    /// state-changing / inspecting tool already takes a <c>hypothesis</c>; the transport publishes it as a
    /// delta so a connected view shows it inline, immediately before the observation it predicts. Default
    /// no-op so existing sinks compile unchanged; braid-aware sinks override. Same O(1) / must-not-throw
    /// contract as the other channels.</summary>
    void OnHypothesis(HypothesisRecord record) { }
}
