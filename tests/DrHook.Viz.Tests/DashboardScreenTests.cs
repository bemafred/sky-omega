// The TUI dashboard's compositor is pure (model → rows) and its presenter writes to an injected TextWriter, so both
// are driven deterministically here — no terminal, no sockets. Geometry is asserted exactly (every row the terminal
// width, the frame the terminal height); pane CONTENT by substring, so wording can evolve without churn.

using System;
using System.IO;
using System.Linq;
using SkyOmega.DrHook.Viz;
using SkyOmega.DrHook.Viz.Tui;
using SkyOmega.DrHook.Wire;
using Xunit;

namespace SkyOmega.DrHook.Viz.Tests;

public sealed class DashboardScreenTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "drhook-dashboard-" + Guid.NewGuid().ToString("N"));
    private readonly SourceWindowReader _source = new(new SourceWindowOptions { ContextLines = DashboardScreen.SourceContextLines });

    public DashboardScreenTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Compose_StoppedSnapshot_FillsEveryPane_AtExactTerminalGeometry()
    {
        string file = Path.Combine(_dir, "Worker.cs");
        File.WriteAllLines(file, Enumerable.Range(1, 60).Select(i => i == 42 ? "        Total += contribution;" : $"        // line {i}"));
        var snap = new WireSnapshot("1970-01-01T00:00:00.0000000+00:00",
            new WireSession(4242, Owned: false, RuntimeMajor: 10, Detached: false, Disposed: false, Execution: "Stopped"),
            new WirePosition("Breakpoint", ExceptionType: null,
                CallStack: [new WireFrame("Acme.Worker.Compute @ Worker.cs:42", file, 42), new WireFrame("Acme.Program.Main @ Program.cs:10", null, null)],
                Locals: [new WireVar("doubled", 0x08, "2"), new WireVar("tags", 0x12, null, HasChildren: true, TypeName: "System.Collections.Generic.List<System.String>")],
                Arguments: [new WireVar("n", 0x08, "1")]),
            Breakpoints: [new WireBreakpoint(1, 3, "line", "/very/long/absolute/path/to/the/repo/src/Acme/Worker.cs", 42, null, null)],  // the wire carries the FULL path
            ExceptionFilters: [],
            Streams: new WireStreams(0, 0, 0, 0, 0, 0));
        WireDelta[] deltas =
        [
            new("hypothesis", "t", HypothesisText: "next stop is beat 2", HypothesisLens: "Navigation"),
            new("event", "t", Event: "ExitThread"),
            new("console", "t", ConsoleStream: "stdout", ConsoleText: "tick 1"),
            new("anomaly", "t", AnomalyKind: "DepthClamped", AnomalyObserved: "depth=999 requested"),
        ];

        string[] rows = DashboardScreen.Compose(snap, deltas, 14, "connected", 100, 30, _source);

        Assert.Equal(30, rows.Length);
        Assert.All(rows, r => Assert.Equal(100, r.Length));
        string screen = string.Join("\n", rows);
        Assert.Contains("pid 4242 borrowed .NET10", rows[0]);
        Assert.Contains("Stopped: Breakpoint", rows[0]);
        Assert.Contains("#14", rows[0]);
        Assert.Contains("▸Acme.Worker.Compute @ Worker.cs:42", screen);
        Assert.Contains("SOURCE  Worker.cs", screen);
        Assert.Contains("►   42          Total += contribution;", screen);
        Assert.Contains("doubled=2", screen);
        Assert.Contains("tags={List<String>}", screen);     // the SHARED formatter (DebugStateTextRenderer.FormatVar)
        Assert.Contains("n=1", screen);
        Assert.Contains("id=1 line Worker.cs:42 hits=3", screen);
        Assert.Contains("▸ navigation: next stop is beat 2", screen);
        Assert.Contains("ExitThread", screen);
        Assert.Contains("[stdout] tick 1", screen);
        Assert.Contains("ANOMALIES  1", screen);
        Assert.Contains("DepthClamped: depth=999 requested", screen);
        Assert.StartsWith("┌", rows[0]);
        Assert.StartsWith("└", rows[^1]);
    }

    [Fact]
    public void Compose_NoSnapshotYet_ShowsWaitingAndTheStatus()
    {
        string[] rows = DashboardScreen.Compose(null, [], 0, "connecting…", 80, 24, _source);

        Assert.Equal(24, rows.Length);
        Assert.All(rows, r => Assert.Equal(80, r.Length));
        Assert.Contains("connecting…", rows[0]);
        Assert.Contains(rows, r => r.Contains("waiting for debug-state…"));
    }

    [Fact]
    public void Compose_TerminalTooSmall_ShowsOneMessage_AtThatGeometry()
    {
        string[] rows = DashboardScreen.Compose(null, [], 0, "connected", 40, 10, _source);

        Assert.Equal(10, rows.Length);
        Assert.All(rows, r => Assert.Equal(40, r.Length));
        Assert.StartsWith("terminal 40x10", rows[0]);     // the size message (clipped to the 40 columns there are)
        Assert.All(rows.Skip(1), r => Assert.True(string.IsNullOrWhiteSpace(r)));
    }

    [Fact]
    public void Compose_DebuggeeControlCharacters_CannotReachTheTerminal()
    {
        WireDelta[] deltas = [new("console", "t", ConsoleStream: "stdout", ConsoleText: "\u001b[2Jwiped\tcol\u0007")];

        string[] rows = DashboardScreen.Compose(null, deltas, 0, "connected", 80, 24, _source);

        Assert.DoesNotContain(rows, r => r.Any(c => c < 0x20 || c == 0x7F));
        Assert.Contains(rows, r => r.Contains("·[2Jwiped    col·"));
    }

    [Fact]
    public void Compose_PaneOverflow_KeepsTheNewestLines()
    {
        WireDelta[] deltas = Enumerable.Range(1, 200)
            .Select(i => new WireDelta("console", "t", ConsoleStream: "stdout", ConsoleText: $"line {i:000}")).ToArray();

        string[] rows = DashboardScreen.Compose(null, deltas, 0, "connected", 80, 24, _source);
        string screen = string.Join("\n", rows);

        Assert.Contains("line 200", screen);
        Assert.DoesNotContain("line 001", screen);
    }

    [Fact]
    public void Compose_RunningTarget_SaysSoInsteadOfAStack()
    {
        var snap = new WireSnapshot("t", new WireSession(7, true, 10, false, false, "Running"),
            new WirePosition(null, null, [], [], []), [], [], new WireStreams(0, 0, 0, 0, 0, 0));

        string[] rows = DashboardScreen.Compose(snap, [], 3, "connected", 80, 24, _source);

        Assert.Contains("Running", rows[0]);
        Assert.Contains(rows, r => r.Contains("running — no frame"));
    }

    [Fact]
    public void Fit_CutsWithEllipsis_AndNeverSplitsASurrogatePair()
    {
        Assert.Equal("abc  ", DashboardScreen.Fit("abc", 5));
        Assert.Equal("abcd…", DashboardScreen.Fit("abcdefgh", 5));
        string emoji = "ab\U0001F600cd";                  // 'a','b',high,low,'c','d'
        string cut = DashboardScreen.Fit(emoji, 4);       // a naive cut at 3 would split the pair
        Assert.Equal(4, cut.Length);
        Assert.False(char.IsHighSurrogate(cut[^2]) && cut[^1] == '…');
    }
}

