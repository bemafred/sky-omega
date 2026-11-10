#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
#:property AllowUnsafeBlocks=true
#:package Microsoft.Diagnostics.NETCore.Client@0.2.750501
//
// DrHook.Engine probe 109 — the runtime's diagnostics-port pause on macOS and Windows (ADR-017 Step 3, Q1) ==========
//
// ADR-017's lead candidate for catching a supervisor's children is the runtime's own pause:
// DOTNET_DiagnosticPorts=<address>,connect,suspend. Finding 107 measured it on Linux x64 only (zbook, probe 107 rev 5):
// the pause holds a target before its first managed code and before dbgshim's startup check, every .NET child inherits
// it, and a child with nobody listening waits until a listener returns. Finding 108 saw Rider rely on it on macOS
// arm64. ADR-017 Q1 asks whether it holds and spreads the same way on macOS and Windows, measured by DrHook.
//
// This probe runs probe 107 rev 5's modes unchanged, on Linux, macOS and Windows. What changed from rev 5:
//   1. ProcessView: what the OS shows about a process, per platform. Linux reads /proc as rev 5 did. macOS reads
//      libproc (proc_pidinfo) and sysctl KERN_PROCARGS2. Windows reads System.Diagnostics.Process and
//      GetThreadDescription. A self-check against the probe's own process runs first; a mismatch is exit 2 (the
//      observation layer is wrong, not the hypothesis).
//   2. The reverse server: a Unix socket on Linux and macOS, a named pipe on Windows.
//   3. PidNamedObjects: what the runtime and dbgshim name after a process (the startup handshake, the debug transport,
//      the default port), observed during a run and removed after it, per platform.
//   4. The target is built with PublishAot=false, so its startup-hook arm tests something. Rev 5's hook arm tested
//      nothing: a .NET 10 file-based app defaults to AOT-style feature switches, startup hooks off (finding 107).
//      Checked 2026-11-10 on omega: with the property, the target's generated runtimeconfig has no feature switches.
//   5. dbgshim is found per RID (libdbgshim.so, libdbgshim.dylib, dbgshim.dll).
//   6. What is left of the handshake is reported right after each unregistration and removed in containment, once its
//      process is gone. Rev 5 removed a Linux semaphore left after Unregister at once.
//   7. Q6 counts a callback as a release only if none came before the Q6 call. In rev 5 a callback during the pause
//      (Q5 falsified) would have been scored released-by-default-port in runs 1-5; on zbook none came.
//   8. (rev 2) The Unix-socket server accepts with AcceptAsync and a token, cancelled before the close. Rev 1 kept
//      rev 5's synchronous Accept: on macOS a close does not wake a thread blocked in accept(), and Socket.Dispose
//      then waits for it forever. Rev 1 on omega hung so at the end of hold run 1 (drhook_snapshot, 2026-11-10);
//      DrHook.Engine's DebugStateServer already avoids the same hang the same way.
// On Linux the probe is a control: it must reproduce finding 107, or the port changed something.
//
// What the source says (runtime release/10.0; dbgshim v9.0.661903, the version DrHook ships; read by sky-omega
// 2026-11-10). Shared by every platform: EEStartupHelper (vm/ceemain.cpp) pauses for the diagnostics monitor, then
// initializes the debugger (finding 107). The diagnostics protocol (wire layouts below) is the same everywhere.
//   Windows, the port (src/native/eventpipe/ds-ipc-pal-namedpipe.c): the address is a pipe name. ds_ipc_alloc forms
//     "\\.\pipe\<address>" (no check for a name that already has the prefix); the default port is
//     "\\.\pipe\dotnet-diagnostic-<pid>". A connect port makes one CreateFileA(OPEN_EXISTING, FILE_FLAG_OVERLAPPED) per
//     attempt, with no WaitNamedPipe; the caller's poll timeout drives the re-dials, as on Linux. So the server keeps
//     one pipe instance waiting at all times, or a dial finds no pipe and waits for its next poll.
//   Windows, the startup handshake (debug/ee/debugger.cpp, NotifyDebuggerOfStartup, the first thing Debugger::Startup
//     does there): g_hContinueStartupEvent starts as INVALID_HANDLE_VALUE. The runtime creates it (manual reset), opens
//     "TelestoStartupEvent_<pid as %08x>", and if that exists, signals it and waits on the continue event with
//     INFINITE; then it closes the continue event and sets it to NULL. Nothing marks a debugger attached on Windows:
//     MarkDebuggerAttachedInternal after the startup notification is TARGET_UNIX only.
//   Windows, dbgshim (src/dbgshim/dbgshim.cpp): Register creates the startup event (auto reset) and starts a thread.
//     InternalGetRuntime looks for coreclr.dll among the target's modules and reads the continue event's handle from
//     its memory. While the handle is INVALID_HANDLE_VALUE it retries, 25 times 100 ms apart, then gives
//     ERROR_TIMEOUT, which is not reported: the thread then waits on the startup event with INFINITE and looks again.
//     A NULL handle (the runtime is past its startup) is S_OK at once: the attach path, with no continue to set.
//     After the callback dbgshim sets the continue event (unless it is NULL or INVALID_HANDLE_VALUE).
//   macOS, the startup handshake (pal/src/thread/process.cpp, ENABLE_RUNTIME_EVENTS_OVER_PIPES on __APPLE__):
//     PAL_NotifyRuntimeStarted tries pipes first, because "On macOS 26, sem_open fails if debugger and debugee are
//     signed with different team ids". It opens clr-debug-pipe-<pid>-<key>-co for reading and ...-st for writing, in
//     the temp folder (<key>: the start time in whole seconds, kinfo_proc p_starttime.tv_sec). ENOENT or EACCES on
//     either falls back to the semaphores "/clr{st,co}%08x%016llx", as on Linux. It writes Started (1) and blocks
//     reading Continue (2), with no timeout. On success it returns TRUE, and MarkDebuggerAttachedInternal follows.
//   macOS, dbgshim 9.0.661903 (src/shared/pal/src/thread/process.cpp): Register creates both semaphores
//     (O_CREAT | O_EXCL) and both FIFOs (mkfifo, S_IRWXU). One thread reads -st in 1 s polls; on Started it posts the
//     startup semaphore, waits for the continue semaphore, and writes Continue to -co. StartupHelperThread first
//     stats the target's debug-transport pipe (-in): present means the runtime has started, and the callback comes
//     at once. Unregister cancels; the destructor sem_unlinks the semaphores and unlinks the FIFOs.
//
// Wire layouts, from the runtime source (rev 5, unchanged; dotnet/runtime release/10.0, src/native/eventpipe/):
//   advertise (runtime -> server on connect): 34 bytes = char[8] "ADVR_V1\0", GUID cookie (16), uint64 pid (LE),
//     uint16 zeroed.
//   header (ds-protocol.h): 20 bytes = uint8[14] "DOTNET_IPC_V1\0", uint16 size (LE, header + payload),
//     uint8 commandset, uint8 commandid, uint16 reserved (0).
//   ResumeRuntime (ds-types.h): DS_SERVER_COMMANDSET_PROCESS 0x04, DS_PROCESS_COMMANDID_RESUME_RUNTIME 0x01, no payload.
//   Replies (ds-protocol.c ds_ipc_message_send_success / _send_error): 0xFF with OK 0x00 or ERROR 0xFF, size 24, the
//     header plus an int32 code, on both. The code is data, not layout.
//   "Each command consumes a connection, and the runtime will reconnect": one command per connection; connections are
//   told apart by the advertised pid. A re-dial is kept open, without a command, and counted.
//   Re-dial timing (ds-ipc.c, ds-ipc-pal-types.h): after a failed connect the poll timeout starts at 10 ms and grows by
//   1.25 (10, 12, 15, ... 366, 457, 571) until it is >= 500 ms, then stays at 500.
// Every magic, set, id and size is checked; a mismatch ends the probe with exit 2, through the same containment.
//
// Questions (rev 5's):
//   Q1  Is the pause before the runtime's startup handshake with dbgshim? (registering 2 s into the pause must still
//       catch the startup)
//   Q2  Does any managed code run before the resume? (the target's [ModuleInitializer] and, now live, a
//       DOTNET_STARTUP_HOOKS hook print marks; neither may come before the resume)
//   Q3  Do the target's .NET children inherit the pause, and what does a child do with no listener?
//   Q3b Is a runtimeconfig property a channel the children would not inherit?
//   Q5  Does dbgshim call back during the pause?
//   Q6  Does a ResumeRuntime over the default port release the pause?
//
// Modes and predictions, per platform (L Linux, M macOS, W Windows; from the source above and finding 107):
//   hold (10)            spawn paused; accept; wait 2 s; Register; hold 2 s; [Q6 in runs 1-5]; ResumeRuntime.
//                        Q1, all: the callback only after the resume, S_OK, not after main, 10/10. Falsified by any
//                        missed.
//                        Q5, all: no callback during the pause. W: coreclr.dll is loaded during the pause, but the
//                        continue event does not exist until the runtime is past it, so dbgshim retries, then waits.
//                        Q2, all: no hook, modinit or main mark before the resume; falsified by any.
//                        The end. L, M: held-stopped-before-main 10/10 (the S_OK callback marks a debugger attached,
//                        none connects, the runtime stops at its first debug event; killed at the 15 s bound, as on
//                        zbook). W: held-reached-main, exit 0, 10/10 (nothing marks a debugger attached; the target
//                        runs main and its 4 s linger and ends). The predicted end is checked per platform.
//                        The handshake, after Register: L sem.clrst and sem.clrco in /dev/shm. M both FIFOs and both
//                        semaphores. W TelestoStartupEvent_<pid> present; absent after Unregister.
//   hold-control (10)    no pause; Register 2 s after the spawn. All: missed 10/10 (main before the callback). M: the
//                        -in pipe exists, so the callback comes at once. W: the continue handle is NULL, so S_OK at
//                        once.
//   child-listener (3)   target in spawn-child, held and resumed; the child dials (told apart by pid), is held 5 s,
//                        then resumed. All: child-paused-inherited 3/3.
//   child-no-listener (3) target resumed; listener closed and its address removed; then the go marker. The child is
//                        watched 30 s; then the address is bound again. All: no child mark for 30 s, a re-dial within
//                        about 0.5 s of the return (the step is 500 ms by then), 3/3. W: the pipe name is gone while
//                        no instance exists.
//   runtimeconfig-property (1)  configProperties DOTNET_DiagnosticPorts, no variable. All: no dial.
//
// Containment (rev 5's, per platform): processes end naturally within each bound, except a held target that does not
// end, whose kill after its bound is the predicted end on Linux and macOS. Only what is still alive afterwards is
// killed, matched by pid and start time (L /proc stat field 22; M proc_bsdinfo start, microseconds; W the process
// start time), and every kill is part of the run's outcome. dbgshim's registrations are undone with
// UnregisterForRuntimeStartup, always. Every spawn is logged with its pid and command line first. The whole run is
// inside a finally, which, however the probe ends:
//   1. logs every thread of each tracked process still alive (L state and wchan; M name and run state; W id,
//      description, state and wait reason);
//   2. kills those processes, matched by pid and start time;
//   3. calls UnregisterForRuntimeStartup for every token not yet unregistered;
//   4. removes only what the runtime and dbgshim named after a tracked process that is gone, by its pid and start
//      key: L /dev/shm sem.clrst and sem.clrco<pid:x8><start:x16>, /tmp/clr-debug-pipe-<pid>-<start>-*,
//      /tmp/dotnet-diagnostic-<pid>-<start>-*; M <temp>/clr-debug-pipe-<pid>-<key>-* (in, out, st, co),
//      <temp>/dotnet-diagnostic-<pid>-<key>-*, the semaphores /clr{st,co}<pid:x8><key:x16> if they exist; W nothing
//      (pipes and events end with their handles). Never a sweep: other dbgshim users (DrHook, Rider) own entries
//      there. Each removal is logged, with whether its process was killed (expected) or exited (a finding); and the
//      probe's own sockets (Unix);
//   5. after an early stop, prints the tally so far and where it stopped.
// Tracked: every target and child the probe spawns or reads a pid for, and every pid that dials a probe address.
//
// Usage: dotnet run --no-cache 109-diagport-hold-smoke.cs [mode ...]   (default: all modes, in the order above)
//        DBGSHIM_PATH=<dbgshim library> overrides the NuGet lookup.
// Exit: 0 hold held 10/10 and control missed 10/10; 1 otherwise; 2 setup, self-check, wire-layout or probe failure.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Diagnostics.NETCore.Client;

