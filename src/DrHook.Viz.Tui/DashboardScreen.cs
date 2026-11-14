using System.Text;
using SkyOmega.DrHook.Viz;
using SkyOmega.DrHook.Wire;

namespace SkyOmega.DrHook.Viz.Tui;

/// <summary>The dashboard's pure compositor: one debug-state picture (the current <see cref="WireSnapshot"/> plus the
/// retained delta tail) → exactly <c>height</c> rows of exactly <c>width</c> characters, ready for a terminal. No
/// terminal I/O here, so every layout rule is unit-testable.
///
/// Layout (ADR-012 Phase 4 — approved 2026-10-03):
/// <code>
/// ┌ title: pid · mode · runtime · execution · stop · #seq · status ┐
/// │ STACK              │ SOURCE  file                              │   section A (split)
/// ├────────────────────┼───────────────────────────────────────────┤
/// │ LOCALS / ARGS      │ BREAKPOINTS                               │   section B (split, same column)
/// ├────────────────────┴───────────────────────────────────────────┤
/// │ BRAID / EVENTS  (hypotheses, lifecycle events, logs)           │   section C (full width)
/// ├────────────────────────────────┬───────────────────────────────┤
/// │ CONSOLE  (target stdout/stderr) │ ANOMALIES  n                  │   section D (split in half)
/// └────────────────────────────────┴───────────────────────────────┘
/// </code>
/// Item formatting (values, deltas, breakpoints) comes from the ONE shared <see cref="DebugStateTextRenderer"/>,
/// never re-implemented here, so the dashboard, the tail view and the image tool read the same.
///
/// Two panes show less than the stream holds, so that what a human looks for stays on screen. The braid pane puts
/// the lifecycle events between two hypotheses on one line, each name once with its count: a target's start alone
/// is some twenty module-load events, and one line each would push every hypothesis out of the pane. The console
/// pane shows the target's output as lines, not as the chunks the pipe happened to deliver. The tail view
/// (<c>drhook-viz-console</c>) prints every delta in order.
///
/// Text from the debuggee (console output, source lines, values) is SANITIZED: control characters — ESC above all —
/// become '·' and tabs expand to spaces, so a target printing ANSI sequences cannot take over the dashboard's
/// screen. Width is counted in UTF-16 code units (one cell each); East-Asian wide glyphs may misalign a row — a
/// stated limit, never a corruption of the frame.</summary>
public static class DashboardScreen
{
    /// <summary>Smallest terminal the layout fits; below it the screen shows a single "too small" message.</summary>
    public const int MinWidth = 60, MinHeight = 16;

    /// <summary>Source lines either side of the current one the reader is asked for; the pane shows the slice that
    /// fits its height, centred on the current line.</summary>
    public const int SourceContextLines = 40;

    /// <param name="connected">False when the view has no connection to a session: the picture is then the last one
    /// of a session that has ended, and the title says so instead of giving its state as the current one.</param>
    public static string[] Compose(WireSnapshot? snapshot, IReadOnlyList<WireDelta> deltas, long seq, string status,
                                   int width, int height, SourceWindowReader source, bool connected = true)
    {
        if (width < MinWidth || height < MinHeight)
            return TooSmall(width, height);

        // Section heights (content rows, each including the pane's own title row): 5 border/separator rows total.
        int content = height - 5;
        int hA = Math.Max(3, content * 35 / 100);
        int hB = Math.Max(3, content * 20 / 100);
        int hC = Math.Max(3, content * 20 / 100);
        int hD = content - hA - hB - hC;
        if (hD < 3) { hA -= 3 - hD; hD = 3; }

        int inner = width - 2;              // between the outer borders
        int splitAB = inner * 2 / 5;        // left column width of sections A and B
        int splitD = inner / 2;             // left column width of section D (console | anomalies — equal: both carry long lines)

        var rows = new List<string>(height) { TitleRow(snapshot, seq, status, width, connected) };

        List<string> stack = StackLines(snapshot), sourceLines = SourceLines(snapshot, source, hA - 1, out string sourceTitle);
        rows.AddRange(SplitSection("STACK", stack, sourceTitle, sourceLines, hA, splitAB, inner));
        rows.Add(Separator('├', splitAB, '┼', '┤', inner));

        rows.AddRange(SplitSection("LOCALS / ARGS", VariableLines(snapshot, splitAB), "BREAKPOINTS", BreakpointLines(snapshot), hB, splitAB, inner));
        rows.Add(Separator('├', splitAB, '┴', '┤', inner));

        rows.AddRange(FullSection("BRAID / EVENTS", Newest(BraidLines(deltas, inner), hC - 1), hC, inner));
        rows.Add(Separator('├', splitD, '┬', '┤', inner));

        List<string> console = Newest(ConsoleLines(deltas), hD - 1);
        WireDelta[] anomalies = deltas.Where(d => d.Kind == "anomaly").ToArray();
        rows.AddRange(SplitSection("CONSOLE  (target stdout/stderr)", console,
            $"ANOMALIES  {anomalies.Length}", Newest(anomalies.Select(DebugStateTextRenderer.FormatDeltaDetail), hD - 1),
            hD, splitD, inner));
        rows.Add(Separator('└', splitD, '┴', '┘', inner));

        return rows.ToArray();
    }

