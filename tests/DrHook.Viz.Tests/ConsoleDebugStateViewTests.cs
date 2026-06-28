// The console visualizer renders to an injected TextWriter, so a StringWriter drives it deterministically
// (no sockets). Substring assertions — not exact formatting — so the layout can evolve without churn.

using System;
using System.IO;
using SkyOmega.DrHook.Viz;
using SkyOmega.DrHook.Viz.ConsoleView;
using SkyOmega.DrHook.Wire;
using Xunit;

namespace SkyOmega.DrHook.Viz.Tests;

public sealed class ConsoleDebugStateViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "drhook-consoleview-" + Guid.NewGuid().ToString("N"));

    public ConsoleDebugStateViewTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void OnSnapshot_RendersSessionPositionBreakpointsAndStreams()
    {
        var sw = new StringWriter();
        var view = new ConsoleDebugStateView(sw);

        var snap = new WireSnapshot("1970-01-01T00:00:00.0000000+00:00",
            new WireSession(4242, Owned: true, RuntimeMajor: 10, Detached: false, Disposed: false, Execution: "Stopped"),
            new WirePosition("Breakpoint", ExceptionType: null,
                CallStack: new[]
                {
                    new WireFrame("Acme.Worker.Run @ Worker.cs:42", "/src/Acme/Worker.cs", 42),
                    new WireFrame("Acme.Program.Main @ Program.cs:10", "/src/Acme/Program.cs", 10),
                },
                Locals: new[] { new WireVar("count", 0x08, "7") },
                Arguments: new[] { new WireVar("n", 0x08, "1") }),
            Breakpoints: new[] { new WireBreakpoint(1, 3, "line", "Worker.cs", 42, null, null) },
            ExceptionFilters: Array.Empty<WireExceptionFilter>(),
            Streams: new WireStreams(2, 0, 0, 0, 0, 0));

        view.OnSnapshot(snap, new DebugStateClientModel(100));
        string output = sw.ToString();

        Assert.Contains("pid=4242", output);
        Assert.Contains("exec=Stopped", output);
        Assert.Contains("Breakpoint", output);
        Assert.Contains("Acme.Worker.Run @ Worker.cs:42", output);
        Assert.Contains("count=7", output);
        Assert.Contains("n=1", output);
        Assert.Contains("Worker.cs:42", output); // the breakpoint line
        Assert.Contains("console=2", output);     // stream counts
    }

    [Fact]
    public void OnSnapshot_Running_RendersNoFrameNotice()
    {
        var sw = new StringWriter();
        var snap = new WireSnapshot("t",
            new WireSession(1, false, 10, false, false, "Running"),
            new WirePosition(null, null, Array.Empty<WireFrame>(), Array.Empty<WireVar>(), Array.Empty<WireVar>()),
            Array.Empty<WireBreakpoint>(), Array.Empty<WireExceptionFilter>(), new WireStreams(0, 0, 0, 0, 0, 0));

        new ConsoleDebugStateView(sw).OnSnapshot(snap, new DebugStateClientModel(100));
        Assert.Contains("running", sw.ToString());
    }

    [Fact]
    public void OnDelta_RendersEachKind()
    {
        var sw = new StringWriter();
        var view = new ConsoleDebugStateView(sw);
        var model = new DebugStateClientModel(100);

        view.OnDelta(new WireDelta("event", "t", Event: "StepComplete"), model);
        view.OnDelta(new WireDelta("console", "t", ConsoleStream: "Stdout", ConsoleText: "hello"), model);
        view.OnDelta(new WireDelta("log", "t", LogMessage: "logged"), model);
        view.OnDelta(new WireDelta("anomaly", "t", AnomalyKind: "DepthClamped", AnomalyObserved: "too deep"), model);
        view.OnDelta(new WireDelta("hypothesis", "t", HypothesisText: "span.Length == 5", HypothesisLens: "Inspection"), model);
        string output = sw.ToString();

        Assert.Contains("StepComplete", output);
        Assert.Contains("[Stdout] hello", output);
        Assert.Contains("logged", output);
        Assert.Contains("DepthClamped", output);
        Assert.Contains("span.Length == 5", output);  // the braid's prediction line renders verbatim
        Assert.Contains("inspection", output);         // tagged with its lens
    }

    [Fact]
    public void OnDisconnected_RendersReason()
    {
        var sw = new StringWriter();
        new ConsoleDebugStateView(sw).OnDisconnected("server closed the connection");
        Assert.Contains("server closed the connection", sw.ToString());
    }

    [Fact]
    public void OnSnapshot_StoppedWithResolvableSource_RendersSourcePane_MarkingTheCurrentLine()
    {
        // A real source file the top frame points at — the source-on-step pane reads it from disk.
        string path = Path.Combine(_dir, "Program.cs");
        File.WriteAllLines(path, new[] { "a();", "b();", "c();", "d();", "e();" });

        var sw = new StringWriter();
        var view = new ConsoleDebugStateView(sw, new SourceWindowReader(new SourceWindowOptions { ContextLines = 2 }));
        var snap = new WireSnapshot("t",
            new WireSession(7, true, 10, false, false, "Stopped"),
            new WirePosition("Breakpoint", ExceptionType: null,
                CallStack: new[] { new WireFrame("Worker.Compute @ Program.cs:3", path, 3) },
                Locals: Array.Empty<WireVar>(), Arguments: Array.Empty<WireVar>()),
            Breakpoints: Array.Empty<WireBreakpoint>(), ExceptionFilters: Array.Empty<WireExceptionFilter>(),
            Streams: new WireStreams(0, 0, 0, 0, 0, 0));

        view.OnSnapshot(snap, new DebugStateClientModel(10));
        string[] lines = sw.ToString().Split('\n');

        Assert.Contains(lines, l => l.Contains("source  : Program.cs"));      // the pane header (basename)
        Assert.Contains(lines, l => l.Contains("►") && l.Contains("c();"));   // marker is ON the stopped line (3)
        Assert.Contains(lines, l => l.Contains("a();") && !l.Contains("►"));  // a context line, unmarked
        Assert.DoesNotContain(lines, l => l.Contains("► ") && l.Contains("a();"));
    }

    [Fact]
    public void OnSnapshot_StoppedButSourceNotOnDisk_RendersAShortNote_NotAnError()
    {
        var sw = new StringWriter();
        var snap = new WireSnapshot("t",
            new WireSession(7, true, 10, false, false, "Stopped"),
            new WirePosition("Breakpoint", ExceptionType: null,
                CallStack: new[] { new WireFrame("Worker.Compute @ Gone.cs:4", Path.Combine(_dir, "Gone.cs"), 4) },
                Locals: Array.Empty<WireVar>(), Arguments: Array.Empty<WireVar>()),
            Breakpoints: Array.Empty<WireBreakpoint>(), ExceptionFilters: Array.Empty<WireExceptionFilter>(),
            Streams: new WireStreams(0, 0, 0, 0, 0, 0));

        new ConsoleDebugStateView(sw).OnSnapshot(snap, new DebugStateClientModel(10));

        Assert.Contains("source  : Gone.cs (not found on disk)", sw.ToString());
    }

    [Fact]
    public void OnSnapshot_ValueRendering_TypedObjectNullAndPrimitive_NoBareQuestionMark()
    {
        // Regression for the 2026-06-27 live dogfood ("this=?") + the substrate-completeness follow-up (the
        // runtime type name). The four value-render branches: a typed object → its short type {Worker}; an
        // object whose type didn't resolve → expandable {…}; a genuinely-null value → null; a primitive → its
        // value. None renders as a bare "?".
        var sw = new StringWriter();
        var snap = new WireSnapshot("t",
            new WireSession(7, true, 10, false, false, "Stopped"),
            new WirePosition("Breakpoint", ExceptionType: null,
                CallStack: new[] { new WireFrame("Worker.Compute @ Program.cs:27", null, null) }, // no source pane
                Locals: new[]
                {
                    new WireVar("box", 0x12, null, HasChildren: true),                            // object, type unresolved → {…}
                    new WireVar("name", 0x0E, null),                                              // null reference → null
                },
                Arguments: new[]
                {
                    new WireVar("this", 0x12, null, HasChildren: true, TypeName: "Acme.Models.Worker"), // → {Worker}
                    new WireVar("n", 0x08, "1"),                                                  // primitive → n=1
                }),
            Breakpoints: Array.Empty<WireBreakpoint>(), ExceptionFilters: Array.Empty<WireExceptionFilter>(),
            Streams: new WireStreams(0, 0, 0, 0, 0, 0));

        new ConsoleDebugStateView(sw).OnSnapshot(snap, new DebugStateClientModel(10));
        string output = sw.ToString();

        Assert.Contains("this={Worker}", output); // typed object → short runtime type (namespace dropped)
        Assert.Contains("box={…}", output);       // object, unresolved type → expandable marker
        Assert.Contains("name=null", output);     // genuinely-null value → "null"
        Assert.Contains("n=1", output);           // primitive → value
        Assert.DoesNotContain("=?", output);      // the bare question-mark rendering is gone
    }

    [Fact]
    public void OnSnapshot_GenericAndArrayTypeNames_AbbreviatedPreservingStructure()
    {
        // The full namespace-qualified generic name on the wire is abbreviated for display: each component is
        // reduced to its simple name while the generic structure (<, >, commas, []) is preserved.
        var sw = new StringWriter();
        var snap = new WireSnapshot("t",
            new WireSession(7, true, 10, false, false, "Stopped"),
            new WirePosition("Breakpoint", ExceptionType: null,
                CallStack: new[] { new WireFrame("M @ Program.cs:1", null, null) },
                Locals: new[]
                {
                    new WireVar("list", 0x12, null, HasChildren: true, TypeName: "System.Collections.Generic.List<System.Int32>"),
                    new WireVar("map", 0x12, null, HasChildren: true, TypeName: "System.Collections.Generic.Dictionary<System.String, Acme.Models.Foo>"),
                    new WireVar("arr", 0x1D, null, HasChildren: true, TypeName: "Acme.Models.Bar[]"),
                },
                Arguments: Array.Empty<WireVar>()),
            Breakpoints: Array.Empty<WireBreakpoint>(), ExceptionFilters: Array.Empty<WireExceptionFilter>(),
            Streams: new WireStreams(0, 0, 0, 0, 0, 0));

        new ConsoleDebugStateView(sw).OnSnapshot(snap, new DebugStateClientModel(10));
        string output = sw.ToString();

        Assert.Contains("list={List<Int32>}", output);
        Assert.Contains("map={Dictionary<String, Foo>}", output);
        Assert.Contains("arr={Bar[]}", output);
    }
}
