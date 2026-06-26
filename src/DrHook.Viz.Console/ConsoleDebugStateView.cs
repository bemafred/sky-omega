using SkyOmega.DrHook.Viz;
using SkyOmega.DrHook.Wire;

namespace SkyOmega.DrHook.Viz.ConsoleView;

/// <summary>The console visualizer — renders the debug-state stream as readable text (a "tail" of the
/// session): a compact block per snapshot, one line per delta. The simplest <see cref="IDebugStateView"/>;
/// the TUI and Avalonia views are richer renderings of the SAME client + model. Writes to an injected
/// <see cref="TextWriter"/> (testable; the shim passes <c>Console.Out</c>) — mirrors the Mercury tool pattern.</summary>
public sealed class ConsoleDebugStateView : IDebugStateView
{
    private readonly TextWriter _out;

    public ConsoleDebugStateView(TextWriter output) => _out = output;

    public void OnConnected(string endpoint)
        => _out.WriteLine($"● connected to {endpoint} — waiting for debug-state…");

    public void OnSnapshot(WireSnapshot s, DebugStateClientModel model)
    {
        _out.WriteLine();
        _out.WriteLine($"── snapshot #{model.LastSeq} @ {s.CapturedAt} ──");
        _out.WriteLine($"  session : pid={s.Session.Pid} {(s.Session.Owned ? "owned" : "borrowed")} runtime=.NET{s.Session.RuntimeMajor?.ToString() ?? "?"} exec={s.Session.Execution}");

        if (s.Position.Stop is { } stop)
        {
            _out.WriteLine($"  stop    : {stop}{(s.Position.ExceptionType is { } et ? $" [{et}]" : "")}");
            if (s.Position.TopFrame is { } tf) _out.WriteLine($"  frame   : {tf}");
            if (s.Position.CallStack.Length > 1)
                foreach (string f in s.Position.CallStack) _out.WriteLine($"      {f}");
            if (s.Position.Locals.Length > 0)
                _out.WriteLine($"  locals  : {string.Join(", ", s.Position.Locals.Select(v => $"{v.Name}={v.Value ?? "?"}"))}");
            if (s.Position.Arguments.Length > 0)
                _out.WriteLine($"  args    : {string.Join(", ", s.Position.Arguments.Select(v => $"{v.Name}={v.Value ?? "?"}"))}");
        }
        else
        {
            _out.WriteLine("  (running — no synchronized frame)");
        }

        foreach (WireBreakpoint b in s.Breakpoints)
            _out.WriteLine($"  break   : id={b.Id} {b.Kind} {(b.Kind == "line" ? $"{b.File}:{b.Line}" : $"{b.Type}.{b.Method}")} hits={b.HitCount}");

        WireStreams st = s.Streams;
        _out.WriteLine($"  streams : console={st.Console}(+{st.ConsoleDropped}) logs={st.Logs}(+{st.LogsDropped}) anomalies={st.Anomalies}(+{st.AnomaliesDropped})");
    }

    public void OnDelta(WireDelta d, DebugStateClientModel model)
    {
        string detail = d.Kind switch
        {
            "event"   => d.Event ?? "",
            "log"     => $"{(d.LogFault == true ? "FAULT " : "")}{d.LogMessage}",
            "anomaly" => $"{d.AnomalyKind}: {d.AnomalyObserved}",
            "console" => $"[{d.ConsoleStream}] {d.ConsoleText}",
            _         => "",
        };
        _out.WriteLine($"  Δ {d.Kind,-8} {detail}");
    }

    public void OnDisconnected(string? reason)
        => _out.WriteLine($"● disconnected: {reason}");
}
