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
    /// <summary>The debuggee exited; no further stops will occur.</summary>
    ProcessExited,
}

/// <summary>A synchronized stop surfaced to the caller. While stopped, the debuggee is frozen;
/// call <see cref="DebugSession.Resume"/> to continue it.</summary>
public sealed record StopInfo(StopReason Reason);
