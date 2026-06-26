// ADR-012 Phase 2: the wire layer. The on-socket NDJSON is the STABLE contract every view depends on, so
// these pin its shape: one single-line message per call, parseable, with the projected fields. Deterministic
// (no sockets).

using System.Text.Json;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Transport;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class DebugStateWireSerializerTests
{
    private static DebugStateSnapshot StoppedSnapshot() => new(
        CapturedAt: DateTimeOffset.UnixEpoch,
        Session: new SessionInfo(4242, OwnsTarget: true, RuntimeMajor: 10, IsDetached: false, IsDisposed: false, ExecutionState.Stopped),
        Position: new ExecutionPosition(
            new StopInfo(StopReason.Breakpoint), ExceptionTypeName: null,
            CallStack: new[] { "Acme.Worker.Run @ Worker.cs:42", "Acme.Program.Main @ Program.cs:10" },
            Locals: new[] { new LocalValue("count", 0x08, 7) },
            Arguments: new[] { new ArgumentValue(0x12, null, Name: "this"), new ArgumentValue(0x08, 1, Name: "n") }),
        Breakpoints: new[] { new BreakpointStatus(new LineBreakpointInfo(1, "Acme", "Worker.cs", 42), HitCount: 3) },
        ExceptionFilters: new[] { new ExceptionFilterStatus(new ExceptionFilterInfo(1, "System.IOException", ExceptionStopKind.Unhandled), HitCount: 0) },
        Console: new ConsoleDrainResult(Array.Empty<ConsoleOutputRecord>(), 0),
        Logs: new DrainResult(Array.Empty<LogRecord>(), 0),
        Anomalies: new AnomalyDrainResult(Array.Empty<EngineAnomaly>(), 0));

    [Fact]
    public void SnapshotLine_IsOneNdjsonLine_WithTheProjectedState()
    {
        string line = DebugStateWireSerializer.SnapshotLine(StoppedSnapshot(), seq: 7);

        Assert.EndsWith("\n", line);
        Assert.DoesNotContain("\n", line[..^1]); // single line — no embedded newlines before the terminator

        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement root = doc.RootElement;
        Assert.Equal("snapshot", root.GetProperty("type").GetString());
        Assert.Equal(7, root.GetProperty("seq").GetInt64());

        JsonElement snap = root.GetProperty("snapshot");
        Assert.Equal(4242, snap.GetProperty("session").GetProperty("pid").GetInt32());
        Assert.Equal("Stopped", snap.GetProperty("session").GetProperty("execution").GetString());
        Assert.Equal("Breakpoint", snap.GetProperty("position").GetProperty("stop").GetString());
        Assert.Equal("Acme.Worker.Run @ Worker.cs:42", snap.GetProperty("position").GetProperty("topFrame").GetString());
        Assert.Equal(2, snap.GetProperty("position").GetProperty("callStack").GetArrayLength());

        JsonElement bp = snap.GetProperty("breakpoints")[0];
        Assert.Equal("line", bp.GetProperty("kind").GetString());
        Assert.Equal(42, bp.GetProperty("line").GetInt32());
        Assert.Equal(3, bp.GetProperty("hitCount").GetInt32());

        // a string-arg renders via StringValue; nulls are omitted (camelCase, WhenWritingNull)
        JsonElement nArg = snap.GetProperty("position").GetProperty("arguments").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "n");
        Assert.Equal("1", nArg.GetProperty("value").GetString());
    }

    [Fact]
    public void DeltaLine_Event_ProjectsKindAndName()
    {
        string line = DebugStateWireSerializer.DeltaLine(DebugStateDelta.ForEvent(DateTimeOffset.UnixEpoch, "Breakpoint"), seq: 3);
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement delta = doc.RootElement.GetProperty("delta");
        Assert.Equal("delta", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("event", delta.GetProperty("kind").GetString());
        Assert.Equal("Breakpoint", delta.GetProperty("event").GetString());
    }

    [Fact]
    public void DeltaLine_Console_ProjectsStreamAndText()
    {
        string line = DebugStateWireSerializer.DeltaLine(
            DebugStateDelta.ForConsole(new ConsoleOutputRecord(DateTimeOffset.UnixEpoch, ConsoleStream.Stdout, "hello")), seq: 9);
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement delta = doc.RootElement.GetProperty("delta");
        Assert.Equal("console", delta.GetProperty("kind").GetString());
        Assert.Equal("Stdout", delta.GetProperty("consoleStream").GetString());
        Assert.Equal("hello", delta.GetProperty("consoleText").GetString());
    }
}