public sealed class AnsiTerminalTests
{
    [Fact]
    public void Present_RewritesOnlyChangedRows_AfterTheFirstFrame()
    {
        var sw = new StringWriter();
        var terminal = new AnsiTerminal(sw);
        terminal.Present(["row one ", "row two ", "row tri "]);
        int afterFirst = sw.ToString().Length;

        terminal.Present(["row one ", "row TWO ", "row tri "]);
        string second = sw.ToString()[afterFirst..];

        Assert.Equal("\u001b[2;1Hrow TWO ", second);
    }

    [Fact]
    public void Present_IdenticalFrame_WritesNothing()
    {
        var sw = new StringWriter();
        var terminal = new AnsiTerminal(sw);
        terminal.Present(["a", "b"]);
        int before = sw.ToString().Length;

        terminal.Present(["a", "b"]);

        Assert.Equal(before, sw.ToString().Length);
    }

    [Fact]
    public void EnterExit_UseTheAlternateScreen_AndRestoreTheCursor()
    {
        var sw = new StringWriter();
        var terminal = new AnsiTerminal(sw);
        terminal.Enter();
        terminal.Exit();
        terminal.Exit(); // idempotent

        string written = sw.ToString();
        Assert.StartsWith("\u001b[?1049h\u001b[?25l", written);
        Assert.EndsWith("\u001b[?25h\u001b[?1049l", written);
        Assert.Equal(1, written.Split("\u001b[?1049l").Length - 1);
    }
}
