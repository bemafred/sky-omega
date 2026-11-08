#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
#:property AllowUnsafeBlocks=true
#:package Microsoft.Diagnostics.NETCore.Client@0.2.750501
//
// DrHook.Engine probe 107 — can the runtime's own startup pause replace the Linux launch shell? =======================
//
// LinuxSpawn holds a launched child with `/bin/sh -c 'kill -STOP $$; exec "$0" "$@"'` so that dbgshim's
// RegisterForRuntimeStartup semaphores exist before the runtime checks for them. The candidate replacement is the
// runtime's diagnostics pause: with DOTNET_DiagnosticPorts=<path>,connect,suspend the runtime dials <path> early in
// EEStartup and waits for ResumeRuntime. ipc-protocol.md: "Ports specifying `suspend` ... pause early on in the startup
// path before most runtime subsystems have started." release/10.0 ceemain.cpp, EEStartupHelper (read from source by
// sky-omega): PauseForDiagnosticsMonitor, then JitHost::Init, then InitializeDebugger (PAL_NotifyRuntimeStarted is under
// it). Nothing in the engine is changed; this probe speaks to libdbgshim and the diagnostics IPC itself.
//
// Wire layouts, from the runtime source (dotnet/runtime release/10.0, src/native/eventpipe/, read 2026-11-08; rev 4
// took them from ipc-protocol.md, whose "OK: no payload" is wrong, and stopped on it in hold run 1):
//   advertise (runtime -> server on connect): 34 bytes = char[8] "ADVR_V1\0", GUID cookie (16), uint64 pid (LE),
//     uint16 zeroed.
//   header (ds-protocol.h): 20 bytes = uint8[14] "DOTNET_IPC_V1\0", uint16 size (LE, header + payload),
//     uint8 commandset, uint8 commandid, uint16 reserved (0).
//   ResumeRuntime (ds-types.h): DS_SERVER_COMMANDSET_PROCESS 0x04, DS_PROCESS_COMMANDID_RESUME_RUNTIME 0x01, no payload.
//   Replies (ds-protocol.c ds_ipc_message_send_success / _send_error, via
//     ds_ipc_message_initialize_header_int32_t_payload): DS_SERVER_COMMANDSET_SERVER 0xFF with DS_SERVER_RESPONSEID_OK
//     0x00 or DS_SERVER_RESPONSEID_ERROR 0xFF, size 24: the header plus an int32 code, on both. The code is data, not
//     layout: OK with a non-zero code is reported as "OK code 0x...", not a layout failure.
//   "Each command consumes a connection, and the runtime will reconnect": one command per connection; connections
//   are told apart by the advertised pid. A re-dial is kept open, without a command, so the runtime waits on it
//   rather than dialling in a loop; it is counted.
//   Re-dial timing (ds-ipc.c ipc_stream_factory_get_next_timeout; ds-ipc-pal-types.h): after a failed connect the
//     poll timeout starts at DS_IPC_POLL_TIMEOUT_MIN_MS 10 and grows by DS_IPC_POLL_TIMEOUT_FALLOFF_FACTOR 1.25
//     (truncated: 10, 12, 15, ... 366, 457, 571) until it is >= DS_IPC_POLL_TIMEOUT_MAX_MS 500, then stays at 500.
//     The one 571 ms step falls about 2.6 s after the listener goes; by 30 s the step is 500 ms.
// Every magic, set, id and size is checked; a mismatch ends the probe with exit 2 (the layout is wrong, not the
// hypothesis), through the same containment as every other end (below).
//
// Questions (from the 2026-11-07 discussion with sky-omega):
//   Q1  Is the pause before the runtime's check for dbgshim's startup semaphore? (registering 2 s into the pause
//       must still catch the startup)
//   Q2  Does any managed code run before the resume? (the target's [ModuleInitializer] and a DOTNET_STARTUP_HOOKS
//       hook print marks; neither may come before the probe's ResumeRuntime)
//   Q3  The pause is inherited: do the target's .NET children pause, and what does a child do with no listener?
//   Q3b Is a runtimeconfig property a channel the children would not inherit? (speculative)
//   Q5  Does dbgshim take its already-loaded path for a paused runtime (callback during the pause)?
//   Q6  Does the default listen port exist during the pause, and does a ResumeRuntime over it (the public
//       DiagnosticsClient) release the pause? (side test, after Register, outcome kept out of Q1's tally)
//
// Modes and predictions:
//   hold (10)            spawn paused; accept; wait 2 s; Register; hold 2 s; [Q6 in runs 1-5]; ResumeRuntime.
//                        Predicted (Q1): the callback only after the resume, with S_OK, and not after main, in 10/10.
//                        Falsified by any missed. (Q2): no modinit or hook before the resume; falsified by any
//                        managed-before-resume.
//                        Predicted end: held-stopped-before-main 10/10. release/10.0 debug/ee/debugger.cpp, end of
//                        Debugger::Startup (TARGET_UNIX): `if (PAL_NotifyRuntimeStarted()) MarkDebuggerAttachedInternal();`
//                        ("The runtime was successfully launched and attached so mark it now so no notifications are
//                        missed especially the initial module load"). An S_OK startup callback leaves the runtime
//                        believing a debugger is attached; the probe releases pCordb without Initialize or
//                        DebugActiveProcess, so no right side connects, and (sky-omega's inference) the runtime stops at
//                        its first debug event, before main. Every thread's stat and wchan are recorded before the
//                        kill, which is this mode's predicted escape hatch. held-reached-main falsifies the inference.
//                        A natural end needs the attach completed: stage 2, with DrHook.Engine.
//   hold-control (10)    no pause; Register 2 s after the spawn. Predicted: missed 10/10 (main before the callback,
//                        or no callback). With hold, this shows the pause is what makes the difference.
//   child-listener (3)   target in spawn-child, held and resumed; the child dials (told apart by pid), is held 5 s,
//                        then resumed. Predicted: the child pauses; DOTNET_DiagnosticPorts inherited unchanged.
//   child-no-listener (3) target resumed; listener closed and unlinked; then the go marker. The child is watched
//                        30 s; then the path is bound again. Predicted: no child mark for 30 s, and a re-dial within
//                        ~0.5 s of the path's return (by then the re-dial step is DS_IPC_POLL_TIMEOUT_MAX_MS, 500 ms;
//                        see Re-dial timing above). The child is then resumed.
//   runtimeconfig-property (1)  `dotnet exec --runtimeconfig <json>` with the target's own runtimeconfig plus
//                        configProperties DOTNET_DiagnosticPorts, no env var. Predicted: no dial.
//
// Containment: processes end naturally within each bound, except a held target in hold, whose kill after its bound
// is the predicted end (held-stopped-before-main, above); only what is still alive afterwards is killed, matched
// by pid and start time (/proc/<pid>/stat field 22), and every kill is part of the run's outcome. dbgshim's semaphores
// are removed with UnregisterForRuntimeStartup, always. Every spawn is logged with its pid and command line first.
// The whole run is inside a finally (rev 5; rev 4's exit-2 path skipped all of this and left a target stuck in
// startup), which, however the probe ends:
//   1. logs every thread's state and wchan for each tracked process still alive (that state is data);
//   2. kills those processes, matched by pid and start time;
//   3. calls UnregisterForRuntimeStartup for every token not yet unregistered;
//   4. removes only the probe's own leftovers, named by a tracked pid and its start time: /dev/shm/sem.clrst and
//      sem.clrco<pid:x8><start:x16>, /tmp/clr-debug-pipe-<pid>-<start>-*, /tmp/dotnet-diagnostic-<pid>-<start>-*,
//      and the probe's p107 sockets. Never a sweep of /dev/shm: another dbgshim user (DrHook) may own entries there.
//      Each removal is logged, with whether its process was killed (expected) or exited (a finding);
//   5. after an early stop, prints the tally so far and where it stopped.
// Tracked: every target and child the probe spawns or reads a pid for, and every pid that dials a probe socket.
//
// Usage: dotnet run --no-cache 107-launch-hold-smoke.cs [mode ...]   (default: all modes, in the order above)
//        DBGSHIM_PATH=<libdbgshim.so> overrides the NuGet lookup.
// Exit: 0 hold held 10/10 and control missed 10/10; 1 otherwise; 2 setup, wire-layout or probe failure.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Diagnostics.NETCore.Client;