    // ── Pane contents ──────────────────────────────────────────────────────────────────────────────────────────

    private static string TitleRow(WireSnapshot? s, long seq, string status, int width, bool connected)
    {
        string title = s is null
            ? $" DrHook ─ {status} "
            : $" DrHook ─ pid {s.Session.Pid} {(s.Session.Owned ? "owned" : "borrowed")} .NET{s.Session.RuntimeMajor?.ToString() ?? "?"} ─ " +
              $"{StateText(s, connected)} ─ #{seq} ─ {status} ";
        string clipped = Fit(Sanitize(title), width - 2);
        return "┌" + clipped.TrimEnd().PadRight(width - 2, '─') + "┐";
    }

    // The target's state for the title. An end (exited, debugger lost) stays true after the session; "Running" and
    // "Stopped: …" do not, so without a connection they are given as the last state the view was told.
    private static string StateText(WireSnapshot s, bool connected)
    {
        if (DebugStateTextRenderer.FormatEnd(s.Session) is { } end) return end;
        string state = $"{s.Session.Execution}{(s.Position.Stop is { } stop ? $": {stop}" : "")}{(s.Position.ExceptionType is { } et ? $" [{et}]" : "")}";
        return connected ? state : $"last: {state}";
    }

    private static List<string> StackLines(WireSnapshot? s)
    {
        if (s is null) return ["waiting for debug-state…"];
        if (s.Position.Stop is null) return [DebugStateTextRenderer.FormatEnd(s.Session) is { } end ? $"{end} — no frame" : "running — no frame"];
        return s.Position.CallStack.Select((f, i) => $"{(i == 0 ? "▸" : " ")}{f.Display}").ToList();
    }

    /// <summary>The braid pane's lines, oldest first. A hypothesis or a log record is one line. The lifecycle events
    /// between two of them are one entry: each event name once, in the order it first occurred, with its count when
    /// it occurred more than once ("LoadAssembly ×9  LoadModule ×9  CreateThread"), wrapped onto further lines when
    /// the pane is narrower than the entry.</summary>
    internal static List<string> BraidLines(IEnumerable<WireDelta> deltas, int paneWidth)
    {
        var lines = new List<string>();
        var run = new List<(string Name, int Count)>();

        void EndRun()
        {
            if (run.Count == 0) return;
            // "Δ event" padded to where Pack's two-space separator puts the first name in the detail column.
            lines.AddRange(Pack($"Δ {"event",-9}", run.Select(e => e.Count == 1 ? e.Name : $"{e.Name} ×{e.Count}"), paneWidth));
            run.Clear();
        }

        foreach (WireDelta d in deltas)
        {
            if (d.Kind == "event")
            {
                string name = d.Event ?? "";
                int i = run.FindIndex(e => e.Name == name);
                if (i < 0) run.Add((name, 1)); else run[i] = (name, run[i].Count + 1);
            }
            else if (d.Kind is "hypothesis" or "log")
            {
                EndRun();
                lines.Add($"Δ {d.Kind,-10} {DebugStateTextRenderer.FormatDeltaDetail(d)}");
            }
        }
        EndRun();
        return lines;
    }