return Probe109.Run(args);

static unsafe class Probe109
{
    static readonly string[] AllModes = ["hold", "hold-control", "child-listener", "child-no-listener", "runtimeconfig-property"];
    static readonly Dictionary<string, int> Runs = new()
    {
        ["hold"] = 10, ["hold-control"] = 10, ["child-listener"] = 3, ["child-no-listener"] = 3, ["runtimeconfig-property"] = 1,
    };

    // The end a held target is predicted to come to, by platform (see the header).
    static readonly string PredictedHoldEnd = OperatingSystem.IsWindows() ? "held-reached-main" : "held-stopped-before-main";

    static string s_targetDll = "", s_hookDll = "";
    static delegate* unmanaged[Cdecl]<uint, nint, nint, nint*, int> s_register;
    static delegate* unmanaged[Cdecl]<nint, int> s_unregister;
    static readonly ConcurrentQueue<Callback> s_callbacks = new();

    public static int Run(string[] args)
    {
        string[] modes = args.Length > 0 ? args : AllModes;
        foreach (string m in modes)
            if (!Runs.ContainsKey(m)) { Say($"unknown mode {m}"); return 2; }

        Say($"probe 109 rev 2 on {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture} ({ProcessView.Platform}), runtime {Environment.Version}");
        Say("observation layer self-check:");
        string? selfCheck = ProcessView.SelfCheck();
        if (selfCheck is not null) { Say($"setup: observation layer: {selfCheck}"); return 2; }

        string dir = Path.GetDirectoryName(SourcePath())!;
        string? target = BuildDll(Path.Combine(dir, "109-diagport-hold-target.cs"));
        string? hook = BuildDll(Path.Combine(dir, "109-diagport-hold-hook.cs"));
        if (target is null || hook is null) { Say("setup: build failed"); return 2; }
        s_targetDll = target;
        s_hookDll = hook;
        Say($"target {s_targetDll}");
        Say($"hook   {s_hookDll}");
        string config = File.ReadAllText(Path.ChangeExtension(s_targetDll, ".runtimeconfig.json"));
        Say($"target startup hooks: {(config.Contains("System.StartupHookProvider.IsSupported\": false", StringComparison.Ordinal) ? "OFF in its runtimeconfig (the hook arm tests nothing)" : "not switched off")}");

        string? shim = FindDbgShim();
        if (shim is null) { Say("setup: dbgshim not found (set DBGSHIM_PATH)"); return 2; }
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
            Say($"hold (end): predicted {PredictedHoldEnd} in every held run on {ProcessView.Platform}: {(h.Where(o => o.StartsWith("held", StringComparison.Ordinal)).All(o => o.StartsWith(PredictedHoldEnd, StringComparison.Ordinal)) ? "holds" : "falsified")}");
        if (c is not null) Say($"hold-control: predicted missed every run: {(controlOk ? "holds" : "falsified")}");
        return holdOk && holdAnyHeld && controlOk ? 0 : 1;
    }

    // ---- hold: Q1, Q2, Q5, Q6 --------------------------------------------------------------------------------------

    static string Hold(int run, bool sideTest)
    {
        string address = PortAddress($"hold{run}");
        using var server = ReverseServer.Bind(address);
        using var t = Target.Start(["plain", "4000"], diagPorts: $"{address},connect,suspend", hook: true, stdin: false);
        long tSpawn = Now();
        var adv = server.WaitFor(cn => cn.Pid == t.Pid, TimeSpan.FromSeconds(10));
        if (adv is null) return Finish(t, null, "setup-failed-no-dial");
        Say($"    advertise from {adv.Pid} after {Ms(tSpawn, adv.Ticks)} ms, cookie {adv.Cookie}; {ProcessView.Describe(t.Pid)}; default port {DefaultPort.State(t.Pid)}");

        Thread.Sleep(2000);
        int hr = Register(t.Pid, run, out nint token);
        Say($"    RegisterForRuntimeStartup 2 s into the pause: 0x{hr:X8}; marks so far: {t.MarksSummary()}; handshake: {PidNamedObjects.Observe(t.Pid)}");

        Thread.Sleep(2000);
        var during = CallbacksFor(run).Where(cb => cb.Ticks <= Now()).ToList();
        Say($"    after holding 2 s more: callbacks {Format(during)}; marks so far: {t.MarksSummary()}; {ProcessView.Describe(t.Pid)}");

        // Q6: after Register, so a release here still comes after the registration.
        if (sideTest)
        {
            string port = DefaultPort.State(t.Pid);
            long tq6 = Now();
            string q6 = DefaultPort.Resume(t.Pid);
            long q6CallMs = Ms(tq6, Now());
            Thread.Sleep(2000);
            // A release shows as a managed mark, or as a callback after tq6 when none came before it (one before it is Q5's
            // early callback, scored below, not a release).
            var q6Callbacks = CallbacksFor(run).Where(cb => cb.Ticks >= tq6).ToList();
            bool calledBackBefore = CallbacksFor(run).Any(cb => cb.Ticks < tq6);
            bool released = t.Mark("modinit") is not null || t.Mark("hook") is not null || t.Mark("main") is not null || (q6Callbacks.Count > 0 && !calledBackBefore);
            Say($"    Q6: default port {port}; DiagnosticsClient.ResumeRuntime: {q6} [call {q6CallMs} ms]; 2 s later the pause {(released ? "is RELEASED" : "holds")} (callbacks since: {Format(q6Callbacks)}; marks: {t.MarksSummary()})");
            if (released)
            {
                if (!t.WaitForExit(TimeSpan.FromSeconds(15)))
                    Say($"    not ended within 15 s of the Q6 release; its threads:\n{ProcessView.Threads(t.Pid)}");
                Unregister(token, t.Pid);
                return Finish(t, CallbacksFor(run).ToList(), "released-by-default-port");
            }
        }

        long tResume = Now();
        string reply = server.Resume(adv);
        Say($"    ResumeRuntime over the reverse connection: {reply}");
        Thread.Sleep(300);
        string portAfter = DefaultPort.State(t.Pid);
        bool exited = t.WaitForExit(TimeSpan.FromSeconds(15));
        if (exited) Thread.Sleep(1000);                         // a late callback, after the exit, is data too
        else Say($"    not ended within 15 s of the resume; its threads:\n{ProcessView.Threads(t.Pid)}");
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
        Say($"    RegisterForRuntimeStartup 2 s after the spawn: 0x{hr:X8}; marks so far: {t.MarksSummary()}; {ProcessView.Describe(t.Pid)}");
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
        // No RegisterForRuntimeStartup here: on Linux and macOS an S_OK callback would mark a debugger attached and stop
        // the target before main (see hold). The target is held by the diagnostics pause alone, resumed, and ends.
        string address = PortAddress($"cl{run}");
        using var server = ReverseServer.Bind(address);
        using var t = Target.Start(["spawn-child"], diagPorts: $"{address},connect,suspend", hook: true, stdin: true);
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
            Say($"    child {cadv.Pid} dialled {Ms(tGo, cadv.Ticks)} ms after go; its environment: {ProcessView.Environ(cadv.Pid)}; {ProcessView.Describe(cadv.Pid)}");
            Thread.Sleep(5000);
            bool ran = t.Mark("child: modinit") is not null || t.Mark("child: hook") is not null || t.Mark("child: main") is not null;
            Say($"    after holding the child 5 s: {(ran ? "child managed code RAN" : "no child mark")}; resume child: {server.Resume(cadv)}");
            result = ran ? "child-not-paused" : "child-paused";
        }
        t.WaitForExit(TimeSpan.FromSeconds(20));
        Say($"    target view: {t.Line("env ")}; child view: {t.Line("child: env ")}; re-dials target {server.CountFrom(t.Pid) - 1}, child {(cadv is null ? 0 : server.CountFrom(cadv.Pid) - 1)}; marks: {t.MarksSummary()}");
        string inherited = t.Line("child: env ")?.Contains(address, StringComparison.Ordinal) == true ? "inherited" : "not-inherited";
        return Finish(t, null, $"{result}-{inherited}", child);
    }

    // ---- child-no-listener: the DrHook-crash case ------------------------------------------------------------------

    static string ChildNoListener(int run)
    {
        // No RegisterForRuntimeStartup, as in child-listener.
        string address = PortAddress($"cn{run}");
        var server = ReverseServer.Bind(address);
        using var t = Target.Start(["spawn-child"], diagPorts: $"{address},connect,suspend", hook: true, stdin: true);
        var adv = server.WaitFor(cn => cn.Pid == t.Pid, TimeSpan.FromSeconds(10));
        if (adv is null) { server.Dispose(); return Finish(t, null, "setup-failed-no-dial"); }
        Say($"    target {t.Pid} dialled; resume: {server.Resume(adv)}");
        if (!t.WaitForMark("main", TimeSpan.FromSeconds(10))) { server.Dispose(); return Finish(t, null, "setup-failed-no-main"); }

        server.Dispose();                                       // listener closed, every connection closed, address removed
        Say($"    listener closed and {address} removed; sending go");
        t.SendLine("go");
        if (!t.WaitForLine("child-pid ", TimeSpan.FromSeconds(10), out string? line)) return Finish(t, null, "setup-failed-no-child");
        int cpid = int.Parse(line!["child-pid ".Length..], CultureInfo.InvariantCulture);
        Proc child = Proc.Of(cpid, "child");
        for (int s = 5; s <= 30; s += 5)
        {
            Thread.Sleep(5000);
            Say($"    {s} s: child {ProcessView.Describe(cpid)}; child marks: {t.MarksSummary("child: ")}");
        }
        bool ranWithout = t.Mark("child: modinit") is not null || t.Mark("child: main") is not null;

        using var server2 = ReverseServer.Bind(address);
        long tRebind = Now();
        var cadv = server2.WaitFor(cn => cn.Pid == cpid, TimeSpan.FromSeconds(10));
        string result = ranWithout ? "child-ran-without-listener"
            : cadv is null ? "child-blocked-no-redial"
            : $"child-blocked-redialled-{Ms(tRebind, cadv.Ticks)}ms";
        if (cadv is not null) Say($"    address bound again; child re-dialled after {Ms(tRebind, cadv.Ticks)} ms; resume child: {server2.Resume(cadv)}");
        t.WaitForExit(TimeSpan.FromSeconds(15));
        Say($"    target re-dials to the new listener: {server2.CountFrom(t.Pid)}; marks: {t.MarksSummary()}");
        return Finish(t, null, result, child);
    }

    // ---- runtimeconfig-property: Q3b -----------------------------------------------------------------------------

    static string RuntimeconfigProperty(int run)
    {
        string address = PortAddress($"rc{run}");
        using var server = ReverseServer.Bind(address);
        string own = Path.ChangeExtension(s_targetDll, ".runtimeconfig.json");
        JsonNode config = JsonNode.Parse(File.ReadAllText(own))!;
        JsonObject options = (JsonObject)config["runtimeOptions"]!;
        if (options["configProperties"] is not JsonObject props) options["configProperties"] = props = new JsonObject();
        props["DOTNET_DiagnosticPorts"] = $"{address},connect,suspend";
        string json = Path.Combine(Path.GetTempPath(), $"p109-{Environment.ProcessId}-rc{run}.runtimeconfig.json");
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

    // Reports what is left of the handshake right after the unregistration; removal waits for containment, when the
    // process is gone and dbgshim's helper has let go.
    static void Unregister(nint token, int pid)
    {
        string hr = token == 0 ? "no token" : $"0x{s_unregister(token):X8}";
        Tracked.Tokens.TryRemove(token, out _);
        Say($"    UnregisterForRuntimeStartup: {hr}; handshake left: {PidNamedObjects.Observe(pid)}");
    }

    // ---- containment: in the finally around the whole run (see the header) ----------------------------------------

    static void Contain()
    {
        Say("containment:");
        var live = Tracked.Procs.Where(p => p.IsAlive).ToList();
        foreach (var p in live)
            Say($"    {p} still alive; its threads:\n{ProcessView.Threads(p.Pid)}");
        foreach (var p in live)
            if (p.KillIfAlive() is { } k) Say($"    {k}");
        var sw = Stopwatch.StartNew();
        while (live.Any(p => p.IsAlive) && sw.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(20);
        foreach (var p in live.Where(p => p.IsAlive)) Say($"    {p} still alive 5 s after the kill");

        foreach (var (token, pid) in Tracked.Tokens.ToArray())
        {
            Say($"    token for {pid} not yet unregistered:");
            Unregister(token, pid);
        }

        int removed = 0;
        foreach (var p in Tracked.Procs.Where(p => !p.IsAlive))
        {
            foreach (var (name, remove) in PidNamedObjects.LeftBy(p))
            {
                try { remove(); removed++; Say($"    removed {name} ({p}, {(p.Killed ? "killed: expected" : "exited: FINDING")})"); }
                catch (Exception e) { Say($"    could not remove {name}: {e.Message}"); }
            }
        }
        foreach (string s in Tracked.Sockets.Keys.Where(File.Exists))
        {
            try { File.Delete(s); removed++; Say($"    removed probe socket {s} (FINDING: not unlinked by its server)"); }
            catch (Exception e) { Say($"    could not remove {s}: {e.Message}"); }
        }
        Say($"    tracked processes {Tracked.Procs.Count()}, alive at containment {live.Count}, objects removed {removed}");
    }

    static IEnumerable<Callback> CallbacksFor(int context) => s_callbacks.Where(cb => cb.Context == context).OrderBy(cb => cb.Ticks);

    static string Format(List<Callback> cbs) =>
        cbs.Count == 0 ? "none" : string.Join(", ", cbs.Select(cb => $"0x{cb.Hr:X8}{(cb.NonNull ? " +cordb" : "")} at {new DateTime(cb.Ticks, DateTimeKind.Utc):HH:mm:ss.fff}"));

    static string? FindDbgShim()
    {
        string? env = Environment.GetEnvironmentVariable("DBGSHIM_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        string arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        string os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.Ordinal) ? "linux-musl" : "linux";
        string rid = $"{os}-{arch}";
        string file = OperatingSystem.IsWindows() ? "dbgshim.dll" : OperatingSystem.IsMacOS() ? "libdbgshim.dylib" : "libdbgshim.so";
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages", $"microsoft.diagnostics.dbgshim.{rid}");
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root)
            .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version(0, 0))
            .Select(d => Path.Combine(d, "runtimes", rid, "native", file))
            .FirstOrDefault(File.Exists);
    }

    // ---- outcome --------------------------------------------------------------------------------------------------

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

    // ---- small things ---------------------------------------------------------------------------------------------

    internal static void Say(string text) { Console.WriteLine(text); Console.Out.Flush(); }
    internal static long Now() => DateTime.UtcNow.Ticks;
    static long Ms(long from, long to) => (to - from) / TimeSpan.TicksPerMillisecond;

    // The address in DOTNET_DiagnosticPorts: a socket path in the temp folder (Linux, macOS), or a pipe name, which the
    // runtime puts under \\.\pipe\ (Windows).
    static string PortAddress(string tag)
    {
        string name = $"p109-{Environment.ProcessId}-{tag}";
        if (OperatingSystem.IsWindows()) return name;
        string path = Path.Combine(Path.GetTempPath(), name + ".sock");
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

// ---- the observation layer --------------------------------------------------------------------------------------
//
// What the operating system shows about a process the probe did not write: its start time (its identity, with the
// pid), its state, its threads, its environment, and the end of it.
//   Linux: /proc, as rev 5.
//   macOS: libproc and sysctl. Layouts as DrHook.Engine verified them against the macOS 27 SDK <sys/proc_info.h> by
//     compiling probes: struct proc_bsdinfo (PROC_PIDTBSDINFO 3) 136 bytes, pbi_status at 4 (SZOMB 5),
//     pbi_start_tvsec at 120, pbi_start_tvusec at 128 (ProcessLifetime.cs); PROC_PIDLISTTHREADS 6; struct
//     proc_threadinfo (PROC_PIDTHREADINFO 5) 112 bytes, pth_name at 48, char[64] (TargetThreads.cs); struct
//     proc_regionwithpathinfo (PROC_PIDREGIONPATHINFO 8) 1272 bytes, pri_address at 80, pri_size at 88, vip_path at
//     248, char[1024] (MappedFiles.cs). pth_run_state at 24 and pth_flags at 28 follow from the header's field order
//     (two uint64, then int32 cpu_usage, policy, run_state, flags, sleep_time, curpri, priority, maxpriority, then
//     the name at 48), which the verified name offset and size agree with; not compiled. The environment:
//     sysctl {CTL_KERN 1, KERN_PROCARGS2 49, pid}: an int32 argc, the executable path, padding NULs, argc arguments,
//     then the environment, NUL-separated, until an empty string; the buffer is KERN_ARGMAX (8) bytes.
//   Windows: System.Diagnostics.Process (start time, threads with state and wait reason, modules) and
//     GetThreadDescription. Another process's environment is not read: there is no supported call for it, and the
//     child reports its own after its resume.
// SelfCheck applies all of it to the probe's own process, whose answers are known, before any mode runs.

static unsafe class ProcessView
{
    public static string Platform => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    // The start time, in the platform's own unit: Linux clock ticks after boot (/proc stat field 22), macOS microseconds
    // since the epoch (proc_bsdinfo), Windows FILETIME (UTC). -1 when no live, non-zombie process has the pid.
    public static long StartTime(int pid)
    {
        if (OperatingSystem.IsMacOS())
        {
            byte* info = stackalloc byte[ProcBsdInfoSize];
            if (proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, info, ProcBsdInfoSize) != ProcBsdInfoSize) return -1;
            if (*(uint*)(info + PbiStatusOffset) == SZOMB) return -1;
            return *(long*)(info + PbiStartSecOffset) * 1_000_000 + *(long*)(info + PbiStartMicroSecOffset);
        }
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return p.HasExited ? -1 : p.StartTime.ToFileTimeUtc();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return -1; }
        }
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            string[] rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return rest[0] == "Z" ? -1 : long.Parse(rest[19], CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return -1; }
    }

    // The start time as the runtime and dbgshim put it in names: Linux the same clock ticks, macOS whole seconds.
    public static long NameKey(long start) => OperatingSystem.IsMacOS() ? start / 1_000_000 : start;

    public static string Describe(int pid)
    {
        if (OperatingSystem.IsMacOS())
        {
            byte* info = stackalloc byte[ProcBsdInfoSize];
            if (proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, info, ProcBsdInfoSize) != ProcBsdInfoSize) return "gone";
            uint status = *(uint*)(info + PbiStatusOffset);
            string state = status switch { 1 => "idle", 2 => "running", 3 => "sleeping", 4 => "stopped", 5 => "zombie", _ => $"status {status}" };
            int threads = MacThreads(pid)?.Count ?? -1;
            bool coreclr = MacMapsFile(pid, "libcoreclr.dylib");
            return $"state {state}, {threads} threads, libcoreclr {(coreclr ? "mapped" : "not mapped")}";
        }
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                var threads = p.Threads.Cast<ProcessThread>().ToList();
                int waiting = threads.Count(th => th.ThreadState == System.Diagnostics.ThreadState.Wait);
                string coreclr;
                try
                {
                    coreclr = p.Modules.Cast<ProcessModule>().Any(m => string.Equals(m.ModuleName, "coreclr.dll", StringComparison.OrdinalIgnoreCase)) ? "loaded" : "not loaded";
                }
                catch (Win32Exception e) { coreclr = $"unknown (error {e.NativeErrorCode})"; }
                return $"{threads.Count} threads ({waiting} waiting), coreclr.dll {coreclr}";
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return "gone"; }
            catch (Win32Exception e) { return $"unreadable (error {e.NativeErrorCode})"; }
        }
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            string[] rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            bool coreclr = File.ReadAllText($"/proc/{pid}/maps").Contains("libcoreclr.so", StringComparison.Ordinal);
            return $"state {rest[0]}, {rest[17]} threads, libcoreclr {(coreclr ? "mapped" : "not mapped")}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "gone"; }
    }

    // Every thread of pid. Linux: tid, name, state and wchan (the kernel function it sleeps in). macOS: name, run state
    // and flags. Windows: id, description, state and wait reason.
    public static string Threads(int pid)
    {
        var sb = new StringBuilder();
        if (OperatingSystem.IsMacOS())
        {
            var threads = MacThreads(pid);
            if (threads is null) return "      gone";
            int i = 0;
            foreach (var (name, runState, flags) in threads)
            {
                string state = runState switch { 1 => "running", 2 => "stopped", 3 => "waiting", 4 => "uninterruptible", 5 => "halted", _ => $"run state {runState}" };
                sb.Append(CultureInfo.InvariantCulture, $"      {i++,-4} {(name.Length == 0 ? "-" : name),-24} {state}{((flags & 2) != 0 ? " idle" : "")}\n");
            }
            return sb.ToString().TrimEnd('\n');
        }
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                foreach (ProcessThread th in p.Threads.Cast<ProcessThread>().OrderBy(th => th.Id))
                {
                    string wait = "-";
                    try { if (th.ThreadState == System.Diagnostics.ThreadState.Wait) wait = th.WaitReason.ToString(); }
                    catch (InvalidOperationException) { }
                    sb.Append(CultureInfo.InvariantCulture, $"      {th.Id,-8} {WindowsThreadDescription(th.Id),-24} {th.ThreadState} {wait}\n");
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return "      gone"; }
            catch (Win32Exception e) { return $"      unreadable (error {e.NativeErrorCode})"; }
            return sb.ToString().TrimEnd('\n');
        }
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

    // What another process's environment says about the two variables the probe passes on.
    public static string Environ(int pid)
    {
        if (OperatingSystem.IsWindows()) return "not read on Windows (no supported call; the child's own view follows its resume)";
        var env = ReadEnvironment(pid);
        if (env is null) return "unreadable";
        string ports = env.TryGetValue("DOTNET_DiagnosticPorts", out string? v) ? $"DOTNET_DiagnosticPorts={v}" : "DOTNET_DiagnosticPorts unset";
        string hooks = env.ContainsKey("DOTNET_STARTUP_HOOKS") ? "DOTNET_STARTUP_HOOKS set" : "DOTNET_STARTUP_HOOKS unset";
        return $"{ports}, {hooks}";
    }

    public static Dictionary<string, string>? ReadEnvironment(int pid)
    {
        if (OperatingSystem.IsMacOS()) return MacEnvironment(pid);
        if (OperatingSystem.IsWindows()) return null;
        try
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string kv in Encoding.UTF8.GetString(File.ReadAllBytes($"/proc/{pid}/environ")).Split('\0'))
            {
                int eq = kv.IndexOf('=');
                if (eq > 0) env[kv[..eq]] = kv[(eq + 1)..];
            }
            return env;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // SIGKILL, or TerminateProcess on Windows. False when the process was already gone.
    public static bool Kill(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
                return true;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
        }
        return kill(pid, 9) == 0;
    }

    // The probe's own process, whose answers are known. Null when every answer matches; otherwise what did not.
    public static string? SelfCheck()
    {
        int self = Environment.ProcessId;
        long start = StartTime(self);
        if (start <= 0) return $"the probe's own start time is unreadable ({start})";
        if (OperatingSystem.IsMacOS())
        {
            DateTime fromLibProc = DateTime.UnixEpoch.AddTicks(start * 10);
            DateTime fromBcl = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            if (Math.Abs((fromLibProc - fromBcl).TotalSeconds) > 2) return $"start time {fromLibProc:O} from libproc, {fromBcl:O} from the BCL";
        }
        string describe = Describe(self);
        Probe109.Say($"    start {start} (name key {NameKey(start)}); {describe}");
        if (!describe.Contains("libcoreclr mapped", StringComparison.Ordinal) && !describe.Contains("coreclr.dll loaded", StringComparison.Ordinal))
            return $"the probe's own runtime is not seen: {describe}";
        string threads = Threads(self);
        Probe109.Say($"    threads:\n{threads}");
        if (threads.Trim().Length == 0 || threads.Trim() == "gone") return "no threads read for the probe itself";
        if (!OperatingSystem.IsWindows())
        {
            var env = ReadEnvironment(self);
            string? home = Environment.GetEnvironmentVariable("HOME");
            if (env is null) return "the probe's own environment is unreadable";
            if (home is null || !env.TryGetValue("HOME", out string? read) || read != home)
                return $"HOME read as {(env.TryGetValue("HOME", out string? r) ? r : "(absent)")}, the probe sees {home ?? "(unset)"}";
            Probe109.Say($"    environment: {env.Count} variables, HOME matches");
        }
        return null;
    }

    // ---- macOS ----

    private const int PROC_PIDTBSDINFO = 3, ProcBsdInfoSize = 136, PbiStatusOffset = 4, PbiStartSecOffset = 120, PbiStartMicroSecOffset = 128;
    private const uint SZOMB = 5;
    private const int PROC_PIDLISTTHREADS = 6, PROC_PIDTHREADINFO = 5, ThreadInfoSize = 112, ThreadRunStateOffset = 24, ThreadFlagsOffset = 28, ThreadNameOffset = 48, ThreadNameLength = 64;
    private const int PROC_PIDREGIONPATHINFO = 8, RegionWithPathInfoSize = 1272, RegionAddressOffset = 80, RegionSizeOffset = 88, RegionPathOffset = 248, MaxPathLength = 1024;
    private const int CTL_KERN = 1, KERN_ARGMAX = 8, KERN_PROCARGS2 = 49;

    private static List<(string Name, int RunState, int Flags)>? MacThreads(int pid)
    {
        ulong[] handles = new ulong[4096];
        int bytes;
        fixed (ulong* list = handles)
            bytes = proc_pidinfo(pid, PROC_PIDLISTTHREADS, 0, (byte*)list, handles.Length * sizeof(ulong));
        if (bytes <= 0) return null;
        var result = new List<(string, int, int)>();
        byte* info = stackalloc byte[ThreadInfoSize];
        for (int i = 0; i < bytes / sizeof(ulong); i++)
        {
            if (proc_pidinfo(pid, PROC_PIDTHREADINFO, handles[i], info, ThreadInfoSize) < ThreadInfoSize) continue;
            var name = new ReadOnlySpan<byte>(info + ThreadNameOffset, ThreadNameLength);
            int length = name.IndexOf((byte)0);
            if (length < 0) length = ThreadNameLength;
            result.Add((Encoding.UTF8.GetString(name[..length]), *(int*)(info + ThreadRunStateOffset), *(int*)(info + ThreadFlagsOffset)));
        }
        return result;
    }

    private static bool MacMapsFile(int pid, string fileName)
    {
        byte* info = stackalloc byte[RegionWithPathInfoSize];
        ulong cursor = 0;
        for (int i = 0; i < 1 << 16; i++)
        {
            if (proc_pidinfo(pid, PROC_PIDREGIONPATHINFO, cursor, info, RegionWithPathInfoSize) < RegionWithPathInfoSize) return false;
            ulong address = *(ulong*)(info + RegionAddressOffset), size = *(ulong*)(info + RegionSizeOffset);
            var path = new ReadOnlySpan<byte>(info + RegionPathOffset, MaxPathLength);
            int length = path.IndexOf((byte)0);
            if (length > 0 && string.Equals(Path.GetFileName(Encoding.UTF8.GetString(path[..length])), fileName, StringComparison.Ordinal)) return true;
            ulong next = address + size;
            if (next <= cursor) return false;
            cursor = next;
        }
        return false;
    }

    private static Dictionary<string, string>? MacEnvironment(int pid)
    {
        int* mib = stackalloc int[3];
        mib[0] = CTL_KERN;
        mib[1] = KERN_ARGMAX;
        int argmax = 0;
        nuint size = sizeof(int);
        if (sysctl(mib, 2, &argmax, &size, null, 0) != 0 || argmax <= 0) return null;
        byte[] buffer = new byte[argmax];
        mib[1] = KERN_PROCARGS2;
        mib[2] = pid;
        size = (nuint)argmax;
        fixed (byte* b = buffer)
        {
            if (sysctl(mib, 3, b, &size, null, 0) != 0) return null;
        }
        int n = (int)size;
        if (n < sizeof(int)) return null;
        int argc = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        int pos = sizeof(int);
        while (pos < n && buffer[pos] != 0) pos++;              // the executable path
        while (pos < n && buffer[pos] == 0) pos++;              // its padding
        for (int i = 0; i < argc && pos < n; i++)               // the arguments
        {
            while (pos < n && buffer[pos] != 0) pos++;
            pos++;
        }
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        while (pos < n && buffer[pos] != 0)                     // the environment, until an empty string
        {
            int from = pos;
            while (pos < n && buffer[pos] != 0) pos++;
            string kv = Encoding.UTF8.GetString(buffer, from, pos - from);
            pos++;
            int eq = kv.IndexOf('=');
            if (eq > 0) env[kv[..eq]] = kv[(eq + 1)..];
        }
        return env;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, byte* buffer, int buffersize);

    [DllImport("libc", SetLastError = true)]
    private static extern int sysctl(int* name, uint namelen, void* oldp, nuint* oldlenp, void* newp, nuint newlen);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    // ---- Windows ----

    private const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;

    private static string WindowsThreadDescription(int tid)
    {
        nint thread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, 0, (uint)tid);
        if (thread == 0) return "?";
        try
        {
            if (GetThreadDescription(thread, out nint description) < 0 || description == 0) return "?";
            try
            {
                string? text = Marshal.PtrToStringUni(description);
                return string.IsNullOrEmpty(text) ? "-" : text;
            }
            finally { LocalFree(description); }
        }
        finally { CloseHandle(thread); }
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern nint OpenThread(uint desiredAccess, int inheritHandle, uint threadId);

    [DllImport("kernel32")]
    private static extern int GetThreadDescription(nint thread, out nint description);

    [DllImport("kernel32")]
    private static extern nint LocalFree(nint memory);

    [DllImport("kernel32")]
    private static extern int CloseHandle(nint handle);
}

