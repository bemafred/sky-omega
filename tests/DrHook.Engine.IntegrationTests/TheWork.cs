// Running an attached test-runner target into its test method before anything else (finding 94; 2026-11-20).
//
// Both runners hold the target in a loop that polls Debugger.IsAttached about once a second: MTP's --debug and
// VSTEST_HOST_DEBUG (finding 91). An attach that ends before the loop has seen it, or that holds the target stopped
// while the loop would run, leaves the target in the loop, and after the detach it waits for a debugger for ever.
// Measured on zbook, 2026-11-20, against the MTP target with a detach that sends nothing: after an attach of 200 ms
// it was still waiting 8 s later (3 of 3); after one of 2.5 s it ran its test and exited by itself (2 of 2).
//
// Until ADR-016 6.2 an owned target was ended at Dispose anyway, by SIGTERM on Linux and macOS and by the kill on
// Windows, so a test that asserted a "natural exit" after a short attach passed without one. 6.2 leaves a running
// target running on Windows, where an attached target has no exit request (6.4), and those tests failed there (CI run 44695388137,
// 5 tests on Windows x64 and arm64). A breakpoint hit inside the test method proves the target is past the loop.

using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkyOmega.DrHook.Engine;

namespace DrHook.Engine.IntegrationTests;

internal static class TheWork
{
    /// <summary>Stops the attached target inside its test method, at the TEST_RUNNING_HERE marker of
    /// <paramref name="source"/>, and leaves it stopped there with the breakpoint removed. A Pause gives a first stop
    /// however early the attach landed; the breakpoint is pending until the test assembly loads; the Pause and the
    /// runner's Break, queued ahead of the breakpoint in either order (finding 91), are each resumed.</summary>
    public static StopInfo RunTo(DebugSession session, string source, Action<string>? log = null)
    {
        int markerLine = Array.FindIndex(File.ReadAllLines(source), l => l.Contains("TEST_RUNNING_HERE", StringComparison.Ordinal)) + 1;
        Assert.IsTrue(markerLine > 0, $"TEST_RUNNING_HERE marker not found in {source}.");
        string file = Path.GetFileName(source);

        session.Pause();
        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(5));
        log?.Invoke($"first stop: {stop?.Reason.ToString() ?? "none"}");
        Assert.IsNotNull(stop, "No stop within 5s of Pause.");

        int breakpointId = session.SetBreakpointAtLine(file, markerLine);
        log?.Invoke($"breakpoint {breakpointId} at {file}:{markerLine}");
        Assert.AreNotEqual(0, breakpointId, $"Failed to set a breakpoint at {file}:{markerLine}.");
        do
        {
            session.Resume();
            stop = session.WaitForStop(TimeSpan.FromSeconds(30));
            log?.Invoke($"resumed; stop: {stop?.Reason.ToString() ?? "none"}");
            Assert.IsNotNull(stop, "The test method's breakpoint was not hit within 30s.");
        }
        while (stop!.Reason is StopReason.Break or StopReason.Pause);
        Assert.AreEqual(StopReason.Breakpoint, stop.Reason);

        Assert.IsTrue(session.RemoveBreakpoint(breakpointId), "Could not remove the test method's breakpoint.");
        log?.Invoke("breakpoint removed");
        return stop;
    }
}
