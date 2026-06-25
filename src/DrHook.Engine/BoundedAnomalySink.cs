namespace SkyOmega.DrHook.Engine;

/// <summary>A snapshot returned by <see cref="BoundedAnomalySink.Drain"/>: the anomalies
/// collected since the previous drain (newest last), and how many were SILENTLY DROPPED to
/// honor the sink's capacity over the same interval. The dropped count is the substrate's
/// honesty marker — when a hot anomaly site exceeds capacity, the consumer sees the truncation
/// rather than getting a misleadingly small picture.</summary>
public sealed record AnomalyDrainResult(IReadOnlyList<EngineAnomaly> Anomalies, long Dropped);

/// <summary>An <see cref="IDebugEventSink"/> that collects <see cref="EngineAnomaly"/> records
/// into a bounded ring buffer, drained on demand. Parallel to <see cref="BoundedLogSink"/> —
/// same capacity / dropped-count / drain shape, but for the anomaly channel (separate concern:
/// anomalies are substrate-correctness evidence, logs are observation output, and consumers
/// may drain them at different cadences).
///
/// When full, the OLDEST records are dropped and counted, so a pathological substrate site
/// emitting anomalies at high rate cannot exhaust the host's memory or blow an LLM consumer's
/// context window. The informational callback stream (<see cref="OnEvent"/>) and logpoint
/// stream (<see cref="OnLog"/>) are ignored; this sink is specifically for the anomaly channel.
///
/// Thread-safe: appends and drains can interleave from different threads (anomaly emission can
/// originate on the pump worker, the MCP request thread, or — when the static-fallback
/// substrate lands — mscordbi's event thread). Caller-supplied capacity is fixed for the
/// lifetime of the sink.</summary>
public sealed class BoundedAnomalySink : IDebugEventSink
{
    private readonly int _capacity;
    private readonly LinkedList<EngineAnomaly> _buffer = new();
    private readonly object _lock = new();
    private long _dropped;

    /// <summary>Construct a sink with the given fixed <paramref name="capacity"/> — the maximum
    /// number of <see cref="EngineAnomaly"/> records retained between drains. Must be positive.</summary>
    public BoundedAnomalySink(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be positive");
        _capacity = capacity;
    }

    /// <summary>Maximum number of anomalies retained between drains.</summary>
    public int Capacity => _capacity;

    /// <summary>Current number of anomalies buffered (≤ <see cref="Capacity"/>). Diagnostic-only;
    /// reading this and acting on it is racy — use <see cref="Drain"/> for the atomic snapshot.</summary>
    public int Count
    {
        get { lock (_lock) return _buffer.Count; }
    }

    /// <summary>Informational ICorDebug callbacks are ignored; this sink is for the anomaly
    /// channel (<see cref="OnAnomaly"/>) only.</summary>
    public void OnEvent(string name) { /* not our channel */ }

    /// <summary>Logpoint records are ignored; this sink is for the anomaly channel only.
    /// Use a separate <see cref="BoundedLogSink"/> for logs, or compose both via a custom
    /// <see cref="IDebugEventSink"/> implementation.</summary>
    public void OnLog(LogRecord record) { /* not our channel */ }

    /// <summary>Append an anomaly. When the buffer is full the OLDEST record is dropped and
    /// counted — never silent: the next <see cref="Drain"/> reports the increment via
    /// <see cref="AnomalyDrainResult.Dropped"/>.</summary>
    public void OnAnomaly(EngineAnomaly anomaly)
    {
        ArgumentNullException.ThrowIfNull(anomaly);
        lock (_lock)
        {
            if (_buffer.Count == _capacity)
            {
                _buffer.RemoveFirst();
                _dropped++;
            }
            _buffer.AddLast(anomaly);
        }
    }

    /// <summary>Atomically take all buffered anomalies (newest last) plus the dropped count
    /// since the previous drain, and clear the buffer. The dropped counter resets on each
    /// drain — sum across drains if a cumulative total is needed.</summary>
    public AnomalyDrainResult Drain()
    {
        lock (_lock)
        {
            EngineAnomaly[] anomalies = new EngineAnomaly[_buffer.Count];
            int i = 0;
            foreach (EngineAnomaly a in _buffer) anomalies[i++] = a;
            long dropped = _dropped;
            _buffer.Clear();
            _dropped = 0;
            return new AnomalyDrainResult(anomalies, dropped);
        }
    }

    /// <summary>Non-destructively read the buffered anomalies (newest last) + the dropped count, WITHOUT
    /// clearing — for an inspection snapshot (ADR-012 Phase 1 / <see cref="DebugSession.CaptureState"/>) that
    /// must not consume records the <c>drhook_drain_anomalies</c> tool still owns. Same shape as
    /// <see cref="Drain"/>; the buffer and dropped counter are left intact.</summary>
    public AnomalyDrainResult Peek()
    {
        lock (_lock)
        {
            EngineAnomaly[] anomalies = new EngineAnomaly[_buffer.Count];
            int i = 0;
            foreach (EngineAnomaly a in _buffer) anomalies[i++] = a;
            return new AnomalyDrainResult(anomalies, _dropped);
        }
    }

    /// <summary>Drop all buffered anomalies and the dropped counter — called when a NEW debug session
    /// starts so its drains reflect only that session. (The buffer intentionally survives a session's
    /// END for a final drain; this resets it at the next session's START.)</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _buffer.Clear();
            _dropped = 0;
        }
    }
}
