// The console visualizer renders to an injected TextWriter, so a StringWriter drives it deterministically
// (no sockets). Substring assertions — not exact formatting — so the layout can evolve without churn.

using SkyOmega.DrHook.Viz;
using SkyOmega.DrHook.Viz.ConsoleView;
using SkyOmega.DrHook.Wire;
using Xunit;

namespace SkyOmega.DrHook.Viz.Tests;

public sealed class ConsoleDebugStateViewTests
{
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
        string output = sw.ToString();

        Assert.Contains("StepComplete", output);
        Assert.Contains("[Stdout] hello", output);
        Assert.Contains("logged", output);
        Assert.Contains("DepthClamped", output);
    }

    [Fact]
    public void OnDisconnected_RendersReason()
    {
        var sw = new StringWriter();
        new ConsoleDebugStateView(sw).OnDisconnected("server closed the connection");
        Assert.Contains("server closed the connection", sw.ToString());
    }
}
