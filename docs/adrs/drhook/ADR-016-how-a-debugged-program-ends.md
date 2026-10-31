# ADR-016: How a Debugged Program Ends — DrHook Stays Dependable When Its Target Is Not

**Status:** Proposed — 2026-10-31 (Emergence). Approved as the basis for the next experimental phase, not as an
implementation decision (Martin's assessment, 2026-10-31). Reframes the 2026-10-30 draft, "Deterministic Target
Teardown Across Platforms".

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
  handling the target's events when the signal lands.
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
| [`DebuggerError`](https://learn.microsoft.com/dotnet/framework/unmanaged-api/debugging/icordebugmanagedcallback-debuggererror-method): debugging services are disabled; only `ICorDebugProcess::GetID` is safe to call, not even `ICorDebug::Terminate`; the process is to be ended with operating-system facilities | forwarded as an ordinary informational event (`ManagedCallbackHost.cs`); nothing marks the debugger unusable, and Dispose would still call `Terminate` |
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
| **Crashes itself:** runaway recursion and stack overflow, `Environment.FailFast`, an unhandled exception, a native fault | nobody | report the crash; survive it | nothing; not tested | everything. A crash lands at a moment nobody chose, like a kill, and discipline cannot prevent it. Whether it can leave mscordbi as finding 100 does is unknown |
| **Machine catastrophe:** a freeze that needs a hard reset | nobody | never cause it | none observed | the paths to it. Plausible ones: a program held at a breakpoint while it holds something the whole system waits on; unbounded growth (finding 100 leaves one blocked thread and two pipe files per killed target; omega's `$TMPDIR` held 954 such files); runaway work started through function evaluation |

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
- **Paused by an outer debugger,** DrHook's own waits go on counting time and may expire while it is paused, and its
  target goes on running, or stays held, unobserved. A paused process may be unable to observe what paused it. When
  DrHook cannot tell its own pause from its target's fault, the outcome is recorded as unknown, never as a target
  failure. A monotonic deadline, an outside observer and explicit pause and resume events may improve the
  classification; none is assumed sufficient until probes show it.
- **Runtime versions.** An outer debugger is locked to the first runtime version it debugs too (finding 86).
- **Observing it without stopping it.** When DrHook's test host hung on CI, it was read with `sample` and
  `dotnet-stack`, which do not stop it at a breakpoint. DrHook's own stack snapshot uses the same mechanism as
  `dotnet-stack` (EventPipe) and should serve there as well.

## Decision (proposed)

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
7. **Every consequence is bounded, detected and reported, including the ends DrHook did not choose.** No DrHook
   call waits without a bound. A bound lets the caller stop waiting; it does not release a native call that is
   blocked, so recovery cannot rest on bounds alone.
8. **Each consequence is contained at the smallest level, and recovered from without a person:** session, then
   debugger process, then client, then machine. Nothing DrHook does may risk the machine. Recovering the service and
   recovering the target are separate obligations.
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

### A candidate containment boundary: the debugger in a worker process

As concepts, not a proposed class structure: the MCP service stays in its own process as a supervisor, and the
native debugger (dbgshim, mscordbi, the DAC) runs in a worker process. The supervisor watches the worker, reports an
unhealthy worker and the session it affects, and starts a fresh worker.

- **Why the boundary is real.** The debugger's native state is per process: the DAC is loaded once per process,
  finding 100's lock is process-wide, and finding 86 locks a debugger process to one runtime version. Once
  `ExitProcess` cannot arrive, the API leaves no clean way to shut the debugger down inside its process (Context).
  A process boundary is a real boundary around that state, not another managed abstraction.
- **What it would also dissolve.** Finding 86's runtime lock becomes per worker, not per service.
- **What it brings.** The worker is a program with its own lifecycle, held to the same rules. A launched target
  becomes the worker's child, so the worker's death affects it. Whether a target that is held stopped resumes, stays
  frozen or dies when its debugger process dies is not known on any platform. Recovering the service and
  recovering the target stay separate obligations.
- **What it needs.** A minimal protocol between supervisor and worker. DrHook.Wire (NDJSON over a socket, ADR-012)
  is an existing candidate, not a decision.
- **How it earns its place.** This is not yet evidence that a worker is needed on any platform. It is tested
  explicitly (Step 5), and built only if simpler means cannot meet principle 1, as the minimum protocol, lifecycle
  state and supervision required, with no general-purpose worker framework.

### State: the target, the session, and the debugger's health

Two independent records, not matched pairs; any combination can occur:

- **What happened to the target:** completed; cancelled cooperatively; crashed; terminated externally; killed
  deliberately; still running; unknown.
- **What happened to the session:** closed normally; detached; failed; debugger lost; recovery required; unknown.

A target can go on running after its session closes; a target can crash while the debugger survives; a debugger can
fail while the target is still alive. Keeping the two apart stops lifecycle decisions from being inferred from the
wrong state, and gives typed anomalies and Mercury records a clean basis.

**The debugger's health** is a third: healthy; degraded; unusable. After a `DebuggerError`, or when a native
operation has stopped making progress, the instance is unusable: no further native debugging call is made on it,
not even `Terminate`, and only the supervisor can recover. That is different from an ordinary session failure and is
never treated as one. `DebuggerError`, bounds expiring, missing progress and failed health checks feed one health
model, each keeping its own cause.

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
| **Abandon** | extraordinary termination; authorized, and recorded before it happens |
| **Dispose** | release the debugger's resources, never killing the target implicitly. A launched target still running is reported as left running, with its process ID, for the client to decide on; it is never orphaned silently. If the resources cannot be released safely, Dispose reports a degraded or unrecoverable outcome, contained, never a clean one |

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

### What this would change (decided in Step 6, not now)

- `DebugSession.Dispose` of an owned session and `drhook_stop` stop sending SIGTERM and SIGKILL as their ordinary
  path, under the operation contracts above. ADR-008's Decision 1 is revisited: SIGTERM remains a cancellation
  request for a program that declares it, and is not counted as a natural exit.
- `DebuggerError` marks the debugger unusable; `Terminate` is called only when its documented precondition holds.
- Waits without a bound get one (the attach that hangs in finding 100), and the state they detect is reported.
- `OwnsTarget` gives way to the three ownership facts; the target's outcome, the session's outcome and the
  debugger's health are recorded separately.
- The kill path (`Abandon`, `drhook_kill`) stays, authorized, with each platform's consequence characterized and
  reported.
- If Step 5 shows it is needed: the minimal worker and supervisor.
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
finish) or an extraordinary one, named as such. Anything that could freeze a machine runs only on a disposable VM or
CI runner, never on omega or zbook.

### The platforms, and what support is evidence for

| Platform | Our machines | GitHub runner | Notes |
|----------|--------------|---------------|-------|
| macOS arm64 | omega (metal, macOS 27, Developer Mode on) | `macos-latest` (VM, macOS 26.6, 3 cores) | where finding 100 was found and reproduced |
| macOS x64 | — | `macos-26-intel` (VM, macOS 26.6) | runner only |
| Linux x64 | zbook (Microsoft .NET 10.0.14, `ptrace_scope=1`, dash); elite (Microsoft 10.0.13, suspected RAM) | `ubuntu-24.04` | |
| Linux arm64 | omega-ubuntu (VM, Ubuntu's .NET 10.0.13, `ptrace_scope=1`, dash) | `ubuntu-24.04-arm` | the only Linux arm64 machine runs the distribution's build |
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

**Step 0 — Preconditions (Martin's decisions).**
- elite: run memtest86+ before any result from it counts.
- Linux arm64: whether to add Microsoft's .NET beside Ubuntu's on omega-ubuntu, so the debugger code can be
  compared on one machine.
- Linux stack capture: `ptrace_scope=1` stops `gdb` from attaching to a non-descendant. Options: sudo, a temporary
  `ptrace_scope=0` during a probe, or constructions that need no capture.
- Which machines may run the extraordinary probes of Steps 4 and 5 (proposed: CI runners and VMs only).
- Every machine at the same commit, with its runtimes listed (`dotnet --list-runtimes`).

**Step 1 — Platform facts (probe 101, read-only).** Attach to an idle target, change nothing, and record the
debugger code loaded, how memory is read, and the environment. It ends the session with a clean detach and leaves
the target to finish. Runs on each of our machines, and on the six runners through a `workflow_dispatch` job (a
workflow change, reviewed when proposed).

**Step 2 — The natural end.** Hypothesis: letting a target complete normally narrows the debugger's teardown races,
and should avoid the failure that forced termination causes. Whether it removes every dangerous race is an empirical
question: `ExitProcess` can arrive while the process appears stopped, and DrHook's own code names a race at the
instant of a detach. Constructions: a flooding target that ends on its own, under the debugger, N times in one
process, after which a further attach must succeed; a clean detach while the target floods, the target then
finishing on its own; a target that exits right after the detach (finding 63's case, without the kill). Then the
same natural end in each scenario: a launched probe, a test run under MTP and under VSTest, an attach followed by a
detach, and DrHook's own test host as the target.

**Step 3 — Cancellation.** Targets with a declared shutdown mechanism: a SIGTERM handler that cancels its work,
asked mid-flood under the debugger; the same target without a handler, to record what an unhandled signal does under
a debugger; a Generic Host server stopped through its own lifetime; on Windows, a console process launched in its own
group and sent CTRL+BREAK, built and reviewed before it runs.

**Step 4 — The failure boundary.** The failure matrix, the extraordinary ends (a kill, a stack overflow,
`Environment.FailFast`) and, where it can be induced, a `DebuggerError`, each in a process of its own. Recorded,
per platform and without generalizing across platforms: whether a native debugger operation stays blocked after the
target vanishes; whether a failure contaminates later sessions; which level each consequence reaches; whether DrHook
can detect it; and whether finding 100's fault is the target's abrupt disappearance rather than the signal that
caused it.

**Step 5 — A minimal process-isolation proof of concept.** A supervisor and one worker. Induce finding 100's exact
failure in the worker; the supervisor must detect it, report the affected target's state, discard the worker, and
conduct a fresh session that succeeds. Also recorded, per platform: what happens to a target held stopped when its
worker dies.

**Step 6 — Decide (Epistemics).** From Steps 2 to 5: the session end for each scenario; the operation contracts; what
replaces ADR-008's Decision 1; the bounds, detection and anomalies for the extraordinary ends; whether the worker is
needed, on which platforms; how DrHook attributes an expired bound. This ADR moves to Accepted with that decision.

**Step 7 — Engineering.** Implement only what Step 6 decided: if the worker is needed, the minimum protocol,
lifecycle state and supervision, and no general-purpose framework. Rewrite the tests in the kinds above, with
recovery validated as rigorously as prevention. Update ADR-007, ADR-008, DRHOOK.md's lifecycle section and the MCP
tools' descriptions together. The hang watcher, or a permanent regression detector in its place, stays: it is not
removed because the suite has turned green.

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
| Native deadlock | a blocked native debugger call cannot permanently block the MCP service |
| Worker recovery | a fresh session succeeds after a deliberately induced unrecoverable debugger failure |
| Target preservation | attached programs survive ordinary session ends and debugger failures, wherever the platform permits that guarantee |
| State correctness | no successful response after an operation has invalidated the session; errors are typed and informative; reading a dead target returns a structured failure, never an internal null dereference (the `NullReferenceException` from `GetStackFrames` and `GetLocals` after a kill, probe 100) |
| Scenarios | every scenario in the table ends naturally on every claimed platform, and DRHOOK.md and the MCP tools' descriptions tell the operator which end fits which scenario |
| Resource stability | long sequences of sessions stay within the thread, memory, handle and temporary-file budgets |
| Cross-platform evidence | each supported OS and architecture has recorded native tests for every claimed capability, along the four axes |
| Continuous regression | ordinary, failure-injection and soak suites run at suitable intervals, keeping machine-readable evidence |
| Safety | no test or probe risks the machine; attach, evaluation and termination are authorized before DrHook runs across hosts |

## Not in this ADR

Separate items, to be taken up with Martin:

- A line breakpoint set on an attached file-based target stayed pending and never bound.
- An upstream report to dotnet/runtime of mscordbi's unbounded read. Secondary: the hang follows from our forced
  stop. Reconsidered after Step 4.
- The cause of the two single-test timeouts of 1.8.38 and 1.8.39.
- A general-purpose worker or distributed-execution framework.

## References

- Martin's assessment of ADR-016, 2026-10-31 (recorded in Mercury): the rule this ADR serves, recovery as a
  first-class requirement, the worker candidate, the separate state records, the operation contracts, cancellation
  as a declared capability, the failure matrix, the production gates
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
