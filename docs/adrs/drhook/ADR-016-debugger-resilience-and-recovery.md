# ADR-016: Debugger Resilience and Recovery — DrHook Stays Dependable When Its Target Is Not

**Status:** Accepted — 2026-11-20 (Epistemics). Step 6 was decided by Martin: "I approve the draft, your
recommendations are my choices as well" ([Step 6: the decisions](#step-6-the-decisions-decided-2026-11-20)). Step 7,
the engineering, is next. Proposed 2026-10-31 (Emergence), and accepted then as the experimental charter: the research
direction, the principles and Steps 0 to 5 were approved, and the production architecture was left to Step 6 (Martin's
assessments, 2026-10-31). The worker process was decided, and designed for from the start, on 2026-11-06 (Martin; see
the containment boundary below). Reframes the 2026-10-30 draft, "Deterministic Target Teardown Across Platforms";
titled "How a Debugged Program Ends" until 2026-10-31, when the title moved from the question it started from to the
rule it serves.

## The rule this ADR serves

**DrHook must remain dependable in the presence of an undependable target.** A target may finish, refuse to stop,
hang, crash, corrupt its own state or vanish. Those behaviours are the evidence DrHook exists to reveal, so the
debugger must not depend on the correctness of the program it observes. If a target's behaviour can permanently
damage DrHook's ability to make the next observation, the observer has inherited the fragility of the observed.

Dependable does not mean that every operation succeeds, or that every damaged target can be recovered. It means that
each failure is detected where possible, its scope is known, its effects are contained, and the observation service
goes on working. Three requirements follow, each separate:

- **Detection:** recognize that the debugger is no longer making progress.
- **Containment:** keep the failure from compromising unrelated debugging work.
- **Recovery:** restore useful debugging capability without a person.

The original question was how to end a program safely. The question this ADR answers is how to build an observer
that stays trustworthy when the program it observes does not behave as expected.

## Context

### What DrHook is for

DrHook lets an LLM inspect running code live, a new design for instance, stating a hypothesis before each
observation: traditional debugging, done by an LLM. It gives the LLM finer feedback than test results, so fewer
custom probes are needed; the LLM can step through a probe, a test or the implementation itself; and it can work
with coding patterns it was not trained on. The common session is therefore a healthy program under development,
and an operator runs many such sessions an hour in one `drhook-mcp` process, often with no person watching each
interaction. The most important property is not that every session succeeds, but that one failed session cannot
destroy DrHook's ability to conduct the next. Today a `drhook-mcp` that can no longer attach ends the operator's
debugging until a person restarts it.

Debugging also meets programs that misbehave: a loop missing its exit condition, a deadlock, runaway recursion.
DrHook must handle those as well, and know what each costs. How a session should end depends on why it began
(Debugging scenarios, below). And DrHook is itself debugged and tested, by DrHook among others, so it must behave
well in both roles.

### What DrHook does today (verified 2026-10-31)

- **Ending an owned session** (`DebugSession.Dispose`; `drhook_stop` for a target started with `drhook_launch`):
  the callback pump stops, the target is resumed (`TryResumeForDetach`), SIGTERM is sent at once (`RequestExit`),
  and if the target is still alive after `NaturalExitTimeout` (2 s), a `TargetStuckAtDispose` anomaly is reported
  and the target is killed. Nothing waits for the program's work to finish, and the debugger is still attached and
  handling the target's events when the signal lands. **Changed 2026-11-16** (Martin's decision,
  [finding 110](../../../poc/drhook-engine/findings/110-owned-teardown-signalled-an-attached-target.md)): the
  debugger detaches first (Stop, deactivate the breakpoints, Detach) and the signal reaches a target that is no longer
  debugged. The old order was the cause of finding 100's cascade in the integration suite: on macbook under CPU load
  it hung 15 of 40 runs, the new one 0 of 40. `drhook_kill` (`Abandon`) is unchanged.
- **On Windows** `RequestExit` cannot signal (`PosixSignals.Kill` throws `PlatformNotSupportedException`; the
  console-control path was never built), so every owned session end reports two anomalies and terminates the
  process.
- **`drhook_detach`** ends the debugger's side cleanly (breakpoints deactivated, the process stopped, then detached)
  and leaves the program running. Stopping before detaching is the fix for the `drhook-clean-detach` limit
  (2026-05-21): a detach that raced mscordbi's flush of queued events crashed the debugger.
- **`drhook_kill`** (`DebugSession.Abandon`): SIGTERM, 200 ms, then SIGKILL.
- **Ownership is one flag,** `DebugSession.OwnsTarget`: launched means owned, attached means not.

### What the debugging API documents, and what DrHook does with it

