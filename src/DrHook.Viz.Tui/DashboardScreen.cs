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

    public static string[] Compose(WireSnapshot? snapshot, IReadOnlyList<WireDelta> deltas, long seq, string status,
                                   int width, int height, SourceWindowReader source)
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

        var rows = new List<string>(height) { TitleRow(snapshot, seq, status, width) };

        List<string> stack = StackLines(snapshot), sourceLines = SourceLines(snapshot, source, hA - 1, out string sourceTitle);
        rows.AddRange(SplitSection("STACK", stack, sourceTitle, sourceLines, hA, splitAB, inner));
        rows.Add(Separator('├', splitAB, '┼', '┤', inner));

        rows.AddRange(SplitSection("LOCALS / ARGS", VariableLines(snapshot, splitAB), "BREAKPOINTS", BreakpointLines(snapshot), hB, splitAB, inner));
        rows.Add(Separator('├', splitAB, '┴', '┤', inner));

        rows.AddRange(FullSection("BRAID / EVENTS", Newest(deltas.Where(d => d.Kind is "event" or "hypothesis" or "log")
            .Select(d => $"Δ {d.Kind,-10} {DebugStateTextRenderer.FormatDeltaDetail(d)}"), hC - 1), hC, inner));
        rows.Add(Separator('├', splitD, '┬', '┤', inner));

        List<string> console = Newest(deltas.Where(d => d.Kind == "console")
            .SelectMany(d => DebugStateTextRenderer.FormatDeltaDetail(d).Split('\n')), hD - 1);
        WireDelta[] anomalies = deltas.Where(d => d.Kind == "anomaly").ToArray();
        rows.AddRange(SplitSection("CONSOLE  (target stdout/stderr)", console,
            $"ANOMALIES  {anomalies.Length}", Newest(anomalies.Select(DebugStateTextRenderer.FormatDeltaDetail), hD - 1),
            hD, splitD, inner));
        rows.Add(Separator('└', splitD, '┴', '┘', inner));

        return rows.ToArray();
    }

    // ── Pane contents ──────────────────────────────────────────────────────────────────────────────────────────

    private static string TitleRow(WireSnapshot? s, long seq, string status, int width)
    {
        string title = s is null
            ? $" DrHook ─ {status} "
            : $" DrHook ─ pid {s.Session.Pid} {(s.Session.Owned ? "owned" : "borrowed")} .NET{s.Session.RuntimeMajor?.ToString() ?? "?"} ─ " +
              $"{s.Session.Execution}{(s.Position.Stop is { } stop ? $": {stop}" : "")}{(s.Position.ExceptionType is { } et ? $" [{et}]" : "")} ─ #{seq} ─ {status} ";
        string clipped = Fit(Sanitize(title), width - 2);
        return "┌" + clipped.TrimEnd().PadRight(width - 2, '─') + "┐";
    }

    private static List<string> StackLines(WireSnapshot? s)
    {
        if (s is null) return ["waiting for debug-state…"];
        if (s.Position.Stop is null) return ["running — no frame"];
        return s.Position.CallStack.Select((f, i) => $"{(i == 0 ? "▸" : " ")}{f.Display}").ToList();
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