// What the runtime and dbgshim name after a process: the startup handshake while it happens, and what is left of it,
// the debug transport and the default port once the process is gone. See the header for the names, per platform.
static class PidNamedObjects
{
    // The startup handshake's objects for every tracked start of pid.
    public static string Observe(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            string name = $"TelestoStartupEvent_{pid:x8}";
            nint handle = OpenEventW(SYNCHRONIZE, 0, name);
            if (handle == 0) return $"{name} absent (error {Marshal.GetLastPInvokeError()})";
            CloseHandle(handle);
            return $"{name} present";
        }
        if (OperatingSystem.IsMacOS())
        {
            var parts = new List<string>();
            foreach (long start in Tracked.StartsOf(pid))
            {
                long key = ProcessView.NameKey(start);
                foreach (string kind in new[] { "st", "co" })
                {
                    bool fifo = File.Exists(Path.Combine(Path.GetTempPath(), $"clr-debug-pipe-{pid}-{key}-{kind}"));
                    bool sem = MacSemaphoreExists(MacSemaphoreName(kind, pid, key));
                    parts.Add($"{kind}: FIFO {(fifo ? "present" : "absent")}, semaphore {(sem ? "present" : "absent")}");
                }
            }
            return parts.Count == 0 ? "no tracked start for this pid" : string.Join("; ", parts);
        }
        string hex = pid.ToString("x8", CultureInfo.InvariantCulture), dec = pid.ToString(CultureInfo.InvariantCulture);
        string[] all;
        try { all = Directory.GetFiles("/dev/shm", "sem.clr*"); } catch (IOException) { all = []; }
        var mine = all.Where(f => f.Contains(hex, StringComparison.Ordinal) || f.Contains(dec, StringComparison.Ordinal)).ToList();
        return $"{(mine.Count == 0 ? "none for this pid" : string.Join(", ", mine.Select(Path.GetFileName)))} (sem.clr* in /dev/shm: {all.Length})";
    }

    // What the runtime and dbgshim left named after a process that is gone: its name, and how to remove it.
    public static IEnumerable<(string Name, Action Remove)> LeftBy(Proc p)
    {
        if (OperatingSystem.IsWindows()) yield break;
        string tmp = Path.GetTempPath();
        long key = ProcessView.NameKey(p.Start);
        var files = new List<string>();
        if (OperatingSystem.IsLinux())
            foreach (string kind in new[] { "clrst", "clrco" })
            {
                string sem = Path.Combine("/dev/shm", $"sem.{kind}{p.Pid:x8}{p.Start:x16}");
                if (File.Exists(sem)) files.Add(sem);
            }
        try
        {
            files.AddRange(Directory.GetFiles(tmp, $"clr-debug-pipe-{p.Pid}-{key}-*"));
            files.AddRange(Directory.GetFiles(tmp, $"dotnet-diagnostic-{p.Pid}-{key}-*"));
        }
        catch (IOException) { }
        foreach (string f in files)
            yield return (f, () => File.Delete(f));
        if (OperatingSystem.IsMacOS())
            foreach (string kind in new[] { "st", "co" })
            {
                string name = MacSemaphoreName(kind, p.Pid, key);
                if (MacSemaphoreExists(name))
                    yield return ($"semaphore {name}", () => { if (sem_unlink(name) != 0) throw new IOException($"sem_unlink errno {Marshal.GetLastPInvokeError()}"); });
            }
    }

    static string MacSemaphoreName(string kind, int pid, long key) => $"/clr{kind}{pid:x8}{key:x16}";

    // sem_open without O_CREAT opens an existing semaphore or fails; it is variadic in C, and no variadic argument is
    // passed, so the two fixed arguments travel in registers as for a plain call (Apple arm64 ABI).
    static bool MacSemaphoreExists(string name)
    {
        nint sem = sem_open(name, 0);
        if (sem == -1) return false;                            // SEM_FAILED
        sem_close(sem);
        return true;
    }

    private const uint SYNCHRONIZE = 0x00100000;

    [DllImport("libc", SetLastError = true)]
    private static extern nint sem_open(string name, int oflag);

    [DllImport("libc")]
    private static extern int sem_close(nint sem);

    [DllImport("libc", SetLastError = true)]
    private static extern int sem_unlink(string name);

    [DllImport("kernel32", EntryPoint = "OpenEventW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint OpenEventW(uint desiredAccess, int inheritHandle, string name);

    [DllImport("kernel32")]
    private static extern int CloseHandle(nint handle);
}

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
    public static IEnumerable<long> StartsOf(int pid) => s_procs.Keys.Where(k => k.Pid == pid).Select(k => k.Start);
}