| Documented (Microsoft Learn) | DrHook today |
|------------------------------|--------------|
| [`ExitProcess`](https://learn.microsoft.com/dotnet/framework/unmanaged-api/debugging/icordebugmanagedcallback-exitprocess-method) "may fire asynchronously to other events while the process appears to be stopped", usually when an external force terminates it; while another callback is being dispatched, it is delayed until that callback returns | DebugSession's own comment names a residual race: a target that exits at the very instant of a detach. A natural end narrows the races; it is not shown to remove them |
| [`ICorDebugController::Stop`](https://learn.microsoft.com/dotnet/framework/unmanaged-api/debugging/icordebugcontroller-stop-method) is synchronous; the debugger keeps a stop counter that each `Stop` and each dispatched callback increments and each `Continue` decrements | [Finding 89](../../../poc/drhook-engine/findings/89-borrowed-detach-leak.md): a resume before the detach brought the stop counter to zero, so the process was running; the detach failed, the debugger's process object leaked, and the host crashed when the target later exited |
| [`DebuggerError`](https://learn.microsoft.com/dotnet/framework/unmanaged-api/debugging/icordebugmanagedcallback-debuggererror-method): debugging services are disabled; only `ICorDebugProcess::GetID` is safe to call, not even `ICorDebug::Terminate`; the process is to be ended with operating-system facilities | until 2026-11-06 forwarded as an ordinary informational event and continued, its codes dropped, with nothing marking the debugger unusable, and Dispose would still call `Terminate`. Since then handled to the documented contract (brought forward from Step 6, below) |
| [`ICorDebug::Terminate`](https://learn.microsoft.com/dotnet/framework/unmanaged-api/debugging/icordebug-terminate-method) "should not be called until an `ExitProcess` callback has been received for all processes being debugged" | Dispose calls it unconditionally (`DebugSession.cs`), also after a kill whose `ExitProcess` never arrived |
| `ICorDebugEval` runs target code; an evaluation can deadlock or never finish, and can be aborted | each evaluation is bounded and aborted when it times out (`Eval.Abort`); not modelled: the difference between observing and intervening, or an abort that itself does not finish |

In finding 100's state `ExitProcess` cannot arrive: the thread that would deliver it is the one blocked. So the API
offers no documented way to shut that debugger instance down cleanly inside its process. A managed timeout lets the
caller stop waiting, but it does not release the native thread or the lock it holds. Discarding the process that
holds the debugger is the only recovery the documentation supports.

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

Since 2026-11-07 every session end says how the target ended, read from the operating system and not inferred
(`TargetExit`): the exit code, or the signal that ended it. On POSIX, a thread started at launch blocks in
`waitid(..., WEXITED | WNOWAIT)`, which reads the child's record without reaping it, so mscordbi's PAL still reaps it
(finding 87). On Windows, DrHook waits on a handle of its own and reads `GetExitCodeProcess`. An attached POSIX
target that is not DrHook's child reads as unknown, with the reason. Before that, every exit was reported as "ran to
completion" and every owned `drhook_stop` as "gracefully terminated": the engine had the status and discarded it.
With a debugger attached, the first reading agrees with finding 68: `drhook_stop` of a launched .NET target with no
handler reads "killed by signal 15 (SIGTERM)" (macOS, 2026-11-07).

So a forced stop is DrHook's routine session end.
[Finding 100](../../../poc/drhook-engine/findings/100-orphaned-transport-read-holds-dac-lock.md) is one consequence:
a target that dies while mscordbi's Win32 event thread reads its memory leaves that debugger process unable to
attach or launch again. It is proven on macOS (probe 100, 5 of 5 on omega), and is not claimed for any other
platform. On the GitHub macOS runners it hit none of 8 jobs before a test change of ours (4b9812a) altered the
timing of the test that runs before the one that hangs, 4 of 8 with that change, and 3 of 8 after the change was
removed (8ed9701). So the hang occurs with today's code; whether the change made it more frequent, counts this small
do not show. Finding 100 also leaves a blocked thread and two pipe files per killed target: a resource that
accumulates, not only a teardown defect. Earlier consequences of forced stops were the test-host crash of
2026-05-25 (ADR-008's Context) and finding 63's crash. Two single-test timeouts on those runners, after which the
suite ran normally, were a different failure whose cause is unknown.
[Finding 103](../../../poc/drhook-engine/findings/103-terminate-waits-on-error-write.md) recorded the stacks of a
third (2026-11-04): `ICorDebug::Terminate` waited forever on the Win32 event thread, which was writing an error into
the gone target over the transport, before sending `DebuggerError`; no lock was held, and later sessions worked.

Finding 100 may not be about SIGTERM or SIGKILL as such. They are two ways to make a target vanish abruptly; the
deeper fault may be an unsafe interaction between a target's disappearance and the debugger's native state. That is
a hypothesis to falsify (Step 4), not a demonstrated cause.

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
| **Cancellation:** a stop request the program honours | the client asks; the program chooses when | send the request through a declared mechanism, keep observing, wait | without a debugger: with a handler the program cleans up and exits 0; without one the same signal is an abort, exit 143 (finding 68) | behaviour under a debugger; Windows (a console control event, never built) |
| **Won't end:** a loop missing its exit condition, a deadlock | nobody; it is a bug | observe it, which is what debugging is; a kill only as a separate, deliberate, authorized and recorded decision | on macOS a kill while mscordbi was still busy with the target has left the debugger unusable (finding 100) and crashed the debugger's host process (2026-05-25; finding 63) | a kill whose consequence is bounded on each platform; what each platform's consequence is |
| **Crashes itself:** runaway recursion and stack overflow, `Environment.FailFast`, an unhandled exception, a native fault | nobody | report the crash; survive it | nothing; not tested | everything. A crash lands at a moment nobody chose, like a kill, and discipline cannot prevent it. It can leave mscordbi as finding 100 does: FailFast, stack overflow, a native fault and `abort()` all did on macOS and Linux arm64, and none did on Windows arm64 (findings 104, 105) |
| **Machine catastrophe:** a freeze that needs a hard reset | nobody | keep every known path to it inside enforced limits and isolation | none observed under DrHook | the paths to it. Plausible ones: a program held at a breakpoint while it holds something the whole system waits on; unbounded growth (finding 100 leaves one blocked thread and two pipe files per killed target; omega's `$TMPDIR` held 954 such files); runaway work started through function evaluation |

A hard reset is the one end nothing can be recovered from: no session survives it, only what was persisted before.

These are why a program ends. When it ends, relative to what the debugger is doing at that moment, may matter as
much (the failure matrix, under Tests).

## Debugging scenarios

How a session should end depends on why it began. Each scenario has its own owner of the program's life, its own
natural end and its own hazards, and DrHook and its client must know which scenario a session is in.

| Scenario | How it starts | Whose program | Its natural end | Ending the session before that | Lifecycle hazards |
|----------|---------------|---------------|-----------------|--------------------------------|-------------------|
| **Inspecting code being designed:** a probe, a POC, a file-based app | `drhook_launch`, breakpoints armed before main | DrHook's | it runs to completion | continue it to the end, or detach and let it finish | mixing runtime versions in one debugger process (finding 86); a breakpoint on a file-based target that stays pending |
| **Debugging a failing test,** instead of writing a probe that logs | the test executable launched (MTP), or `dotnet test` started with `VSTEST_HOST_DEBUG=1` and its test host attached to (VSTest) | DrHook's for a test executable it launched; otherwise the test runner's | the test run finishes and reports | remove the breakpoints and continue to the end | the runner waits for its host: a host killed mid-handshake leaves vstest.console waiting 90 s (finding 94); an early attach can skip the host's debug hold (finding 91); a host left waiting for a debugger is orphaned (finding 90); a test framework's timeout may expire while the test is paused (NUnit does not enforce its timeouts under a debugger; MSTest and xunit v3 not established) |
| **Attaching to a running program,** a debug build, to learn the context of a reported bug | `drhook_attach` | its owner's: a user, a service manager, or another Sky Omega component | none on DrHook's side: the program goes on after the session | detach and leave it running; never kill it implicitly | a breakpoint stops the program for everyone using it: their requests time out, health checks fail, a watchdog may restart it and end the session; a program started with `DOTNET_EnableDiagnostics=0` refuses the attach |
| **Diagnosing a hang** | attach; or a launched program stops making progress | its owner's, or DrHook's | none: it does not end | a stack snapshot first (`drhook_snapshot`, through EventPipe like `dotnet-stack`), which does not stop it at a breakpoint; then pause and inspect; detach, or, where a kill is authorized, a kill as the extraordinary act | the "won't end" row above |
| **Catching a crash** | launch, with exception breakpoints to stop before the crash | DrHook's | the crash | — | the "crashes itself" row above; a stack overflow gives an `Exception(Unhandled)` stop, as FailFast does; a native fault and `abort()` give none (finding 104) |
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
- **Paused by an outer debugger,** DrHook's own waits go on counting time and may expire while it is paused, and its
  target goes on running, or stays held, unobserved. A paused process may be unable to observe what paused it. When
  DrHook cannot tell its own pause from its target's fault, the outcome is recorded as unknown, never as a target
  failure. A monotonic deadline, an outside observer and explicit pause and resume events may improve the
  classification; none is assumed sufficient until probes show it.
- **Runtime versions.** An outer debugger is locked to the first runtime version it debugs too (finding 86).
- **Observing it without stopping it.** When DrHook's test host hung on CI, it was read with `sample` and
  `dotnet-stack`, which do not stop it at a breakpoint. DrHook's own stack snapshot uses the same mechanism as
  `dotnet-stack` (EventPipe) and should serve there as well.

## Decision

### Principles

1. **Dependable in the presence of an undependable target.** Detection, containment and recovery are each required.
   A failed or irrecoverable session may destroy what holds its native debugger state, but must not make the
   supervising DrHook service permanently unusable.
2. **A natural end is the default.** A session ends by letting the program finish, or by detaching cleanly and
   leaving the program to finish. DrHook never terminates a program as cleanup.
3. **Cancellation is a request through a declared mechanism.** When a program must end before its work is done, it
   is asked through a shutdown mechanism that is part of its declared lifecycle contract, and DrHook watches it end
   at its own pace, reporting completion, refusal or a bound expiring. The presence of an operating-system signal is
   not such a contract.
4. **A forced end is extraordinary.** A kill is a separate, explicit, authorized decision (`drhook_kill`,
   `Abandon`), with its reason and expected cost stated first. It is never the fallback of an ordinary end.
5. **Each scenario has its own natural end.** A probe or a test runs to completion; a program DrHook attached to
   goes on after the session; a server ends on a stop request. In a program DrHook did not start, a breakpoint stops
   it for everyone using it, so observing without stopping comes first.
6. **Ending a session and ending a program are different operations,** with different ownership and safety
   contracts. What happened to the target and what happened to the session are recorded separately.
7. **Every public operation is bounded from the client's side, and every consequence is detected where possible and
   reported, including the ends DrHook did not choose.** An operation returns within its bound even when a native
   call beneath it cannot be interrupted, and then reports that call as still outstanding. A bound lets the caller
   stop waiting; it does not release a native call that is blocked, so recovery cannot rest on bounds alone.
8. **Each consequence is contained at the smallest level, and recovered from without a person:** session, then
   debugger process, then client, then machine. Risk to the machine is held by enforceable limits (time, memory,
   threads, processes, files) and by isolation, and the risk accepted is defined beforehand for each kind of probe and
   each machine. Recovering the service and recovering the target are separate obligations.
9. **Intervention is governed.** Every operation that can run target code is identifiable, governed and recoverable
   at the service level, even when its effect on the target cannot be undone.
10. **Resources are conserved across sessions.** A long run of sessions stays within budgets for threads,
    descriptors or handles, memory and temporary files.
11. **Record before an extraordinary act,** to a local durable journal first, so that recording never becomes
    another dependency of the service's safety. What is recorded follows a retention and redaction policy.
12. **DrHook behaves well as a debug target too.** Its own processes end naturally and honour cancellation, and an
    expired bound it cannot attribute is recorded as unknown, not blamed on its target.
13. **Native, per platform** (kept from the first draft). Every claim about a platform comes from a run on that
    platform, with no emulation and no inference from another platform; a CI runner counts as the platform it is,
    and as no other installation of that OS. Results are reported N of N, and a result that varies is itself a
    finding. Each construction is reviewed with Martin before it runs, and each step's result goes into a finding
    and into Mercury before the next.

### The containment boundary: the debugger in a worker process

As concepts, not a proposed class structure: the MCP service stays in its own process as a supervisor, and the
native debugger (dbgshim, mscordbi, the DAC) runs in a worker process. The supervisor watches the worker, reports an
unhealthy worker and the session it affects, and starts a fresh worker.

- **Why the boundary is real.** The debugger's native state is per process: the DAC is loaded once per process,
  finding 100's lock is process-wide, and finding 86 locks a debugger process to one runtime version. Once
  `ExitProcess` cannot arrive, the API leaves no clean way to shut the debugger down inside its process (Context).
  A process boundary is a real boundary around that state, not another managed abstraction.
- **What it would also dissolve.** Finding 86's runtime lock becomes per worker, not per service.
- **A fresh engine without a reconnect** (observed 2026-11-07, an input to Step 6's choice of worker lifetime, not a
  decision).
  - **The problem.** Today an engine change reaches the operator only through a new MCP process, and the client may
    not offer one. The Claude desktop app's Code sessions refuse `/mcp reconnect`, at the machine as well as over
    Remote Control. Its alternatives, a new session or an app restart, lose the operator's view or interrupt the
    work. So the session that changed the engine goes on debugging with the engine it started with.
  - **What a worker per session would give.** If each worker starts from the current build, the MCP process is only
    the supervisor (the tool surface, the protocol and the supervision) and rarely changes. An engine change then
    reaches the next `drhook_launch` or `drhook_attach` with no reconnect.
  - **What that needs.** The worker runs from its own copy of the build, so a rebuild cannot replace the files of a
    running worker. On 2026-11-07 a build replaced the binaries under two running `drhook-mcp` processes.
  - **What it leaves.** A change to the tool surface itself still needs a new MCP process, unless the client honours
    MCP's `notifications/tools/list_changed` (untested).
  - *Not pursued (2026-11-21, Martin).* Using code while it is being developed and fixed is a lifecycle matter, handled
    as one: disconnect, fix and rebuild, reconnect. The terminal client, which DrHook has been used from since
    2026-11-07, reconnects. A copy of the build that protects a running worker from a rebuild handles a case that rule
    rules out, so it is unwarranted complexity, and it is not built (6.7, below).
- **What it brings.** The worker is a program with its own lifecycle, held to the same rules. A launched target
  becomes the worker's child, so the worker's death affects it. Whether a target that is held stopped resumes, stays
  frozen or dies when its debugger process dies is not known on any platform. Recovering the service and
  recovering the target stay separate obligations.
- **What it needs.** A minimal protocol between supervisor and worker. DrHook.Wire (NDJSON over a socket, ADR-012)
  is an existing candidate, not a decision.
- **Decided: the worker is needed, and designed for from the start** (Martin, 2026-11-06). Relaunching the debugger
  is done by process indirection. The grounds:
  - Some crashes cannot be avoided. A native fault or `abort()` gives no stop at which to end the session, and such
    a crash during a request leaves the debugger hung (finding 104).
  - No recovery is possible inside the process. A request the target dies under is never woken: mscordbi's PAL has
    no thread that re-checks a waited process, and nothing exported starts one (finding 105). The blocked thread
    holds the DAC lock, so every later attach in that process hangs (finding 100).
  - Avoidance (end at a stop) and detection (the target is gone and a request has not returned) stay; they make the
    worker's discard rare and its cause known, they do not replace it.

  It is built as the minimum protocol, lifecycle state and supervision required, with no general-purpose worker
  framework, and as the one path on every platform, not a variant for some. Step 5 proves the design; it no longer
  tests the need.

### State: the target, the session, and the debugger's health

Two independent records, not matched pairs; any combination can occur:

- **What happened to the target:** completed; cancelled cooperatively; crashed; terminated externally; killed
  deliberately; still running; unknown.
- **What happened to the session:** closed normally; detached; failed; debugger lost; recovery required; unknown.

A target can go on running after its session closes; a target can crash while the debugger survives; a debugger can
fail while the target is still alive. Keeping the two apart stops lifecycle decisions from being inferred from the
wrong state, and gives typed anomalies and Mercury records a clean basis.

**The debugger's health** is a third: healthy; degraded; suspected unresponsive; unusable.

- **Suspected unresponsive:** an operation's bound expired, or progress stopped. That alone does not establish that
  the instance is lost: the operation may still complete.
- **Unusable:** confirmed. A `DebuggerError` confirms it, since Microsoft documents that debugging services are then
  disabled. Evidence from outside the instance can also confirm it, such as a stack snapshot that shows a native
  call blocked where finding 100 blocks. No further native debugging call is made on an unusable instance, not even
  `Terminate`, and only the supervisor can recover.

How a suspected instance is confirmed or cleared, and when the supervisor may discard one without confirmation, is
decided in Step 6 from Step 4's evidence. Either state differs from an ordinary session failure and is never
treated as one. `DebuggerError`, bounds expiring, missing progress and failed health checks feed one health model,
each keeping its own cause.

### Ownership: how a target started, whose it is, what is authorized

`OwnsTarget` is replaced by three facts:

- **How it started:** launched by DrHook; attached by DrHook; attached to a host that another component started.
- **Whose it is:** DrHook's; another Sky Omega component's; a user's or a service manager's.
- **Which lifecycle actions are authorized:** detach; a shutdown request through a declared mechanism; abandon.

An attached program is never killed implicitly. A deliberate, separately authorized emergency kill can be
legitimate, for a test host that another component started, say, but never for an unrelated service.

### Lifecycle operations and their contracts

These are responsibilities, not signatures the engine must adopt.

| Operation | Contract |
|-----------|----------|
| **Wait for exit** | observe the target's completion, without starting a shutdown |
| **Request shutdown** | ask the target to end through a mechanism in its declared lifecycle contract; report completion, refusal, or the bound expiring |
| **Detach** | end the debugger's attachment; a live target goes on running |
| **Abandon** | extraordinary termination; authorized, and recorded before it happens; aimed at the process identified, never at whatever now holds its process ID |
| **Close** | end the session by the end chosen for its scenario, and return the outcome: the target's, the session's and the debugger's health |
| **Dispose** | release the debugger's resources, never killing the target implicitly. Dispose returns nothing, so what it did is carried by the session's state, which the client can read afterwards; a client that needs the outcome closes first. A launched target still running is recorded as left running, for the client to decide on; it is never orphaned silently. If the resources cannot be released safely, the recorded outcome is degraded or unrecoverable, contained, never a clean one |

A target is identified by more than its process ID, which the operating system reuses once a process has ended:
the ID together with something that is not reused, such as the process's start time. Anything that acts on a
process later, a kill above all, checks that identity first.

A launched program left running is why DAP terminates launched programs by default: otherwise a program that won't
end runs on with nobody to end it. Here that is a reported outcome and a decision for the client, not a silent one.

Changing `drhook_stop` from ending a launched program to this is a compatibility change: the MCP tools'
descriptions, DRHOOK.md and the operator instructions change together.

### Cancellation is a declared capability

DrHook cannot reliably discover whether an arbitrary process will handle a shutdown signal, and the signals differ by
platform:

- On Unix an unhandled SIGTERM ends the program at once (finding 68).
- [`PosixSignalRegistration`](https://learn.microsoft.com/dotnet/api/system.runtime.interopservices.posixsignalregistration.create):
  SIGINT and SIGQUIT can be cancelled on Windows and on Unix; SIGTERM can be cancelled only on Unix.
- [`GenerateConsoleCtrlEvent`](https://learn.microsoft.com/windows/console/generateconsolectrlevent): a CTRL+C
  event cannot be limited to one process group; a CTRL+BREAK event can be sent to a group created with
  `CREATE_NEW_PROCESS_GROUP`, and reaches only the members that share the caller's console.

So a shutdown mechanism is part of a target's declared lifecycle contract: a probe can expose a shutdown command; a
service a controlled shutdown; a Windows console process launched in its own process group can accept a CTRL+BREAK
event.

### Function evaluation is intervention

Reading a stack frame observes. Evaluating a method runs target code that can deadlock, loop forever, raise more
debugger events, take a lock, start background work or change the program's state; an LLM operator may write an
expression whose side effects it does not fully foresee. DrHook already bounds each evaluation and aborts one that
times out. Added: each operation is classified as observation or intervention, with its own limits; an abort that
does not finish is a debugger-health event; and the operator states an intervention's intended effect in its
hypothesis.

### Resources across sessions

For sessions that end well, long sequences are measured: thread count, open descriptors or handles, native memory,
temporary files (the debugger's pipe files), outstanding callbacks and live transports. A suite that passes a
thousand times can still leak toward failing on the next session. For extraordinary ends, resources are accounted for
at the containment boundary: what the operating system reclaims with a discarded worker, and what is left on the
target's side. On Windows, [Job Objects](https://learn.microsoft.com/windows/win32/procthread/job-objects) group a
process tree and enforce limits; children join their parent's job unless breakaway is allowed, so that needs
deliberate configuration.

### Health and observability

A liveness heartbeat is not enough: a worker can be alive while one native call is blocked forever. The supervisor
sees the current operation, how long it has run, its last observed progress and where progress stopped. The
debugger can be inspected from outside itself (a stack snapshot through EventPipe, `sample`, `dotnet-stack`), since
asking a damaged debugger to diagnose itself may make things worse.

### Recording and data

- Before an extraordinary act, the intent and the state go to a local, bounded, durable journal, append-only like
  Mercury's own write-ahead log, and are consolidated into Mercury afterwards. The supervisor can record an impending
  worker termination even when the debugger no longer responds, and the service's safety never waits on a Mercury
  write.
- Stack frames, locals, arguments and memory can hold credentials and other sensitive data. Durable diagnostic
  records follow an explicit retention and redaction policy.

### Authorization

DrHook can inspect and change another process's execution, so its MCP interface is a privileged capability. Today
`drhook-mcp` serves one local operator over stdio. Before DrHook is enabled across Sky Omega hosts, attach,
evaluation and termination each need explicit authorization; isolating the host alone is not enough.

### What each party must know

- **DrHook** (the engine and its supervisor): how each session started, whose program it is and what is authorized;
  the target's outcome, the session's outcome and the debugger's health, separately; which end is under way; the
  operation in progress, its duration and its last progress; each consequence, as a typed anomaly; and whether an
  expired bound can be attributed at all.
- **Its clients** (`drhook-mcp` and other hosts): the scenario, the ends available, and which one is natural for it
  (the table above). The order: let it finish, then ask it to stop through its declared mechanism, then, if
  authorized, kill, each a separate step, observed before the next. In a program DrHook did not start: detach,
  never kill implicitly.
- **The operator** (an LLM or a person): the same. In a program others use, observe without stopping first. Never
  kill to clean up. State an intervention's intended effect, and a kill's expected cost, before acting. The
  operator's guide is DRHOOK.md's lifecycle section, which gains the scenarios.

### What this changes (decided in Step 6)

- `DebugSession.Dispose` of an owned session and `drhook_stop` stop sending SIGTERM and SIGKILL as their ordinary
  path, under the operation contracts above. ADR-008's Decision 1 is revisited: SIGTERM remains a cancellation
  request for a program that declares it, and is not counted as a natural exit.
- `DebuggerError` marks the debugger unusable; `Terminate` is called only when its documented precondition holds.
  **Brought forward and done, 2026-11-06** (Martin: a flawed implementation in place could be us introducing "ghost
  errors"; get it right to the documentation first). The contract, verbatim: "ICorDebugProcess::GetID will be safe
  to call, but all other methods, including ICorDebug::Terminate, should not be called. The debugger should use
  operating-system facilities for terminating processes." `IUnknown::Release` is one of "all other methods". So:
  - The callback thunk keeps `errorHR` and `errorCode` and does not touch `pProcess`.
  - The pump publishes the loss on mscordbi's thread at once (the worker may be parked at the stop whose command
    timed out), raises a `DebuggerError` anomaly with both codes, never continues, and makes no native call again;
    every later `WaitForStop` returns `StopReason.DebuggerLost` with the codes.
  - Every session operation that would reach ICorDebug refuses with `DebuggerLostException`; `CaptureState` reports
    `ExecutionState.DebuggerLost` and reads no frame.
  - Teardown calls nothing on mscordbi's objects, releases nothing, keeps the callback object mscordbi holds, and roots
    the session for the process's life, because its two `ComWrappers` wrappers would `Release` when collected. An
    owned target is killed with the OS; a borrowed one is left as it is. The MCP layer ends the session and reports
    the codes and the target's state.
  - Tested in-process (the thunk through its vtable slot, the pump with injected callbacks); each new test fails with
    the handling disabled. Not yet seen with a real `DebuggerError`: that is what the Step 4 construction would show.
  - A loss that arrives during an ordinary teardown makes its next guarded step refuse; the teardown then stops
    calling and roots the session.
  - Residual: an operation already inside mscordbi when the loss arrives cannot be stopped from inside the process;
    the worker contains it.
- Waits without a bound get one (the attach that hangs in finding 100), and the state they detect is reported.
- `OwnsTarget` gives way to the three ownership facts; the target's outcome, the session's outcome and the
  debugger's health are recorded separately.
- The kill path (`Abandon`, `drhook_kill`) stays, authorized, with each platform's consequence characterized and
  reported.
- The minimal worker and supervisor (decided 2026-11-06), proved first by Step 5.
- The integration tests follow the same discipline (below).

### Tests that fit the problem

- **Routine:** finite targets that end on their own. Each test then checks that the debugger can still attach in the
  same process, so a test that leaves the debugger unusable fails itself, not the test after it.
- **Cancellation:** targets with a declared shutdown mechanism (a SIGTERM handler that cancels a `CancellationToken`;
  a Generic Host server stopped through its own lifetime; on Windows, a console process in its own group). The test
  asks mid-work, and checks that the program ends at its own pace, cleanly, that refusal and timeout are reported
  correctly, and that the debugger stays usable.
- **Extraordinary:** a target whose loop has no exit condition (it needs a kill), one that overflows its stack, one
  that calls `Environment.FailFast`. Each runs in a test process of its own, bounded in time and resources, and
  checks that the consequence is detected, reported and contained, not that nothing happens.
- **The failure matrix:** what the debugger is doing when the target ends, against how it ends. Each case is placed
  deliberately, with a barrier where feasible rather than by timing: probe 100's freeze-and-sample, which catches the
  event thread inside a read before the kill, is the template. The marked cases are where a failure is expected.

  | What the debugger is doing | Natural exit | Cooperative exit | Crash or external kill |
  |----------------------------|--------------|------------------|------------------------|
  | running freely | test | test | test |
  | handling a managed callback | test | test | test |
  | reading target memory | test | test | **critical** |
  | evaluating a function | test | test | **critical** |
  | detaching | test | test | **critical** |

- **Recovery:** every failure test is followed by an independent session that must succeed. A failing session
  followed by a successful one is a standard pattern of the suite.
- **Soak:** long sequences of sessions, measured against the resource budgets.

The routine tests cover the scenarios: a probe launched and run to completion; a failing test debugged to the end of
its run, under MTP and under VSTest; an attach to a running program, then a detach that leaves it running; and DrHook
as the target, a DrHook test host debugged by `drhook-mcp` and left to end on its own.

The flood and pause tests of today end their sessions mid-work by design. Each becomes a routine test (let the work
finish) or an extraordinary one, named as such. Anything that could freeze a machine runs only where a freeze is an
accepted risk (Step 0), never on omega or zbook, and within enforced limits there too.

### The platforms, and what support is evidence for

| Platform | Our machines | GitHub runner | Notes |
|----------|--------------|---------------|-------|
| macOS arm64 | omega (metal, macOS 27, Developer Mode on, SIP enabled) | `macos-latest` (VM, macOS 26.6, SIP disabled) | where finding 100 was found and reproduced; `task_for_pid` fails on omega for every target, and on the runners succeeds for a `dotnet`-hosted target and fails for an ad hoc signed apphost (finding 101) |
| macOS x64 | — | `macos-26-intel` (VM, macOS 26.6) | runner only |
| Linux x64 | zbook (Microsoft .NET 10.0.14, `ptrace_scope=1`, dash) | `ubuntu-24.04` | elite (Microsoft 10.0.13) is excluded: it hung on 2026-10-31 and cannot be reset for now; its kernel log suggested failing RAM before that |
| Linux arm64 | omega-ubuntu (VM, Ubuntu's .NET 10.0.14 by default, Microsoft's 10.0.13 and 10.0.14 in `~/.dotnet`, `ptrace_scope=1`, dash) | `ubuntu-24.04-arm` | the one machine where the two builds can be compared, at 10.0.14 from one source commit (finding 101) |
| Windows x64 | — | `windows-latest` | runner only; no debugger transport |
| Windows arm64 | omega-windows (VM, 10.0.14) | `windows-11-arm` | no debugger transport |

Support is claimed along four independent axes, and evidence on one does not carry to another:

- **OS and architecture:** macOS, Linux, Windows; arm64 and x64; OS versions and native permissions.
- **Runtime distribution:** Microsoft's or the distribution's .NET build, the CoreCLR version, and the DAC and
  mscordbi that come with it (10.0.9 to 10.0.14 here).
- **Execution environment:** metal, VM or CI runner; process permissions and resource limits. A green CI run is
  evidence for that runner, not for every installation of its OS: omega and the macOS arm64 runner are different
  environments, and both are worth having because they behave differently.
- **Scenario:** launch, attach, tests, services, windowed programs, crash handling and repeated sessions.

What differs between the platforms, from the first draft: how target memory is read (directly when
`PAL_OpenProcessMemory` succeeds, which on macOS needs `task_for_pid` and on Linux `/proc/<pid>/mem` under
`ptrace_scope`, otherwise over the debugger transport; Windows has no transport, `FEATURE_DBGIPC` being Unix-only);
how a target's death is noticed (mscordbi's PAL on Unix, a process handle on Windows); and the machine itself (3 to
18 cores, kernel, libc, `/bin/sh`, signal numbers). A worker per session would also reduce the consequences of
finding 86's runtime-version lock.

### Steps

Each step answers one question and ends with a recorded result and a review before the next. Steps 1 to 4 establish
what succeeds and where the failure boundary is before any new infrastructure is built.

**Step 0 — Preconditions (Martin's decisions, 2026-10-31).**
- elite: excluded. It hung on 2026-10-31 and is out of reach for a hard reset; no result from it counts until it is
  reset and memtest86+ has run.
- Linux arm64: Microsoft's .NET beside Ubuntu's on omega-ubuntu, so the debugger code of the two builds can be
  compared on one machine. Done 2026-10-31: Microsoft's SDK 10.0.113 (runtime 10.0.13) in `~/.dotnet`, the same
  versions Ubuntu builds from source. Both runtimes name source commit `4099f83`, and neither links the system
  libunwind, but their `libmscordbi.so`, `libmscordaccore.so` and `libcoreclr.so` differ. Ubuntu's stays the
  default; Microsoft's is selected by running `~/.dotnet/dotnet` (and `DOTNET_ROOT` for apphosts). Seven minutes
  later, at 13:28:51 UTC, unattended-upgrades raised Ubuntu's to 10.0.14 (SDK 10.0.114), so the two builds no longer
  share a version ([finding 101](../../../poc/drhook-engine/findings/101-platform-facts.md)). Martin: both. Microsoft's
  SDK 10.0.114 (runtime 10.0.14, source commit `0427b9a`, as Ubuntu's) added beside 10.0.113 the same day. Martin
  then held Ubuntu's ten .NET packages at 10.0.14 / SDK 10.0.114 (`apt-mark hold`; `apt-mark showhold` lists all ten,
  checked 2026-11-01).
- Linux stack capture: `ptrace_scope=1` stops `gdb` from attaching to a non-descendant. Decided: constructions that
  need no capture. No outside debugger or stack sampler is attached to the debugger process; a probe establishes
  what it needs from inside that process or from outcomes alone.
- Which machines may run the extraordinary probes of Steps 4 and 5, and the risk accepted on each. Proposed on
  2026-10-31, the day elite hung out of reach of a reset: CI runners and VMs only. Since then, Step 4's runs have hung
  only the probe's own child process, which its parent ends; the machine itself was never affected (findings 104,
  105). Martin approved omega for Step 4 on 2026-11-05. **Decided 2026-11-17 (Martin), for Step 5:** macbook,
  omega-windows and omega-ubuntu as well as the runners ("We have macbook as well. And we also have omega-windows and
  omega-ubuntu").
- Every machine at the same commit, with its runtimes listed (`dotnet --list-runtimes`).

**Step 1 — Platform facts (probe 101, read-only).** Attach to an idle target, change nothing, and record the
debugger code loaded, how memory is read, and the environment; on Linux also what the debugger process can read of
its own threads' state without ptrace rights, which the no-capture constructions depend on. It ends the session with
a clean detach and leaves the target to finish. Runs on each of our machines, and on the six runners through a
`workflow_dispatch` job (a workflow change, reviewed when proposed).
*Recorded 2026-10-31* ([finding 101](../../../poc/drhook-engine/findings/101-platform-facts.md); our four machines,
omega-ubuntu with both builds, and the six runners through `.github/workflows/drhook-probe.yml`, then named
`drhook-probe-101.yml`; every run
recorded, every target ended by itself). On Linux mscordbi reads a descendant's memory directly and any other
process's over the transport (`ptrace_scope=1` everywhere). On macOS it depends on the machine: on omega (SIP enabled)
`task_for_pid` fails and every read goes over the transport; on the runners (SIP disabled) it depends on the
target's executable: it succeeds for a `dotnet`-hosted target and fails for one started through its own apphost, which
the SDK signs ad hoc as it signs the MTP test target (second run, probe at `9e29407`, 2026-11-01, on omega and the
six runners). Windows has no transport. DrHook's integration tests attach to descendants only, so on Linux they never
take the transport path that finding 100 hangs in; on the macOS runners only the MTP sessions do, which is where
finding 100 places the stuck read. On Linux the
debugger can read its own threads' `comm`, state, `wchan` and syscall but not their stacks; mscordbi's transport
thread is visible that way, and it outlives the detach.
*Closed 2026-11-01* (Martin: "move on to Step 2"). Left open, recorded in finding 101: which part of a macOS
signature lets `task_for_pid` succeed (signer, hardened runtime and `get-task-allow` changed together); whether SIP
alone makes omega read every target over the transport; whether a debugger signed with `cs.debugger` would read
directly on omega.

**Step 2 — The natural end.** Hypothesis: letting a target complete normally narrows the debugger's teardown races,
and should avoid the failure that forced termination causes. Whether it removes every dangerous race is an empirical
question: `ExitProcess` can arrive while the process appears stopped, and DrHook's own code names a race at the
instant of a detach. Constructions: a flooding target that ends on its own, under the debugger, N times in one
process, after which a further attach must succeed; a clean detach while the target floods, the target then
finishing on its own; a target that exits right after the detach (finding 63's case, without the kill). Then the
same natural end in each scenario: a launched probe, a test run under MTP and under VSTest, an attach followed by a
detach, and DrHook's own test host as the target.
*Constructions recorded 2026-11-01* ([finding 102](../../../poc/drhook-engine/findings/102-natural-end.md); probe 102
at `6bb7ef8` on our machines, omega-ubuntu with both builds, and the six runners through `drhook-probe.yml`; 1,520
borrowed sessions, 20 per child process, each child ending with an attach that must succeed). A target that ends by
itself under the debugger, idle or mid-flood, left DrHook usable everywhere: 760 of 760, 360 of them over the
transport, and 76 of 76 final attaches. A detach of a flooding target drops one callback in almost every session (754
of 760, never two): `Dispose` closes the callback pump before it synchronizes the target, and a callback in between
is neither queued nor continued. Once, on omega-windows, the target stayed held after the detach: it never ended,
though it ends itself after 60 s; whether it was suspended or a thread was blocked is not recorded. This touches the
Target preservation gate, not the debugger's own state. Also recorded: `Detach` gives `CORDBG_E_PROCESS_TERMINATED`
the value `0x8013134F` (that is `OBJECT_NEUTERED`; the right value is `0x80131301`), so it reports a gone target as an
anomaly; debug pipes left after
exit-at-detach on the Linux x64 runner; mscordbi's transport threads do not accumulate across sessions (finding 101's
open question). Step 2's scenarios (launched targets, MTP and VSTest runs, DrHook's own test host) are not yet run.
*The owned session end, 2026-11-16* ([finding 110](../../../poc/drhook-engine/findings/110-owned-teardown-signalled-an-attached-target.md)):
finding 100's cascade was reproduced on demand on macbook (macOS arm64, Developer Mode off) by loading every core. In
the integration suite its trigger was DrHook's own order: `drhook_stop` resumed a launched target with its breakpoints
active and signalled it while attached. Detaching first is decided (Martin) and built; it does not decide the session
end for the other scenarios, or what a kill does (Step 6).

**Step 3 — Cancellation.** Targets with a declared shutdown mechanism: a SIGTERM handler that cancels its work,
asked mid-flood under the debugger; the same target without a handler, to record what an unhandled signal does under
a debugger; a Generic Host server stopped through its own lifetime; on Windows, a console process launched in its own
group and sent CTRL+BREAK, built and reviewed before it runs.
*Constructions recorded 2026-11-16* ([finding 112](../../../poc/drhook-engine/findings/112-cancellation.md); probe 112
on zbook and macbook, 20 borrowed sessions per child, each child ending with an attach that must succeed; Windows
below).

- **Its own shutdown code.** A target with its own SIGTERM handler, or a Generic Host stopped by SIGTERM or by itself,
  ended cleanly under the debugger mid-flood: 240 of 240 sessions, and every final attach succeeded.
- **After a detach.** All three signalled targets ended normally: 240 of 240.
- **No handler.** SIGTERM to a program without one is an abrupt end: exit 143, and no `ProcessExit`. Attached, it lost
  the debugger in 6 of 14 sessions, on both machines and both read paths. Each lost session had no `ProcessExited`,
  and the next attach blocked, which fits finding 105's race.

So `drhook_stop`'s detach-first order is safe for each of these targets. `drhook_kill` is not, for a program without a
handler, since it signals while attached; nor is an owner's signal to a borrowed target. What Abandon should do is
left to Step 6.
*Windows recorded 2026-11-17* ([finding 112, Windows](../../../poc/drhook-engine/findings/112-cancellation.md#windows-ctrlbreak-to-the-targets-own-process-group); probe 112 at `b08b8604` on omega-windows, 20 sessions
per child, then break-handler detached alone, 100 sessions). The target was started in its own process group and sent
CTRL+BREAK from its console.

- **Its own shutdown code.** A handler (`PosixSignal.SIGQUIT`) or a Generic Host ended cleanly while attached, 40 of
  40, and host-self-stop 20 of 20.
- **No handler, attached.** DrHook takes the event and the target runs on. The runtime passes CTRL+C and CTRL+BREAK
  to an attached debugger as `ControlCTrap`, and DrHook's `S_OK` answer counts the event as handled, so the target
  never reaches Windows' default end (read in `release/10.0`; 20 of 20). The debugger was never lost.
- **No handler, detached.** An abrupt end: `0xC000013A`, without `ProcessExit`.
- **After a detach.** The target stayed held twice in 120 break-handler sessions. This is Step 2's held Windows target
  (finding 102, 2 of 80); the record cannot say what held it.

What DrHook answers to `ControlCTrap`, and whether `drhook_stop` on Windows should send CTRL+BREAK after its detach
instead of killing, are left to Step 6.

**Step 4 — The failure boundary.** The failure matrix, the extraordinary ends (a kill, a stack overflow,
`Environment.FailFast`) and, where it can be induced, a `DebuggerError`, each in a process of its own. Recorded,
per platform and without generalizing across platforms: whether a native debugger operation stays blocked after the
target vanishes, and whether one whose bound expired ever completes later; whether a failure contaminates later
sessions; which level each consequence reaches; whether DrHook
can detect it; and whether finding 100's fault is the target's abrupt disappearance rather than the signal that
caused it.
*Kill and FailFast run 2026-11-05* ([finding 104](../../../poc/drhook-engine/findings/104-crash-boundary.md); probe
100 at `04aff45` on omega, and on omega-ubuntu with both builds; 10 runs per mode per machine and build; Martin
approved the omega runs, which hang only the probe's own child process).
- A target that ends itself with `Environment.FailFast`, every stop continued, left the debugger unusable 2 of 10 on
  omega and 8 of 20 on omega-ubuntu. So the fault follows the disappearance, not DrHook's signal.
- Ending the session at the `Exception(Unhandled)` stop that precedes the crash was clean 60 of 60: kill 30, detach 30.
- So was a kill after a Pause, 30 of 30.
- Later sessions in the process were contaminated whenever it hung.
- DrHook detected none of the hangs.

*Stack overflow, native fault and `abort()` run 2026-11-06* (finding 104; probe 100 at `4da7093`, same machines and
builds, every stop continued).
- A stack overflow gives the same `Exception(Unhandled)` stop as FailFast (30 of 30, via
  `EEPolicy::HandleFatalStackOverflow`). Continued, it hung the debugger 5 of 10 on omega, 0 of 20 on omega-ubuntu.
- A native fault and a direct `abort()` give no stop (60 of 60), and still hung the debugger: 6 of 20 on omega, 4 of
  40 on omega-ubuntu. Ending the session at a stop cannot avoid these.
- A `ProcessExited` stop after the end came in 74 runs, all usable; without one, 15 of 16 hung. A one-sided
  detection candidate, not yet used by DrHook.

*Ending at a stack overflow's stop, how long `ProcessExited` takes, and Windows, run 2026-11-06* (finding 104,
second batch; [finding 105](../../../poc/drhook-engine/findings/105-dll-pal-never-rechecks-a-waited-process.md);
omega, omega-ubuntu with both builds, omega-windows; 10 runs per mode).
- Ending the session at a stack overflow's stop was clean, 60 of 60 on omega and omega-ubuntu.
- On those two, `ProcessExited` came within 34 ms of the end in every usable run (92) and not within 30 s in any hung
  one (28); no second attach that missed 30 s finished within 120 s. A detector with both sides.
- The cause (finding 105): a request in flight when the target dies is never woken, because mscordbi's PAL checks a
  waited process only when the wait starts and starts no thread to check it again. Nothing exported starts one.
- omega-windows, which has no PAL and no transport: usable in all 130 runs, every crash continued included;
  `ProcessExited` came in every one, 13 to 172 ms after the end. A native fault gives a stop there; `abort()` does
  not.
- The first batch's `ProcessExited` window was about 200 ms, not the 2 s it stated (corrected in finding 104).

*The same second batch on Linux x64, run 2026-11-06* (findings 104 and 105; zbook, bare metal, Microsoft's .NET
10.0.14, probe 100 at `63a26b2`, 10 runs per mode, every probe child sampled with gdb at 45 s).
- Ending the session at a stack overflow's stop was clean, 20 of 20. Continued: FailFast and stack overflow hung 0 of
  10 each, a native fault 1 of 10, `abort()` 3 of 10.
- `ProcessExited` came within 25 ms of the end in all 36 usable runs and not within 30 s in any of the 4 hung ones;
  every hung second attach was still pending at 120 s. The detector holds on x64.
- The stacks, named with Microsoft's symbol files, show finding 105's mechanism on Linux: mscordbi's Win32 event
  thread waiting with no timeout in `DbgTransportSession::WriteMemory` → `SendRequestMessageAndWait`, from the DAC's
  `RequestSyncAtEvent` (5 of 5); the second attach waiting on the DAC's `g_dacMutex` in `FlushCache`, finding 100's
  chain; and no PAL worker thread in the debugger's PAL (`libmscordaccore`), in the hung runs and in 3 usable ones.
- The second a Linux target takes to die after its stop is continued is Ubuntu's apport, which receives the core dump
  (zbook, and omega-ubuntu checked by sky-omega). With the pipe dump skipped the target died in 8 to 23 ms, and
  `abort()` still hung 1 of 5. Not a factor in the hang.

*The six CI runners, run 2026-11-06* (finding 104; run 42248605917 at `63a26b2`, nine modes, 10 runs each).
- Ending the session at the stop was clean on every runner, 239 of 239 runs with a stop; a kill after a Pause, 60 of
  60.
- Continued crashes hung the debugger on all four macOS and Linux runners (40 of 160) and on neither Windows runner
  (0 of 80). No second attach that missed 30 s finished within 120 s.
- `ProcessExited` came within 175 ms of the end or not within 30 s. Its absence went with a stuck event thread in all
  22 macOS runs that lacked it, and with a blocked next attach in 20 of them: it marks the session's debugger as lost,
  not always the process as poisoned.
- On Windows x64 a stack overflow gave no stop in 2 of 30 runs (no harm there).

*A target DrHook launched, run 2026-11-07* (findings 104 and 105; probe 100 at `3a7ab57` plus four `launch-measure-*`
modes; omega, omega-ubuntu, zbook and omega-windows; 10 runs per mode, launched and attached in one batch).
- A launched target, the debugger's own child, hung the debugger as an attached one did: 26 of 120 launched runs on
  the three Unix machines, 32 of 120 attached; none of 80 on Windows. As finding 105 predicted.
- `ProcessExited` came in every usable run and in no hung one, launched or attached; no second attach that missed
  30 s finished within 120 s. The detector does not depend on how the session began.
- zbook's named stacks of all 10 hung children show the same request and lock as before, and no thread in `waitpid`
  or `waitid`.

Not yet run: a `DebuggerError` (handled to its documented contract since `3a7ab57`, not induced), and whether a
bounded operation completes later.

**Step 5 — A minimal process-isolation proof of concept.** The worker is decided (2026-11-06); this step proves its
design, not its need. A supervisor and one worker. Induce finding 100's exact
failure in the worker; the supervisor must detect it, discard the worker, and conduct a fresh session that succeeds.
Having lost the worker, the supervisor cannot always know what became of the target, so it reports the target's last
known state, when that was observed and how certain it is, and unknown when nothing establishes it. Also recorded,
per platform: what happens to a target held stopped when its worker dies. What the snapshot cannot see, native frames
and native threads, is filed in the limits register: [drhook-snapshot-native-frames](../../limits/drhook-snapshot-native-frames.md).
*Recorded 2026-11-17* ([finding 106](../../../poc/drhook-engine/findings/106-worker-process.md); probe 106 at
`81859423` on zbook, macbook, omega-windows and omega-ubuntu, 20 runs per mode each, RESULT(0) on every machine; not
yet run on the runners).

- **Recovery.** On Linux and macOS a native crash lost the worker's debugger in 11 to 45 % of attempts. The worker
  called it lost 2 s after the target was gone; a `ProcessExited` that did come came within 35 ms. The supervisor
  discarded the worker in 1 to 15 ms, and a fresh worker's session succeeded 2.3 to 3.7 s later: 104 of 104.
- **Detection from outside.** With the worker's verdict ignored, a 30 s bound on its next command found every loss,
  53 of 53, and agreed with the worker's facts each time.
- **Windows.** The debugger was never lost (0 of 320 attempts), as findings 104 and 105 predict.
- **A target held at a stop when its worker dies.** On Linux and macOS it stays held, and a new attach fails with
  `CORDBG_E_TIMEOUT` (120 of 120). On Windows it dies with the worker (40 of 40).
- **The journal.** It recorded each discard before the kill, with the target's last known state, who observed it, and
  how certain that was.

**Step 6 — Decide (Epistemics).** From Steps 2 to 5: the session end for each scenario; the operation contracts; what
replaces ADR-008's Decision 1; the bounds, detection and anomalies for the extraordinary ends; the worker's lifetime
(per session or per runtime version; a worker per session would also bring an engine change to the next session without
a reconnect, see the containment boundary) and its protocol; how DrHook attributes an expired bound. This ADR moves to Accepted with that decision.
*Decided 2026-11-20* (Martin: "I approve the draft, your recommendations are my choices as well"):
[the decisions](#step-6-the-decisions-decided-2026-11-20).

**Step 7 — Engineering.** Implement only what Step 6 decided: for the worker, the minimum protocol,
lifecycle state and supervision, and no general-purpose framework. Rewrite the tests in the kinds above, with
recovery validated as rigorously as prevention. Update ADR-007, ADR-008, DRHOOK.md's lifecycle section and the MCP
tools' descriptions together. The hang watcher, or a permanent regression detector in its place, stays: it is not
removed because the suite has turned green.
*6.2 and 6.3 built 2026-11-20* (zbook, Linux x64; Windows and macOS through CI).
- **6.2.** An owned `Dispose` no longer kills a target still running after its exit request. It leaves it running
  (`TargetLeftRunning`, the `TargetStuckAtDispose` anomaly) with its pipes open and its process handle kept. On Unix
  it is reaped once its end has been read, under the lock `KillLeftRunningTarget` holds, so a kill cannot reach a
  reused PID. On Windows a running owned target is left running at once.
- **The MCP layer.** `drhook_stop` reports `targetLeftRunning` with the pid. `drhook_kill` without a session ends such
  a target after the identity check.
- **6.3.** `Abandon` reads whether the pump holds the target at a stop (`CallbackPump.IsParked`, raised before the
  stop is published). If not, it pauses the target, waiting up to 2 s, a first bound to be measured. It then kills,
  and reports `KilledWhileStopped` or `KilledWhileRunning`, which `drhook_kill` returns as `killedWhile`.
- **Found while building it: zombies.** Since finding 110 an owned stop detaches before the target ends. mscordbi's
  PAL reaps only a child it still debugs (finding 87), so every launched target that `drhook_stop` ended stayed a
  zombie of `drhook-mcp` (zbook, 2026-11-20, in the live server). Each such child is now reaped once its end has been
  read, the same way as a target left running.
- **Tests.** `SessionEndTest` covers five cases:
  - a target that refuses SIGTERM, left running and then killed;
  - no zombie after a stop;
  - a kill at a breakpoint;
  - a kill of a running target, followed by a launch in the same process.

  Each fails under a mutation that undoes its change. The `CallbackPumpTests` cover `IsParked`.
- **Live.** Seen through `drhook-mcp` on zbook:
  - a target that ignores SIGTERM was left running, and `drhook_kill` then killed it (signal 9), with no zombie;
  - a running target was paused, then killed while stopped;
  - the next launch stopped at its breakpoint.
- **Results on zbook.** DrHook.Engine.Tests 232, the integration suite 29, DrHook.Viz.Tests 74, DrHook.Wire.Tests 7,
  0 failed in four further full runs of the suite.
- **A lost exit status.** A target killed while the debugger is attached can lose its exit status to mscordbi's PAL,
  which reaps the child as DrHook's wait wakes. It happened once in about 20 kills at a breakpoint, in a full run of
  the suite, and in none of 30 runs of the kill tests alone; DrHook reported the end as unknown. Finding 87 had
  assumed that a wait already in place reads first.
  - **Decided 2026-11-20 (Martin):** DrHook reports its own SIGKILL when the OS record is lost.
  - `TargetExit.OrTheKillDrHookSent` turns that one unknown into "killed by signal 9, which DrHook sent", marked
    `sentByDrHook`. It applies only when this session sent the kill. An OS record always wins, and an end unknown
    for any other reason stays unknown.
- **Not yet.**
  - What happens, when `drhook-mcp` exits, to an owned target it left running (6.2's open rule).
  - The stop bound's measurement.
  - Whether a kill of a stopped target makes that race likelier.

*6.7's start-up measured 2026-11-21* (sky-omega; [finding 113](../../../poc/drhook-engine/findings/113-worker-startup.md),
probe 113 at `0ff69282` on omega, omega-ubuntu and omega-windows, 100 sessions each, in fresh workers and in workers
that had run a session before).

- **What a worker per session adds to a session.** 43 to 141 ms at the median, at most 600 ms. Closing a session whose
  target has ended takes about 200 ms on every machine, more than that.
- **What it is.** On omega and omega-ubuntu it is mostly the worker's own process start (73 and 37 ms); on omega-windows
  that start is about half of it. A first session is slower than a later one in the same worker mostly on its launch.
  Debug and Release workers cost the same.
- **Seen on the way, the same in every design.** A launch on macOS takes about 550 ms (11 to 26 ms on omega-ubuntu), and
  in 2 of 18 fresh workers on omega a first launch took about 500 ms more. A session's close sleeps 200 ms after its
  target has ended (`ExitWorkSettleMs`).

*6.7's design, drafted 2026-11-21 and accepted the same day* (sky-omega; Martin: "Draft accepted."; nothing of it is
built yet). This is the smallest worker that does what 6.5, 6.7, 6.8 and 6.9 decided. Where the design names a
measurement, that measurement comes first.

- **Where the boundary falls: at the tool.**
  - **The worker** is a new program, `drhook-worker` (`src/DrHook.Worker`).
    - It runs a session as `EngineSteppingSession` runs one today: the session's tools, their JSON results, the braid
      and the debug-state publisher.
    - `EngineSteppingSession` moves into it from DrHook.Mcp.
  - **drhook-mcp becomes the supervisor.** It keeps what has no debugger in it, or outlives a session:
    - the MCP surface;
    - `drhook_processes` and `drhook_snapshot`, which read through EventPipe and load no mscordbi;
    - the anomaly, log and console buffers that the drain tools read, which already outlive a session for its last
      drain;
    - the targets `drhook_stop` left running (below);
    - the journal (6.9).
  - **The 25 tools and their results stay as they are.**
  - **Why not at the engine's own calls.** A boundary there would move about 1,600 lines of result rendering into the
    supervisor, and carry the engine's types (stops, values, fields) across the pipe, one round trip per engine call.
- **The protocol.** NDJSON on the worker's stdin and stdout, one message per line.
  - **The streams.** The end of the worker's stdout means the worker is gone. Its stderr goes to drhook-mcp's stderr.
  - **To the worker:**
    - `{id, tool, args}`, for each tool that needs the session, with the tool's own name and arguments;
    - `{id, health}`;
    - `exit`.
  - **Answers from the worker:**
    - `{id, result}`: the tool's own JSON. An image goes as base64, beside its caption.
    - `{id, health}`: the tool running, its phase, and how long it has run.
  - **Events from the worker, as they happen:**
    - `ready`: its PID, its runtime and its protocol version;
    - `target`: the target's PID, its start time, and whether the session owns it, as soon as the session knows them;
    - `state`: stopped, and where, or running. It is sent from the one place where a stop is recorded today
      (`PublishTransportSnapshot`);
    - `anomaly`, `log` and `console`: each record as it reaches the worker's sinks;
    - `ended`: a target left running has ended.
  - **One request at a time, in order.** `health` is answered at once by the thread that reads stdin, so it is answered
    while a request runs.
  - **DrHook.Wire's codec** does not carry these messages: they carry the tools' JSON, not Wire's types. It stays the
    codec of the debug-state stream.
  - **Versions.** The supervisor refuses a worker whose protocol version differs from its own, and says that drhook-mcp
    must be restarted. The worker follows the build; the supervisor does not.
- **The worker's life.** One worker per session, and one session at a time, as today.
  1. **Starting.** `ready` comes within 30 s, the bound 6.8 set for attach and launch. Finding 113 measured at most
     75 ms; the runners are not measured.
  2. **In session.** From `drhook_launch` or `drhook_attach` to the end of the session.
  3. **Ending.** However the session ended, the supervisor then sends `exit`, and the worker ends by itself within 5 s.
     Finding 113 measured 5 to 19 ms.
  4. **Holding a target left running.** After a `drhook_stop` that left an owned target running (6.2), its worker
     stays, debugging nothing.
     - It answers `drhook_kill` and `exit`. When the target ends it reports `ended`, then ends itself.
     - This keeps 6.2's one way to kill such a target: `KillLeftRunningTarget`, under its lock against the reap, with
       the target's pipes still open. The supervisor does not get a second one.

  **When drhook-mcp ends** (a reconnect), each worker reads the end of its stdin.
  - A worker in session ends the session as `drhook_stop` does, then exits. *Amended 2026-11-22 (Martin), after
    finding 116:* a request waiting for its target to stop (a launch or attach to its breakpoint, a continue, a step)
    is ended first: its wait is cancelled, so the end is not held behind it.
  - From its own end, drhook-mcp reads nothing a worker does as a failure: a request still pending is not asked for
    health, and its answer is returned as it comes. The worker is neither kept nor discarded.
  - A worker holding a target exits and leaves the target running.
  - So a reconnect ends a session by its scenario's end (6.5), even when drhook-mcp is killed. What then becomes of a
    target left running is 6.2's open rule, unchanged.
- **Detection and the discard** (6.8).
  - **The worker's own verdict, built into the engine.** If the target is gone, read from the operating system, and no
    `ProcessExited` has come within 2 s, `WaitForStop` returns `DebuggerLost` with that cause, as probe 106's worker
    did. Today it waits out its budget, up to 2 minutes, and the tool reports a timeout. `DebuggerError` stays as built.
  - **From outside.** While a request runs, the supervisor sends `health` every 5 s. The phase in the answer is one of
    two:
    - waiting for the target: a stop, or an evaluation's end, each bounded by its own timeout. The engine marks where
      it waits.
    - debugger work: everything else.
  - **Suspected.** The supervisor suspects the worker in any of these cases:
    - debugger work has run for more than 30 s (6.8's bound);
    - `health` gets no answer within 5 s. That bound is checked against the first measured round trips before it is
      kept;
    - `ready`, or the worker's end after `exit`, is late.
  - **The discard.** The worker is discarded when it is suspected, when it has called its debugger lost, or when its
    stdout ends. The supervisor:
    1. journals the intent: the worker, the reason, and the target's identity and last known state, with who observed
       that state, when, and how certain it is;
    2. kills the worker, and waits for its end;
    3. deals with the target:
       - an owned target still alive with the same PID and start time is ended through the operating system;
       - a borrowed target is left as it is. On Unix, one held at a stop stays frozen (finding 106);
    4. answers the pending tool call with three things: the session's outcome; the target's state, as the operating
       system shows it; and the cause, as the evidence shows it (lost, `DebuggerError`, or unknown);
    5. raises the anomalies 6.8 named: debugger lost, worker suspected, worker discarded, target left held.

    The next `drhook_launch` or `drhook_attach` starts a fresh worker, as every session does.
- **The debug-state publisher (ADR-012) runs in the worker.**
  - It already starts at a session's launch or attach and stops at its end.
  - Everything it publishes is made in the session: the snapshot, the event stream and the braid.
  - Relaying it through the supervisor would serialize every snapshot twice and make the supervisor a second
    publisher.
  - When a worker is lost, its views lose the connection without a last message, as they do today when drhook-mcp dies.
    They wait for the next session, whose publisher removes the stale socket file.
  - Each record is held twice:
    - the worker keeps its sinks' recent records, for the snapshots it publishes;
    - the supervisor's buffers get every record the worker forwards, and the drain tools read those.

    Both processes use the same sink classes.
- **The worker's own copy of the build.** *Amended 2026-11-21 (Martin), before it was built: no copy.* The worker runs
  from its own directory beside drhook-mcp, `worker/`, in drhook-mcp's output and in its package. A change to DrHook
  reaches it as it reaches drhook-mcp: disconnect, rebuild, reconnect. The copy below protected a running worker from a
  rebuild under it, which that rule rules out, and its stale copies needed an owner's identity (the PID and start time
  that broke Mercury's TempPath, and that DrHook cannot read on Windows). What follows is the design as accepted, kept
  as the record.
  - **Where it comes from.** The worker is built into its own directory, in drhook-mcp's output and in its package.
  - **The copy.**
    - At each start, the supervisor copies that directory into `workers/` in DrHook's directory:
      `~/Library/SkyOmega/drhook` on macOS, `~/.local/share/sky-omega/drhook` elsewhere, as for the rendezvous socket.
    - The worker runs from the copy, and the copy is deleted once the worker has ended.
    - Each copy is named by its drhook-mcp's PID and start time. Copies left by a drhook-mcp that has since ended are
      deleted when the next drhook-mcp starts.
  - **Run from source** (`.mcp.json`): a rebuild reaches the next session without a reconnect.
  - **Installed as a global tool:** an update removes drhook-mcp's own directory. The next session then fails and says
    why, and a reconnect is needed, as today.
  - **To measure first:** what the copy costs, on finding 113's three machines. If it is not small beside the start-up,
    the copy is made once per build instead.
    *Measured 2026-11-21* ([finding 114](../../../poc/drhook-engine/findings/114-worker-copy.md); probe 114 at
    `dbd336d1`, 20 copies of drhook-mcp's whole Debug output, 92 files and 37 MB, an upper bound on the worker's).
    - The copy took 12.8 ms at the median on omega, 3.9 on omega-ubuntu and 33.4 on omega-windows, at most 45.6 ms.
    - That is 8 to 26 % of the start-up. So the copy is made at each worker's start, not once per build. (Not built:
      see the amendment above.)
- **The journal** (6.9).
  - **Where.** `journal.ndjson` in DrHook's directory, append-only.
  - **When.** It is written and flushed before each of these:
    - every discard;
    - every kill of an owned target after a discard;
    - every `drhook_kill`: the supervisor journals an Abandon before it sends it to the worker.
  - **Its size.** Bounded as mercury.log is: 4 MB, then one older file.
  - **What it holds.** Identities (PIDs, start times, the program's path), reasons, states and stop locations. Never
    values, arguments or environment: principle 11's redaction lies in what it records.
  - **Into Mercury.** The operator consolidates it: each discard's anomaly carries its journal entry. The service
    never waits on Mercury.
- **Tests.**
  - The session's logic keeps its tests in one process: the integration suite drives `EngineSteppingSession`
    directly.
  - The worker gets tests of its own:
    - a session through a worker;
    - a discard after an induced loss (probe 106's constructions), then a fresh session;
    - drhook-mcp ending with a session open;
    - a target left running, then killed through its worker.
- **Not in this design:**
  - a worker reused across runs (ADR-015 decides that);
  - Job Objects on Windows;
  - the supervisor's own anomalies on the views;
  - a tool list that changes without a reconnect.

*6.8's own verdict built 2026-11-21* (sky-omega, omega): the first part of 6.7's design.
- **Where.** In the callback pump's wait, so that every wait in the engine has it: a stop, a step, and an evaluation's
  end. It reads the operating system every 100 ms (`DebugSession.TargetHasExited`, through the PID and start time on
  Unix). When the target has ended and the pump has published no `ProcessExited` for 2 s
  (`DebugSession.ExitDeliveryBound`), the wait returns `DebuggerLost` with `ExitNotDelivered` and raises the
  `ExitNotDelivered` anomaly.
- **Not while the target is held at a stop.** mscordbi delivers a held target's exit only after the stop's `Continue`,
  so a wait then proves nothing about the debugger.
- **The same loss as `DebuggerError`.** The pump records the first loss of either kind and keeps it. From then on the
  session refuses every native call, and its teardown makes none. The teardown would otherwise wait in the request
  that never returns (finding 103).
- **Tests.** Four in `CallbackPumpTests`: the verdict within the bound, and nothing continued after it; a delivered
  exit; a held target; a running one. The held-target and delivered-exit tests each fail when their guard is removed.
  On omega the engine's tests passed 236 of 239 (3 skipped) and the integration suite 28 of 29 (1 skipped).
- **Not yet seen on a real stuck session.** Probe 106's constructions come with the worker's own tests.

*6.7's worker and drhook-mcp's relay built 2026-11-21* (sky-omega, omega): the second part.
- **The worker.** `drhook-worker` (`src/DrHook.Worker`). `EngineSteppingSession` moved into it, and the libdbgshim
  packages with it. Its life is as designed:
  - it says it is ready, then runs the requests one at a time;
  - it ends on `exit`, at the end of its stdin, or on SIGTERM, SIGINT or SIGHUP. A session still open is ended as
    `drhook_stop` ends it, and a target left running is left running. If a request is hung, it exits after 30 s
    anyway;
  - it holds a target left running until that target ends.
- **The protocol.** `WorkerProtocol`, in DrHook.Engine, written and read by the same code on both sides. Two changes
  from the design:
  - every answer says what the session is afterwards: `active`, `holding` or `ended`. So the supervisor never reads
    the tools' JSON;
  - `target` has no start time yet, and is sent with the answer, not as soon as the session knows it. Both come with
    the discard, which needs them.

  `state` and `health` come with the discard too.
- **The supervisor.** drhook-mcp:
  - the drain tools read its buffers, which it resets when a new session starts;
  - it relays one request at a time, each session in a fresh worker;
  - a session tool asked for with no session open goes to a fresh worker, which answers as the session does, so
    every result still comes from one place;
  - `drhook_kill` with no session open goes to the worker that holds the target it names. Both the supervisor and
    the session choose with `LeftRunningChoice`;
  - at its own end it closes every worker's stdin, and waits up to 10 s for them.
- **Built into `worker/`** in drhook-mcp's output, in the output of a project that references it, and in its package:
  60 files, 32 MB in Debug on omega. The worker runs from there; it is not copied (the amendment to 6.7, above).
  drhook-mcp now references neither the debugger nor DrHook.Viz.
- **Tests.** `WorkerTest` has four:
  - a session through a worker, whose anomalies reach the drain;
  - a target that ends before its breakpoint, whose console reaches the drain;
  - drhook-mcp ending with a session open;
  - a target left running, held by its worker, then killed through it.

  Results on omega:

  | Suite | Passed | Skipped |
  |---|---|---|
  | DrHook.Engine.Tests | 236 | 3 |
  | Integration suite | 32 | 1 |
  | DrHook.Viz.Tests | 74 | 0 |
  | DrHook.Wire.Tests | 7 | 0 |
  | DrHook.Capture.Tests | 1 | 0 |
- **Finding 86's lock is gone between sessions.** A .NET 9 session and then a .NET 10 session both stopped at their
  breakpoints, each worker locked to its own target's version. In one process, the second launch was refused (probe
  115, [finding 115](../../../poc/drhook-engine/findings/115-runtime-switch.md)).
- **Seen live**, after Martin's reconnect: one session through drhook-mcp on omega. The worker was drhook-mcp's child
  and the target the worker's. `drhook_snapshot_image` returned its caption and image. The worker's two `DepthClamped`
  anomalies reached `drhook_drain_anomalies`. After `drhook_stop` neither the worker nor the target was left.
- **Not yet:**
  - health, suspicion, the discard and the journal;
  - the runners and the other platforms.

*6.8's health checks and the discard built 2026-11-21* (sky-omega, omega): the third part.
- **What the worker reports.**
  - `target`: the target's PID, its start time on Unix (`DebugSession.TargetStart`, read as `TargetHasExited` reads
    it), and whether the session owns it. Sent as soon as the session has a target, before it runs to its breakpoint.
  - `state`: stopped, running or ended, with the stop's reason and top frame. Sent from the one place a stop is
    recorded (`PublishTransportSnapshot`); the top frame costs one frame walk per stop.
  - **Health.** The thread that reads stdin answers a health request at once, with the tool running, its phase and
    how long it has been in it. The phase is the engine's own mark: the pump flags each wait for a stop or an
    evaluation's end (`DebugSession.IsWaitingForTarget`); everything else is debugger work.
  - **A lost debugger.** An answer whose session's debugger is lost says so (`lost`, with the cause).
- **Suspicion.** While a request runs, drhook-mcp asks for health every 5 s. It suspects the worker when:
  - a health request is unanswered after 5 s;
  - debugger work has run past 30 s;
  - the worker is not ready within 30 s, or not ended 5 s after `exit`.

  Each raises `WorkerSuspected` or `WorkerDiscarded`.
- **The discard.** It happens when the worker is suspected, when it calls its debugger lost, or when it ended before
  it answered:
  1. drhook-mcp kills the worker and waits for it;
  2. an owned target still running under the same identity is ended through the operating system. On Unix the
     identity is the PID and start time; between that check and the kill the PID could in principle be reused. On
     Windows it is a handle drhook-mcp opened at the `target` event, so there is no such window;
  3. a borrowed target is left as it is. If it was last reported stopped, on Unix, `TargetLeftHeld` says so;
  4. the pending call is answered with `status: workerDiscarded`: the reason, the cause (`DebuggerError`,
     `ExitNotDelivered` or unknown), and the target's PID, ownership, state as the operating system shows it, and
     last reported state;
  5. `WorkerDiscarded` is raised. The worker's own `DebuggerError` or `ExitNotDelivered` anomaly is 6.8's "debugger
     lost".

  When the worker called its debugger lost, its own answer is the one returned, and it is discarded afterwards rather
  than asked to exit.
- **Tests.** `WorkerTest` gained a discard. A worker stopped with SIGSTOP during a continue that waits:
  - is discarded 10.0 s after the continue began, the health request at 5 s unanswered;
  - its owned target is ended through the operating system;
  - both anomalies are raised;
  - a fresh worker's session works.

  On omega every DrHook suite passed: DrHook.Engine.Tests 236 (3 skipped), the integration suite 33 (1 skipped),
  DrHook.Viz.Tests 74, DrHook.Wire.Tests 7, DrHook.Capture.Tests 1.

  Not tested yet:
  - the lost path through a worker, which needs probe 106's induced loss, which is not deterministic;
  - the 30 s debugger-work bound, which no construction yet holds a native call for;
  - Windows, where the SIGSTOP construction has no equivalent.
- **The 5 s health bound is kept** (probe 116, [finding 116](../../../poc/drhook-engine/findings/116-health-round-trip.md)).
  A health request every 20 ms, 150 or more per phase, idle, during launches and while a continue waited: every one
  answered, the longest round trip 3.32 ms, on omega-windows. omega and omega-ubuntu agree. In the runs after the fix
  below, 5.91 ms at most, and 17.16 ms in one Windows run with a process watcher polling beside it.
- **The worker's end waited for a request that waits** (finding 116). The worker runs its end after the request
  running, and a `drhook_continue` that waits returns only at a stop, at the target's end, or after 2 minutes. So a
  reconnect during such a continue ended the session as `drhook_stop` does only if the target stopped or ended within
  `EndBound` (30 s); otherwise the worker exited with its session not ended. On omega-windows it did, at 30.1 s.

*The end of a wait at the worker's end, built 2026-11-22* (sky-omega, omega), on Martin's decision.
- **The wait is cancelled.** The worker cancels a token when its end is asked for (`exit`, the end of its stdin, a
  signal). Every request is given it. The wait whose length the target sets observes it:
  `EngineSteppingSession.WaitForStopShowingRunning`, used by launch and attach to their breakpoint, by continue and by
  the steps. Through `DebugSession.WaitForStop(timeout, cancel)`, `CallbackPump.WaitForStop` reads the token once per
  100 ms poll, after a stop or a loss that has come, and throws `OperationCanceledException`. The request fails with
  that message, and the end then runs as `drhook_stop` runs. The debugger's own short waits (10 s or less) and an
  evaluation's own bound are not cancelled.
- **drhook-mcp's own end.** Building it showed a second gap. When drhook-mcp ended, the continue's pending health
  request could not be sent to a worker whose stdin drhook-mcp had just closed. It was read as no answer: the worker
  was suspected, discarded and killed, and its owned target with it. The answer of a request still pending was then
  settled too, as if drhook-mcp still ran. Now, from its end, the supervisor asks no health and settles no answer.
- **Tests.**
  - `CallbackPumpTests`: a cancelled wait throws; a stop or a loss that has come is returned first. Each fails under
    its mutation: the token not read, or read before the stop is taken.
  - `WorkerTest.DrHookMcpEnding_DuringAContinueThatWaits_EndsTheWait_ThenTheSessionAsStopDoes`: the worker exits
    within 10 s of drhook-mcp's end (133 ms on omega). The continue answers that its wait was cancelled. Nothing is
    journaled and no worker is suspected, and on Unix the target ends with its session. Without the fix it fails: the
    answer is `workerDiscarded`, "it did not answer a health request within 5 s". With the supervisor's part but no
    cancellation it fails too: the worker had not exited within 10 s.

  On omega every DrHook suite passed: DrHook.Engine.Tests 239 (3 skipped), the integration suite 35 (1 skipped),
  DrHook.Viz.Tests 74, DrHook.Wire.Tests 7, DrHook.Capture.Tests 1.
- **Probe 116 at `b71d9cc`:** asked to exit during a continue that waits, the worker exited 155 ms after the request
  on omega, 144 ms on omega-ubuntu, and 265 ms and 84 ms in two runs on omega-windows (finding 116). On Windows the
  last target, detached and left running as 6.4 has it, ran on and ended by itself; no target of the run's kills was
  left.

*Windows' exit request (6.4), built 2026-11-22* (sky-omega, omega), on Martin's decision: the worker way.
- **The launch.** `WindowsSpawn` adds `CREATE_NEW_PROCESS_GROUP`, so a launched target roots a process group of its
  own, whose ID is its PID.
- **The send.** `WindowsConsoleBreak.SendTo` is finding 117's construction. The process running the session leaves its
  console and attaches to the target's. Only once it is there does it send CTRL+BREAK to the target's group. It then
  leaves, returns to its parent's console (drhook-worker's is drhook-mcp's) and sets its standard handles back.
  - One send at a time per process.
  - A step that does not go so is reported: `UnexpectedHResult`, operation `RequestExit (CTRL+BREAK by way of the
    target's console)`.
  - The wait for the target's end follows only an event that was sent.
- **Which targets.** `DebugSession.ExitRequest` names the request:
  - SIGTERM on Linux and macOS;
  - CTRL+BREAK on Windows, for a target the session launched;
  - none for a borrowed target, and none for an attached Windows target, whose group is not known. Windows does not
    document what `GenerateConsoleCtrlEvent` does with a PID that roots no group.

  A target with none is left running at once, as before. drhook-mcp's owned targets are all launched, so every
  `drhook_stop` of one now sends a request. The stop's result and `TargetStuckAtDispose` name the request sent.
- **A difference from Unix.** CTRL+BREAK also reaches the processes the target started in its group (finding 117).
  SIGTERM reaches the target alone.
- **Tests.** The snapshot target answers the platform's request; .NET names CTRL+BREAK `SIGQUIT`. On Windows, where
  before a running launched target was left running:
  - `TeardownOrderTest` runs: the target's handler found no debugger attached when the request came.
  - `SessionEndTest`: a target that refuses CTRL+BREAK is left running, with the same record from its handler, and
    `drhook_kill` ends it. One without a handler ends with `0xC000013A`.
  - `TargetExitTest`: `drhook_stop` reports the target's end.
  - `WorkerTest`: a drhook-worker sends the request, and the target ends. No anomaly of `RequestExit` reaches
    drhook-mcp. A mutation that raised one on every send failed the test on omega. A target that refuses is left
    running, and its handler's record shows the request came after the detach.

  At `98416b5`, on omega-windows: DrHook.Engine.Tests 220 (22 skipped, POSIX-only), the integration suite 33 (3
  skipped, POSIX-only). The log is omega-windows `C:\Users\bemafred\diag\b64-98416b5.log`, sha256
  `7623d24f…73b27507`. On omega: DrHook.Engine.Tests 239 (3 skipped), the integration suite 35 (1 skipped),
  DrHook.Viz.Tests 74, DrHook.Wire.Tests 7, DrHook.Capture.Tests 1.

  CI run 45028119039, at `581c131`, passed on all six runners. On Windows x64 and Windows arm64 the integration
  suite skipped only its three POSIX-only tests, so the tests above ran and passed on both, x64 included.
- **Not established.** Finding 117's open items stay open: standard handles that are console handles, a sender with
  no console or a console not its parent's, and a console event while the sender is away.

*6.9's journal built 2026-11-21* (sky-omega, omega): the last part.
- **Where.** `journal.ndjson` in DrHook's directory, beside the rendezvous socket, one JSON object per line,
  append-only. drhook-mcp is given its path, so the tests keep a journal of their own.
- **When.** Each entry is written and flushed to disk (`fsync`) before the act it records:
  - a discard;
  - the kill of an owned target after a discard;
  - every `drhook_kill`, sent to the session's worker or to the worker holding a target left running.
- **What.** Each entry has the time of writing, the entry's kind and the PID of the drhook-mcp that wrote it. Then:
  - the worker's PID;
  - the reason: the discard's, or the kill's hypothesis, which drhook_kill already records as the anomaly's value;
  - the cause;
  - the target's PID, start time and ownership, the program's path, and its last reported state, with the stop's
    top frame.

  Never arguments, environment or values; a test checks that the target's arguments are absent.
- **Its size.** At 4 MB it is moved to `journal.ndjson.1`, which it replaces, as mercury.log is.
- **Several drhook-mcp processes,** one per client session, share it. Each write holds `journal.ndjson.lock`
  exclusively, waiting up to 2 s.
- **A failed write** is reported on stderr and in the anomaly, and the act goes ahead: the service never waits on it.
- **Into Mercury.** The `WorkerDiscarded` anomaly carries its journal entry, for the operator to consolidate.
- **Tests.** In `WorkerTest`:
  - the discard test reads the discard's entry, then the kill of its owned target;
  - the left-running test reads the `drhook_kill` entry.

  `JournalTest` checks the appending and the bound. On omega every DrHook suite passed: DrHook.Engine.Tests 236 (3
  skipped), the integration suite 34 (1 skipped), DrHook.Viz.Tests 74, DrHook.Wire.Tests 7, DrHook.Capture.Tests 1.

### Step 6: the decisions (decided 2026-11-20)

Drafted from Steps 1 to 5, and **decided by Martin on 2026-11-20**, every item as drafted: "I approve the draft, your
recommendations are my choices as well". Each item gives the question, the evidence, the options and the decision.
Where an item names a measurement, that measurement comes before the part it bears on is built (Step 7).

**6.1 — The session end for each scenario.**

| Scenario | The end | Evidence |
|----------|--------------|----------|
| Inspecting code being designed (launched) | it runs to its end; ended early, detach first, then the exit request (6.2) | a natural end left the debugger usable 760 of 760 (finding 102); detaching first, the integration suite hung 0 of 40 against 15 of 40 (finding 110) |
| A failing test, under MTP or VSTest | remove the breakpoints and continue to the end of the run | not measured: Step 2's scenarios have not run |
| Attaching to a running program | detach and leave it running | after a detach the target ran on, 759 of 760 (finding 102) and 240 of 240 (finding 112); on Windows it stayed held 4 times (6.6) |
| Diagnosing a hang | a snapshot first, then pause and inspect, then detach; a kill only as in 6.3 | — |
| Catching a crash | end the session at the `Exception(Unhandled)` stop by default: detach, or for an owned target a kill while stopped. Continuing is the operator's choice, with its cost stated at the stop | ending at the stop was clean: 60 of 60 for FailFast, 80 of 80 for a stack overflow on our machines, 239 of 239 runs with a stop on CI (finding 104). Continued, the debugger hung on Unix in 26 of 120 launched and 32 of 120 attached runs, and in none of 80 on Windows |
| Hunting an intermittent failure (ADR-015) | each run ends by itself | as the first row |
| A program with a window | detach and leave it running | not measured in this ADR |
| A server or a service | continue it, detach, then the stop request (6.2) | a Generic Host ended cleanly under the debugger, on SIGTERM or by itself (finding 112, within 240 of 240), and on CTRL+BREAK (Windows, within 40 of 40) |

Continuing past a crash's stop is how a debugger shows the program's own death (finding 104 leaves that trade-off
here). With a worker (6.7), a continued crash that loses the debugger costs that worker and a few seconds, not the
service: a fresh session completed 2.3 to 3.7 s after the discard (finding 106). So continuing is allowed, but it is
not the default.

**6.2 — The ordinary end of an owned target, and what replaces ADR-008's Decision 1.**

- **Evidence.**
  - Attached, a SIGTERM to a target without a handler lost the debugger in 6 of 14 sessions (finding 112).
  - After a detach, every signalled target ended and the debugger stayed usable, 240 of 240 (finding 112).
  - Detaching first removed the suite's cascade (finding 110).
  - Without a handler the request is an abrupt end: exit 143 on Unix (finding 68), `0xC000013A` on Windows
    (finding 112).
- **Decided, in place of Decision 1.** An ordinary end never signals an attached target. It detaches (synchronized)
  and then sends the platform's catchable request: SIGTERM on Unix, CTRL+BREAK on Windows (6.4). It reports how the
  target ended, read from the operating system (`TargetExit`), so a handler's clean exit and a default's abrupt one
  are not both called graceful. SIGTERM stays a cancellation request and is not counted as a natural exit.
- **The choice: a target still alive when the bound expires** (2 s, ADR-008's Decision 2).
  - (a) Kill it, as today, with `TargetStuckAtDispose`. The debugger has detached, so the kill cannot hurt it; the
    cost falls on the target alone. DAP's `disconnect` also terminates a launched program by default.
  - (b) Report it as left running, with its identity, for the client to end with `drhook_kill`. This is the Dispose
    contract above ("never killing the target implicitly") and principle 4, both approved with the charter.
  - Decided: (b). A target that ignores its stop request is an anomaly worth a deliberate decision. This is a
    compatibility change, so the MCP tools' descriptions and DRHOOK.md change with it. It leaves open what happens to
    an owned target still running when `drhook-mcp` itself exits.

**6.3 — Abandon (`drhook_kill`).**

- **Today.** An owned target gets SIGTERM, 200 ms, then SIGKILL; a borrowed one gets SIGKILL at once. Both happen
  while attached, with the target possibly running (`DebugSession.Abandon`).
- **Evidence.**
  - A kill while the target was synchronized was clean: 30 of 30 at a Pause stop, and 30 of 30 at the
    unhandled-exception stop (finding 104).
  - Finding 105 explains why: no request is in flight to the target.
  - A kill of a running, attached target is finding 100's route.
- **Decided.**
  - Abandon first asks the target to stop, within a short bound (a detach needs the same answer from the target's
    runtime).
  - If it stops, the target is killed while synchronized, and runs no more code.
  - If the bound expires, it is killed as today, and a lost debugger is the worker's to contain (6.7).
  - The SIGTERM grace is dropped: a stopped target cannot run its handler, and an attached SIGTERM is the case that
    lost the debugger. A polite request is `drhook_stop`'s path (6.2).
  - Abandon is journaled before it acts (6.9) and checks the target's identity, its ID and start time.
  - The stop bound is set from measurement.
- **Not measured.** Abandon itself (probe 112 did not call it); a stop-then-kill of a flooding target.

**6.4 — Windows: the exit request, and the answer to `ControlCTrap`.**

- **The exit request.** CTRL+BREAK to the target's own process group, after the detach.
  - Evidence: a handler or a Generic Host ended cleanly, 40 of 40, and host-self-stop 20 of 20; without a handler, an
    abrupt `0xC000013A` (finding 112).
  - It needs the target launched with `CREATE_NEW_PROCESS_GROUP`, which also disables CTRL+C in that group
    (documented for `CreateProcess`), and a sender that shares the target's console.
  - DrHook launches with `CREATE_NO_WINDOW` (`WindowsSpawn.cs`), so the target's console is not `drhook-mcp`'s.
    Probe 112's sender shared the console by construction. *Measured 2026-11-22* on omega-windows ([finding 117](../../../poc/drhook-engine/findings/117-ctrl-break.md)): a
    sender that attaches to the target's console (`AttachConsole`) delivers the event to a target launched as DrHook
    launches it with `CREATE_NEW_PROCESS_GROUP` added, 80 of 80, and neither the sender nor its parent receives it.
    Both ways delivered: the worker itself, off drhook-mcp's console for about 1 ms, and a helper process, about 43 ms
    from its start to its exit. *Decided 2026-11-22 (Martin):* the worker way, "Build the worker way." *Built
    2026-11-22* (Step 7, "Windows' exit request").
  - Until it was built, Windows had no exit request: `drhook_stop` detached and reported a running owned target left
    running (6.2 (b), built 2026-11-20). *Corrected 2026-11-22:* this line said "today it kills", which stopped being
    true when 6.2 (b) was built. An attached Windows target still has none: CTRL+BREAK goes to a process group, and
    an attached target's is not known.
- **`ControlCTrap`.**
  - Today DrHook answers `S_OK`, so the runtime counts the event as handled: a target without a handler runs on (20
    of 20), and the debugger was never lost (finding 112).
  - `S_FALSE` would pass the event on to the target's handlers and Windows' default, so a target without a handler
    would end abruptly while attached. No crash or abrupt end has lost the debugger on Windows in any run (findings
    104 to 106, 112), so `S_FALSE` is probably safe there. It is not measured.
  - Decided: `S_FALSE`, so that a program behaves under DrHook as it does without it, and the event is reported to
    the operator. To be measured first: probe 112's no-handler child, attached, with `S_FALSE`, on omega-windows and
    both Windows runners, with the debugger usable N of N.
  - *Measured 2026-11-23 on omega-windows* ([finding 112](../../../poc/drhook-engine/findings/112-cancellation.md#windows-with-drhook-answering-s_false-2026-11-23)),
    at `4e72de5`, which makes the change. The no-handler target, attached, was usable and clean 40 of 40:
    - one `ControlCTrap`, then exit `0xC000013A` 25 to 211 ms after the event, as without a debugger;
    - the debugger saw every exit, and every final attach succeeded.

    The other Windows constructions were unchanged from the `S_OK` run. The answer reaches the target: mscordbi sends
    the callback's return value back, and the runtime counts the event as handled only for `S_OK` (read in
    `release/10.0`, finding 112). Not run on the Windows runners. Reporting the event to the operator is not built:
    informational callbacks stop in the worker.

**6.5 — Target preservation when a worker goes.**

- **Evidence.**
  - On Linux and macOS, a target held at a stop when its worker died stayed held, alive and frozen, and a new attach
    failed with `CORDBG_E_TIMEOUT`, 120 of 120 (finding 106).
  - On Windows it died with the worker, 40 of 40. Windows documents that a process debugged through the Win32 API
    ends with its debugger unless `DebugSetProcessKillOnExit(FALSE)` was called; whether mscordbi calls it was not
    read.
  - A target that is running when its worker dies is not measured.
- **Decided.**
  - A healthy worker is never discarded while a target is attached. The supervisor first ends the session by its
    scenario's end (6.1). So the gate holds for every ordinary end: the end of a worker's life, a change of runtime
    version, a reconnect.
  - When a worker is lost, no native call can release its target.
    - An owned target is ended through the operating system, as Dispose's debugger-lost branch does today.
    - A borrowed target is left as it is, and reported. On Unix: alive but suspended, and not debuggable again until
      it restarts. On Windows: ended with the worker.
    - Either way, its last known state goes in the journal (6.9).
  - The gate's "wherever the platform permits" is then recorded per platform. On Unix a lost debugger leaves a
    stopped target frozen; on Windows it ends it. DrHook cannot guarantee either target's survival.
- **Open, and measurable.**
  - Whether `DebugSetProcessKillOnExit(FALSE)` in the worker would keep a borrowed Windows target alive through the
    worker's loss, and if so, whether it then runs or stays held.
  - A target running when its worker dies (finding 106, next 3).

**6.6 — No callback is left un-continued at a detach.**

- **Evidence.**
  - A borrowed detach of a busy target drops one callback almost every time: 754 of 760, then 200 of 200 and 278 of
    280 in the rerun (finding 102).
  - `Dispose` closes the callback pump before it synchronizes the target, so a callback in that window is neither
    queued nor continued. The owned detach (finding 110) also runs after the pump has closed.
  - On omega-windows the target stayed held after a detach 4 times: 2 of 80 (finding 102) and 2 of 120 (finding
    112). On the Windows runners it never did (0 of 160).
  - Hypothesis, not established: the held target is held at the dropped callback. Finding 42 saw a target stay held
    after a detach from a stop that was never continued.
- **Decided.** The teardown leaves no callback un-continued. Either a callback that arrives once the pump has stopped
  dispatching is continued and reported, or the target is synchronized before the pump closes.
  - Which one is decided by measurement. Probe 102's detach constructions must show no dropped callback. Many detach
    sessions on omega-windows give the held-target rate, before and after the change.
  - It is a teardown change, measured before it is accepted.

**6.7 — The worker: its lifetime and protocol.**

- **Evidence.**
  - A fresh worker's session (launch, a Break stop, locals, a natural end) completed 2.3 to 3.7 s after a discard
    (finding 106).
  - A debugger process is locked to one runtime version (finding 86).
  - An engine change reaches the next session without a reconnect, if the worker starts from the current build
    (2026-11-07, the containment boundary above). *Not pursued (2026-11-21): a change reaches DrHook by disconnect,
    rebuild, reconnect.*
- **The options for its lifetime:** a worker per session; a worker per runtime version; one long-lived worker,
  replaced only when lost.
- **Decided: a worker per session.**
  - The discard is then the normal end of every session, so recovery is the path exercised most, not least.
  - Finding 86's lock goes away, and an engine change reaches the next session.
  - The worker runs from its own copy of the build.
  - *Amended 2026-11-21 (Martin), before it was built:* no copy. The worker runs from `worker/` beside drhook-mcp, and a
    change reaches DrHook by disconnect, rebuild, reconnect, so "an engine change reaches the next session" is not a
    reason any more. A worker per session stands on the others: the discard as the normal end, finding 86's lock gone
    (finding 115), and a lost debugger that costs only its worker.
  - Its cost is a start-up per session. That was not measured on its own, and is measured before Step 7 is accepted.
    ADR-015's runs in a row may need a worker reused across runs; that is decided there.
- **Decided: the protocol.** NDJSON requests and responses over the worker's stdin and stdout, as in probe 106; the end
  of the stream means the worker is gone. DrHook.Wire's codec carries the messages where it fits.
  - The debug-state publisher (ADR-012) moves with the engine into the worker, or the supervisor relays it. That is
    for Step 7.

**6.8 — Detection, bounds and attribution.**

- **Evidence.**
  - Once the target was gone, `ProcessExited` came within 175 ms or not within 30 s (findings 104, 105). In finding
    106 it came within 0 to 138 ms.
  - The worker's 2 s check reported every loss and no false one, at 2,002 to 2,031 ms (finding 106).
  - From outside, a 30 s bound on the next command found 53 of 53 losses, and suspected no worker that had not lost
    its debugger (finding 106).
  - An attach took 13 to 14 ms at the median, and 7.8 s at worst, on the macOS x64 runner (finding 102).
- **Decided.**
  - **Lost.** The worker calls its debugger lost when the target is gone, read from the operating system, and no
    `ProcessExited` has come within 2 s, or when a `DebuggerError` arrives. It reports the cause and makes no
    further native call.
  - **Bounds.** Every command to the worker has a bound on the supervisor's side. A command that waits for the
    target, such as a continue to the next stop, is bounded by what the operator asked for, and meanwhile the worker
    answers a health request with its current native operation and how long it has run. Attach and launch get 30
    s, as in probe 106; the other bounds come from measurement in Step 7.
  - **Suspected.** An expired bound marks the worker suspected. The supervisor discards it without waiting for
    confirmation, since a fresh session costs seconds (6.7).
  - **The cost.** A worker that was only slow is lost with its session, and on Unix a target it held stays frozen
    (6.5). The 30 s bound is four times the slowest attach measured.
  - **Attribution.** The cause is recorded as the evidence shows it: lost (the target gone, no `ProcessExited`),
    `DebuggerError`, or unknown. An expired bound DrHook cannot attribute is recorded as unknown, never as the
    target's fault (principle 12).
  - Each of these is a typed anomaly: debugger lost, worker suspected, worker discarded, target left held.

**6.9 — The journal.**

- **Evidence.** Probe 106's journal recorded each discard before the kill: the intent, the worker, the reason, and
  the target's last known state, who observed it, when, and how certain it was. That held for every discard, 80 of
  80 on zbook and 68 of 68 on omega-ubuntu (finding 106).
- **Decided.** That record becomes the supervisor's local journal: append-only and bounded, written before every
  discard and every Abandon, and consolidated into Mercury afterwards. The service never waits on a Mercury write.
  Its location, retention and redaction (principle 11) are for Step 7.

**What these decisions rest on that is not measured yet** (each measured before the part it bears on is built):

- Step 2's scenarios: a test run under MTP and under VSTest, and DrHook's own test host (6.1).
- Probe 112's owned path, and `drhook_kill` itself (6.2, 6.3).
- `S_FALSE` for `ControlCTrap` (6.4). Measured 2026-11-23 on omega-windows, 40 of 40 clean
  ([finding 112](../../../poc/drhook-engine/findings/112-cancellation.md#windows-with-drhook-answering-s_false-2026-11-23));
  not on the runners.
- A CTRL+BREAK sender for a target launched with `CREATE_NO_WINDOW` (6.4). Measured 2026-11-22 on omega-windows
  ([finding 117](../../../poc/drhook-engine/findings/117-ctrl-break.md)); not on x64 or the runners. Built the same day;
  its tests passed on omega-windows and on both Windows runners, x64 included (CI run 45028119039).
- A target running when its worker dies, and `DebugSetProcessKillOnExit` (6.5).
- What holds a Windows target after a detach (6.6).
- A worker's own start-up time (6.7). Measured 2026-11-21 on omega, omega-ubuntu and omega-windows
  ([finding 113](../../../poc/drhook-engine/findings/113-worker-startup.md)); not on x64 or the runners.
- What a worker's copy of the build costs (6.7's design, above). Measured 2026-11-21 on omega, omega-ubuntu and
  omega-windows ([finding 114](../../../poc/drhook-engine/findings/114-worker-copy.md)); not on x64 or the runners. The
  copy was then dropped (2026-11-21).
- A real `DebuggerError` (6.8).
- Probes 106 and 112 on the six runners.

6.2 and 6.3 replace ADR-008's Decisions 1 and 3. ADR-007, ADR-008, DRHOOK.md and the MCP tools'
descriptions change together in Step 7.

### Development milestones

- On every platform, a natural end and a clean detach never leave the debugger unusable: N of N in Step 2's probes.
- 10 consecutive green CI runs on all six runners with the watcher recording nothing. That shows the suite behaving
  consistently in those runs; it does not show a rare lifecycle failure is gone.

### Production gates

DrHook is production-ready when each of these holds:

| Area | Gate |
|------|------|
| Natural completion | repeated exits, detaches and immediate relaunches without loss of debugger capability |
| Cooperative shutdown | an explicit, declared shutdown mechanism, observed completion, and correct reporting on refusal or timeout |
| Ordinary ends | no ordinary session end uses a potentially terminating operating-system signal unless that mechanism is part of the target's declared lifecycle contract |
| Extraordinary termination | a known, reported consequence, with bounded impact on the supervising service |
| Native deadlock | a blocked native debugger call cannot permanently block the MCP service; every public operation returns within its bound from the client's side |
| Worker recovery | a fresh session succeeds after a deliberately induced unrecoverable debugger failure, and the lost target is reported with its last known state, when that was observed and how certain it is |
| Target preservation | attached programs survive ordinary session ends and debugger failures, wherever the platform permits that guarantee |
| State correctness | no successful response after an operation has invalidated the session; errors are typed and informative; reading a dead target returns a structured failure, never an internal null dereference (the `NullReferenceException` from `GetStackFrames` and `GetLocals` after a kill, probe 100) |
| Scenarios | every scenario in the table reaches its specified lifecycle outcome on every claimed platform, with failure containment and recovery verified, and DRHOOK.md and the MCP tools' descriptions tell the operator which end fits which scenario |
| Resource stability | long sequences of sessions stay within the thread, memory, handle and temporary-file budgets |
| Cross-platform evidence | each supported OS and architecture has recorded native tests for every claimed capability, along the four axes |
| Continuous regression | ordinary, failure-injection and soak suites run at suitable intervals, keeping machine-readable evidence |
| Safety | every test and probe runs within enforced limits and isolation, with the risk it may pose to its machine defined and accepted beforehand; attach, evaluation and termination are authorized before DrHook runs across hosts |

## Not in this ADR

Separate items, to be taken up with Martin:

- A line breakpoint set on an attached file-based target stayed pending and never bound.
- An upstream report to dotnet/runtime of mscordbi's unbounded read. Secondary: the hang follows from our forced
  stop. Reconsidered after Step 4.
- The cause of the two single-test timeouts of 1.8.38 and 1.8.39 (no stacks exist; finding 103 records a third).
- A general-purpose worker or distributed-execution framework.

## References

- Martin's assessment of ADR-016, 2026-10-31 (recorded in Mercury): the rule this ADR serves, recovery as a
  first-class requirement, the worker candidate, the separate state records, the operation contracts, cancellation
  as a declared capability, the failure matrix, the production gates
- Martin's second assessment, 2026-10-31 (recorded in Mercury): accepted as the experimental charter; the
  scenario gate as lifecycle outcomes; suspected against confirmed health; the lost target's last known state;
  Close and process identity; limits, isolation and accepted risk in place of absolute promises
- [Finding 100](../../../poc/drhook-engine/findings/100-orphaned-transport-read-holds-dac-lock.md) and probe 100
  (`poc/drhook-engine/100-orphaned-read-smoke.cs`)
- [ADR-008](ADR-008-process-lifecycle-discipline.md) (Decision 1, revisited here) and
  [ADR-007](ADR-007-teardown-concurrency-test-debug.md) Phase 9 (the cross-platform validation campaign)
- [ADR-012](ADR-012-debug-state-surfaces.md) (DrHook.Wire) and [ADR-015](ADR-015-run-until-anomaly.md) (run until an
  anomaly)
- Findings 63 (kill-first chosen), 66 (target-death detection), 67 (natural endings by default), 68 (signal
  disposition without a debugger), 86 (one runtime version per debugger process), 89 (the stop counter), 90 and 91
  (the VSTest host's process group and hold), 94 (the VSTest handshake), 87, 92, 96 and 99 (first runs on Linux and
  Windows)
- Microsoft Learn, the debugging API:
  [`ICorDebugManagedCallback::ExitProcess`](https://learn.microsoft.com/dotnet/framework/unmanaged-api/debugging/icordebugmanagedcallback-exitprocess-method),
  [`ICorDebugManagedCallback::DebuggerError`](https://learn.microsoft.com/dotnet/framework/unmanaged-api/debugging/icordebugmanagedcallback-debuggererror-method),
  [`ICorDebug::Terminate`](https://learn.microsoft.com/dotnet/framework/unmanaged-api/debugging/icordebug-terminate-method),
  [`ICorDebugController::Stop`](https://learn.microsoft.com/dotnet/framework/unmanaged-api/debugging/icordebugcontroller-stop-method)
- Microsoft Learn, shutdown and processes:
  [`PosixSignalRegistration.Create`](https://learn.microsoft.com/dotnet/api/system.runtime.interopservices.posixsignalregistration.create),
  [`GenerateConsoleCtrlEvent`](https://learn.microsoft.com/windows/console/generateconsolectrlevent),
  [Job Objects](https://learn.microsoft.com/windows/win32/procthread/job-objects),
  [.NET Generic Host](https://learn.microsoft.com/aspnet/core/fundamentals/host/generic-host) and
  [`HostOptions.ShutdownTimeout`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.hosting.hostoptions.shutdowntimeout)
- [Debug Adapter Protocol specification](https://microsoft.github.io/debug-adapter-protocol/specification): `launch`,
  `attach`, `disconnect`, `terminate`, the `exited` and `terminated` events
- [Visual Studio: debug multiple processes](https://learn.microsoft.com/visualstudio/debugger/debug-multiple-processes):
  Stop Debugging, Terminate All, Detach All
- vscode-go, [`goDebugFactory.ts`](https://go.googlesource.com/vscode-go/+/refs/tags/v0.24.2/src/goDebugFactory.ts):
  a new adapter process for each session
- [NUnit issue 3054](https://github.com/nunit/nunit/issues/3054): test timeouts while a debugger is attached
- Limits [drhook-clean-detach](../../limits/drhook-clean-detach.md) (stop before detaching) and
  [drhook-detach-exit-race](../../limits/drhook-detach-exit-race.md) (a target that exits at Dispose; finding 66)
- [DRHOOK.md](../../../DRHOOK.md), "Lifecycle discipline & common pitfalls"
- [Runbook: Microsoft's .NET on Linux](../../operations/runbook-linux-dotnet-runtime.md)
- `tools/watch-drhook-hang.sh` and the macOS hang watcher in `.github/workflows/test.yml`
