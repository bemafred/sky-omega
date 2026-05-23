namespace SkyOmega.DrHook.Engine;

/// <summary>Why the debuggee stopped. On a stop the engine suppresses the auto-Continue and
/// holds the process synchronized (frozen for inspection) until the caller resumes or steps.</summary>
public enum StopReason
{
    /// <summary>A breakpoint was hit (<c>ICorDebugManagedCallback.Breakpoint</c>).</summary>
    Breakpoint,
    /// <summary>A step operation completed (<c>ICorDebugManagedCallback.StepComplete</c>).</summary>
    Step,
    /// <summary>The debuggee executed <c>Debugger.Break()</c> (<c>ICorDebugManagedCallback.Break</c>).</summary>
    Break,
    /// <summary>A function evaluation completed (<c>ICorDebugManagedCallback.EvalComplete</c>) — the
    /// result is available via the eval that initiated it.</summary>
    EvalComplete,
    /// <summary>A function evaluation threw (<c>ICorDebugManagedCallback.EvalException</c>).</summary>
    EvalException,
    /// <summary>A managed exception was raised (<c>ICorDebugManagedCallback2.Exception</c>). The
    /// phase (first-chance / unhandled / …) is in <see cref="StopInfo.ExceptionKind"/>.</summary>
    Exception,
    /// <summary>A <see cref="BreakpointPolicy"/>'s condition delegate threw — the engine could not
    /// evaluate the condition. Surfaced as a distinct stop (finding 35): a broken condition must
    /// never silently behave like a never-true condition. The diagnostic is emitted to the sink as a
    /// <see cref="LogRecord"/> with <see cref="LogRecord.IsFault"/> = <c>true</c>.</summary>
    ConditionError,
    /// <summary>The caller invoked <see cref="DebugSession.Pause"/> — an async-break that
    /// synchronized the running debuggee. There is no specific thread associated with the stop.</summary>
    Pause,
    /// <summary>The debuggee exited; no further stops will occur.</summary>
    ProcessExited,
}

/// <summary>The phase at which an exception stop was raised — the
/// <c>CorDebugExceptionCallbackType</c> from <c>ICorDebugManagedCallback2.Exception</c> (values
/// per cordebug.idl). <see cref="None"/> for non-exception stops.</summary>
public enum ExceptionStopKind
{
    /// <summary>Not an exception stop.</summary>
    None = 0,
    /// <summary><c>DEBUG_EXCEPTION_FIRST_CHANCE</c> — fired when the exception is thrown.</summary>
    FirstChance = 1,
    /// <summary><c>DEBUG_EXCEPTION_USER_FIRST_CHANCE</c> — fired when the search reaches first user code.</summary>
    UserFirstChance = 2,
    /// <summary><c>DEBUG_EXCEPTION_CATCH_HANDLER_FOUND</c> — fired if & when the search finds a handler.</summary>
    CatchHandlerFound = 3,
    /// <summary><c>DEBUG_EXCEPTION_UNHANDLED</c> — fired if the search doesn't find a handler.</summary>
    Unhandled = 4,
}

/// <summary>A synchronized stop surfaced to the caller. While stopped, the debuggee is frozen;
/// call <see cref="DebugSession.Resume"/> to continue it. <see cref="ExceptionKind"/> is meaningful
/// only when <see cref="Reason"/> is <see cref="StopReason.Exception"/>.</summary>
public sealed record StopInfo(StopReason Reason, ExceptionStopKind ExceptionKind = ExceptionStopKind.None);
