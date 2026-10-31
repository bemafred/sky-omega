# ADR-016: How a Debugged Program Ends — A Natural End by Default; Every Extraordinary End Known, Bounded and Reported, on Every Platform

**Status:** Proposed — 2026-10-31 (Emergence; reframes the 2026-10-30 draft, "Deterministic Target Teardown Across
Platforms")

## Context

### What DrHook is for

DrHook lets an LLM inspect running code live, a new design for instance, stating a hypothesis before each
observation: traditional debugging, done by an LLM. It gives the LLM finer feedback than test results, so fewer
custom probes are needed; the LLM can step through a probe, a test or the implementation itself; and it can work
with coding patterns it was not trained on. The common session is therefore a healthy program under development,
and an operator runs many such sessions an hour in one `drhook-mcp` process. Their routine end must leave the
debugger usable: a `drhook-mcp` that can no longer attach ends the operator's debugging until a person restarts it.

Debugging also meets programs that misbehave: a loop missing its exit condition, a deadlock, runaway recursion.
DrHook must handle those as well, and know what each costs. How a session should end depends on why it began
(Debugging scenarios, below). And DrHook is itself debugged and tested, by DrHook among others, so it must behave
well in both roles.

### What DrHook does today (verified 2026-10-31)

- **Ending an owned session** (`DebugSession.Dispose`; `drhook_stop` for a target started with `drhook_launch`):
  the callback pump stops, the target is resumed (`TryResumeForDetach`), SIGTERM is sent at once (`RequestExit`),
  and if the target is still alive after `NaturalExitTimeout` (2 s), a `TargetStuckAtDispose` anomaly is reported
  and the target is killed. Nothing waits for the program's work to finish, and the debugger is still attached and
  handling the target's events when the signal lands.
- **On Windows** `RequestExit` cannot signal (`PosixSignals.Kill` throws `PlatformNotSupportedException`; the
  console-control path was never built), so every owned session end reports two anomalies and terminates the
  process.
- **`drhook_detach`** ends the debugger's side cleanly (breakpoints deactivated, the process stopped, then detached)
  and leaves the program running. Stopping before detaching is the fix for the `drhook-clean-detach` limit
  (2026-05-21): a detach that raced mscordbi's flush of queued events crashed the debugger.
- **`drhook_kill`** (`DebugSession.Abandon`): SIGTERM, 200 ms, then SIGKILL.

### How the routine end came to be a forced one

[Finding 63](../../../poc/drhook-engine/findings/63-mch-re-2-investigation.md) (2026-05-24) chose "kill first" as
the simpler fix for a crash when the test harness killed a target right after a detach.
[Finding 67](../../../poc/drhook-engine/findings/67-lifecycle-discipline.md) (2026-05-25) said kill-first is the
wrong default. [ADR-008](ADR-008-process-lifecycle-discipline.md) then made SIGTERM the first stage and counted
"exits on SIGTERM" as a natural exit.
[Finding 68](../../../poc/drhook-engine/findings/68-process-lifecycle-ground-truth.md) shows what that exit is for a
program with no handler: CoreCLR's default disposition ends it with exit code 143, a death by signal, however far
its work had got. Only a program that registers a handler (`PosixSignalRegistration`, `Console.CancelKeyPress`)
cancels cooperatively and exits 0. Finding 68's probes ran without a debugger attached.

So a forced stop is DrHook's routine session end.
[Finding 100](../../../poc/drhook-engine/findings/100-orphaned-transport-read-holds-dac-lock.md) is one consequence:
a target that dies while mscordbi's Win32 event thread reads its memory leaves that debugger process unable to
attach or launch again. It is proven on macOS (probe 100, 5 of 5 on omega). On the GitHub macOS runners it hit none
of 8 jobs before a test change of ours (4b9812a) altered the timing of the test that runs before the one that hangs,
4 of 8 with that change, and 3 of 8 after the change was removed (8ed9701). So the hang occurs with today's code;
whether the change made it more frequent, counts this small do not show. Earlier consequences of forced stops were
the test-host crash of 2026-05-25 (ADR-008's Context) and finding 63's crash. Two single-test timeouts on those
runners, after which the suite ran normally, were a different failure whose cause is unknown.

The 2026-10-30 draft of this ADR asked how to make termination deterministic on each platform. That was the wrong
question: it accepted the forced stop as routine and planned to engineer around its consequences.

### What established debuggers do

- The [Debug Adapter Protocol](https://microsoft.github.io/debug-adapter-protocol/specification) (DAP), which VS
  Code and other editors speak, starts a session with `launch` or `attach`. `disconnect` ends it: by default the
  adapter terminates a program it launched and leaves one it attached to running; `terminateDebuggee` and
  `suspendDebuggee` override that. `terminate` asks for a graceful shutdown, typically a signal the program can
  intercept; the program may refuse, and the session then continues. A client may use `terminate` as the first stage
  of a stop and fall back to `disconnect`. The `exited` event reports that the program exited; `terminated` reports
  that debugging ended, which need not mean the program did.
- [Visual Studio](https://learn.microsoft.com/visualstudio/debugger/debug-multiple-processes): Stop Debugging ends
  the processes it launched and detaches from those it attached to; Terminate All ends all of them; Detach All leaves
  all of them running; each process can be set to "Detach when debugging stops".
- VS Code asks for a debug adapter at the start of each session, and the Go extension starts a new adapter process
  every time (vscode-go, `goDebugFactory.ts`).

So the convention treats ending a launched program as the normal end of a session: a person who presses Stop wants
it gone. DrHook follows it today (`drhook_stop` ends a launched program and detaches from an attached one), and DAP's
`terminate` is what this ADR calls cancellation: a request the program may refuse. Two things differ for DrHook. Its
operator, an LLM, runs many sessions an hour and cannot restart a stuck debugger itself. And `drhook-mcp` hosts
mscordbi in one long-lived process for all its sessions, so a debugger left stuck costs every later session, not
one.

## The ways a debugged program ends

| How it ends | Who decides | DrHook's part | Known under DrHook | Not known |
|-------------|-------------|---------------|--------------------|-----------|
| **Natural completion:** the work is done, main returns | the program | end its own side in order: receive the exit, or detach cleanly first and let the program finish | the clean detach works on macOS under a flood (probe 08, 3 of 3) | whether a program ending on its own, debugger attached, ever leaves the debugger unusable; whether a program that exits during or right after a detach is safe (finding 66's fix covers a target already dead when Dispose runs; finding 63's crashes involved a kill) |
| **Cancellation:** a stop request the program honours | the client asks; the program chooses when | send the request, keep observing, wait | without a debugger: with a handler the program cleans up and exits 0; without one the same signal is an abort, exit 143 (finding 68) | behaviour under a debugger; whether DrHook can tell in advance that a program handles the request; Windows (a console control event, never built) |
| **Won't end:** a loop missing its exit condition, a deadlock | nobody; it is a bug | observe it, which is what debugging is; a kill only as a separate, deliberate, recorded decision | on macOS a kill while mscordbi was still busy with the target has left the debugger unusable (finding 100) and crashed the debugger's host process (2026-05-25; finding 63) | a kill whose consequence is bounded on each platform; what each platform's consequence is |
| **Crashes itself:** runaway recursion and stack overflow, `Environment.FailFast`, an unhandled exception, a native fault | nobody | report the crash; survive it | nothing; not tested | everything. A crash lands at a moment nobody chose, like a kill, and discipline cannot prevent it. Whether it can leave mscordbi as finding 100 does is unknown |
| **Machine catastrophe:** a freeze that needs a hard reset | nobody | never cause it | none observed | the paths to it. Plausible ones: a program held at a breakpoint while it holds something the whole system waits on; unbounded growth (finding 100 leaves one blocked thread and two pipe files per killed target; omega's `$TMPDIR` held 954 such files); runaway work started through function evaluation |

A hard reset is the one end nothing can be recovered from: no session survives it, only what was persisted before.

## Debugging scenarios

How a session should end depends on why it began. Each scenario has its own owner of the program's life, its own
natural end and its own hazards, and DrHook and its client must know which scenario a session is in.

| Scenario | How it starts | Whose program | Its natural end | Ending the session before that | Lifecycle hazards |
|----------|---------------|---------------|-----------------|--------------------------------|-------------------|
| **Inspecting code being designed:** a probe, a POC, a file-based app | `drhook_launch`, breakpoints armed before main | DrHook's | it runs to completion | continue it to the end, or detach and let it finish | mixing runtime versions in one debugger process (finding 86); a breakpoint on a file-based target that stays pending |
| **Debugging a failing test,** instead of writing a probe that logs | the test executable launched (MTP), or `dotnet test` started with `VSTEST_HOST_DEBUG=1` and its test host attached to (VSTest) | DrHook's for a test executable it launched; otherwise the test runner's | the test run finishes and reports | remove the breakpoints and continue to the end | the runner waits for its host: a host killed mid-handshake leaves vstest.console waiting 90 s (finding 94); an early attach can skip the host's debug hold (finding 91); a host left waiting for a debugger is orphaned (finding 90); a test framework's timeout may expire while the test is paused (NUnit does not enforce its timeouts under a debugger; MSTest and xunit v3 not established) |
| **Attaching to a running program,** a debug build, to learn the context of a reported bug | `drhook_attach` | its owner's: a user, a service manager | none on DrHook's side: the program goes on after the session | detach and leave it running; never kill it | a breakpoint stops the program for everyone using it: their requests time out, health checks fail, a watchdog may restart it and end the session; a program started with `DOTNET_EnableDiagnostics=0` refuses the attach |
| **Diagnosing a hang** | attach; or a launched program stops making progress | its owner's, or DrHook's | none: it does not end | a stack snapshot first (`drhook_snapshot`, through EventPipe like `dotnet-stack`), which does not stop it at a breakpoint; then pause and inspect; detach, or, for DrHook's own program, a kill as the extraordinary act | the "won't end" row above |
| **Catching a crash** | launch, with exception breakpoints to stop before the crash | DrHook's | the crash | — | the "crashes itself" row above; whether a stack overflow gives the debugger a chance to stop is unknown |
| **Hunting an intermittent failure** ([ADR-015](ADR-015-run-until-anomaly.md), run until an anomaly) | many launches in a row in one debugger process | DrHook's | each run ends on its own | — | each end must leave the debugger clean, and nothing may pile up: finding 100 leaves a blocked thread and two pipe files per killed target |
| **A program with a window** (Avalonia) | launch or attach | either | the user closes it, or the app quits on its own terms | detach and leave it running | a UI thread held at a breakpoint freezes the window; after a visual capture, the detach reports `uiLiveness`, whether the window recovered |
| **A server or a service** (the Generic Host, ASP.NET Core) | launch or attach | either | a stop request: the host's `ConsoleLifetime` turns SIGINT or SIGTERM into `StopApplication`, and shutdown gets `HostOptions.ShutdownTimeout` (30 s by default) | a stop request | a program held at a breakpoint cannot shut down: continue it before asking it to stop |

### DrHook as a debugger and as a debug target

DrHook is debugged and tested too: its probes, its integration tests and `drhook-mcp` run under DrHook, and under
other debuggers (Rider is the reference for correctness). It must behave well in both roles.

- **As a program, it ends naturally.** `drhook-mcp` must end when its client closes the connection, and a test host
  that uses DrHook.Engine when its tests have run, each ending its own sessions naturally first.
- **One debugger per process.** An outer debugger attaches to DrHook's process, never to DrHook's target, which
  DrHook already debugs.
- **Paused by an outer debugger,** DrHook's own waits go on counting wall-clock time and may expire while it is
  paused, and its target goes on running, or stays held, unobserved. A bound that expires because DrHook itself was
  paused must not be reported as the target's fault. How DrHook tells the two apart is open.
- **Runtime versions.** An outer debugger is locked to the first runtime version it debugs too (finding 86).
- **Observing it without stopping it.** When DrHook's test host hung on CI, it was read with `sample` and
  `dotnet-stack`, which do not stop it at a breakpoint. DrHook's own stack snapshot uses the same mechanism as
  `dotnet-stack` (EventPipe) and should serve there as well.

## Decision (proposed)

### Principles

1. **A natural end is the default.** A session ends by letting the program finish, or by detaching cleanly and
   leaving the program to finish. DrHook never terminates a program as cleanup.
2. **Cancellation is a request, not a stop.** When a program must end before its work is done, it is asked through
   a channel it honours, and DrHook watches it end at its own pace. A signal the program does not handle is not
   cancellation, and DrHook says so rather than sending it as one.
3. **A forced end is extraordinary.** A kill is a separate, explicit decision (`drhook_kill`, `Abandon`), with its
   reason and expected cost stated first. It is never the fallback of an ordinary end.
4. **Each scenario has its own natural end.** A probe or a test runs to completion; a program DrHook attached to
   goes on after the session; a server ends on a stop request. DrHook knows how each session started and whose
   program it is; the client and the operator know which scenario they are in, and end the session accordingly.
5. **A program others use is not stopped lightly.** In a program DrHook did not start, a breakpoint stops it for
   everyone using it. Observing without stopping comes first.
6. **DrHook survives the ends it did not choose.** A program that crashes, or is killed by someone else, leaves
   DrHook able to report it and, where the platform allows, to keep working.
7. **Every consequence is bounded, detected and reported.** No DrHook call waits without a bound. When an end
   leaves the debugger process unusable, DrHook detects it and reports a typed anomaly that names the level reached
   and what can still be done (for example: restart the debugger process).
8. **Each consequence is contained at the smallest level:** session, then debugger process, then client, then
   machine. Nothing DrHook does may risk the machine.
9. **Record before an extraordinary act.** Before a kill, or anything else that could freeze, the intent and the
   state go to persistent storage (Mercury), since a hard reset erases everything else.
10. **DrHook behaves well as a debug target too.** Its own processes end naturally and honour cancellation, and a
    bound that expires while DrHook itself is paused is not blamed on its target.
11. **Native, per platform** (kept from the first draft). Every claim about a platform comes from a run on that
    platform, with no emulation and no inference from another platform; a CI runner counts as the platform it is.
    Results are reported N of N, and a result that varies is itself a finding. Each construction is reviewed with
    Martin before it runs, and each step's result goes into a finding and into Mercury before the next.

### What each party must know

- **DrHook** (the engine): how each session started (launched or attached) and whose program it is; the target's
  state; which end is under way; whether the target can receive a cancellation request; each consequence, as a typed
  anomaly; and whether a bound expired because DrHook itself was paused.
- **Its clients** (`drhook-mcp` and other hosts): the scenario, the ends available, and which one is natural for it
  (the table above). The order: let it finish, then ask it to stop, then kill, each a separate step, observed before
  the next. In a program DrHook did not start: detach, never kill.
- **The operator** (an LLM or a person): the same. In a program others use, observe without stopping first. Never
  kill to clean up. State the expected cost before a kill, and record it first. The operator's guide is DRHOOK.md's
  lifecycle section, which gains the scenarios.

### What this would change (decided in Step 5, not now)

- `DebugSession.Dispose` of an owned session and `drhook_stop` stop sending SIGTERM and SIGKILL as their ordinary
  path. ADR-008's Decision 1 is revisited: SIGTERM remains a cancellation request for a program that handles it, and
  is not counted as a natural exit.
- Waits without a bound get one (the attach that hangs in finding 100), and the state they detect is reported.
- The kill path (`Abandon`, `drhook_kill`) stays, with each platform's consequence characterized and reported.
- The integration tests follow the same discipline (below).

### Tests that fit the problem

- **Routine:** finite targets that end on their own. Each test then checks that the debugger can still attach in the
  same process, so a test that leaves the debugger unusable fails itself, not the test after it.
- **Cancellation:** targets that honour a stop request (a SIGTERM handler that cancels a `CancellationToken`; the
  Generic Host). The test asks mid-work, and checks that the program ends at its own pace, cleanly, and that the
  debugger stays usable.
- **Extraordinary:** a target whose loop has no exit condition (it needs a kill), one that overflows its stack, one
  that calls `Environment.FailFast`. Each runs in a test process of its own, bounded in time and resources, and
  checks that the consequence is detected, reported and contained, not that nothing happens.

The routine tests cover the scenarios: a probe launched and run to completion; a failing test debugged to the end of
its run, under MTP and under VSTest; an attach to a running program, then a detach that leaves it running; and DrHook
as the target, a DrHook test host debugged by `drhook-mcp` and left to end on its own. The cancellation tests include
a Generic Host server stopped through its own lifetime.

The flood and pause tests of today end their sessions mid-work by design. Each becomes a routine test (let the work
finish) or an extraordinary one, named as such. Anything that could freeze a machine runs only on a disposable VM or
CI runner, never on omega or zbook.

### The platforms and where each is observed

| Platform | Our machines | GitHub runner | Notes |
|----------|--------------|---------------|-------|
| macOS arm64 | omega (metal, macOS 27, Developer Mode on) | `macos-latest` (VM, macOS 26.6, 3 cores) | where finding 100 was found and reproduced |
| macOS x64 | — | `macos-26-intel` (VM, macOS 26.6) | runner only |
| Linux x64 | zbook (Microsoft .NET 10.0.14, `ptrace_scope=1`, dash); elite (Microsoft 10.0.13, suspected RAM) | `ubuntu-24.04` | |
| Linux arm64 | omega-ubuntu (VM, Ubuntu's .NET 10.0.13, `ptrace_scope=1`, dash) | `ubuntu-24.04-arm` | the only Linux arm64 machine runs the distribution's build |
| Windows x64 | — | `windows-latest` | runner only; no debugger transport |
| Windows arm64 | omega-windows (VM, 10.0.14) | `windows-11-arm` | no debugger transport |

What differs between them, from the first draft: which mscordbi, DAC and dbgshim are loaded (the target's runtime:
Microsoft's or Ubuntu's build, 10.0.9 to 10.0.14); how target memory is read (directly when `PAL_OpenProcessMemory`
succeeds, which on macOS needs `task_for_pid` and on Linux `/proc/<pid>/mem` under `ptrace_scope`, otherwise over
the debugger transport; Windows has no transport, `FEATURE_DBGIPC` being Unix-only); how a target's death is noticed
(mscordbi's PAL on Unix, a process handle on Windows); and the machine itself (metal or VM, 3 to 18 cores, OS,
kernel, libc, `/bin/sh`, signal numbers).

### Steps

Each step answers one question and ends with a recorded result and a review before the next.

**Step 0 — Preconditions (Martin's decisions).**
- elite: run memtest86+ before any result from it counts.
- Linux arm64: whether to add Microsoft's .NET beside Ubuntu's on omega-ubuntu, so the debugger code can be
  compared on one machine.
- Linux stack capture: `ptrace_scope=1` stops `gdb` from attaching to a non-descendant. Options: sudo, a temporary
  `ptrace_scope=0` during a probe, or constructions that need no capture.
- Which machines may run the extraordinary probes of Step 4 (proposed: CI runners and VMs only).
- Every machine at the same commit, with its runtimes listed (`dotnet --list-runtimes`).

**Step 1 — Platform facts (probe 101, read-only).** Attach to an idle target, change nothing, and record the
debugger code loaded, how memory is read, and the environment. It ends the session with a clean detach and leaves
the target to finish. Runs on each of our machines, and on the six runners through a `workflow_dispatch` job (a
workflow change, reviewed when proposed).

**Step 2 — The natural end.** Hypothesis: while the debugger handles a managed event the target is held until the
debugger continues it, so a program that ends on its own cannot die in the middle of a debugger read, and a natural
end never leaves the debugger unusable. Constructions: a flooding target that ends on its own, under the debugger,
N times in one process, after which a further attach must succeed; a clean detach while the target floods, the
target then finishing on its own; a target that exits right after the detach (finding 63's case, without the kill).
Then the same natural end in each scenario: a launched probe, a test run under MTP and under VSTest, an attach
followed by a detach, and DrHook's own test host as the target.

**Step 3 — Cancellation.** A target with a SIGTERM handler that cancels its work, asked mid-flood under the
debugger; the same target without a handler, to record what an unhandled signal does under a debugger; a Generic
Host server stopped through its own lifetime. Windows: a console control event, built and reviewed before it runs.

**Step 4 — The extraordinary ends,** each in a process of its own: a kill (probe 100's modes on macOS; per-platform
constructions built from Step 1's facts elsewhere), a stack overflow, `Environment.FailFast`. Result, per platform:
what each consequence is, which level it reaches, and whether DrHook can detect it.

**Step 5 — Decide (Epistemics).** From Steps 2 to 4: the session end for each scenario; what replaces ADR-008's
Decision 1; the bounds, detection and anomalies for the extraordinary ends; how DrHook tells its own pauses from its
target's; whether containment needs the debugger in a process of its own per session, as the Go extension for VS
Code does. This ADR moves to Accepted with that decision.

**Step 6 — Engineering.** Implement; rewrite the tests in the three kinds above; run CI with the hang watcher until
the criteria below hold, then remove the watcher. Update ADR-007, ADR-008, DRHOOK.md's lifecycle section and the
MCP tools' descriptions.

### Acceptance criteria

- On every platform, a natural end and a clean detach never leave the debugger unusable: N of N in Step 2's probes,
  and 10 consecutive green CI runs on all six runners with the watcher recording nothing.
- No ordinary session end sends a signal the program has not registered for, or kills it.
- Every scenario in the table ends naturally on every platform, and DRHOOK.md and the MCP tools' descriptions tell
  the operator which end fits which scenario.
- Every extraordinary end is detected and reported within a bound, naming the level it reached. No DrHook call waits
  without a bound.
- No test or probe risks the machine.

## Not in this ADR

Separate items, to be taken up with Martin:

- After a target is killed, `GetStackFrames` and `GetLocals` threw `NullReferenceException` instead of a structured
  error (probe 100, kill mode). Principle 6 bears on it.
- A line breakpoint set on an attached file-based target stayed pending and never bound.
- An upstream report to dotnet/runtime of mscordbi's unbounded read. Secondary: the hang follows from our forced
  stop. Reconsidered after Step 4.
- The cause of the two single-test timeouts of 1.8.38 and 1.8.39.

## References

- [Finding 100](../../../poc/drhook-engine/findings/100-orphaned-transport-read-holds-dac-lock.md) and probe 100
  (`poc/drhook-engine/100-orphaned-read-smoke.cs`)
- [ADR-008](ADR-008-process-lifecycle-discipline.md) (Decision 1, revisited here) and
  [ADR-007](ADR-007-teardown-concurrency-test-debug.md) Phase 9 (the cross-platform validation campaign)
- Findings 63 (kill-first chosen), 66 (target-death detection), 67 (natural endings by default), 68 (signal
  disposition without a debugger), 86 (one runtime version per debugger process), 90 and 91 (the VSTest host's
  process group and hold), 94 (the VSTest handshake), 87, 92, 96 and 99 (first runs on Linux and Windows)
- [ADR-015](ADR-015-run-until-anomaly.md) (run until an anomaly)
- [Debug Adapter Protocol specification](https://microsoft.github.io/debug-adapter-protocol/specification): `launch`,
  `attach`, `disconnect`, `terminate`, the `exited` and `terminated` events
- [Visual Studio: debug multiple processes](https://learn.microsoft.com/visualstudio/debugger/debug-multiple-processes):
  Stop Debugging, Terminate All, Detach All
- vscode-go, [`goDebugFactory.ts`](https://go.googlesource.com/vscode-go/+/refs/tags/v0.24.2/src/goDebugFactory.ts):
  a new adapter process for each session
- [.NET Generic Host](https://learn.microsoft.com/aspnet/core/fundamentals/host/generic-host) and
  [`HostOptions.ShutdownTimeout`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.hosting.hostoptions.shutdowntimeout)
- [NUnit issue 3054](https://github.com/nunit/nunit/issues/3054): test timeouts while a debugger is attached
- Limits [drhook-clean-detach](../../limits/drhook-clean-detach.md) (stop before detaching) and
  [drhook-detach-exit-race](../../limits/drhook-detach-exit-race.md) (a target that exits at Dispose; finding 66)
- [DRHOOK.md](../../../DRHOOK.md), "Lifecycle discipline & common pitfalls"
- [Runbook: Microsoft's .NET on Linux](../../operations/runbook-linux-dotnet-runtime.md)
- `tools/watch-drhook-hang.sh` and the macOS hang watcher in `.github/workflows/test.yml`
