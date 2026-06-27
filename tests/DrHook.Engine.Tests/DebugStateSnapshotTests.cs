// ADR-012 Phase 1: the snapshot is the self-contained "point-in-time" face — a view connecting mid-session
// must render the full current picture from the snapshot ALONE (the 2026-06-25 ownership directive). These
// tests pin the record's shape and the running/stopped contract. Live assembly from a real session
// (DebugSession.CaptureState) is covered by the integration tests.

using SkyOmega.DrHook.Engine;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class DebugStateSnapshotTests
{
    [Fact]
    public void StoppedSnapshot_CarriesEverythingAViewNeeds_FromTheSnapshotAlone()
    {
        var snapshot = new DebugStateSnapshot(
            CapturedAt: DateTimeOffset.UnixEpoch,
            Session: new SessionInfo(4242, OwnsTarget: true, RuntimeMajor: 10, IsDetached: false, IsDisposed: false, ExecutionState.Stopped),
            Position: new ExecutionPosition(
                new StopInfo(StopReason.Breakpoint),
                ExceptionTypeName: null,
                CallStack: new[]
                {
                    new FrameLocation("Acme.Worker.Run @ Worker.cs:42", "/src/Acme/Worker.cs", 42),
                    new FrameLocation("Acme.Program.Main @ Program.cs:10", "/src/Acme/Program.cs", 10),
                },
                Locals: new[] { new LocalValue("count", 0, 7) },
                Arguments: new[] { new ArgumentValue(0, null, Name: "this") }),
            Breakpoints: new[] { new BreakpointStatus(new LineBreakpointInfo(1, "Acme", "Worker.cs", 42), HitCount: 3) },
            ExceptionFilters: new[] { new ExceptionFilterStatus(new ExceptionFilterInfo(1, "System.IOException", ExceptionStopKind.Unhandled), HitCount: 0) },
            Console: new ConsoleDrainResult(new[] { new ConsoleOutputRecord(DateTimeOffset.UnixEpoch, ConsoleStream.Stdout, "hello") }, 0),
            Logs: new DrainResult(Array.Empty<LogRecord>(), 0),
            Anomalies: new AnomalyDrainResult(Array.Empty<EngineAnomaly>(), 0));

        // A view renders identity + disposition with no prior context:
        Assert.Equal(4242, snapshot.Session.ProcessId);
        Assert.True(snapshot.Session.OwnsTarget);
        Assert.Equal(ExecutionState.Stopped, snapshot.Session.Execution);

        // ...the execution position (top frame is the stack head)...
        Assert.Equal("Acme.Worker.Run @ Worker.cs:42", snapshot.Position.TopFrame);
        Assert.Equal(2, snapshot.Position.CallStack.Count);
        // ...each frame's structured source location — the FULL path + line a source-rendering view needs
        // (ADR-012 Phase-2 enrichment), not just the abbreviated display string:
        Assert.Equal("/src/Acme/Worker.cs", snapshot.Position.CallStack[0].File);
        Assert.Equal(42, snapshot.Position.CallStack[0].Line);
        Assert.Equal("count", snapshot.Position.Locals[0].Name);
        Assert.Equal("this", snapshot.Position.Arguments[0].Name);

        // ...breakpoints with hit counts, and the console tail.
        Assert.Equal(3, snapshot.Breakpoints[0].HitCount);
        Assert.Equal(42, Assert.IsType<LineBreakpointInfo>(snapshot.Breakpoints[0].Info).Line);
        Assert.Equal("hello", snapshot.Console.Records[0].Text);
    }

    [Fact]
    public void RunningPosition_None_HasNoFrame()
    {
        Assert.Null(ExecutionPosition.None.TopFrame);
        Assert.Empty(ExecutionPosition.None.CallStack);
        Assert.Empty(ExecutionPosition.None.Locals);
        Assert.Empty(ExecutionPosition.None.Arguments);
    }
}
