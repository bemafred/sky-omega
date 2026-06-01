#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 61 — POST-EXIT STACK-FRAME WALK SAFETY (finding 77)
// =======================================================================
//
// Substrate claim: after the debuggee EXITS, DebugSession.GetStackFrames() returns an empty list
// WITHOUT throwing. The ADR-011 D3 live drain_log smoke surfaced a crash: a suspend=none logpoint
// ran to program end, drhook_continue's WaitForStop returned StopReason.ProcessExited, and the MCP
// response builder called GetStackFrames() → Frames.WalkManagedFrames(_pump.StopThread). The pump
// left _stopThread STALE on ExitProcess (it cleared it only on Pause and reassigned it on each
// stop), so StopThread returned the last stop's now-dead ICorDebugThread, walked PAST
// WalkManagedFrames' (pThread == 0) guard, and dereferenced a released thread → NRE. Fix:
// CallbackPump clears _stopThread = 0 on ExitProcess, so the existing guard returns empty post-exit.
//
// Construction: DebugSession.Launch spawns the built Launch.dll; after the Debugger.Break setup
// stop, Resume with NO breakpoint armed → the 100×20ms loop runs to completion → WaitForStop
// returns ProcessExited. THEN call GetStackFrames() and assert it returns 0 frames and does not
// throw. Pre-fix this call NRE'd; post-fix it returns empty.
//
// Falsification: 2 dll missing; 4 Launch; 5 no setup Break; 6 resume did not reach ProcessExited;
//   7 GetStackFrames threw (the NRE — fix absent) or returned non-empty (a dead process has no
//   frames); 0 PASS.
//
// Usage:  dotnet 61-post-exit-frames-smoke.cs        (run from poc/drhook-engine)

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SkyOmega.DrHook.Engine;

return PostExitFrames61.Run();

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { /* informational stream — ignored; OnLog/OnAnomaly/OnConsoleOutput default no-op */ }
}

static class PostExitFrames61
{
    const string DllRel = "33-launch-target/bin/Debug/net10.0/Launch.dll";

    public static int Run()
    {
        string dll = Path.GetFullPath(DllRel);
        if (!File.Exists(dll)) { Console.Error.WriteLine($"FALSIFIED (missing dll): {dll}. Build 33-launch-target first."); return 2; }
        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"target     : {dll}");

        DebugSession session;
        try { session = DebugSession.Launch("dotnet", new[] { "exec", dll }, Path.GetDirectoryName(dll), new NullSink()); }
        catch (Exception ex) { Console.Error.WriteLine($"FALSIFIED (Launch): {ex.GetType().Name}: {ex.Message}"); return 4; }
        Console.WriteLine($"launched   : pid {session.ProcessId}");

        int code = Drive(session);
        try { session.Dispose(); } catch { /* best effort — process already exited */ }
        return code;
    }

    static int Drive(DebugSession session)
    {
        StopInfo? setup = session.WaitForStop(TimeSpan.FromSeconds(15));
        if (setup is null || setup.Reason != StopReason.Break)
        {
            Console.Error.WriteLine($"FALSIFIED (no setup stop): {(setup is null ? "timeout" : setup.Reason.ToString())}.");
            return 5;
        }
        Console.WriteLine($"setup stop : {setup.Reason}");

        // No breakpoint armed — resume and let the 100×20ms loop run to completion (~2s).
        session.Resume();
        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (stop is null || stop.Reason != StopReason.ProcessExited)
        {
            Console.Error.WriteLine($"FALSIFIED (did not reach ProcessExited): {(stop is null ? "timeout" : stop.Reason.ToString())}.");
            return 6;
        }
        Console.WriteLine($"exited     : {stop.Reason}");

        // THE FIX UNDER TEST: GetStackFrames after exit must return empty, not NRE on a stale thread.
        IReadOnlyList<string> frames;
        try { frames = session.GetStackFrames(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED: GetStackFrames threw after ProcessExited — {ex.GetType().Name}: {ex.Message}. The stale _stopThread was walked past the (pThread==0) guard (the pre-fix NRE).");
            return 7;
        }
        Console.WriteLine($"frames     : count={frames.Count} (expected 0 — a dead process has no frames)");
        if (frames.Count != 0)
        {
            Console.Error.WriteLine($"FALSIFIED: expected 0 frames after exit, got {frames.Count}.");
            return 7;
        }

        Console.WriteLine($"\nPROBE 61 PASSED — GetStackFrames() returned empty without throwing after the debuggee exited. CallbackPump clears _stopThread on ExitProcess; WalkManagedFrames' (pThread==0) guard now holds post-exit. The drhook_continue ProcessExited NRE (finding 77) is removed at the substrate.");
        return 0;
    }
}