return Probe107.Run(args);

static unsafe class Probe107
{
    static readonly string[] AllModes = ["hold", "hold-control", "child-listener", "child-no-listener", "runtimeconfig-property"];
    static readonly Dictionary<string, int> Runs = new()
    {
        ["hold"] = 10, ["hold-control"] = 10, ["child-listener"] = 3, ["child-no-listener"] = 3, ["runtimeconfig-property"] = 1,
    };

    static string s_targetDll = "", s_hookDll = "";
    static delegate* unmanaged[Cdecl]<uint, nint, nint, nint*, int> s_register;
    static delegate* unmanaged[Cdecl]<nint, int> s_unregister;
    static readonly ConcurrentQueue<Callback> s_callbacks = new();

    public static int Run(string[] args)
    {
        string[] modes = args.Length > 0 ? args : AllModes;
        foreach (string m in modes)
            if (!Runs.ContainsKey(m)) { Say($"unknown mode {m}"); return 2; }

        Say($"probe 107 rev 5 on {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, runtime {Environment.Version}");
        string dir = Path.GetDirectoryName(SourcePath())!;
        string? target = BuildDll(Path.Combine(dir, "107-launch-hold-target.cs"));
        string? hook = BuildDll(Path.Combine(dir, "107-launch-hold-hook.cs"));
        if (target is null || hook is null) { Say("setup: build failed"); return 2; }
        s_targetDll = target;
        s_hookDll = hook;
        Say($"target {s_targetDll}");
        Say($"hook   {s_hookDll}");

        string? shim = FindDbgShim();
        if (shim is null) { Say("setup: libdbgshim not found (set DBGSHIM_PATH)"); return 2; }
        nint lib = NativeLibrary.Load(shim);
        s_register = (delegate* unmanaged[Cdecl]<uint, nint, nint, nint*, int>)NativeLibrary.GetExport(lib, "RegisterForRuntimeStartup");
        s_unregister = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(lib, "UnregisterForRuntimeStartup");
        Say($"dbgshim {shim}");

        var tally = new Dictionary<string, List<string>>();
        string? current = null, stoppedBy = null;
        try
        {
            foreach (string mode in modes)
            {
                var outcomes = tally[mode] = new List<string>();
                for (int i = 1; i <= Runs[mode]; i++)
                {
                    current = $"{mode} run {i}";
                    Say($"{mode} run {i}:");
                    string outcome = mode switch
                    {
                        "hold" => Hold(i, sideTest: i <= 5),
                        "hold-control" => HoldControl(i),
                        "child-listener" => ChildListener(i),
                        "child-no-listener" => ChildNoListener(i),
                        _ => RuntimeconfigProperty(i),
                    };
                    outcomes.Add(outcome);
                    Say($"{mode} run {i}: {outcome}");
                }
            }
            current = null;
        }
        catch (WireLayoutException e) { stoppedBy = $"wire layout mismatch: {e.Message}"; }
        catch (Exception e) { stoppedBy = $"probe failure: {e}"; }
        finally
        {
            if (stoppedBy is not null) Say(stoppedBy);
            Contain();
        }

        Say("");
        foreach (var (mode, outcomes) in tally)
            Say($"{mode}: " + (outcomes.Count == 0 ? "no run completed" : string.Join("; ", outcomes.GroupBy(o => o).Select(g => $"{g.Key}: {g.Count()}"))));
        if (stoppedBy is not null)
        {
            Say($"stopped in {current ?? "setup"}; the tally above is the runs completed before it; exit 2");
            return 2;
        }

        bool holdOk = !tally.TryGetValue("hold", out var h) || h.All(o => o.StartsWith("held", StringComparison.Ordinal) || o.StartsWith("released-by-default-port", StringComparison.Ordinal));
        bool holdAnyHeld = h is null || h.Any(o => o.StartsWith("held", StringComparison.Ordinal));
        bool controlOk = !tally.TryGetValue("hold-control", out var c) || c.All(o => o.StartsWith("missed", StringComparison.Ordinal));
        if (h is not null) Say($"hold (Q1): predicted held in every run counted (released-by-default-port runs excluded): {(holdOk && holdAnyHeld ? "holds" : "falsified")}");
        if (h is not null && h.Any(o => o.StartsWith("held", StringComparison.Ordinal)))
            Say($"hold (end): predicted held-stopped-before-main in every held run: {(h.Where(o => o.StartsWith("held", StringComparison.Ordinal)).All(o => o.StartsWith("held-stopped-before-main", StringComparison.Ordinal)) ? "holds" : "falsified")}");
        if (c is not null) Say($"hold-control: predicted missed every run: {(controlOk ? "holds" : "falsified")}");
        return holdOk && holdAnyHeld && controlOk ? 0 : 1;
    }

