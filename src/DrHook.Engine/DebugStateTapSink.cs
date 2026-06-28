namespace SkyOmega.DrHook.Engine;

/// <summary>A non-destructive read returned by <see cref="DebugStateTapSink.Peek"/> /
/// <see cref="DebugStateTapSink.Drain"/>: the unified deltas buffered (newest last) + how many were
/// SILENTLY DROPPED to honor capacity (the honesty marker, parallel to the per-channel sinks' drain
/// results — a hot event site cannot blow the host's memory or an LLM consumer's context, and the
/// truncation stays visible).</summary>
public sealed record DebugStateDeltaResult(IReadOnlyList<DebugStateDelta> Deltas, long Dropped);

/// <summary>An <see cref="IDebugEventSink"/> that taps ALL four channels into one ordered, bounded stream of
/// <see cref="DebugStateDelta"/> — the live "delta stream" face of the ADR-012 surface-agnostic model, and the
/// Phase-1 proof that a new consumer is "just another <see cref="IDebugEventSink"/>": added to the existing
/// <see cref="CompositeEventSink"/>, it observes the unified event stream WITHOUT disturbing the per-channel
/// <see cref="BoundedConsoleSink"/> / <see cref="BoundedLogSink"/> / <see cref="BoundedAnomalySink"/>. Unlike
/// those, it also captures <see cref="OnEvent"/> (the ICorDebug lifecycle / position signal), which no
/// per-channel sink buffers — so the tap is the first place the WHOLE stream is observable in order.
///
/// Bounded like the per-channel sinks: when full the OLDEST delta is dropped and counted. Thread-safe and
/// non-throwing per the <see cref="IDebugEventSink"/> contract — appends are O(1) under a lock, and null
/// payloads are IGNORED rather than thrown. The no-throw rule is strict on the anomaly channel: a throw from
/// <see cref="OnAnomaly"/> runs inside the pump worker's last-resort catch and would kill the worker (hence
/// the process), so this sink never throws there (WE-OA-1 / finding 60).</summary>
public sealed class DebugStateTapSink : IDebugEventSink
{
    private readonly int _capacity;
    private readonly LinkedList<DebugStateDelta> _buffer = new();
    private readonly object _lock = new();
    private long _dropped;

    /// <summary>Construct a tap with the given fixed <paramref name="capacity"/> — the maximum number of
    /// deltas retained between drains. Must be positive.</summary>
    public DebugStateTapSink(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be positive");
        _capacity = capacity;
    }

    /// <summary>Maximum number of deltas retained between drains.</summary>
    public int Capacity => _capacity;

    /// <summary>Current number of deltas buffered (≤ <see cref="Capacity"/>). Diagnostic-only; reading and
    /// acting on it is racy — use <see cref="Peek"/> / <see cref="Drain"/> for the atomic read.</summary>
    public int Count { get { lock (_lock) return _buffer.Count; } }

    /// <summary>Tap a lifecycle callback by name. Null is ignored (the contract forbids throwing on the
    /// shared event thread).</summary>
    public void OnEvent(string name)
    {
        if (name is null) return;
        Append(DebugStateDelta.ForEvent(DateTimeOffset.UtcNow, name));
    }

    /// <summary>Tap a log record. Null is ignored.</summary>
    public void OnLog(LogRecord record) { if (record is not null) Append(DebugStateDelta.ForLog(record)); }

    /// <summary>Tap an anomaly. Null is ignored — and this method must NEVER throw (WE-OA-1 / finding 60).</summary>
    public void OnAnomaly(EngineAnomaly anomaly) { if (anomaly is not null) Append(DebugStateDelta.ForAnomaly(anomaly)); }

    /// <summary>Tap a console chunk. Null is ignored.</summary>
    public void OnConsoleOutput(ConsoleOutputRecord record) { if (record is not null) Append(DebugStateDelta.ForConsole(record)); }

    /// <summary>Tap a hypothesis (the braid's prediction half, ADR-012 Phase 3). Null is ignored.</summary>
    public void OnHypothesis(HypothesisRecord record) { if (record is not null) Append(DebugStateDelta.ForHypothesis(record)); }

    private void Append(DebugStateDelta delta)
    {
        lock (_lock)
        {
            if (_buffer.Count == _capacity) { _buffer.RemoveFirst(); _dropped++; }
            _buffer.AddLast(delta);
        }
    }

    /// <summary>Non-destructively read the buffered deltas (newest last) + the dropped count — for an
    /// observer / transport that must not consume what other peers still read. The buffer and dropped
    /// counter are unchanged (parallel to <see cref="BoundedConsoleSink.Peek"/>).</summary>
    public DebugStateDeltaResult Peek()
    {
        lock (_lock)
        {
            DebugStateDelta[] deltas = new DebugStateDelta[_buffer.Count];
            int i = 0;
            foreach (DebugStateDelta d in _buffer) deltas[i++] = d;
            return new DebugStateDeltaResult(deltas, _dropped);
        }
    }

    /// <summary>Atomically take all buffered deltas (newest last) + the dropped count, and clear the buffer.
    /// The dropped counter resets on each drain.</summary>
    public DebugStateDeltaResult Drain()
    {
        lock (_lock)
        {
            DebugStateDelta[] deltas = new DebugStateDelta[_buffer.Count];
            int i = 0;
            foreach (DebugStateDelta d in _buffer) deltas[i++] = d;
            long dropped = _dropped;
            _buffer.Clear();
            _dropped = 0;
            return new DebugStateDeltaResult(deltas, dropped);
        }
    }

    /// <summary>Drop all buffered deltas and the dropped counter — called when a NEW debug session starts so
    /// the stream reflects only that session (parallel to the per-channel sinks' <c>Reset</c>).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _buffer.Clear();
            _dropped = 0;
        }
    }
}
