namespace SkyOmega.DrHook.Engine;

/// <summary>Which standard stream a <see cref="ConsoleOutputRecord"/> came from.</summary>
public enum ConsoleStream { Stdout, Stderr }

/// <summary>One chunk of a LAUNCHED debuggee's console output, captured from the DrHook-owned pipe
/// the child's stdout/stderr were redirected to (ADR-011 D2). A surface-agnostic substrate event
/// (ADR-011 D3/D5): the MCP layer buffers it (drained by drhook_drain_console); Mira's human
/// surfaces consume the same record later. <see cref="Text"/> is a UTF-8 decode of one pipe read —
/// chunk boundaries are arbitrary, not line-aligned.</summary>
public sealed record ConsoleOutputRecord(DateTimeOffset CapturedAt, ConsoleStream Stream, string Text);

/// <summary>A snapshot returned by <see cref="BoundedConsoleSink.Drain"/>: the console chunks
/// collected since the previous drain (newest last) + how many were SILENTLY DROPPED to honor
/// capacity. The dropped count is the honesty marker (parallel to <see cref="DrainResult"/> /
/// <see cref="AnomalyDrainResult"/>) — a chatty debuggee cannot blow the host's memory or an LLM
/// consumer's context window, and the truncation stays visible.</summary>
public sealed record ConsoleDrainResult(IReadOnlyList<ConsoleOutputRecord> Records, long Dropped);

/// <summary>An <see cref="IDebugEventSink"/> that collects a launched debuggee's
/// <see cref="ConsoleOutputRecord"/> chunks into a bounded ring buffer, drained on demand. Parallel
/// to <see cref="BoundedAnomalySink"/> / <see cref="BoundedLogSink"/> — same capacity / dropped-count
/// / drain shape, for the console channel (<see cref="IDebugEventSink.OnConsoleOutput"/>). When full,
/// the OLDEST chunks are dropped and counted. The other channels (OnEvent / OnLog / OnAnomaly) are
/// ignored. Thread-safe: appends come from DrHook's console-drain threads, drains from the MCP
/// request thread.</summary>
public sealed class BoundedConsoleSink : IDebugEventSink
{
    private readonly int _capacity;
    private readonly LinkedList<ConsoleOutputRecord> _buffer = new();
    private readonly object _lock = new();
    private long _dropped;

    /// <summary>Construct a sink with the given fixed <paramref name="capacity"/> — the maximum
    /// number of console chunks retained between drains. Must be positive.</summary>
    public BoundedConsoleSink(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be positive");
        _capacity = capacity;
    }

    /// <summary>Maximum number of console chunks retained between drains.</summary>
    public int Capacity => _capacity;

    /// <summary>Current number of chunks buffered (≤ <see cref="Capacity"/>). Diagnostic-only;
    /// reading and acting on it is racy — use <see cref="Drain"/> for the atomic snapshot.</summary>
    public int Count { get { lock (_lock) return _buffer.Count; } }

    /// <summary>Informational ICorDebug callbacks are ignored; this sink is for the console channel
    /// (<see cref="OnConsoleOutput"/>) only. (OnLog / OnAnomaly inherit the interface's no-op default.)</summary>
    public void OnEvent(string name) { /* not our channel */ }

    /// <summary>Append a console chunk. When the buffer is full the OLDEST chunk is dropped and
    /// counted — never silent: the next <see cref="Drain"/> reports it via
    /// <see cref="ConsoleDrainResult.Dropped"/>.</summary>
    public void OnConsoleOutput(ConsoleOutputRecord record)
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

    /// <summary>Atomically take all buffered chunks (newest last) + the dropped count since the
    /// previous drain, and clear the buffer. The dropped counter resets on each drain.</summary>
    public ConsoleDrainResult Drain()
    {
        lock (_lock)
        {
            ConsoleOutputRecord[] records = new ConsoleOutputRecord[_buffer.Count];
            int i = 0;
            foreach (ConsoleOutputRecord r in _buffer) records[i++] = r;
            long dropped = _dropped;
            _buffer.Clear();
            _dropped = 0;
            return new ConsoleDrainResult(records, dropped);
        }
    }
}
