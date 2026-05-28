namespace SkyOmega.DrHook.Engine;

/// <summary>How a delivered ICorDebug callback is classified for the continue-loop.
/// <see cref="Informational"/> events are surfaced and auto-continued; the rest are STOPPING —
/// each synchronizes the debuggee and must surface a stop the caller controls, never an
/// auto-Continue.</summary>
internal enum CallbackKind
{
    Informational,
    BreakpointHit,
    StepComplete,
    Break,
    EvalComplete,
    EvalException,
    Exception,
    /// <summary>Synthetic — added by <see cref="CallbackPump.RequestPause"/>, not delivered by
    /// ICorDebug. The pump worker calls <c>ICorDebugController.Stop</c> to synchronize the process,
    /// then publishes a <see cref="StopReason.Pause"/> stop and parks like any other stop.</summary>
    PauseRequest,
}

/// <summary>The RECEIVE-direction contract from <see cref="Interop.ManagedCallbackHost"/> (the
/// hand-rolled native vtable) to the continue-loop. Invoked on mscordbi's event thread, so the
/// implementation must enqueue and return promptly — it must never block that thread.</summary>
internal interface IManagedCallbackSink
{
    void OnCallback(CallbackKind kind, string name, nint appDomain, nint thread, int detail, nint breakpoint = 0);
}

/// <summary>A delivered callback queued for the worker. <paramref name="Thread"/> /
/// <paramref name="AppDomain"/> are raw <c>ICorDebugThread</c>/<c>ICorDebugAppDomain</c>
/// pointers, captured for stopping events (used by stepping in a later increment).
/// <paramref name="Detail"/> carries a callback-specific scalar — the
/// <c>CorDebugExceptionCallbackType</c> for <see cref="CallbackKind.Exception"/>, else 0.
/// <paramref name="Breakpoint"/> is the raw <c>ICorDebugBreakpoint</c> pointer for
/// <see cref="CallbackKind.BreakpointHit"/> (the specific breakpoint that fired); <c>0</c>
/// for all other callback kinds. Captured so the substrate can resolve hits back to their
/// stored <see cref="BreakpointPolicy"/> for evaluation (finding 34 + ADR-010 Increment 2).</summary>
internal readonly record struct CallbackEvent(CallbackKind Kind, string Name, nint AppDomain, nint Thread, int Detail, nint Breakpoint = 0);
