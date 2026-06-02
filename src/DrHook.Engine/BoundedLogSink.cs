namespace SkyOmega.DrHook.Engine;

/// <summary>A snapshot returned by <see cref="BoundedLogSink.Drain"/>: the records collected since
/// the previous drain (newest last), and how many records were SILENTLY DROPPED to honor the sink's
/// capacity over the same interval. The dropped count is the "checked nothing" marker from finding
/// 35 — never silent loss; the consumer can render it (e.g. "… (M lines dropped)") so volume
/// truncation stays visible.</summary>
public sealed record DrainResult(IReadOnlyList<LogRecord> Records, long Dropped);

/// <summary>An <see cref="IDebugEventSink"/> that collects <see cref="LogRecord"/>s into a bounded
/// ring buffer, drained on demand. The default destination for logpoint output (finding 35) —
/// backpressure by construction: when full, the OLDEST records are dropped and counted, so a hot
/// logpoint cannot exhaust the host's memory or blow an LLM consumer's context window. The
/// informational callback stream (<see cref="OnEvent"/>) is ignored; this sink is specifically for
/// the structured log channel.
///
/// Thread-safe: appends and drains can interleave from different threads (the engine's policy loop
/// runs on the caller's thread, the ICorDebug callback worker on its own). Caller-supplied capacity
/// is fixed for the lifetime of the sink.</summary>
public sealed class BoundedLogSink : IDebugEventSink
{
    private readonly int _capacity;
    private readonly LinkedList<LogRecord> _buffer = new();
    private readonly object _lock = new();
    private long _dropped;

    /// <summary>Construct a sink with the given fixed <paramref name="capacity"/> — the maximum
    /// number of <see cref="LogRecord"/>s retained between drains. Must be positive.</summary>
    public BoundedLogSink(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be positive");
        _capacity = capacity;
    }

    /// <summary>Maximum number of records retained between drains.</summary>
    public int Capacity => _capacity;

    /// <summary>Current number of records buffered (≤ <see cref="Capacity"/>). Diagnostic-only;
    /// reading this and acting on it is racy — use <see cref="Drain"/> for the atomic snapshot.</summary>
    public int Count
    {
        get { lock (_lock) return _buffer.Count; }
    }

    /// <summary>Informational ICorDebug callbacks are ignored; this sink is for the structured log
    /// channel (<see cref="OnLog"/>) only.</summary>
    public void OnEvent(string name) { /* not our channel */ }

    /// <summary>Append a record. When the buffer is full the OLDEST record is dropped and counted
    /// — never silent: the next <see cref="Drain"/> reports the increment via
    /// <see cref="DrainResult.Dropped"/>.</summary>
    public void OnLog(LogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            if (_buffer.Count == _capacity)
            {
                _buffer.RemoveFirst();
                _dropped++;
            }
            _buffer.AddLast(record);
        }
    }

    /// <summary>Atomically take all buffered records (newest last) plus the dropped count since the
    /// previous drain, and clear the buffer. The dropped counter resets on each drain — sum across
    /// drains if a cumulative total is needed.</summary>
    public DrainResult Drain()
    {
        lock (_lock)
        {
            LogRecord[] records = new LogRecord[_buffer.Count];
            int i = 0;
            foreach (LogRecord r in _buffer) records[i++] = r;
            long dropped = _dropped;
            _buffer.Clear();
            _dropped = 0;
            return new DrainResult(records, dropped);
        }
    }

    /// <summary>Drop all buffered records and the dropped counter — called when a NEW debug session
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
