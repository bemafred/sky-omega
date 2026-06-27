// Layer 1 — INTEGRATION TEST: the breakpoint that drhook_launch sets must be removable by its id.
//
// Regression for the ADR-013 finale finding: the launch/attach initial breakpoint registered in the
// MCP layer's _lineBreakpoints map but NOT in _breakpointKinds, while RemoveByIdAsync gates on
// _breakpointKinds — so drhook_break_remove rejected an id that drhook_break_list happily showed
// ("No breakpoint with id=N is tracked at the MCP layer"). The fix unified all source-breakpoint
// registration (launch + attach initial bp, and break_source) through one method
// (EngineSteppingSession.TrackSourceBreakpoint) that populates every tracking map atomically, so a
// listed source breakpoint is always removable by id.
//
// This drives the real MCP-layer session (EngineSteppingSession) — launch the snapshot target, read
// the initial breakpoint's id from ListBreakpoints (engine truth), then RemoveByIdAsync it. Pre-fix
// the remove returns the "not tracked" error; post-fix it returns status "removed". macOS/arm64 today.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkyOmega.DrHook.Mcp;

namespace DrHook.Engine.IntegrationTests;

[TestClass]
public sealed class LaunchBreakpointRemovableTest
{
    private const string MarkerToken = "SNAPSHOT_HERE";

    [TestMethod]
    public async Task LaunchInitialBreakpoint_IsRemovableById()
    {
        string targetDll = IntegrationTargetPaths.SnapshotTargetDll();
        Assert.IsTrue(File.Exists(targetDll), $"Snapshot target DLL not found at {targetDll} — build dependency missing?");
        string source = IntegrationTargetPaths.SnapshotTargetSource();
        int markerLine = FindMarker(source, MarkerToken);
        Assert.IsTrue(markerLine > 0, $"'{MarkerToken}' marker not found in the snapshot target source.");

        using var session = new EngineSteppingSession();
        int pid = 0;
        try
        {
            string launch = await session.LaunchAsync(
                ResolveDotnet(), new[] { "exec", targetDll }, cwd: null,
                source, markerLine, hypothesis: "launch sets an initial source breakpoint that must be removable by id",
                env: null, CancellationToken.None);
            using (JsonDocument launchDoc = JsonDocument.Parse(launch))
            {
                JsonElement root = launchDoc.RootElement;
                Assert.IsTrue(root.TryGetProperty("breakpoint", out _), $"launch did not bind a breakpoint: {launch}");
                if (root.TryGetProperty("pid", out JsonElement p)) pid = p.GetInt32();
            }

            // The engine is authoritative for what is armed — discover the initial breakpoint's id from the list.
            int id = FirstSourceBreakpointId(session.ListBreakpoints());
            Assert.AreNotEqual(0, id, "ListBreakpoints showed no source breakpoint after launch.");

            // THE REGRESSION: the launch-created breakpoint must be removable by that id. Pre-fix this
            // returned "No breakpoint with id=N is tracked at the MCP layer"; post-fix, status "removed".
            string removed = await session.RemoveByIdAsync(id, CancellationToken.None);
            Assert.IsFalse(removed.Contains("tracked at the MCP layer", StringComparison.Ordinal),
                $"launch breakpoint id={id} was listed but not removable by id — the MCP tracking maps drifted. Result: {removed}");
            using JsonDocument removeDoc = JsonDocument.Parse(removed);
            Assert.IsTrue(removeDoc.RootElement.TryGetProperty("status", out JsonElement status),
                $"remove returned no status: {removed}");
            Assert.AreEqual("removed", status.GetString(), $"launch breakpoint id={id} should remove cleanly; got: {removed}");

            // ...and it is genuinely gone from the engine's list.
            Assert.AreEqual(0, FirstSourceBreakpointId(session.ListBreakpoints()), "the source breakpoint should be gone after removal.");
        }
        finally
        {
            try { session.Dispose(); } catch { /* idempotent */ }
            if (pid != 0) { try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* already gone */ } }
        }
    }

    private static int FirstSourceBreakpointId(string listJson)
    {
        using JsonDocument doc = JsonDocument.Parse(listJson);
        if (doc.RootElement.TryGetProperty("source", out JsonElement src) && src.ValueKind == JsonValueKind.Array && src.GetArrayLength() > 0)
            return src[0].GetProperty("id").GetInt32();
        return 0;
    }

    private static int FindMarker(string path, string marker)
    {
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains(marker, StringComparison.Ordinal))
                return i + 1;
        return -1;
    }

    private static string ResolveDotnet()
    {
        string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        string? dotnetRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir)));
        if (dotnetRoot is not null)
        {
            string candidate = Path.Combine(dotnetRoot, "dotnet");
            if (File.Exists(candidate)) return candidate;
        }
        return "dotnet";
    }
}