// A process the probe will only kill if it is still the same process: pid plus start time.
sealed class Proc
{
    private readonly string _what;
    public int Pid { get; }
    public long Start { get; }
    public bool Killed { get; private set; }

    private Proc(int pid, long start, string what) { Pid = pid; Start = start; _what = what; }

    public static Proc Of(int pid, string what) => Tracked.Add(new Proc(pid, ProcessView.StartTime(pid), what));

    public bool IsAlive => Start >= 0 && ProcessView.StartTime(Pid) == Start;
    public override string ToString() => $"{_what} {Pid}";

    public string? KillIfAlive()
    {
        if (!IsAlive || !ProcessView.Kill(Pid)) return null;
        Killed = true;
        return $"{_what} {Pid} killed";
    }
}

// A launched target: `dotnet <target dll> <args>` (or `dotnet exec --runtimeconfig <json> <dll> <args>`), its stdout
// collected line by line.
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
        psi.ArgumentList.Add(Probe109.TargetDll);
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment.Remove("DOTNET_DiagnosticPorts");
        psi.Environment.Remove("DOTNET_STARTUP_HOOKS");
        if (diagPorts is not null) psi.Environment["DOTNET_DiagnosticPorts"] = diagPorts;
        if (hook) psi.Environment["DOTNET_STARTUP_HOOKS"] = Probe109.HookDll;
        var p = Process.Start(psi)!;
        Probe109.Say($"    spawned {p.Id}: dotnet {string.Join(' ', psi.ArgumentList)}{(diagPorts is null ? "" : $" [DOTNET_DiagnosticPorts={diagPorts}]")}{(hook ? " [startup hook]" : "")}");
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

