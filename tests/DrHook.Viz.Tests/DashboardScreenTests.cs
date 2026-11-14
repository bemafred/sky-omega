// The TUI dashboard's compositor is pure (model → rows) and its presenter writes to an injected TextWriter, so both
// are driven deterministically here — no terminal, no sockets. Geometry is asserted exactly (every row the terminal
// width, the frame the terminal height); pane CONTENT by substring, so wording can evolve without churn.

using System;
using System.Collections.Generic;
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
            .Select(i => new WireDelta("console", "t", ConsoleStream: "stdout", ConsoleText: $"line {i:000}\n")).ToArray(); // a line each: a read that ends without a newline is continued by the next

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

    private static WireSnapshot Snap(string execution, string? stop = null, string? exit = null) => new("t",
        new WireSession(7, true, 10, false, false, execution, exit),
        new WirePosition(stop, null, stop is null ? [] : [new WireFrame("Acme.Program.Main @ Program.cs:7", null, null)], [], []),
        [], [], new WireStreams(0, 0, 0, 0, 0, 0));

    private static WireDelta Hypothesis(string text) => new("hypothesis", "t", HypothesisText: text, HypothesisLens: "Navigation");
    private static WireDelta Event(string name) => new("event", "t", Event: name);
    private static WireDelta Out(string text, string stream = "Stdout") => new("console", "t", ConsoleStream: stream, ConsoleText: text);

    [Fact]
    public void Compose_ATargetsModuleLoads_DoNotPushTheHypothesesOutOfTheBraid()
    {
        // What a real launch emits: the session's hypothesis, some twenty load events, then the next hypothesis
        // and the exit. At 24 rows the braid pane has two content rows; one line per event showed neither hypothesis.
        var deltas = new List<WireDelta> { Hypothesis("the loop has run three times") };
        for (int i = 0; i < 9; i++) { deltas.Add(Event("LoadAssembly")); deltas.Add(Event("LoadModule")); }
        deltas.Add(Event("CreateThread"));
        deltas.Add(Hypothesis("the target prints done 14"));
        deltas.AddRange([Event("ExitThread"), Event("CreateThread"), Event("ExitThread"), Event("ExitProcess")]);

        string[] rows = DashboardScreen.Compose(Snap("Stopped", "Breakpoint"), deltas, 30, "connected", 120, 40, _source);
        string screen = string.Join("\n", rows);

        Assert.Contains("▸ navigation: the loop has run three times", screen);
        Assert.Contains("Δ event      LoadAssembly ×9  LoadModule ×9  CreateThread", screen);
        Assert.Contains("▸ navigation: the target prints done 14", screen);
        Assert.Contains("Δ event      ExitThread ×2  CreateThread  ExitProcess", screen);
        Assert.Equal(4, rows.Count(r => r.Contains("│ Δ ")));
    }

    [Fact]
    public void BraidLines_AnEntryWiderThanThePane_WrapsUnderItsFirstName_AndLosesNoEvent()
    {
        string[] names = ["CreateProcess", "CreateAppDomain", "LoadAssembly", "LoadModule", "NameChange", "CreateThread", "LoadClass"];
        WireDelta[] deltas = names.Select(Event).Concat(names.Take(2).Select(Event)).ToArray();

        List<string> lines = DashboardScreen.BraidLines(deltas, paneWidth: 58);

        Assert.True(lines.Count > 1);
        Assert.All(lines, l => Assert.True(l.Length <= 57, l)); // the pane's cell: its width less the leading space
        Assert.StartsWith("Δ event      CreateProcess ×2  CreateAppDomain ×2", lines[0]);
        Assert.All(lines.Skip(1), l => Assert.StartsWith("             ", l));
        string all = string.Join(" ", lines);
        Assert.All(names, n => Assert.Contains(n, all));
    }

    [Fact]
    public void BraidLines_ALogBetweenEvents_EndsTheEntry_SoOrderAroundItIsKept()
    {
        WireDelta[] deltas =
            [Event("LoadModule"), Event("LoadModule"), new("log", "t", LogMessage: "hit 3"), Event("LoadModule"), new("console", "t", ConsoleText: "x"), Event("ExitProcess")];

        List<string> lines = DashboardScreen.BraidLines(deltas, paneWidth: 100);

        Assert.Equal(["Δ event      LoadModule ×2", "Δ log        hit 3", "Δ event      LoadModule  ExitProcess"], lines);
    }

    [Fact]
    public void ConsoleLines_AreTheTargetsLines_NotThePipesChunks()
    {
        WireDelta[] deltas =
        [
            Out("i=1 total=1\n"),                 // one line and its newline: no empty row after it
            Out("i=2 total=5\ni=3 total=14\n"),   // two lines in one read: each led by its stream
            Out("partial "),                       // a line the next read continues
            Out("oops\n", "Stderr"),               // the other stream in between
            Out("rest\r\n"),
            Out("a\n\nb\n"),                      // an empty line the target printed is kept
        ];

        List<string> lines = DashboardScreen.ConsoleLines(deltas);

        Assert.Equal(
        [
            "[Stdout] i=1 total=1", "[Stdout] i=2 total=5", "[Stdout] i=3 total=14",
            "[Stdout] partial rest", "[Stderr] oops", "[Stdout] a", "[Stdout] ", "[Stdout] b",
        ], lines);
    }

    [Fact]
    public void Compose_AfterTheSessionEnded_AStopIsGivenAsTheLastState_NotTheCurrentOne()
    {
        WireSnapshot stopped = Snap("Stopped", "Breakpoint");

        string live = DashboardScreen.Compose(stopped, [], 27, "connected", 140, 24, _source)[0];
        string ended = DashboardScreen.Compose(stopped, [], 27, "session ended, waiting for the next (q to quit): x", 140, 24, _source, connected: false)[0];

        Assert.Contains("─ Stopped: Breakpoint ─", live);
        Assert.DoesNotContain("last:", live);
        Assert.Contains("─ last: Stopped: Breakpoint ─", ended);
    }

    [Theory]
    [InlineData("exited with code 0", "target exited with code 0")]
    [InlineData("was killed by signal 11 (SIGSEGV)", "target was killed by signal 11 (SIGSEGV)")]
    [InlineData(null, "target exited")]
    public void Compose_AnExitedTarget_SaysHowItEnded_InTheTitleAndInPlaceOfTheStack(string? exit, string expected)
    {
        WireSnapshot exited = Snap("Exited", exit: exit);

        foreach (bool connected in new[] { true, false }) // the same either way: an end stays true
        {
            string[] rows = DashboardScreen.Compose(exited, [], 29, "session ended", 140, 24, _source, connected);

            Assert.Contains($"─ {expected} ─", rows[0]);
            Assert.DoesNotContain("last:", rows[0]);
            Assert.Contains(rows, r => r.Contains($"{expected} — no frame"));
            Assert.DoesNotContain(rows, r => r.Contains("running"));
        }
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

    [Fact]
    public void DisconnectedStatus_ADashboardThatReconnects_IsWaiting_AndKeepsTheQuitHintFirst()
    {
        const string reason = "server closed the connection";

        Assert.Equal($"disconnected (q to quit): {reason}",
            DashboardDebugStateView.DisconnectedStatus(reason, reconnects: false, connectedOnce: true));
        Assert.Equal($"waiting for a session (q to quit): {reason}",
            DashboardDebugStateView.DisconnectedStatus(reason, reconnects: true, connectedOnce: false));
        Assert.Equal($"session ended, waiting for the next (q to quit): {reason}",
            DashboardDebugStateView.DisconnectedStatus(reason, reconnects: true, connectedOnce: true));
    }
}
