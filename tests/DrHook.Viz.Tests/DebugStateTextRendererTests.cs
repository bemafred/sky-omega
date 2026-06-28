// The shared debug-state → text projection. ConsoleDebugStateView delegates to it, and drhook_snapshot_image
// rasterizes its output — so this is the one renderer both the live view and the image tool use. Tested directly
// (not only via the console view) because the image path calls RenderSnapshot with a StringWriter.

using SkyOmega.DrHook.Viz;
using SkyOmega.DrHook.Wire;
using Xunit;

namespace SkyOmega.DrHook.Viz.Tests;

public sealed class DebugStateTextRendererTests
{
    [Fact]
    public void RenderSnapshot_WritesTheCompactBlock_SessionStopFramesLocalsBreakpointsStreams()
    {
        var sw = new StringWriter();
        var snap = new WireSnapshot("t",
            new WireSession(4242, Owned: true, RuntimeMajor: 10, Detached: false, Disposed: false, Execution: "Stopped"),
            new WirePosition("Breakpoint", ExceptionType: null,
                CallStack: new[] { new WireFrame("Acme.Worker.Compute @ Program.cs:12", null, null) },
                Locals: new[] { new WireVar("doubled", 0x08, "4"), new WireVar("this", 0x12, null, HasChildren: true, TypeName: "Acme.Worker") },
                Arguments: new[] { new WireVar("n", 0x08, "2") }),
            Breakpoints: new[] { new WireBreakpoint(1, 3, "line", "Program.cs", 12, null, null) },
            ExceptionFilters: Array.Empty<WireExceptionFilter>(),
            Streams: new WireStreams(2, 0, 0, 0, 0, 0));

        DebugStateTextRenderer.RenderSnapshot(sw, snap, seq: 7, new SourceWindowReader());
        string output = sw.ToString();

        Assert.Contains("snapshot #7", output);
        Assert.Contains("pid=4242", output);
        Assert.Contains("exec=Stopped", output);
        Assert.Contains("Acme.Worker.Compute @ Program.cs:12", output);
        Assert.Contains("doubled=4", output);
        Assert.Contains("this={Worker}", output);   // typed object → short runtime type, not a bare "?"
        Assert.Contains("n=2", output);
        Assert.Contains("break   : id=1 line Program.cs:12 hits=3", output);
        Assert.Contains("console=2", output);        // stream counts
    }

    [Fact]
    public void RenderDelta_RendersHypothesisWithItsLens()
    {
        var sw = new StringWriter();
        DebugStateTextRenderer.RenderDelta(sw, new WireDelta("hypothesis", "t",
            HypothesisText: "contribution should be 7", HypothesisLens: "Inspection"));
        string output = sw.ToString();

        Assert.Contains("contribution should be 7", output); // the braid's prediction line, verbatim
        Assert.Contains("inspection", output);               // tagged with its (lowercased) lens
    }
}