    // ---- hold: Q1, Q2, Q5, Q6 --------------------------------------------------------------------------------------

    static string Hold(int run, bool sideTest)
    {
        string sock = SocketPath($"hold{run}");
        using var server = new ReverseServer(sock);
        using var t = Target.Start(["plain", "4000"], diagPorts: $"{sock},connect,suspend", hook: true, stdin: false);
        long tSpawn = Now();
        var adv = server.WaitFor(cn => cn.Pid == t.Pid, TimeSpan.FromSeconds(10));
        if (adv is null) return Finish(t, null, "setup-failed-no-dial");
        Say($"    advertise from {adv.Pid} after {Ms(tSpawn, adv.Ticks)} ms, cookie {adv.Cookie}; {Describe(t.Pid)}; default port {DefaultPortState(t.Pid)}");

        Thread.Sleep(2000);
        long tReg = Now();
        int hr = Register(t.Pid, run, out nint token);
        Say($"    RegisterForRuntimeStartup 2 s into the pause: 0x{hr:X8}; marks so far: {t.MarksSummary()}; semaphores: {Semaphores(t.Pid)}");

        Thread.Sleep(2000);
        var during = CallbacksFor(run).Where(cb => cb.Ticks <= Now()).ToList();
        Say($"    after holding 2 s more: callbacks {Format(during)}; marks so far: {t.MarksSummary()}; {Describe(t.Pid)}");

        // Q6: after Register, so a release here still comes after the semaphores exist.
        if (sideTest)
        {
            string port = DefaultPortState(t.Pid);
            long tq6 = Now();
            string q6 = DefaultPortResume(t.Pid);
            long q6CallMs = Ms(tq6, Now());                     // the call alone; rev 4 timed it with the 2 s wait
            Thread.Sleep(2000);
            // A release shows as a managed mark, or (Register came first, so the runtime goes on to InitializeDebugger,
            // calls back S_OK, is marked attached and stops before any managed code) as a callback after tq6.
            var q6Callbacks = CallbacksFor(run).Where(cb => cb.Ticks >= tq6).ToList();
            bool released = t.Mark("modinit") is not null || t.Mark("hook") is not null || t.Mark("main") is not null || q6Callbacks.Count > 0;
            Say($"    Q6: default port {port}; DiagnosticsClient.ResumeRuntime: {q6} [call {q6CallMs} ms]; 2 s later the pause {(released ? "is RELEASED" : "holds")} (callbacks since: {Format(q6Callbacks)}; marks: {t.MarksSummary()})");
            if (released)
            {
                if (!t.WaitForExit(TimeSpan.FromSeconds(15)))
                    Say($"    not ended within 15 s of the Q6 release; where its threads wait:\n{Threads(t.Pid)}");
                Unregister(token, t.Pid);
                return Finish(t, CallbacksFor(run).ToList(), "released-by-default-port");
            }
        }

        long tResume = Now();
        string reply = server.Resume(adv);
        Say($"    ResumeRuntime over the reverse connection: {reply}");
        Thread.Sleep(300);
        string portAfter = DefaultPortState(t.Pid);
        bool exited = t.WaitForExit(TimeSpan.FromSeconds(15));
        if (exited) Thread.Sleep(1000);                         // a late callback, after the exit, is data too
        else Say($"    not ended within 15 s of the resume; where its threads wait:\n{Threads(t.Pid)}");
        var cbs = CallbacksFor(run).ToList();
        Unregister(token, t.Pid);
        int redials = server.CountFrom(t.Pid) - 1;
        Say($"    callbacks {Format(cbs)}; marks: {t.MarksSummary()}; re-dials after the resume: {redials}; default port while running: {portAfter}");

        long? modinit = t.Mark("modinit"), hookMark = t.Mark("hook"), main = t.Mark("main");
        if ((modinit ?? long.MaxValue) < tResume || (hookMark ?? long.MaxValue) < tResume || (main ?? long.MaxValue) < tResume)
            return Finish(t, cbs, "managed-before-resume");
        var early = cbs.FirstOrDefault(cb => cb.Ticks < tResume);
        if (early is not null) return Finish(t, cbs, $"early-callback-0x{early.Hr:X8}");
        var after = cbs.FirstOrDefault();
        if (after is null) return Finish(t, cbs, "missed-no-callback");
        if (after.Hr != 0) return Finish(t, cbs, $"missed-hr-0x{after.Hr:X8}");
        if (main is not null && main < after.Ticks) return Finish(t, cbs, "missed-main-first");
        // Q1 holds from here (an S_OK callback after the resume, main not before it); the suffix is how the run ended.
        // held-stopped-before-main only for a target still alive at the bound; one that exited or crashed before main
        // within it is held-ended-before-main.
        return Finish(t, cbs, main is not null ? "held-reached-main" : exited ? "held-ended-before-main" : "held-stopped-before-main");
    }