// The probe's end of DOTNET_DiagnosticPorts=<address>,connect: accepts every dial, reads its advertise, and keeps each
// connection by pid. The transport is a Unix socket (Linux, macOS) or a named pipe (Windows); the protocol is the same.
abstract class ReverseServer : IDisposable
{
    public sealed record Conn(Stream Stream, int Pid, long Ticks, string Cookie);

    private static readonly byte[] AdvertiseMagic = "ADVR_V1\0"u8.ToArray();
    private static readonly byte[] IpcMagic = "DOTNET_IPC_V1\0"u8.ToArray();
    private readonly List<Conn> _all = new();
    private readonly HashSet<Conn> _taken = new();
    private volatile bool _stopped;
    private volatile WireLayoutException? _layoutError;

    protected ReverseServer(string address) => Address = address;

    public string Address { get; }

    public static ReverseServer Bind(string address) =>
        OperatingSystem.IsWindows() ? new NamedPipeReverseServer(address) : new UnixSocketReverseServer(address);

    protected void StartAccepting() => new Thread(AcceptLoop) { IsBackground = true, Name = "p109-accept" }.Start();

    // The next connection, or null once the listener is closed.
    protected abstract Stream? AcceptNext();

    // Closes the listener and removes the address: what a DrHook crash would leave the runtime.
    protected abstract void CloseListener();

