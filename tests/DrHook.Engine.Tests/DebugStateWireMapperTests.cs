// ADR-012 Phase 2: the wire layer. The on-socket NDJSON is the STABLE contract every view depends on, so
// these pin its shape: one single-line message per call, parseable, with the projected fields. Deterministic
// (no sockets).

using System.Text.Json;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Transport;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class DebugStateWireMapperTests
{
    private static DebugStateSnapshot StoppedSnapshot() => new(
        CapturedAt: DateTimeOffset.UnixEpoch,
        Session: new SessionInfo(4242, OwnsTarget: true, RuntimeMajor: 10, IsDetached: false, IsDisposed: false, ExecutionState.Stopped),
        Position: new ExecutionPosition(
            new StopInfo(StopReason.Breakpoint), ExceptionTypeName: null,
            CallStack: new[]
            {
                new FrameLocation("Acme.Worker.Run @ Worker.cs:42", "/src/Acme/Worker.cs", 42),
                new FrameLocation("Acme.Program.Main @ Program.cs:10", "/src/Acme/Program.cs", 10),
            },
            Locals: new[] { new LocalValue("count", 0x08, 7) },
            Arguments: new[] { new ArgumentValue(0x12, null, HasChildren: true, Name: "this", TypeName: "Acme.Worker"), new ArgumentValue(0x08, 1, Name: "n") }),
        Breakpoints: new[] { new BreakpointStatus(new LineBreakpointInfo(1, "Acme", "Worker.cs", 42), HitCount: 3) },
        ExceptionFilters: new[] { new ExceptionFilterStatus(new ExceptionFilterInfo(1, "System.IOException", ExceptionStopKind.Unhandled), HitCount: 0) },
        Console: new ConsoleDrainResult(Array.Empty<ConsoleOutputRecord>(), 0),
        Logs: new DrainResult(Array.Empty<LogRecord>(), 0),
        Anomalies: new AnomalyDrainResult(Array.Empty<EngineAnomaly>(), 0));

    [Fact]
    public void SnapshotLine_IsOneNdjsonLine_WithTheProjectedState()
    {
        string line = DebugStateWireMapper.SnapshotLine(StoppedSnapshot(), seq: 7);

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
        JsonElement callStack = snap.GetProperty("position").GetProperty("callStack");
        Assert.Equal(2, callStack.GetArrayLength());
        // The top frame carries its display string AND the structured source location (ADR-012 Phase-2
        // enrichment): the FULL path survives to the wire (not the basename the display abbreviates to).
        JsonElement topFrame = callStack[0];
        Assert.Equal("Acme.Worker.Run @ Worker.cs:42", topFrame.GetProperty("display").GetString());
        Assert.Equal("/src/Acme/Worker.cs", topFrame.GetProperty("file").GetString());
        Assert.Equal(42, topFrame.GetProperty("line").GetInt32());

        JsonElement bp = snap.GetProperty("breakpoints")[0];
        Assert.Equal("line", bp.GetProperty("kind").GetString());
        Assert.Equal(42, bp.GetProperty("line").GetInt32());
        Assert.Equal(3, bp.GetProperty("hitCount").GetInt32());

        // a string-arg renders via StringValue; nulls are omitted (camelCase, WhenWritingNull)
        JsonElement nArg = snap.GetProperty("position").GetProperty("arguments").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "n");
        Assert.Equal("1", nArg.GetProperty("value").GetString());

        // an object-reference arg (this) carries hasChildren — NOT a value — so a view renders it as an
        // expandable object, not a bare "?" (dogfood 2026-06-27: `this=?` was the lossy projection).
        JsonElement thisArg = snap.GetProperty("position").GetProperty("arguments").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "this");
        Assert.True(thisArg.GetProperty("hasChildren").GetBoolean());
        Assert.False(thisArg.TryGetProperty("value", out _));            // object ref: no rendered value
        Assert.Equal("Acme.Worker", thisArg.GetProperty("typeName").GetString()); // ...carries its runtime type instead
    }

    [Fact]
    public void DeltaLine_Event_ProjectsKindAndName()
    {
        string line = DebugStateWireMapper.DeltaLine(DebugStateDelta.ForEvent(DateTimeOffset.UnixEpoch, "Breakpoint"), seq: 3);
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement delta = doc.RootElement.GetProperty("delta");
        Assert.Equal("delta", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("event", delta.GetProperty("kind").GetString());
        Assert.Equal("Breakpoint", delta.GetProperty("event").GetString());
    }

    [Fact]
    public void DeltaLine_Console_ProjectsStreamAndText()
    {
        string line = DebugStateWireMapper.DeltaLine(
            DebugStateDelta.ForConsole(new ConsoleOutputRecord(DateTimeOffset.UnixEpoch, ConsoleStream.Stdout, "hello")), seq: 9);
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement delta = doc.RootElement.GetProperty("delta");
        Assert.Equal("console", delta.GetProperty("kind").GetString());
        Assert.Equal("Stdout", delta.GetProperty("consoleStream").GetString());
        Assert.Equal("hello", delta.GetProperty("consoleText").GetString());
    }

    [Fact]
    public void DeltaLine_Hypothesis_ProjectsTextAndLens()
    {
        string line = DebugStateWireMapper.DeltaLine(
            DebugStateDelta.ForHypothesis(new HypothesisRecord(DateTimeOffset.UnixEpoch, "span.Length == 5", HypothesisLens.Inspection)), seq: 11);
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement delta = doc.RootElement.GetProperty("delta");
        Assert.Equal("hypothesis", delta.GetProperty("kind").GetString());
        Assert.Equal("span.Length == 5", delta.GetProperty("hypothesisText").GetString());
        Assert.Equal("Inspection", delta.GetProperty("hypothesisLens").GetString());
    }
}
