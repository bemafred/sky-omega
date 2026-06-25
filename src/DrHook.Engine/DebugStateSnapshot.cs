namespace SkyOmega.DrHook.Engine;

/// <summary>The execution disposition of a <see cref="DebugSession"/> at snapshot time (ADR-012 D1).
/// Derived from the current stop the session DRIVER holds: the loop that drives
/// <see cref="DebugSession.WaitForStop"/> is the authority on whether the debuggee is frozen or running —
/// the session itself does not track it (WaitForStop returns the stop and forgets it), so this is computed
/// from the stop supplied to <see cref="DebugSession.CaptureState"/>, not read back from the session.</summary>
public enum ExecutionState
{
    /// <summary>The debuggee is running — no current stop. Inspection (stack / locals / arguments) is
    /// unavailable; <see cref="ExecutionPosition"/> is <see cref="ExecutionPosition.None"/>.</summary>
    Running,

    /// <summary>The debuggee is frozen at a stop — inspection is valid and <see cref="ExecutionPosition"/>
    /// is populated from the synchronized frame.</summary>
    Stopped,

    /// <summary>The debuggee has exited (<see cref="StopReason.ProcessExited"/>); no further stops occur.</summary>
    Exited,
}

/// <summary>Session / lifecycle facet of a <see cref="DebugStateSnapshot"/> — enough for a view that
/// connects mid-session to identify the target and its disposition with no prior context (the 2026-06-25
/// ownership directive: a human-launched view joins an already-running, LLM-owned session and must render
/// from the snapshot alone). <see cref="OwnsTarget"/> is the Owned-vs-Borrowed distinction (finding 64);
/// <see cref="RuntimeMajor"/> is the .NET major this debugger process is locked to (finding 86).</summary>
public sealed record SessionInfo(
    int ProcessId,
    bool OwnsTarget,
    int? RuntimeMajor,
    bool IsDetached,
    bool IsDisposed,
    ExecutionState Execution);

/// <summary>Execution-position facet — where the debuggee is frozen, populated only when
/// <see cref="SessionInfo.Execution"/> is <see cref="ExecutionState.Stopped"/>. Empty
/// (<see cref="None"/>) while running or exited: a running debuggee has no synchronized frame to walk.
/// <see cref="CallStack"/> is top-frame-first "Type.Method @ file:line" strings
/// (<see cref="DebugSession.GetStackFrames"/>); <see cref="TopFrame"/> is its head.
/// <see cref="ExceptionTypeName"/> is set only at a <see cref="StopReason.Exception"/> stop.</summary>
public sealed record ExecutionPosition(
    StopInfo? Stop,
    string? ExceptionTypeName,
    IReadOnlyList<string> CallStack,
    IReadOnlyList<LocalValue> Locals,
    IReadOnlyList<ArgumentValue> Arguments)
{
    /// <summary>The top (innermost) frame, or null when the stack is empty / the session is not stopped.</summary>
    public string? TopFrame => CallStack.Count > 0 ? CallStack[0] : null;

    /// <summary>The position of a session that is not at a stop (running or exited) — no frame, no locals.</summary>
    public static ExecutionPosition None { get; } =
        new(null, null, Array.Empty<string>(), Array.Empty<LocalValue>(), Array.Empty<ArgumentValue>());
}

/// <summary>A breakpoint paired with its running hit count (<see cref="DebugSession.GetBreakpointHits"/>) —
/// the listing facet a view renders. <see cref="Info"/> is the concrete
/// <see cref="LineBreakpointInfo"/> / <see cref="FunctionBreakpointInfo"/>, so a view pattern-matches the kind.</summary>
public sealed record BreakpointStatus(BreakpointInfo Info, int HitCount);

/// <summary>An armed exception filter paired with its running hit count
/// (<see cref="DebugSession.GetExceptionFilterHits"/>).</summary>
public sealed record ExceptionFilterStatus(ExceptionFilterInfo Info, int HitCount);

/// <summary>One self-contained, point-in-time view of a <see cref="DebugSession"/>'s debug-state — the
/// "snapshot" face of the ADR-012 surface-agnostic model (its companion is the live delta stream,
/// <see cref="DebugStateDelta"/>). Promotes the previously piecemeal queryable state — session / lifecycle,
/// execution position, breakpoints, exception filters, and the three bounded stream tails — into ONE value a
/// consumer can render with NO prior context. That self-containedness is the load-bearing requirement of the
/// 2026-06-25 ownership directive: a human-launched view connects to an already-running session and must show
/// the full current picture from the snapshot alone, then stay current on the delta stream.
///
/// BCL-only and view-agnostic: the substrate produces it; the agent (via MCP), a TUI, an Avalonia GUI, or an
/// eventual IDE all consume the same contract. Assemble via <see cref="DebugSession.CaptureState"/>. The
/// (hypothesis, observation) braid (ADR-012 D4) is deliberately ABSENT here — it is Phase 3 substrate work and
/// joins this model as an additional facet then (one unknown per phase).</summary>
public sealed record DebugStateSnapshot(
    DateTimeOffset CapturedAt,
    SessionInfo Session,
    ExecutionPosition Position,
    IReadOnlyList<BreakpointStatus> Breakpoints,
    IReadOnlyList<ExceptionFilterStatus> ExceptionFilters,
    ConsoleDrainResult Console,
    DrainResult Logs,
    AnomalyDrainResult Anomalies);
