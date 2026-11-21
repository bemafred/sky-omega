using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Worker;

namespace SkyOmega.DrHook.Worker;

/// <summary>The worker's stdout, the protocol's one channel to drhook-mcp (<see cref="WorkerProtocol"/>): one message
/// per line, each written whole. Messages come from several threads (the request thread, the engine's callback and
/// console-drain threads), so each line is written under a lock. Once drhook-mcp is gone a write fails; nothing is
/// sent after that, and the worker learns of the end from its stdin.</summary>
internal sealed class WorkerChannel(TextWriter stdout)
{
    private readonly object _lock = new();
    private bool _broken;

    public void Send(string line)
    {
        lock (_lock)
        {
            if (_broken) return;
            try { stdout.Write(line); stdout.Write('\n'); stdout.Flush(); }
            catch (IOException) { _broken = true; }
            catch (ObjectDisposedException) { _broken = true; }
        }
    }
}

/// <summary>Every record the session's sinks get, sent to drhook-mcp as it happens, for the buffers its drain tools read
/// (ADR-016 6.7). Hypotheses and the informational callbacks stay in the worker: the debug-state publisher shows them.
/// Never throws, as <see cref="IDebugEventSink"/> requires of its sinks.</summary>
internal sealed class ForwardedRecords(WorkerChannel channel) : IDebugEventSink
{
    public void OnEvent(string name) { }
    public void OnAnomaly(EngineAnomaly anomaly) => channel.Send(WorkerProtocol.Anomaly(anomaly));
    public void OnLog(LogRecord record) => channel.Send(WorkerProtocol.Log(record));
    public void OnConsoleOutput(ConsoleOutputRecord record) => channel.Send(WorkerProtocol.Console(record));
}
