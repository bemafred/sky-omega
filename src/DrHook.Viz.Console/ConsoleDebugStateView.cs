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
    private readonly SourceWindowReader _source;

    public ConsoleDebugStateView(TextWriter output, SourceWindowReader? source = null)
    {
        _out = output;
        _source = source ?? new SourceWindowReader();
    }

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
            if (s.Position.CallStack.Length > 0) _out.WriteLine($"  frame   : {s.Position.CallStack[0].Display}");
            if (s.Position.CallStack.Length > 1)
                foreach (WireFrame f in s.Position.CallStack) _out.WriteLine($"      {f.Display}");

            // source-on-step (ADR-012 Phase 4): the structured top-frame File+Line lets us read the actual
            // source from disk (the transport is a local UDS, so a view is co-located with the source tree) and
            // show a window with the stopped line marked. Best-effort — a missing / oversized / drifted file
            // shows a short note instead of source, never an error.
            if (s.Position.CallStack.Length > 0)
            {
                WireFrame top = s.Position.CallStack[0];
                SourceWindow window = _source.Read(top);
                if (window.HasSource)
                {
                    _out.WriteLine($"  source  : {Path.GetFileName(window.FilePath)}");
                    foreach (SourceLine line in window.Lines)
                        _out.WriteLine($"  {(line.IsCurrent ? "►" : " ")} {line.Number,4}  {line.Text}");
                }
                else if (top.File is not null)
                {
                    string why = window.Status switch
                    {
                        SourceWindowStatus.FileNotFound => "not found on disk",
                        SourceWindowStatus.FileTooLarge => "too large to show",
                        SourceWindowStatus.LineOutOfRange => "source out of date",
                        _ => "unavailable",
                    };
                    _out.WriteLine($"  source  : {Path.GetFileName(top.File)} ({why})");
                }
            }

            if (s.Position.Locals.Length > 0)
                _out.WriteLine($"  locals  : {string.Join(", ", s.Position.Locals.Select(RenderVar))}");
            if (s.Position.Arguments.Length > 0)
                _out.WriteLine($"  args    : {string.Join(", ", s.Position.Arguments.Select(RenderVar))}");
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

    // A local/argument as "name=value": the rendered value when present (a primitive or string); else the
    // runtime type for a non-null object (so `this` reads as `{Worker}`, not a bare "?"); else an expandable
    // marker "{…}" for an object whose type didn't resolve; else "null" for a null / unavailable value.
    private static string RenderVar(WireVar v)
        => $"{v.Name}={v.Value ?? (v.TypeName is { } t ? $"{{{ShortType(t)}}}" : v.HasChildren ? "{…}" : "null")}";

    // The readable tail of a metadata type name for display: drop the namespace (last '.' segment) and the
    // generic-arity backtick (System.Collections.Generic.List`1 → List). The wire carries the full name; the
    // view abbreviates it, the same way it shows a source file by basename.
    private static string ShortType(string typeName)
    {
        int dot = typeName.LastIndexOf('.');
        string simple = dot >= 0 ? typeName[(dot + 1)..] : typeName;
        int tick = simple.IndexOf('`');
        return tick >= 0 ? simple[..tick] : simple;
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
