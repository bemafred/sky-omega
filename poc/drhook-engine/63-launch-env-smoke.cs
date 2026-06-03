#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 63 — Launch ENV override (Owned) ==========================================
//
// The MCP advertised drhook_launch `env` but the substrate SILENTLY DROPPED it: EngineSteppingSession
// received the dictionary and never passed it to DebugSession.Launch ("env override is not yet plumbed
// through DebugSession.Launch"), so the launched child inherited the MCP server's environment and a
// per-launch override had no effect. This probe validates the now-implemented passthrough:
// DebugSession.Launch threads env -> LaunchWithDebuggerPosix -> SpawnSuspendedRedirected, which builds
// the child's envp as inherit-plus-override (BuildChildEnv). Asserts BOTH the override took effect AND
// an inherited var survived (i.e. merge, not replace).
//
// Env is Owned-only by nature: a Borrowed (attached) target's environment is fixed at its own spawn.
//
// Falsification: 2 target not built; 3 Launch failed; 4 target never wrote the file;
//   5 override NOT applied; 6 inherited var lost (replace instead of merge); 0 PASS.
//
// Usage: dotnet run --no-cache 63-launch-env-smoke.cs   (build 63-env-target first)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Env63.Run();

sealed class NullSink : IDebugEventSink { public void OnEvent(string name) { } }

static class Env63
{
    const string TargetDir = "63-env-target";
    const string TargetDll = "EnvTarget.dll";

    public static int Run()
    {
        string scriptDir = Directory.Exists(Path.Combine(Environment.CurrentDirectory, TargetDir))
            ? Environment.CurrentDirectory : Environment.CurrentDirectory;
        string targetDll = Path.Combine(scriptDir, TargetDir, "bin", "Debug", "net10.0", TargetDll);
        if (!File.Exists(targetDll))
        {
            Console.Error.WriteLine($"FALSIFIED (target not built): {targetDll} — run `dotnet build {Path.Combine(scriptDir, TargetDir)}` first.");
            return 2;
        }

        string outFile = Path.Combine(Path.GetTempPath(), $"drhook-p63-env-{Environment.ProcessId}.txt");
        try { if (File.Exists(outFile)) File.Delete(outFile); } catch { /* fresh */ }
        string expected = $"override-{Environment.ProcessId}";

        string dotnet = ResolveDotnet();
        var env = new Dictionary<string, string> { ["DRHOOK_PROBE_ENV"] = expected };

        Console.WriteLine($"runtime : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"plan    : Launch EnvTarget with env override DRHOOK_PROBE_ENV={expected}; assert the child sees the override AND keeps inherited HOME.");

        DebugSession session;
        try { session = DebugSession.Launch(dotnet, new[] { targetDll, outFile }, workingDirectory: null, sink: new NullSink(), env: env); }
        catch (Exception ex) { Console.Error.WriteLine($"FALSIFIED (Launch): {ex.GetType().Name}: {ex.Message}"); return 3; }
        int pid = session.ProcessId;
        Console.WriteLine($"launched: pid={pid}");

        // The target writes the file early and exits; poll briefly for the complete record.
        string? content = null;
        for (int i = 0; i < 100; i++)
        {
            try { if (File.Exists(outFile)) { content = File.ReadAllText(outFile); if (content.Contains("HOME_PRESENT")) break; } } catch { /* mid-write */ }
            Thread.Sleep(50);
        }

        try { session.Dispose(); } catch { /* best effort */ }
        try { if (!Process.GetProcessById(pid).HasExited) Process.GetProcessById(pid).Kill(); } catch { /* already gone */ }

        if (string.IsNullOrEmpty(content))
        {
            Console.Error.WriteLine("FALSIFIED: target never wrote the env file.");
            return 4;
        }
        Console.WriteLine($"child wrote:\n  {content.TrimEnd().Replace("\n", "\n  ")}");

        if (!content.Contains($"DRHOOK_PROBE_ENV={expected}"))
        {
            Console.Error.WriteLine($"FALSIFIED: env override NOT applied — expected DRHOOK_PROBE_ENV={expected} (the silent-drop bug).");
            return 5;
        }
        if (!content.Contains("HOME_PRESENT=True"))
        {
            Console.Error.WriteLine("FALSIFIED: inherited HOME lost — env was REPLACED, not merged (inherit-plus-override broken).");
            return 6;
        }

        Console.WriteLine($"\nPROBE 63 PASSED — Launch env override reached the child (DRHOOK_PROBE_ENV={expected}) AND inherited HOME survived (inherit-plus-override). Owned-launch env passthrough validated on macOS-arm64.");
        try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
        return 0;
    }

    static string ResolveDotnet()
    {
        string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        string? root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir)));
        if (root is not null) { string c = Path.Combine(root, "dotnet"); if (File.Exists(c)) return c; }
        return "dotnet";
    }
}
