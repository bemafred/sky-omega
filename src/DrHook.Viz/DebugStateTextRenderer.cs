using SkyOmega.DrHook.Wire;

namespace SkyOmega.DrHook.Viz;

/// <summary>The ONE projection of a debug-state <see cref="WireSnapshot"/> / <see cref="WireDelta"/> to readable
/// text — a compact block per snapshot, one line per delta. Both the live console view (<c>ConsoleDebugStateView</c>,
/// which tails the stream) and the on-demand image tool (<c>drhook_snapshot_image</c>, which rasterizes this text
/// to a PNG) render through THIS, so the two never fork: the image is a photograph of exactly what a text view
/// shows. Writes to an injected <see cref="TextWriter"/> (a <c>StringWriter</c> for the image path, the console for
/// the live view).</summary>
public static class DebugStateTextRenderer
{
    /// <summary>Render one self-contained snapshot as a block. <paramref name="seq"/> is the snapshot's sequence
    /// number for the header; <paramref name="source"/> reads the source-on-step window from disk (best-effort —
    /// the transport is a local UDS, so a view is co-located with the source tree).</summary>
    public static void RenderSnapshot(TextWriter o, WireSnapshot s, long seq, SourceWindowReader source)
    {
        o.WriteLine();
        o.WriteLine($"── snapshot #{seq} @ {s.CapturedAt} ──");
        o.WriteLine($"  session : pid={s.Session.Pid} {(s.Session.Owned ? "owned" : "borrowed")} runtime=.NET{s.Session.RuntimeMajor?.ToString() ?? "?"} exec={s.Session.Execution}");

        if (s.Position.Stop is { } stop)
        {
            o.WriteLine($"  stop    : {stop}{(s.Position.ExceptionType is { } et ? $" [{et}]" : "")}");
            if (s.Position.CallStack.Length > 0) o.WriteLine($"  frame   : {s.Position.CallStack[0].Display}");
            if (s.Position.CallStack.Length > 1)
                foreach (WireFrame f in s.Position.CallStack) o.WriteLine($"      {f.Display}");

            // source-on-step (ADR-012 Phase 4): the structured top-frame File+Line lets us read the actual source
            // from disk and show a window with the stopped line marked. Best-effort — a missing / oversized /
            // drifted file shows a short note instead of source, never an error.
            if (s.Position.CallStack.Length > 0)
            {
                WireFrame top = s.Position.CallStack[0];
                SourceWindow window = source.Read(top);
                if (window.HasSource)
                {
                    o.WriteLine($"  source  : {Path.GetFileName(window.FilePath)}");
                    foreach (SourceLine line in window.Lines)
                        o.WriteLine($"  {(line.IsCurrent ? "►" : " ")} {line.Number,4}  {line.Text}");
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
                    o.WriteLine($"  source  : {Path.GetFileName(top.File)} ({why})");
                }
            }

            if (s.Position.Locals.Length > 0)
                o.WriteLine($"  locals  : {string.Join(", ", s.Position.Locals.Select(RenderVar))}");
            if (s.Position.Arguments.Length > 0)
                o.WriteLine($"  args    : {string.Join(", ", s.Position.Arguments.Select(RenderVar))}");
        }
        else
        {
            o.WriteLine("  (running — no synchronized frame)");
        }

        foreach (WireBreakpoint b in s.Breakpoints)
            o.WriteLine($"  break   : id={b.Id} {b.Kind} {(b.Kind == "line" ? $"{b.File}:{b.Line}" : $"{b.Type}.{b.Method}")} hits={b.HitCount}");

        WireStreams st = s.Streams;
        o.WriteLine($"  streams : console={st.Console}(+{st.ConsoleDropped}) logs={st.Logs}(+{st.LogsDropped}) anomalies={st.Anomalies}(+{st.AnomaliesDropped})");
    }

    /// <summary>Render one live delta as a single line (lifecycle event / log / anomaly / console / hypothesis).</summary>
    public static void RenderDelta(TextWriter o, WireDelta d)
    {
        string detail = d.Kind switch
        {
            "event"   => d.Event ?? "",
            "log"     => $"{(d.LogFault == true ? "FAULT " : "")}{d.LogMessage}",
            "anomaly" => $"{d.AnomalyKind}: {d.AnomalyObserved}",
            "console" => $"[{d.ConsoleStream}] {d.ConsoleText}",
            "hypothesis" => $"▸ {(d.HypothesisLens ?? "").ToLowerInvariant()}: {d.HypothesisText}",
            _         => "",
        };
        o.WriteLine($"  Δ {d.Kind,-10} {detail}");
    }

    // A local/argument as "name=value": the rendered value when present (a primitive or string); else the runtime
    // type for a non-null object (so `this` reads as `{Worker}`, not a bare "?"); else an expandable marker "{…}"
    // for an object whose type didn't resolve; else "null" for a null / unavailable value.
    private static string RenderVar(WireVar v)
        => $"{v.Name}={v.Value ?? (v.TypeName is { } t ? $"{{{ShortType(t)}}}" : v.HasChildren ? "{…}" : "null")}";

    // Abbreviate a (possibly generic) full type name for display: reduce each qualified name component to its
    // simple name (namespace dropped, generic-arity backtick dropped) while preserving the generic structure
    // (<, >, commas, []). "System.Collections.Generic.List<System.Int32>" → "List<Int32>".
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
}
