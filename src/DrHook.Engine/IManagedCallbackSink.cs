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
}

/// <summary>The RECEIVE-direction contract from <see cref="Interop.ManagedCallbackHost"/> (the
/// hand-rolled native vtable) to the continue-loop. Invoked on mscordbi's event thread, so the
/// implementation must enqueue and return promptly — it must never block that thread.</summary>
internal interface IManagedCallbackSink
{
    void OnCallback(CallbackKind kind, string name, nint appDomain, nint thread);
}

/// <summary>A delivered callback queued for the worker. <paramref name="Thread"/> /
/// <paramref name="AppDomain"/> are raw <c>ICorDebugThread</c>/<c>ICorDebugAppDomain</c>
/// pointers, captured for stopping events (used by stepping in a later increment).</summary>
internal readonly record struct CallbackEvent(CallbackKind Kind, string Name, nint AppDomain, nint Thread);
