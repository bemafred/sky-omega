namespace SkyOmega.DrHook.Engine;

/// <summary>An <see cref="IDebugEventSink"/> that fans every event to a fixed set of child sinks —
/// the surface-agnostic seam (ADR-011 D5). The substrate emits to ONE sink; the host composes the
/// per-channel buffers behind it (e.g. <see cref="BoundedAnomalySink"/> + <see cref="BoundedLogSink"/>
/// + <see cref="BoundedConsoleSink"/>), and future Mira views add their own consumers without the
/// substrate knowing anything about views. Children typically handle only their own channel and
/// no-op the rest (via the interface's default methods), so fan-out is cheap.
///
/// Children must — per the <see cref="IDebugEventSink"/> contract — be thread-safe and not throw;
/// this wrapper adds no locking of its own and assumes that contract (notably for
/// <see cref="OnAnomaly"/>, whose throw-kills-the-process rule a buffering child must honor).</summary>
public sealed class CompositeEventSink : IDebugEventSink
{
    private readonly IDebugEventSink[] _sinks;

    public CompositeEventSink(params IDebugEventSink[] sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = sinks;
    }

    public void OnEvent(string name) { foreach (IDebugEventSink s in _sinks) s.OnEvent(name); }
    public void OnLog(LogRecord record) { foreach (IDebugEventSink s in _sinks) s.OnLog(record); }
    public void OnAnomaly(EngineAnomaly anomaly) { foreach (IDebugEventSink s in _sinks) s.OnAnomaly(anomaly); }
    public void OnConsoleOutput(ConsoleOutputRecord record) { foreach (IDebugEventSink s in _sinks) s.OnConsoleOutput(record); }
    public void OnHypothesis(HypothesisRecord record) { foreach (IDebugEventSink s in _sinks) s.OnHypothesis(record); }
}