    private void AcceptLoop()
    {
        while (!_stopped)
        {
            Stream? s;
            try { s = AcceptNext(); }
            catch (Exception) { return; }                       // the listener was closed
            if (s is null) return;
            try
            {
                byte[] adv = ReadExactly(s, 34);
                if (!adv.AsSpan(0, 8).SequenceEqual(AdvertiseMagic))
                    throw new WireLayoutException($"advertise magic {Convert.ToHexString(adv, 0, 8)}, expected ADVR_V1\\0");
                string cookie = new Guid(adv.AsSpan(8, 16)).ToString();
                ulong pid = BinaryPrimitives.ReadUInt64LittleEndian(adv.AsSpan(24, 8));
                Proc.Of((int)pid, "dialler");                    // tracked for containment, whoever it is
                lock (_all) _all.Add(new Conn(s, (int)pid, Probe109.Now(), cookie));
            }
            catch (WireLayoutException e) { Probe109.Say($"    WIRE: {e.Message}"); s.Dispose(); _layoutError = e; }
            catch (Exception e) { Probe109.Say($"    advertise read failed: {e.Message}"); s.Dispose(); }
        }
    }

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
    public string Resume(Conn c)
    {
        byte[] header = new byte[20];
        IpcMagic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14, 2), 20);
        header[16] = 0x04;
        header[17] = 0x01;
        c.Stream.Write(header);
        c.Stream.Flush();
        byte[] reply = ReadExactly(c.Stream, 20);
        if (!reply.AsSpan(0, 14).SequenceEqual(IpcMagic))
            throw new WireLayoutException($"reply magic {Convert.ToHexString(reply, 0, 14)}");
        ushort size = BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(14, 2));
        byte set = reply[16], id = reply[17];
        if (set != 0xFF || (id != 0x00 && id != 0xFF) || size != 24)
            throw new WireLayoutException($"reply set 0x{set:X2} id 0x{id:X2} size {size}");
        int code = BinaryPrimitives.ReadInt32LittleEndian(ReadExactly(c.Stream, 4));
        string result = id == 0x00 ? (code == 0 ? "OK" : $"OK code 0x{code:X8}") : $"error 0x{code:X8}";
        c.Stream.Dispose();
        return result;
    }

    // Reads exactly count bytes within 5 s.
    private static byte[] ReadExactly(Stream s, int count)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (read < count)
        {
            int n;
            try { n = s.ReadAsync(buffer.AsMemory(read, count - read), bound.Token).AsTask().GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { throw new IOException($"nothing within 5 s after {read} of {count} bytes"); }
            if (n == 0) throw new IOException($"connection closed after {read} of {count} bytes");
            read += n;
        }
        return buffer;
    }

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        CloseListener();
        lock (_all) foreach (var c in _all) c.Stream.Dispose();
    }
}

