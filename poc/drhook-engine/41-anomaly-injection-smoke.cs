#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Mcp/DrHook.Mcp.csproj
//
// DrHook.Engine probe 41 — ANOMALY-INJECTION VALIDATION (Phase 1 Validation gate per ADR-007) ==
//
// Validates the EngineAnomaly substrate end-to-end through the MCP-facing surface. The previous
// audit triplet (findings 53/54/55) identified the eleven capture sites; the EA infrastructure
// (EA-1..6, committed 2026-05-24) built type + sink + capture-site wiring + MCP envelope.
// This probe closes the ADR-007 Validation criterion:
//
//   "The EngineAnomaly infrastructure exists, its capture mechanism is validated by a designed
//    probe (intentional anomaly injection exercising the surfacing path), and the surfacing
//    reaches the log sink + MCP response as designed."
//
// Test shape: live target attach via EngineSteppingSession (the MCP-layer holder of the
// BoundedAnomalySink), set breakpoint, hit it, call InspectVariablesAsync(depth=999) to force
// TWO DepthClamped anomalies (one from GetLocals, one from GetArguments — both clamp paths).
// Drain via DrainAnomaliesAsJson (the MCP-tool-facing JSON envelope method). Parse the JSON
// and assert the structured envelope contains:
//   - status = "ok"
//   - count = 2
//   - dropped = 0
//   - capacity = 256
//   - anomalies array with exactly two entries, each:
//       kind = "DepthClamped"
//       thread = "mcp-request"
//       operation in {"GetLocals", "GetArguments"}
//       context.requested = "999"
//       context.clamped = "10"
//
// One of each operation must appear. Both occurrences validate that the per-emission-site
// clamps in DebugSession.GetLocals/GetArguments wire through to the EngineSteppingSession sink.
//
// Falsification: 2 usage/marker; 3 no READY; 4 EngineSteppingSession.LaunchAsync failed;
//   5 LaunchAsync response not "launched"/"attached"; 6 InspectVariablesAsync failed;
//   7 DrainAnomaliesAsJson returned invalid JSON; 8 envelope status != "ok";
//   9 anomaly count != 2; 10 anomaly shape wrong (kind/thread/operation/context);
//   0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 41-anomaly-injection-smoke.cs <path-to-41-anomaly-target.cs>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.DrHook.Mcp;

return await AnomalyInjection41.Run(args);

static class AnomalyInjection41
{
    const string Marker = "ANOMALY_HERE";

