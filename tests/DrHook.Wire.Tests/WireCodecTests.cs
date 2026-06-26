// DrHook.Wire — the shared NDJSON contract. The codec is source-generated (reflection-based System.Text.Json
// is disabled in this repo), so these pin: one newline-terminated line per message, and a faithful
// Serialize -> Parse round-trip that both the server and every view client depend on. Pure wire, no engine.

using SkyOmega.DrHook.Wire;
using Xunit;

namespace SkyOmega.DrHook.Wire.Tests;

public sealed class WireCodecTests
{
    private static WireMessage SnapshotMessage() => new("snapshot", 7, Snapshot: new WireSnapshot(
        CapturedAt: "1970-01-01T00:00:00.0000000+00:00",
        Session: new WireSession(4242, Owned: true, RuntimeMajor: 10, Detached: false, Disposed: false, Execution: "Stopped"),
        Position: new WirePosition("Breakpoint", ExceptionType: null, TopFrame: "Acme.Worker.Run @ Worker.cs:42",
            CallStack: new[] { "Acme.Worker.Run @ Worker.cs:42", "Acme.Program.Main @ Program.cs:10" },
            Locals: new[] { new WireVar("count", 0x08, "7") },
            Arguments: new[] { new WireVar("n", 0x08, "1") }),
        Breakpoints: new[] { new WireBreakpoint(1, 3, "line", "Worker.cs", 42, null, null) },
        ExceptionFilters: Array.Empty<WireExceptionFilter>(),
        Streams: new WireStreams(0, 0, 0, 0, 0, 0)));

    [Fact]
    public void Serialize_IsOneNewlineTerminatedLine()
    {
        string line = WireCodec.Serialize(SnapshotMessage());
        Assert.EndsWith("\n", line);
        Assert.DoesNotContain("\n", line[..^1]); // single line — no embedded newline before the terminator
    }

    [Fact]
    public void Serialize_ThenParse_RoundTripsASnapshot()
    {
        WireMessage? parsed = WireCodec.Parse(WireCodec.Serialize(SnapshotMessage()));

        Assert.NotNull(parsed);
        Assert.Equal("snapshot", parsed!.Type);
        Assert.Equal(7, parsed.Seq);
        Assert.Null(parsed.Delta); // omitted on the wire (WhenWritingNull) -> parses back null

        WireSnapshot snap = Assert.IsType<WireSnapshot>(parsed.Snapshot);
        Assert.Equal(4242, snap.Session.Pid);
        Assert.Equal("Stopped", snap.Session.Execution);
        Assert.Equal("Breakpoint", snap.Position.Stop);
        Assert.Equal("Acme.Worker.Run @ Worker.cs:42", snap.Position.TopFrame);
        Assert.Equal(2, snap.Position.CallStack.Length);
        Assert.Equal("count", snap.Position.Locals[0].Name);
        Assert.Equal("1", snap.Position.Arguments.Single(a => a.Name == "n").Value);
        Assert.Equal(42, snap.Breakpoints[0].Line);
        Assert.Equal(3, snap.Breakpoints[0].HitCount);
        Assert.Empty(snap.ExceptionFilters);
    }

    [Fact]
    public void Serialize_ThenParse_RoundTripsADelta()
    {
        var msg = new WireMessage("delta", 9, Delta: new WireDelta("console", "1970-01-01T00:00:00.0000000+00:00",
            ConsoleStream: "Stdout", ConsoleText: "hello"));

        WireMessage? parsed = WireCodec.Parse(WireCodec.Serialize(msg));

        Assert.NotNull(parsed);
        Assert.Equal("delta", parsed!.Type);
        Assert.Null(parsed.Snapshot);
        WireDelta delta = Assert.IsType<WireDelta>(parsed.Delta);
        Assert.Equal("console", delta.Kind);
        Assert.Equal("Stdout", delta.ConsoleStream);
        Assert.Equal("hello", delta.ConsoleText);
        Assert.Null(delta.Event); // unset fields omitted on the wire
    }

    [Fact]
    public void Parse_BlankLine_ReturnsNull()
    {
        Assert.Null(WireCodec.Parse(""));
        Assert.Null(WireCodec.Parse("   \n"));
    }
}