sealed class UnixSocketReverseServer : ReverseServer
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _closing = new();

    public UnixSocketReverseServer(string path) : base(path)
    {
        File.Delete(path);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(path));
        _listener.Listen(64);
        StartAccepting();
    }

    // AcceptAsync with a token, not Accept(): on macOS a close does not wake a blocked accept(), and Socket.Dispose then
    // waits for it forever (rev 1 on omega). The token is cancelled first, so no accept is in flight at the close.
    protected override Stream? AcceptNext() =>
        new NetworkStream(_listener.AcceptAsync(_closing.Token).AsTask().GetAwaiter().GetResult(), ownsSocket: true);

    protected override void CloseListener()
    {
        _closing.Cancel();
        _listener.Dispose();
        File.Delete(Address);
    }
}

// One pipe instance always waits: the runtime makes one CreateFileA per dial and no WaitNamedPipe (see the header), so
// a dial between instances would find nothing. The next instance is made before a connection is handed on. While no
// instance exists, the name does not exist.
sealed class NamedPipeReverseServer : ReverseServer
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _closing = new();
    private NamedPipeServerStream? _waiting;
    private bool _closed;

    public NamedPipeReverseServer(string name) : base(name)
    {
        _waiting = NewInstance();
        StartAccepting();
    }

    private NamedPipeServerStream NewInstance() =>
        new(Address, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    protected override Stream? AcceptNext()
    {
        NamedPipeServerStream instance;
        lock (_gate)
        {
            if (_closed || _waiting is null) return null;
            instance = _waiting;
        }
        try { instance.WaitForConnectionAsync(_closing.Token).GetAwaiter().GetResult(); }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException)
        {
            instance.Dispose();
            return null;
        }
        lock (_gate) _waiting = _closed ? null : NewInstance();
        return instance;
    }

    protected override void CloseListener()
    {
        lock (_gate) _closed = true;
        _closing.Cancel();
        lock (_gate)
        {
            _waiting?.Dispose();
            _waiting = null;
        }
    }
}

// The default port (Q6): <temp>/dotnet-diagnostic-<pid>-<key>-socket, or \\.\pipe\dotnet-diagnostic-<pid> on Windows.
static class DefaultPort
{
    public static string State(int pid)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string name = $"dotnet-diagnostic-{pid}";
                bool present = Directory.GetFiles(@"\\.\pipe\").Any(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
                return present ? $"present (\\\\.\\pipe\\{name})" : "absent";
            }
            string[] found = Directory.GetFiles(Path.GetTempPath(), $"dotnet-diagnostic-{pid}-*-socket");
            return found.Length == 0 ? "absent" : $"present ({Path.GetFileName(found[0])})";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return $"unknown ({e.Message})"; }
    }

    public static string Resume(int pid)
    {
        var call = Task.Run(() => new DiagnosticsClient(pid).ResumeRuntime());
        try
        {
            return call.Wait(TimeSpan.FromSeconds(5)) ? "returned" : "no answer within 5 s";
        }
        catch (AggregateException e) { return $"threw {e.InnerException?.GetType().Name}: {e.InnerException?.Message}"; }
    }
}
