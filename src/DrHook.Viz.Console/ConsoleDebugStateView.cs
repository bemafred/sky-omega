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

    // Abbreviate a (possibly generic) full type name for display: reduce each qualified name component to its
    // simple name (namespace dropped, generic-arity backtick dropped) while preserving the generic structure
    // (<, >, commas, []). "System.Collections.Generic.List<System.Int32>" → "List<Int32>". The wire carries
    // the full name; the view abbreviates it, the same way it shows a source file by basename.
    private static string ShortType(string typeName)
    {
        var sb = new System.Text.StringBuilder(typeName.Length);
        int i = 0;
        while (i < typeName.Length)
        {
            char c = typeName[i];
            if (char.IsLetterOrDigit(c) || c is '_' or '.' or '`')
            {
                int start = i;
                while (i < typeName.Length && (char.IsLetterOrDigit(typeName[i]) || typeName[i] is '_' or '.' or '`')) i++;
                sb.Append(SimpleName(typeName.AsSpan(start, i - start)));
            }
            else { sb.Append(c); i++; }
        }
        return sb.ToString();
    }

    // One qualified component → its simple name: drop the namespace (after the last '.') and the arity backtick.
    private static string SimpleName(ReadOnlySpan<char> qualified)
    {
        int dot = qualified.LastIndexOf('.');
        ReadOnlySpan<char> simple = dot >= 0 ? qualified[(dot + 1)..] : qualified;
        int tick = simple.IndexOf('`');
        return (tick >= 0 ? simple[..tick] : simple).ToString();
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
