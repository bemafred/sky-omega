// Layer 1 — INTEGRATION TEST (ADR-011 D2 on every platform; probe 64).
//
// A launched debuggee must never touch the host's standard streams: under drhook-mcp they are the JSON-RPC channel.
// On Windows, dbgshim's CreateProcessForLaunch let the debuggee share them — probe 64 saw it write to the server's
// stdout and stderr and read the client's request line from stdin. DebugSession.Launch now owns the spawn on every
// platform: stdin is the null device, stdout and stderr are DrHook-owned pipes drained to the sink.
//
// The target's console-streams mode writes a marker to stdout and to stderr and tries to read stdin. The test checks
// both markers arrive as OnConsoleOutput on the right streams, and the target's report says stdin was empty (EOF).
// A marker the sink saw cannot also have gone to an inherited handle: a stream has one destination.

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkyOmega.DrHook.Engine;

namespace DrHook.Engine.IntegrationTests;

[TestClass]
public sealed class ConsoleIsolationTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Launch_ConsoleStreams_ReachTheSink_AndStdinIsEmpty()
    {
        string targetDll = IntegrationTargetPaths.SnapshotTargetDll();
        Assert.IsTrue(File.Exists(targetDll), $"Snapshot target DLL not found at {targetDll} — build dependency missing?");
        string report = Path.Combine(Path.GetTempPath(), $"drhook-console-isolation-{Guid.NewGuid():N}.txt");

        // <root>/shared/Microsoft.NETCore.App/<version>/ -> <root>/dotnet[.exe]
        string runtimeDir = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        string dotnet = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir)))!,
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        Assert.IsTrue(File.Exists(dotnet), $"dotnet host not found at {dotnet}.");

        var console = new BoundedConsoleSink(256);
        var anomalies = new BoundedAnomalySink(64);
        var sink = new CompositeEventSink(anomalies, console);
        try
        {
            using (DebugSession session = DebugSession.Launch(dotnet, new[] { targetDll, "console-streams", report }, workingDirectory: null, sink))
            {
                // Run to natural exit: the target ends by itself in ~1.5 s.
                StopInfo? stop;
                do
                {
                    stop = session.WaitForStop(TimeSpan.FromSeconds(30));
                    Assert.IsNotNull(stop, "The target neither stopped nor exited within 30 s.");
                    if (stop.Reason != StopReason.ProcessExited) session.Resume();
                } while (stop.Reason != StopReason.ProcessExited);
            }

            // The drain threads deliver a chunk after the target's write; the pipe's EOF follows the exit. Sync on
            // the sink, bounded by the test's cancellation.
            string stdout = "", stderr = "";
            while (!(stdout.Contains("CONSOLE_ISOLATION_STDOUT") && stderr.Contains("CONSOLE_ISOLATION_STDERR")))
            {
                TestContext.CancellationToken.ThrowIfCancellationRequested();
                ConsoleDrainResult drained = console.Drain();
                stdout += string.Concat(drained.Records.Where(r => r.Stream == ConsoleStream.Stdout).Select(r => r.Text));
                stderr += string.Concat(drained.Records.Where(r => r.Stream == ConsoleStream.Stderr).Select(r => r.Text));
                if (!(stdout.Contains("CONSOLE_ISOLATION_STDOUT") && stderr.Contains("CONSOLE_ISOLATION_STDERR"))) Thread.Sleep(50);
            }
            Assert.IsFalse(stdout.Contains("CONSOLE_ISOLATION_STDERR"), "stderr text arrived on the stdout stream.");
            Assert.IsFalse(stderr.Contains("CONSOLE_ISOLATION_STDOUT"), "stdout text arrived on the stderr stream.");

            Assert.IsTrue(File.Exists(report), "The target did not write its report.");
            Assert.AreEqual("stdin=EOF", File.ReadAllText(report).Trim(),
                "The launched target's stdin was not the null device: it could read the host's stdin.");
        }
        finally
        {
            File.Delete(report);
        }
    }
}