    // ---- hold-control: no pause, Register 2 s after the spawn ------------------------------------------------------

    static string HoldControl(int run)
    {
        int ctx = 1000 + run;
        using var t = Target.Start(["plain", "4000"], diagPorts: null, hook: true, stdin: false);
        long tSpawn = Now();
        Thread.Sleep(2000);
        int hr = Register(t.Pid, ctx, out nint token);
        Say($"    RegisterForRuntimeStartup 2 s after the spawn: 0x{hr:X8}; marks so far: {t.MarksSummary()}; {Describe(t.Pid)}");
        bool exited = t.WaitForExit(TimeSpan.FromSeconds(15));
        if (exited) Thread.Sleep(1000);
        var cbs = CallbacksFor(ctx).ToList();
        Unregister(token, t.Pid);
        long? main = t.Mark("main");
        Say($"    callbacks {Format(cbs)}; marks: {t.MarksSummary()}; spawn to main: {(main is { } m ? Ms(tSpawn, m) + " ms" : "none")}");
        var first = cbs.FirstOrDefault();
        if (first is null) return Finish(t, cbs, "missed-no-callback");
        if (first.Hr != 0) return Finish(t, cbs, $"missed-hr-0x{first.Hr:X8}");
        if (main is not null && main < first.Ticks) return Finish(t, cbs, "missed-main-first");
        return Finish(t, cbs, "held");
    }

    // ---- child-listener: Q3 --------------------------------------------------------------------------------------

    static string ChildListener(int run)
    {
        // No RegisterForRuntimeStartup here: an S_OK callback would mark a debugger attached and stop the target before
        // main (see hold). The target is held by the diagnostics pause alone, resumed, and ends by itself.
        string sock = SocketPath($"cl{run}");
        using var server = new ReverseServer(sock);
        using var t = Target.Start(["spawn-child"], diagPorts: $"{sock},connect,suspend", hook: true, stdin: true);
        var adv = server.WaitFor(cn => cn.Pid == t.Pid, TimeSpan.FromSeconds(10));
        if (adv is null) return Finish(t, null, "setup-failed-no-dial");
        Say($"    target {t.Pid} dialled; resume: {server.Resume(adv)}");
        if (!t.WaitForMark("main", TimeSpan.FromSeconds(10))) return Finish(t, null, "setup-failed-no-main");

        long tGo = Now();
        t.SendLine("go");
        var cadv = server.WaitFor(cn => cn.Pid != t.Pid, TimeSpan.FromSeconds(10));
        Proc? child = null;
        string result;
        if (cadv is null)
        {
            result = "child-did-not-dial";
        }
        else
        {
            child = Proc.Of(cadv.Pid, "child");
            Say($"    child {cadv.Pid} dialled {Ms(tGo, cadv.Ticks)} ms after go; its environ: {Environ(cadv.Pid)}; {Describe(cadv.Pid)}");
            Thread.Sleep(5000);
            bool ran = t.Mark("child: modinit") is not null || t.Mark("child: hook") is not null || t.Mark("child: main") is not null;
            Say($"    after holding the child 5 s: {(ran ? "child managed code RAN" : "no child mark")}; resume child: {server.Resume(cadv)}");
            result = ran ? "child-not-paused" : "child-paused";
        }
        t.WaitForExit(TimeSpan.FromSeconds(20));
        Say($"    target view: {t.Line("env ")}; child view: {t.Line("child: env ")}; re-dials target {server.CountFrom(t.Pid) - 1}, child {(cadv is null ? 0 : server.CountFrom(cadv.Pid) - 1)}; marks: {t.MarksSummary()}");
        string inherited = t.Line("child: env ")?.Contains(sock, StringComparison.Ordinal) == true ? "inherited" : "not-inherited";
        return Finish(t, null, $"{result}-{inherited}", child);
    }

    // ---- child-no-listener: the DrHook-crash case ------------------------------------------------------------------

    static string ChildNoListener(int run)
    {
        // No RegisterForRuntimeStartup, as in child-listener.
        string sock = SocketPath($"cn{run}");
        var server = new ReverseServer(sock);
        using var t = Target.Start(["spawn-child"], diagPorts: $"{sock},connect,suspend", hook: true, stdin: true);
        var adv = server.WaitFor(cn => cn.Pid == t.Pid, TimeSpan.FromSeconds(10));
        if (adv is null) { server.Dispose(); return Finish(t, null, "setup-failed-no-dial"); }
        Say($"    target {t.Pid} dialled; resume: {server.Resume(adv)}");
        if (!t.WaitForMark("main", TimeSpan.FromSeconds(10))) { server.Dispose(); return Finish(t, null, "setup-failed-no-main"); }

        server.Dispose();                                       // listener closed, every connection closed, path unlinked
        Say($"    listener closed and {sock} unlinked; sending go");
        t.SendLine("go");
        if (!t.WaitForLine("child-pid ", TimeSpan.FromSeconds(10), out string? line)) return Finish(t, null, "setup-failed-no-child");
        int cpid = int.Parse(line!["child-pid ".Length..], CultureInfo.InvariantCulture);
        Proc child = Proc.Of(cpid, "child");
        for (int s = 5; s <= 30; s += 5)
        {
            Thread.Sleep(5000);
            Say($"    {s} s: child {Describe(cpid)}; child marks: {t.MarksSummary("child: ")}");
        }
        bool ranWithout = t.Mark("child: modinit") is not null || t.Mark("child: main") is not null;

        using var server2 = new ReverseServer(sock);
        long tRebind = Now();
        var cadv = server2.WaitFor(cn => cn.Pid == cpid, TimeSpan.FromSeconds(10));
        string result = ranWithout ? "child-ran-without-listener"
            : cadv is null ? "child-blocked-no-redial"
            : $"child-blocked-redialled-{Ms(tRebind, cadv.Ticks)}ms";
        if (cadv is not null) Say($"    path bound again; child re-dialled after {Ms(tRebind, cadv.Ticks)} ms; resume child: {server2.Resume(cadv)}");
        t.WaitForExit(TimeSpan.FromSeconds(15));
        Say($"    target re-dials to the new listener: {server2.CountFrom(t.Pid)}; marks: {t.MarksSummary()}");
        return Finish(t, null, result, child);
    }

