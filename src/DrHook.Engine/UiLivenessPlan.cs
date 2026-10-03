namespace SkyOmega.DrHook.Engine;

/// <summary>The framework-specific half of a UI-liveness probe (<see cref="DebugSession.TryEvalPostLivenessJob"/>):
/// where the debuggee's UI dispatcher lives and how a job is queued to it. The job itself is fixed —
/// <c>System.IO.File.Delete(sentinel)</c> bound into an <c>Action</c> — because its execution is observable from
/// outside the process with no debugger and no live observer: the <see cref="DispatcherDrainSentinel"/> file
/// disappears. A UI thread that drains its dispatcher queue runs the job; a hung one — stuck in managed OR native
/// code — never does. Defaults are Avalonia 11; WPF's
/// <c>System.Windows.Threading.Dispatcher.InvokeAsync(Action)</c> has the same shape (getter
/// <c>get_CurrentDispatcher</c>, module <c>WindowsBase</c>).</summary>
/// <param name="DispatcherModule">Module-name substring holding the dispatcher type (Avalonia: <c>Avalonia.Base</c>).</param>
/// <param name="DispatcherType">Namespace-qualified dispatcher type (Avalonia: <c>Avalonia.Threading.Dispatcher</c>).</param>
/// <param name="DispatcherGetter">Static, no-arg getter for the UI dispatcher (Avalonia: <c>get_UIThread</c>).</param>
/// <param name="PostMethod">Instance method queueing an <c>Action</c> — its single-parameter overload is used
/// (Avalonia/WPF: <c>InvokeAsync</c>).</param>
public readonly record struct UiLivenessPlan(
    string DispatcherModule,
    string DispatcherType,
    string DispatcherGetter,
    string PostMethod)
{
    /// <summary>Avalonia 11's UI dispatcher: <c>Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(Action)</c>.</summary>
    public static UiLivenessPlan Avalonia { get; } =
        new("Avalonia.Base", "Avalonia.Threading.Dispatcher", "get_UIThread", "InvokeAsync");
}