    public static async Task<int> Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 41-anomaly-injection-smoke.cs <path-to-41-anomaly-target.cs>");
            return 2;
        }

        int markerLine = FindMarker(args[0], Marker);
        if (markerLine < 0) { Console.Error.WriteLine($"FALSIFIED (usage): '{Marker}' not found."); return 2; }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"plan       : at {Path.GetFileName(args[0])}:{markerLine}, InspectVariablesAsync(depth=999) fires DepthClamped twice (GetLocals + GetArguments); drain via DrainAnomaliesAsJson; assert envelope.");

        using Process proc = new()
        {
            StartInfo = new ProcessStartInfo("dotnet", $"\"{args[0]}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        proc.Start();

        int realPid = -1;
        ManualResetEventSlim ready = new(false);
        Thread reader = new(() =>
        {
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                if (line.StartsWith("READY ", StringComparison.Ordinal) &&
                    int.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
                {
                    Volatile.Write(ref realPid, pid);
                    ready.Set();
                }
            }
        }) { IsBackground = true, Name = "target-stdout" };
        reader.Start();
        Thread errDrain = new(() => { while (proc.StandardError.ReadLine() is not null) { } })
        { IsBackground = true, Name = "target-stderr" };
        errDrain.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(90)))
        {
            Console.Error.WriteLine("FALSIFIED (target): no READY sentinel within 90s.");
            KillTree(proc);
            return 3;
        }
        realPid = Volatile.Read(ref realPid);
        Console.WriteLine($"target pid : {realPid}");

        var session = new EngineSteppingSession();
        int code;
        try
        {
            code = await Drive(session, realPid, args[0], markerLine);
        }
        finally
        {
            try { session.Dispose(); } catch { /* best effort */ }
            KillTree(proc);
        }

        WriteFixture(realPid, code);
        return code;
    }

    static async Task<int> Drive(EngineSteppingSession session, int pid, string sourceFile, int markerLine)
    {
        // ── LaunchAsync — attach to existing pid + set initial breakpoint + run to hit ──────────
        string launchJson = await session.LaunchAsync(pid, sourceFile, markerLine,
            "expect attach + breakpoint hit at ANOMALY_HERE", CancellationToken.None);
        Console.WriteLine($"launch     : {Truncate(launchJson)}");

        using JsonDocument launchDoc = JsonDocument.Parse(launchJson);
        string? launchStatus = launchDoc.RootElement.TryGetProperty("status", out var ls) ? ls.GetString() : null;
        if (launchStatus is not ("attached" or "launched"))
        {
            Console.Error.WriteLine($"FALSIFIED (LaunchAsync): status='{launchStatus ?? "(null)"}', expected 'attached'.");
            return 5;
        }

        // ── InspectVariablesAsync(depth=999) — fires DepthClamped TWICE (GetLocals + GetArguments) ──
        string inspectJson = await session.InspectVariablesAsync(depth: 999,
            "expect depth to clamp to 10 and surface DepthClamped anomalies", CancellationToken.None);
        Console.WriteLine($"inspect    : {Truncate(inspectJson)}");

        using JsonDocument inspectDoc = JsonDocument.Parse(inspectJson);
        if (!inspectDoc.RootElement.TryGetProperty("variableCount", out _))
        {
            Console.Error.WriteLine($"FALSIFIED (InspectVariablesAsync): response has no variableCount field — was: {inspectJson}");
            return 6;
        }

        // ── DrainAnomaliesAsJson — assert the substrate's MCP-facing envelope ────────────────────
        string drainJson = session.DrainAnomaliesAsJson();
        Console.WriteLine($"drain      : {Truncate(drainJson)}");

        JsonDocument drainDoc;
        try { drainDoc = JsonDocument.Parse(drainJson); }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"FALSIFIED (Drain): invalid JSON: {ex.Message}"); return 7;
        }

        using (drainDoc)
        {
            string? status = drainDoc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (status != "ok")
            {
                Console.Error.WriteLine($"FALSIFIED (envelope): status='{status ?? "(null)"}', expected 'ok'."); return 8;
            }

            int count = drainDoc.RootElement.TryGetProperty("count", out var c) ? c.GetInt32() : -1;
            long dropped = drainDoc.RootElement.TryGetProperty("dropped", out var d) ? d.GetInt64() : -1;
            int capacity = drainDoc.RootElement.TryGetProperty("capacity", out var cap) ? cap.GetInt32() : -1;
            Console.WriteLine($"envelope   : count={count}, dropped={dropped}, capacity={capacity}");

            if (count != 2)
            {
                Console.Error.WriteLine($"FALSIFIED (count): expected 2 DepthClamped anomalies (one per GetLocals + GetArguments), got {count}."); return 9;
            }
            if (dropped != 0)
            {
                Console.Error.WriteLine($"FALSIFIED (dropped): expected 0, got {dropped} (capacity {capacity} should be ample for two records)."); return 9;
            }
            if (capacity != 256)
            {
                Console.Error.WriteLine($"FALSIFIED (capacity): expected 256, got {capacity}."); return 9;
            }

            JsonElement anomalies = drainDoc.RootElement.GetProperty("anomalies");
            HashSet<string> operationsSeen = new();
            foreach (JsonElement a in anomalies.EnumerateArray())
            {
                string? kind = a.TryGetProperty("kind", out var k) ? k.GetString() : null;
                string? thread = a.TryGetProperty("thread", out var t) ? t.GetString() : null;
                string? operation = a.TryGetProperty("operation", out var op) ? op.GetString() : null;
                string? requested = a.TryGetProperty("context", out var ctx) && ctx.TryGetProperty("requested", out var r) ? r.GetString() : null;
                string? clamped = ctx.ValueKind == JsonValueKind.Object && ctx.TryGetProperty("clamped", out var cl) ? cl.GetString() : null;

                Console.WriteLine($"  anomaly  : kind={kind} thread={thread} operation={operation} requested={requested} clamped={clamped}");

                if (kind != "DepthClamped")
                {
                    Console.Error.WriteLine($"FALSIFIED (kind): expected 'DepthClamped', got '{kind}'."); return 10;
                }
                if (thread != "mcp-request")
                {
                    Console.Error.WriteLine($"FALSIFIED (thread): expected 'mcp-request', got '{thread}'."); return 10;
                }
                if (operation is not ("GetLocals" or "GetArguments"))
                {
                    Console.Error.WriteLine($"FALSIFIED (operation): expected 'GetLocals' or 'GetArguments', got '{operation}'."); return 10;
                }
                if (requested != "999")
                {
                    Console.Error.WriteLine($"FALSIFIED (context.requested): expected '999', got '{requested}'."); return 10;
                }
                if (clamped != "10")
                {
                    Console.Error.WriteLine($"FALSIFIED (context.clamped): expected '10', got '{clamped}'."); return 10;
                }

                operationsSeen.Add(operation);
            }

            if (operationsSeen.Count != 2 || !operationsSeen.Contains("GetLocals") || !operationsSeen.Contains("GetArguments"))
            {
                Console.Error.WriteLine($"FALSIFIED (coverage): expected one anomaly each from GetLocals and GetArguments; saw operations {string.Join(",", operationsSeen)}."); return 10;
            }
        }

        string stopJson = await session.StopAsync(CancellationToken.None);
        Console.WriteLine($"stop       : {Truncate(stopJson)}");

        Console.WriteLine($"\nPROBE 41 PASSED — EngineAnomaly substrate validated end-to-end: capture site (DebugSession.GetLocals/GetArguments) → BoundedAnomalySink → DrainAnomaliesAsJson envelope reaches MCP-shaped JSON with all expected fields. ADR-007 Phase 1 Validation gate is closed.");
        return 0;
    }

    static int FindMarker(string path, string marker)
    {
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains(marker, StringComparison.Ordinal))
                return i + 1;
        return -1;
    }

    static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    static string Truncate(string s, int max = 200)
        => s.Length <= max ? s.Replace("\n", " ").Replace("\r", "") : s[..max].Replace("\n", " ").Replace("\r", "") + "…";

    static void WriteFixture(int pid, int code)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"41-anomaly-injection-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 41 fixture — EngineAnomaly designed-injection validation (Phase 1 Validation gate)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"injection        = InspectVariablesAsync(depth=999) → DepthClamped from GetLocals + GetArguments\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