    // ---- runtimeconfig-property: Q3b -----------------------------------------------------------------------------

    static string RuntimeconfigProperty(int run)
    {
        string sock = SocketPath($"rc{run}");
        using var server = new ReverseServer(sock);
        string own = Path.ChangeExtension(s_targetDll, ".runtimeconfig.json");
        JsonNode config = JsonNode.Parse(File.ReadAllText(own))!;
        JsonObject options = (JsonObject)config["runtimeOptions"]!;
        if (options["configProperties"] is not JsonObject props) options["configProperties"] = props = new JsonObject();
        props["DOTNET_DiagnosticPorts"] = $"{sock},connect,suspend";
        string json = Path.Combine(Path.GetTempPath(), $"p107-{Environment.ProcessId}-rc{run}.runtimeconfig.json");
        File.WriteAllText(json, config.ToJsonString());
        try
        {
            using var t = Target.Start(["plain", "0"], diagPorts: null, hook: false, stdin: false, runtimeconfig: json);
            var adv = server.WaitFor(cn => cn.Pid == t.Pid, TimeSpan.FromSeconds(5));
            if (adv is not null) Say($"    dialled; resume: {server.Resume(adv)}");
            t.WaitForExit(TimeSpan.FromSeconds(10));
            Say($"    marks: {t.MarksSummary()}; view: {t.Line("env ")}");
            return Finish(t, null, adv is null ? "no-dial" : "dialled-and-paused");
        }
        finally { File.Delete(json); }
    }

    // ---- dbgshim ---------------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnRuntimeStartup(nint pCordb, nint context, int hr)
    {
        long ticks = DateTime.UtcNow.Ticks;
        bool nonNull = pCordb != 0;
        if (nonNull) Marshal.Release(pCordb);                  // the callback owns one reference; the probe needs none
        s_callbacks.Enqueue(new Callback((int)context, ticks, hr, nonNull));
    }

    static int Register(int pid, int context, out nint token)
    {
        nint t = 0;
        int hr = s_register((uint)pid, (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, void>)&OnRuntimeStartup, context, &t);
        token = t;
        if (t != 0) Tracked.Tokens[t] = pid;
        return hr;
    }

    // Lists every sem.clr* matching the pid (both forms, so a different naming shows up), but removes only the two
    // names dbgshim gives this pid and start time (see Containment); anything else is reported and left alone.
    static void Unregister(nint token, int pid)
    {
        string hr = token == 0 ? "no token" : $"0x{s_unregister(token):X8}";
        Tracked.Tokens.TryRemove(token, out _);
        var left = SemaphoreFiles(pid);
        Say($"    UnregisterForRuntimeStartup: {hr}; semaphores left: {(left.Count == 0 ? "none" : string.Join(", ", left.Select(Path.GetFileName)))}");
        var own = Tracked.OwnSemaphoreNames(pid);
        foreach (string f in left)
        {
            if (own.Contains(Path.GetFileName(f)))
            {
                Say($"    FINDING: semaphore {Path.GetFileName(f)} left after Unregister; removing it by hand");
                try { File.Delete(f); } catch (Exception e) { Say($"    could not remove {f}: {e.Message}"); }
            }
            else Say($"    FINDING: semaphore {Path.GetFileName(f)} matches pid {pid} but not its start time; not the probe's, left alone");
        }
    }

    // ---- containment: in the finally around the whole run (see the header) ----------------------------------------