    /// <summary>The console pane's lines, oldest first: the target's output line by line, each led by its stream.
    /// A console delta is what one read of the target's pipe returned, so it can hold several lines, end with the
    /// newline of its last line, or end in the middle of a line that the stream's next delta continues.</summary>
    internal static List<string> ConsoleLines(IEnumerable<WireDelta> deltas)
    {
        var lines = new List<string>();
        var open = new Dictionary<string, int>(); // per stream: the index of its line that no newline has ended yet
        foreach (WireDelta d in deltas)
        {
            if (d.Kind != "console") continue;
            string stream = d.ConsoleStream ?? "";
            string[] parts = (d.ConsoleText ?? "").Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                bool last = i == parts.Length - 1;
                if (i == 0 && open.TryGetValue(stream, out int at))
                {
                    lines[at] += parts[i];
                    if (!last) open.Remove(stream);
                }
                else if (!last || parts[i].Length > 0) // the empty piece after a final newline is not a line
                {
                    lines.Add($"[{stream}] {parts[i]}");
                    if (last) open[stream] = lines.Count - 1;
                }
            }
        }
        return lines;
    }

    private static List<string> SourceLines(WireSnapshot? s, SourceWindowReader source, int rowsAvailable, out string title)
    {
        title = "SOURCE";
        if (s is null || s.Position.Stop is null || s.Position.CallStack.Length == 0) return [];
        WireFrame top = s.Position.CallStack[0];
        SourceWindow window = source.Read(top);
        if (!window.HasSource)
        {
            if (top.File is null) return [];
            string why = window.Status switch
            {
                SourceWindowStatus.FileNotFound => "not found on disk",
                SourceWindowStatus.FileTooLarge => "too large to show",
                SourceWindowStatus.LineOutOfRange => "source out of date",
                _ => "unavailable",
            };
            title = $"SOURCE  {Path.GetFileName(top.File)}";
            return [$"({why})"];
        }
        title = $"SOURCE  {Path.GetFileName(window.FilePath)}";
        // Slice the (wide) window to the pane, keeping the current line centred.
        int current = Math.Max(0, window.Lines.ToList().FindIndex(l => l.IsCurrent));
        int start = Math.Clamp(current - rowsAvailable / 2, 0, Math.Max(0, window.Lines.Count - rowsAvailable));
        return window.Lines.Skip(start).Take(rowsAvailable)
            .Select(l => $"{(l.IsCurrent ? "►" : " ")} {l.Number,4}  {l.Text}").ToList();
    }

    private static List<string> VariableLines(WireSnapshot? s, int paneWidth)
    {
        if (s is null || s.Position.Stop is null) return [];
        var lines = new List<string>();
        lines.AddRange(Pack("locals:", s.Position.Locals.Select(DebugStateTextRenderer.FormatVar), paneWidth));
        lines.AddRange(Pack("args:", s.Position.Arguments.Select(DebugStateTextRenderer.FormatVar), paneWidth));
        return lines;
    }

    private static List<string> BreakpointLines(WireSnapshot? s)
    {
        if (s is null) return [];
        var lines = s.Breakpoints.Select(DebugStateTextRenderer.FormatBreakpoint).ToList();
        lines.AddRange(s.ExceptionFilters.Select(f => $"id={f.Id} exception {f.TypeName} ({f.Phase}) hits={f.HitCount}"));
        if (lines.Count == 0) lines.Add("(none)");
        return lines;
    }

    // Items packed onto lines no wider than the pane, the label leading the first line.
    private static List<string> Pack(string label, IEnumerable<string> items, int paneWidth)
    {
        var lines = new List<string>();
        var line = new StringBuilder(label);
        int onLine = 0;
        foreach (string item in items)
        {
            if (onLine > 0 && line.Length + 2 + item.Length > paneWidth - 2)
            {
                lines.Add(line.ToString());
                line.Clear().Append(' ', label.Length);
                onLine = 0;
            }
            line.Append("  ").Append(item);
            onLine++;
        }
        if (onLine > 0) lines.Add(line.ToString());
        return lines;
    }

    private static List<string> Newest(IEnumerable<string> lines, int count)
    {
        var all = lines.ToList();
        return all.Skip(Math.Max(0, all.Count - count)).ToList();
    }

    // ── Frame geometry ─────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<string> SplitSection(string leftTitle, List<string> left, string rightTitle, List<string> right,
                                                    int rowCount, int split, int inner)
    {
        int leftWidth = split, rightWidth = inner - split - 1;
        for (int r = 0; r < rowCount; r++)
        {
            string l = r == 0 ? Title(leftTitle) : r - 1 < left.Count ? Cell(left[r - 1]) : "";
            string rt = r == 0 ? Title(rightTitle) : r - 1 < right.Count ? Cell(right[r - 1]) : "";
            yield return "│" + Fit(l, leftWidth) + "│" + Fit(rt, rightWidth) + "│";
        }
    }

    private static IEnumerable<string> FullSection(string title, List<string> lines, int rowCount, int inner)
    {
        for (int r = 0; r < rowCount; r++)
            yield return "│" + Fit(r == 0 ? Title(title) : r - 1 < lines.Count ? Cell(lines[r - 1]) : "", inner) + "│";
    }

    private static string Separator(char leftEdge, int split, char join, char rightEdge, int inner)
        => leftEdge + new string('─', split) + join + new string('─', inner - split - 1) + rightEdge;

    private static string Title(string text) => " " + Sanitize(text);
    private static string Cell(string text) => " " + Sanitize(text);

    private static string[] TooSmall(int width, int height)
    {
        int w = Math.Max(1, width), h = Math.Max(1, height);
        var rows = new string[h];
        string message = $"terminal {width}x{height} — the dashboard needs at least {MinWidth}x{MinHeight}";
        for (int r = 0; r < h; r++) rows[r] = r == 0 ? Fit(message, w) : new string(' ', w);
        return rows;
    }

    /// <summary>Exactly <paramref name="width"/> cells: pad with spaces, or cut with an ellipsis — never splitting a
    /// surrogate pair.</summary>
    internal static string Fit(string text, int width)
    {
        if (width <= 0) return "";
        if (text.Length <= width) return text.PadRight(width);
        int cut = width - 1;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return (text[..cut] + "…").PadRight(width);
    }

    /// <summary>Debuggee-controlled text made safe for the frame: tabs → 4 spaces; every other C0/C1 control
    /// character (ESC included) and DEL → '·'.</summary>
    internal static string Sanitize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '\t') sb.Append("    ");
            else if (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F)) sb.Append('·');
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
