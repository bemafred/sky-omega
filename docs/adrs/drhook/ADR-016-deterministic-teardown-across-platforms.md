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
DrHook must handle those as well, and know what each costs.

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
4 of 8 with that change, and 1 of 6 after the change was removed (8ed9701). So the hang occurs with today's code;
whether the change made it more frequent, counts this small do not show. Earlier consequences of forced stops were
the test-host crash of 2026-05-25 (ADR-008's Context) and finding 63's crash. Two single-test timeouts on those
runners, after which the suite ran normally, were a different failure whose cause is unknown.

The 2026-10-30 draft of this ADR asked how to make termination deterministic on each platform. That was the wrong
question: it accepted the forced stop as routine and planned to engineer around its consequences.

## The ways a debugged program ends

| How it ends | Who decides | DrHook's part | Known under DrHook | Not known |
|-------------|-------------|---------------|--------------------|-----------|
| **Natural completion:** the work is done, main returns | the program | end its own side in order: receive the exit, or detach cleanly first and let the program finish | the clean detach works on macOS under a flood (probe 08, 3 of 3) | whether a program ending on its own, debugger attached, ever leaves the debugger unusable; whether a program that exits during or right after a detach is safe (finding 66's fix covers a target already dead when Dispose runs; finding 63's crashes involved a kill) |
| **Cancellation:** a stop request the program honours | the client asks; the program chooses when | send the request, keep observing, wait | without a debugger: with a handler the program cleans up and exits 0; without one the same signal is an abort, exit 143 (finding 68) | behaviour under a debugger; whether DrHook can tell in advance that a program handles the request; Windows (a console control event, never built) |
| **Won't end:** a loop missing its exit condition, a deadlock | nobody; it is a bug | observe it, which is what debugging is; a kill only as a separate, deliberate, recorded decision | on macOS a kill while mscordbi was still busy with the target has left the debugger unusable (finding 100) and crashed the debugger's host process (2026-05-25; finding 63) | a kill whose consequence is bounded on each platform; what each platform's consequence is |
| **Crashes itself:** runaway recursion and stack overflow, `Environment.FailFast`, an unhandled exception, a native fault | nobody | report the crash; survive it | nothing; not tested | everything. A crash lands at a moment nobody chose, like a kill, and discipline cannot prevent it. Whether it can leave mscordbi as finding 100 does is unknown |
| **Machine catastrophe:** a freeze that needs a hard reset | nobody | never cause it | none observed | the paths to it. Plausible ones: a program held at a breakpoint while it holds something the whole system waits on; unbounded growth (finding 100 leaves one blocked thread and two pipe files per killed target; omega's `$TMPDIR` held 954 such files); runaway work started through function evaluation |

A hard reset is the one end nothing can be recovered from: no session survives it, only what was persisted before.

## Decision (proposed)

### Principles

1. **A natural end is the default.** A session ends by letting the program finish, or by detaching cleanly and
   leaving the program to finish. DrHook never terminates a program as cleanup.
2. **Cancellation is a request, not a stop.** When a program must end before its work is done, it is asked through
   a channel it honours, and DrHook watches it end at its own pace. A signal the program does not handle is not
   cancellation, and DrHook says so rather than sending it as one.
3. **A forced end is extraordinary.** A kill is a separate, explicit decision (`drhook_kill`, `Abandon`), with its
   reason and expected cost stated first. It is never the fallback of an ordinary end.
4. **DrHook survives the ends it did not choose.** A program that crashes, or is killed by someone else, leaves
   DrHook able to report it and, where the platform allows, to keep working.
5. **Every consequence is bounded, detected and reported.** No DrHook call waits without a bound. When an end
   leaves the debugger process unusable, DrHook detects it and reports a typed anomaly that names the level reached
   and what can still be done (for example: restart the debugger process).
6. **Each consequence is contained at the smallest level:** session, then debugger process, then client, then
   machine. Nothing DrHook does may risk the machine.
7. **Record before an extraordinary act.** Before a kill, or anything else that could freeze, the intent and the
   state go to persistent storage (Mercury), since a hard reset erases everything else.
8. **Native, per platform** (kept from the first draft). Every claim about a platform comes from a run on that
   platform, with no emulation and no inference from another platform; a CI runner counts as the platform it is.
   Results are reported N of N, and a result that varies is itself a finding. Each construction is reviewed with
   Martin before it runs, and each step's result goes into a finding and into Mercury before the next.

### What each party must know

- **DrHook** (the engine): the target's state; which end is under way; whether the target can receive a
  cancellation request; each consequence, as a typed anomaly.
- **Its clients** (`drhook-mcp` and other hosts): the ends available, and which one is natural for the target. A
  probe or a test runs to completion; a server or an app ends on a stop request. The order: let it finish, then ask
  it to stop, then kill, each a separate step, observed before the next.
- **The operator** (an LLM or a person): the same order. Never kill to clean up. State the expected cost before a
  kill, and record it first. The operator's guide is DRHOOK.md's lifecycle section.

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

**Step 3 — Cancellation.** A target with a SIGTERM handler that cancels its work, asked mid-flood under the
debugger; the same target without a handler, to record what an unhandled signal does under a debugger. Windows: a
console control event, built and reviewed before it runs.

**Step 4 — The extraordinary ends,** each in a process of its own: a kill (probe 100's modes on macOS; per-platform
constructions built from Step 1's facts elsewhere), a stack overflow, `Environment.FailFast`. Result, per platform:
what each consequence is, which level it reaches, and whether DrHook can detect it.

**Step 5 — Decide (Epistemics).** From Steps 2 to 4: the session end for each kind of target; what replaces
ADR-008's Decision 1; the bounds, detection and anomalies for the extraordinary ends; whether containment needs the
debugger in a process of its own per session. This ADR moves to Accepted with that decision.

**Step 6 — Engineering.** Implement; rewrite the tests in the three kinds above; run CI with the hang watcher until
the criteria below hold, then remove the watcher. Update ADR-007, ADR-008, DRHOOK.md's lifecycle section and the
MCP tools' descriptions.

### Acceptance criteria

- On every platform, a natural end and a clean detach never leave the debugger unusable: N of N in Step 2's probes,
  and 10 consecutive green CI runs on all six runners with the watcher recording nothing.
- No ordinary session end sends a signal the program has not registered for, or kills it.
- Every extraordinary end is detected and reported within a bound, naming the level it reached. No DrHook call waits
  without a bound.
- No test or probe risks the machine.

## Not in this ADR

Separate items, to be taken up with Martin:

- After a target is killed, `GetStackFrames` and `GetLocals` threw `NullReferenceException` instead of a structured
  error (probe 100, kill mode). Principle 4 bears on it.
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
  disposition without a debugger), 87, 92, 96 and 99 (first runs on Linux and Windows)
- Limits [drhook-clean-detach](../../limits/drhook-clean-detach.md) (stop before detaching) and
  [drhook-detach-exit-race](../../limits/drhook-detach-exit-race.md) (a target that exits at Dispose; finding 66)
- [DRHOOK.md](../../../DRHOOK.md), "Lifecycle discipline & common pitfalls"
- [Runbook: Microsoft's .NET on Linux](../../operations/runbook-linux-dotnet-runtime.md)
- `tools/watch-drhook-hang.sh` and the macOS hang watcher in `.github/workflows/test.yml`