    static void Contain()
    {
        Say("containment:");
        var live = Tracked.Procs.Where(p => p.IsAlive).ToList();
        foreach (var p in live)
            Say($"    {p} still alive; where its threads wait:\n{Threads(p.Pid)}");
        foreach (var p in live)
            if (p.KillIfAlive() is { } k) Say($"    {k}");
        var sw = Stopwatch.StartNew();
        while (live.Any(p => p.IsAlive) && sw.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(20);
        foreach (var p in live.Where(p => p.IsAlive)) Say($"    {p} still alive 5 s after SIGKILL");

        foreach (var (token, pid) in Tracked.Tokens.ToArray())
        {
            Say($"    token for {pid} not yet unregistered:");
            Unregister(token, pid);
        }

        int removed = 0;
        foreach (var p in Tracked.Procs.Where(p => !p.IsAlive))
        {
            string tmp = Path.GetTempPath();
            var files = Tracked.OwnSemaphoreNames(p.Pid).Select(n => Path.Combine("/dev/shm", n)).Where(File.Exists).ToList();
            try
            {
                files.AddRange(Directory.GetFiles(tmp, $"clr-debug-pipe-{p.Pid}-{p.Start}-*"));
                files.AddRange(Directory.GetFiles(tmp, $"dotnet-diagnostic-{p.Pid}-{p.Start}-*"));
            }
            catch (IOException e) { Say($"    could not list {tmp}: {e.Message}"); }
            foreach (string f in files)
            {
                try { File.Delete(f); removed++; Say($"    removed {f} ({p}, {(p.Killed ? "killed: expected" : "exited: FINDING")})"); }
                catch (Exception e) { Say($"    could not remove {f}: {e.Message}"); }
            }
        }
        foreach (string s in Tracked.Sockets.Keys.Where(File.Exists))
        {
            try { File.Delete(s); removed++; Say($"    removed probe socket {s} (FINDING: not unlinked by its server)"); }
            catch (Exception e) { Say($"    could not remove {s}: {e.Message}"); }
        }
        Say($"    tracked processes {Tracked.Procs.Count()}, alive at containment {live.Count}, files removed {removed}");
    }

    static IEnumerable<Callback> CallbacksFor(int context) => s_callbacks.Where(cb => cb.Context == context).OrderBy(cb => cb.Ticks);

    static string Format(List<Callback> cbs) =>
        cbs.Count == 0 ? "none" : string.Join(", ", cbs.Select(cb => $"0x{cb.Hr:X8}{(cb.NonNull ? " +cordb" : "")} at {new DateTime(cb.Ticks, DateTimeKind.Utc):HH:mm:ss.fff}"));

    // dbgshim's PAL names its startup/continue semaphores with the pid in hex (8 digits); both forms are matched, and
    // every sem.clr* is listed so that a different naming shows up rather than hides.
    static List<string> SemaphoreFiles(int pid)
    {
        string hex = pid.ToString("x8", CultureInfo.InvariantCulture), dec = pid.ToString(CultureInfo.InvariantCulture);
        try { return Directory.GetFiles("/dev/shm", "sem.clr*").Where(f => f.Contains(hex, StringComparison.Ordinal) || f.Contains(dec, StringComparison.Ordinal)).ToList(); }
        catch (IOException) { return []; }
    }

    static string Semaphores(int pid)
    {
        var mine = SemaphoreFiles(pid);
        string[] all;
        try { all = Directory.GetFiles("/dev/shm", "sem.clr*"); } catch (IOException) { all = []; }
        return $"{(mine.Count == 0 ? "none for this pid" : string.Join(", ", mine.Select(Path.GetFileName)))} (sem.clr* in /dev/shm: {all.Length})";
    }

    static string? FindDbgShim()
    {
        string? env = Environment.GetEnvironmentVariable("DBGSHIM_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        string rid = RuntimeInformation.RuntimeIdentifier.Contains("musl") ? "linux-musl-x64" : $"linux-{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages", $"microsoft.diagnostics.dbgshim.{rid}");
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root)
            .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version(0, 0))
            .Select(d => Path.Combine(d, "runtimes", rid, "native", "libdbgshim.so"))
            .FirstOrDefault(File.Exists);
    }

    // ---- default port (Q6) ----------------------------------------------------------------------------------------

    static string DefaultPortState(int pid)
    {
        try
        {
            string[] found = Directory.GetFiles(Path.GetTempPath(), $"dotnet-diagnostic-{pid}-*-socket");
            return found.Length == 0 ? "absent" : $"present ({Path.GetFileName(found[0])})";
        }
        catch (IOException e) { return $"unknown ({e.Message})"; }
    }

    static string DefaultPortResume(int pid)
    {
        var call = Task.Run(() => new DiagnosticsClient(pid).ResumeRuntime());
        try
        {
            return call.Wait(TimeSpan.FromSeconds(5)) ? "returned" : "no answer within 5 s";
        }
        catch (AggregateException e) { return $"threw {e.InnerException?.GetType().Name}: {e.InnerException?.Message}"; }
    }

    // ---- outcome and containment ----------------------------------------------------------------------------------

    // Ends the run: whatever is still alive after the run's own bound is killed (matched by pid and start time), and
    // the kill is part of the outcome.
    static string Finish(Target t, List<Callback>? cbs, string outcome, Proc? child = null)
    {
        var kills = new List<string>();
        if (child is not null && child.KillIfAlive() is { } kc) kills.Add(kc);
        if (t.Proc.KillIfAlive() is { } kt) kills.Add(kt);
        if (kills.Count > 0)
        {
            Say($"    still alive after the bound: {string.Join(", ", kills)}");
            outcome += "-killed";
        }
        else
        {
            outcome += t.ExitCode is { } code ? $"-exited({code})" : "";
        }
        return outcome;
    }

    static string Describe(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            string[] rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            bool coreclr = File.ReadAllText($"/proc/{pid}/maps").Contains("libcoreclr.so", StringComparison.Ordinal);
            return $"state {rest[0]}, {rest[17]} threads, libcoreclr {(coreclr ? "mapped" : "not mapped")}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "gone"; }
    }

    // Every thread of pid: tid, name, state and wchan (the kernel function it sleeps in), from /proc/<pid>/task/*.
    static string Threads(int pid)
    {
        var sb = new StringBuilder();
        try
        {
            foreach (string task in Directory.GetDirectories($"/proc/{pid}/task").OrderBy(d => int.Parse(Path.GetFileName(d), CultureInfo.InvariantCulture)))
            {
                try
                {
                    string stat = File.ReadAllText(Path.Combine(task, "stat"));
                    int open = stat.IndexOf('('), close = stat.LastIndexOf(')');
                    string name = stat[(open + 1)..close], state = stat[(close + 2)..].Split(' ')[0];
                    string wchan = File.ReadAllText(Path.Combine(task, "wchan")).Trim();
                    sb.Append(CultureInfo.InvariantCulture, $"      {Path.GetFileName(task),-8} {name,-16} {state} {(wchan.Length == 0 || wchan == "0" ? "-" : wchan)}\n");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "      gone"; }
        return sb.ToString().TrimEnd('\n');
    }

    static string Environ(int pid)
    {
        try
        {
            string env = Encoding.UTF8.GetString(File.ReadAllBytes($"/proc/{pid}/environ"));
            string[] vars = env.Split('\0');
            string ports = vars.FirstOrDefault(v => v.StartsWith("DOTNET_DiagnosticPorts=", StringComparison.Ordinal)) ?? "DOTNET_DiagnosticPorts unset";
            string hooks = vars.Any(v => v.StartsWith("DOTNET_STARTUP_HOOKS=", StringComparison.Ordinal)) ? "DOTNET_STARTUP_HOOKS set" : "DOTNET_STARTUP_HOOKS unset";
            return $"{ports}, {hooks}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "gone"; }
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    internal static void Say(string text) { Console.WriteLine(text); Console.Out.Flush(); }
    internal static long Now() => DateTime.UtcNow.Ticks;
    static long Ms(long from, long to) => (to - from) / TimeSpan.TicksPerMillisecond;
    static string SocketPath(string tag)
    {
        string path = Path.Combine(Path.GetTempPath(), $"p107-{Environment.ProcessId}-{tag}.sock");
        Tracked.Sockets[path] = 0;
        return path;
    }
    static string SourcePath([CallerFilePath] string path = "") => path;

    internal static string TargetDll => s_targetDll;
    internal static string HookDll => s_hookDll;

    static string? BuildDll(string csPath)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(csPath);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Debug");
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) { Console.Error.WriteLine(output); return null; }
        foreach (string line in output.Split('\n'))
        {
            int arrow = line.IndexOf("-> ", StringComparison.Ordinal);
            if (arrow < 0) continue;
            string path = line[(arrow + 3)..].Trim();
            if (path.EndsWith(".dll", StringComparison.Ordinal) && File.Exists(path)) return path;
        }
        return null;
    }
}

sealed record Callback(int Context, long Ticks, int Hr, bool NonNull);

sealed class WireLayoutException(string message) : Exception(message);

// Everything the probe started, registered or bound, so that containment reaches it however the run ends.
static class Tracked
{
    private static readonly ConcurrentDictionary<(int Pid, long Start), Proc> s_procs = new();
    public static readonly ConcurrentDictionary<nint, int> Tokens = new();
    public static readonly ConcurrentDictionary<string, byte> Sockets = new();

    // One Proc per pid and start time: a later Of() for the same process (a target that also dials) gets the first,
    // so a kill recorded through either is seen by containment.
    public static Proc Add(Proc p) => p.Start >= 0 ? s_procs.GetOrAdd((p.Pid, p.Start), p) : p;
    public static IEnumerable<Proc> Procs => s_procs.Values.OrderBy(p => p.Pid);

    // dbgshim's PAL names: "clrst"/"clrco" + pid (%08x) + process start time (%016llx), as seen on zbook in rev 4's
    // leftover (sem.clrst0017a2060000000001fd4b34: pid 1548806, start 33377076). One name pair per tracked start time.
    public static HashSet<string> OwnSemaphoreNames(int pid) =>
        s_procs.Keys.Where(k => k.Pid == pid)
            .SelectMany(k => new[] { "clrst", "clrco" }.Select(kind => $"sem.{kind}{k.Pid:x8}{k.Start:x16}"))
            .ToHashSet();
}

// A process the probe will only kill if it is still the same process: pid plus start time (/proc/<pid>/stat field 22).
sealed class Proc
{
    private readonly string _what;
    public int Pid { get; }
    public long Start { get; }
    public bool Killed { get; private set; }

    private Proc(int pid, long start, string what) { Pid = pid; Start = start; _what = what; }

    public static Proc Of(int pid, string what) => Tracked.Add(new Proc(pid, StartTime(pid), what));

    public bool IsAlive => Start >= 0 && StartTime(Pid) == Start;
    public override string ToString() => $"{_what} {Pid}";

    public static long StartTime(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            string[] rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return rest[0] == "Z" ? -1 : long.Parse(rest[19], CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return -1; }
    }

    public string? KillIfAlive()
    {
        if (!IsAlive) return null;
        kill(Pid, 9);
        Killed = true;
        return $"{_what} {Pid} SIGKILLed";
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}

// A launched target: `dotnet <target dll> <args>` (or `dotnet exec --runtimeconfig <json> <dll> <args>`), its stdout
// collected line by line with the probe's arrival time.
sealed class Target : IDisposable
{
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _lines = new();
    public Proc Proc { get; }
    public int Pid => _process.Id;
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    private Target(Process process)
    {
        _process = process;
        Proc = Proc.Of(process.Id, "target");
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) _lines.Enqueue(e.Data); };
        process.BeginOutputReadLine();
    }

    public static Target Start(string[] args, string? diagPorts, bool hook, bool stdin, string? runtimeconfig = null)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardInput = stdin, UseShellExecute = false };
        if (runtimeconfig is not null)
        {
            psi.ArgumentList.Add("exec");
            psi.ArgumentList.Add("--runtimeconfig");
            psi.ArgumentList.Add(runtimeconfig);
        }
        psi.ArgumentList.Add(Probe107.TargetDll);
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment.Remove("DOTNET_DiagnosticPorts");
        psi.Environment.Remove("DOTNET_STARTUP_HOOKS");
        if (diagPorts is not null) psi.Environment["DOTNET_DiagnosticPorts"] = diagPorts;
        if (hook) psi.Environment["DOTNET_STARTUP_HOOKS"] = Probe107.HookDll;
        var p = Process.Start(psi)!;
        Probe107.Say($"    spawned {p.Id}: dotnet {string.Join(' ', psi.ArgumentList)}{(diagPorts is null ? "" : $" [DOTNET_DiagnosticPorts={diagPorts}]")}{(hook ? " [startup hook]" : "")}");
        return new Target(p);
    }

    public void SendLine(string text) { _process.StandardInput.WriteLine(text); _process.StandardInput.Flush(); }

    public bool WaitForExit(TimeSpan bound)
    {
        if (!_process.WaitForExit(bound)) return false;
        _process.WaitForExit();                                // drains redirected output
        return true;
    }

    // "<name> <ticks>" lines; the target's own ticks.
    public long? Mark(string name)
    {
        foreach (string line in _lines)
        {
            if (!line.StartsWith(name + " ", StringComparison.Ordinal)) continue;
            if (long.TryParse(line[(name.Length + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)) return ticks;
        }
        return null;
    }

    public string? Line(string prefix) => _lines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));

    public bool WaitForMark(string name, TimeSpan bound)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < bound) { if (Mark(name) is not null) return true; Thread.Sleep(10); }
        return false;
    }

    public bool WaitForLine(string prefix, TimeSpan bound, out string? line)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < bound) { if ((line = Line(prefix)) is not null) return true; Thread.Sleep(10); }
        line = null;
        return false;
    }

    // The marks seen so far, in order, as "name" (no ticks), optionally only those with a prefix.
    public string MarksSummary(string prefix = "")
    {
        var names = _lines.Where(l => l.StartsWith(prefix, StringComparison.Ordinal))
            .Select(l => (l, i: l.LastIndexOf(' ')))
            .Where(x => x.i > 0 && long.TryParse(x.l[(x.i + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(x => x.l[..x.i]).ToList();
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    public void Dispose() => _process.Dispose();
}

// The probe's end of DOTNET_DiagnosticPorts=<path>,connect: accepts every dial, reads its advertise, and keeps each
// connection by pid. ipc-protocol.md layouts; see the header.
sealed class ReverseServer : IDisposable
{
    public sealed record Conn(Socket Socket, int Pid, long Ticks, string Cookie);

    private static readonly byte[] AdvertiseMagic = "ADVR_V1\0"u8.ToArray();
    private static readonly byte[] IpcMagic = "DOTNET_IPC_V1\0"u8.ToArray();
    private readonly string _path;
    private readonly Socket _listener;
    private readonly List<Conn> _all = new();
    private readonly HashSet<Conn> _taken = new();
    private volatile bool _stopped;

    public ReverseServer(string path)
    {
        _path = path;
        File.Delete(path);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(path));
        _listener.Listen(64);
        new Thread(AcceptLoop) { IsBackground = true, Name = "p107-accept" }.Start();
    }

    private void AcceptLoop()
    {
        while (!_stopped)
        {
            Socket s;
            try { s = _listener.Accept(); }
            catch (Exception) { return; }                      // the listener was closed
            try
            {
                s.ReceiveTimeout = 5000;
                byte[] adv = ReadExactly(s, 34);
                if (!adv.AsSpan(0, 8).SequenceEqual(AdvertiseMagic))
                    throw new WireLayoutException($"advertise magic {Convert.ToHexString(adv, 0, 8)}, expected ADVR_V1\\0");
                string cookie = new Guid(adv.AsSpan(8, 16)).ToString();
                ulong pid = BinaryPrimitives.ReadUInt64LittleEndian(adv.AsSpan(24, 8));
                Proc.Of((int)pid, "dialler");                    // tracked for containment, whoever it is
                lock (_all) _all.Add(new Conn(s, (int)pid, Probe107.Now(), cookie));
            }
            catch (WireLayoutException e) { Probe107.Say($"    WIRE: {e.Message}"); s.Dispose(); _layoutError = e; }
            catch (Exception e) { Probe107.Say($"    advertise read failed: {e.Message}"); s.Dispose(); }
        }
    }

    private volatile WireLayoutException? _layoutError;

    // The first connection matching the predicate that no one has taken yet, within the bound.
    public Conn? WaitFor(Func<Conn, bool> match, TimeSpan bound)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < bound)
        {
            if (_layoutError is { } e) throw e;
            lock (_all)
            {
                var c = _all.FirstOrDefault(c => !_taken.Contains(c) && match(c));
                if (c is not null) { _taken.Add(c); return c; }
            }
            Thread.Sleep(5);
        }
        return null;
    }

    public int CountFrom(int pid) { lock (_all) return _all.Count(c => c.Pid == pid); }

    // ResumeRuntime (0x04/0x01, no payload) on this connection; the connection is then consumed and closed.
    // The reply is 0xFF/0x00 (OK) or 0xFF/0xFF (error), size 24, with an int32 code on both (ds_ipc_message_send_success
    // and ds_ipc_message_send_error, ds-protocol.c). The code is data; only the set, id and size are layout.
    public string Resume(Conn c)
    {
        byte[] header = new byte[20];
        IpcMagic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14, 2), 20);
        header[16] = 0x04;
        header[17] = 0x01;
        c.Socket.Send(header);
        byte[] reply = ReadExactly(c.Socket, 20);
        if (!reply.AsSpan(0, 14).SequenceEqual(IpcMagic))
            throw new WireLayoutException($"reply magic {Convert.ToHexString(reply, 0, 14)}");
        ushort size = BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(14, 2));
        byte set = reply[16], id = reply[17];
        if (set != 0xFF || (id != 0x00 && id != 0xFF) || size != 24)
            throw new WireLayoutException($"reply set 0x{set:X2} id 0x{id:X2} size {size}");
        int code = BinaryPrimitives.ReadInt32LittleEndian(ReadExactly(c.Socket, 4));
        string result = id == 0x00 ? (code == 0 ? "OK" : $"OK code 0x{code:X8}") : $"error 0x{code:X8}";
        c.Socket.Dispose();
        return result;
    }

    private static byte[] ReadExactly(Socket s, int count)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = s.Receive(buffer, read, count - read, SocketFlags.None);
            if (n == 0) throw new IOException($"connection closed after {read} of {count} bytes");
            read += n;
        }
        return buffer;
    }

    // Closes the listener and every connection, and unlinks the path: what a DrHook crash would leave the runtime.
    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        _listener.Dispose();
        lock (_all) foreach (var c in _all) c.Socket.Dispose();
        File.Delete(_path);
    }
}
