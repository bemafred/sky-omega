# Finding 118 — what holds a Windows target after a detach (ADR-016 6.6): one of its own threads, in a wait no debugger left it in; the teardown change continues every callback, and the target is held as often as before

**Date:** 2026-11-24
**Status:** **RECORDED** on omega-windows (Windows 11 26200 arm64, 4 cores, .NET 10.0.15): probe 118 before ADR-016 6.6's
teardown change (`4c6d8f9`, 400 sessions) and after it (`5842fe4`, 1,200 sessions), and its dump reader (`5c747f9`) over
the held targets' dumps. Probe 102 at the change on omega, omega-ubuntu and omega-windows. The DrHook suites at the change
(`c751e25`) on all three, and at a mutation with the old teardown on omega-ubuntu. On 2026-11-28, probe 118 on both
Windows runners, x64 and arm64, 400 sessions each, at `6b39d13` (CI run 46128839394).
What closes the handle is [finding 119](119-detach-closes-a-handle.md).

## Why

After a borrowed detach DrHook means to leave the target running, un-debugged. On omega-windows the target stayed held
4 times in 200 detach sessions (findings 102 and 112), and once on the Windows x64 runner (finding 112). Every held target
looked like one stuck thread, and no state was recorded. ADR-016 6.6 decided that the teardown leaves no callback
un-continued: a detach dropped one callback almost every time (finding 102), and the held target was thought to be held
at it. Which way the teardown does it, and whether it changes the held-target rate, was to be measured, before and after.

## What was known from the source

Read in dotnet/runtime `release/10.0`:

- On Windows mscordbi also debugs the target natively (`DebugActiveProcess`), and in managed-only debugging it continues
  every native debug event at once (`ShimProcess::HandleWin32DebugEvent`). Detach ends native debugging with
  `DebugActiveProcessStop` (`CordbWin32EventThread::ExitProcess`).
- mscordbi asks for `DebugSetProcessKillOnExit(FALSE)`, but the Windows pipeline only stores the value
  (`WindowsNativePipeline::DebugSetProcessKillOnExit`), and nothing reads it. So a target still natively debugged dies
  when its debugger exits. Finding 102's held target outlived its debugger process, so it was not natively debugged any
  more.
- When the target's runtime receives the detach, it releases every thread it stopped for the debugger
  (`DB_IPCE_DETACH_FROM_PROCESS` → `ResumeThreads` → `Thread::SysResumeFromDebug`).
- `CordbProcess::Detach` requires a synchronized process, and "that the user has drained the managed event queue, but
  there is currently no way to really enforce that here". `CordbProcess::ContinueInternal`: "Every stop or event must be
  matched by a corresponding Continue. m_stopCount counts outstanding stopping events along with calls to Stop."

## What was run

[`118-held-after-detach-smoke.cs`](../118-held-after-detach-smoke.cs) runs probe 102's detach-then-end construction many
times, in child processes of 20 sessions each:

- **The target** ([`118-held-after-detach-target.cs`](../118-held-after-detach-target.cs)) floods thread events: one empty
  thread started and joined after another.
- **The debugger** attaches (borrowed), waits for 10 CreateThread events and a random 0 to 499 ms more, and disposes.
- **The target's end.** It floods 1 s more, stops, joins the flood thread and returns. It writes each phase it reaches to
  a file: attached, detached, stop, joined, done.
- **What is recorded.** Each session records the callbacks the teardown dropped (`LateCallback`), how long the target took
  to end, its exit code and its phases.
- **A held target** is one still alive 10 s after `Dispose`; they end 0.9 to 2.5 s after it otherwise. The probe records
  it, passive parts first:
  - whether a native debugger is attached (`CheckRemoteDebuggerPresent`);
  - each thread's state, wait reason, suspend count, name and start address;
  - the last phase reached;
  - a dump with the heap;
  - the managed stacks, sampled through EventPipe for 1 s;
  - what a fresh attach and detach does to it.

Then it ends the target.

The before run took 400 sessions, from 05:05 to 05:16 UTC. Probe 102 ran on omega during its last minutes. The after run
took 1,200 sessions, from 05:19 to 05:50, with nothing else running on omega or its VMs.

## Before the change: a callback dropped in every detach, and 2 targets held

| | Before (`4c6d8f9`) | After (`5842fe4`) |
|---|---|---|
| Sessions | 400 | 1,200 |
| Dropped callbacks | 400, one per session: CreateThread 272, ExitThread 128 | **none** |
| Held targets | 2 (0.5 %) | 4 (0.33 %) |
| Targets that failed a write to their own phase file (`IOException`, "the handle is invalid", exit `0xE0434352`) | 17 | 9 |
| Targets that ended with "Internal CLR error" (`0x80131506`) | 0 | 4 |
| `Dispose`, median / 95th percentile / max | 88 / 171 / 219 ms | 63 / 104 / 200 ms |
| Target's end after `Dispose`, median / max | 1,038 / 2,573 ms | 1,040 / 3,142 ms |

Both held targets of the before run had dropped a CreateThread after the queue was disposed, but 184 of the 400 sessions
had.

## What holds the target

The six held targets looked the same, before and after.

- **Windows.**
  - No native debugger was attached.
  - No thread was suspended: every suspend count was 0.
  - Beside the runtime's own named threads (`.NET Debugger`, `.NET Finalizer`, `.NET EventPipe`) and three thread-pool
    waits, there were the main thread, the `flood` thread, and one more thread with the start address of every managed
    thread (`coreclr.dll+0x5ce40`). That thread had no name, 0 ms of CPU, and was waiting (`UserRequest`).
- **EventPipe.**
  - The main thread was in `Thread.Join`, waiting for the flood thread: it had written `stop`.
  - The flood thread was in `Thread.Join`, waiting for its child. So `Thread.Start` had returned.
  - The child was not sampled once in 1 s.
- **A fresh attach and detach** succeeded (mscordbi reported 6 threads) and did not release it.
- **The runtime's own state of each thread**, from three dumps (`118-held-after-detach-dump.cs`, ClrMD 4.2.751201, through
  the runtime's own data access component):
  - The child (managed IDs 599, 981 and 1020, in sessions of 598, 979 and 1019 CreateThread events; a foreground thread, as
    the flood's children are) was
    `TS_LegalToJoin TS_WeOwn TS_CoInitialized TS_InMTA TS_FullyInitialized TS_Interruptible`, in preemptive mode, with one
    managed frame ClrMD could not name.
  - It had **no** `TS_DebugSuspendPending`, `TS_SyncSuspended` or `TS_Unstarted`. So no debugger suspension was left on
    it, and it had started.
  - `TS_Interruptible` is set only in the runtime's alertable waits (`Thread::DoAppropriateWaitWorkerAlertableHelper`,
    `Thread::UserSleep`). So the child waits in an alertable wait of the runtime's, entered from managed code.

The two createdump attempts of the before run failed: run directly, `createdump.exe` 10.0.15 answers "The pid argument is
no longer supported". The after run asked the target's runtime for the dump instead (`DiagnosticsClient.WriteDump`).

## Why it waits: a handle closed under it

The targets that failed a write to their phase file show what holds the others.

- **Where they failed.** 26 targets, in both runs, failed on the write of `detached`, just after the debugger had left.
  The write was to a file they had opened a moment before, and it failed with `ERROR_INVALID_HANDLE`.
- **What closed it.** Probe 119 found the cause: every detach closes one handle value in a Windows target twice, and the
  second close lands on whatever handle the target made in between ([finding 119](119-detach-closes-a-handle.md)).
  - When that handle is a file's, the write fails.
  - When it is one a thread waits on, or one the waking thread signals, the wait is never ended: the held target.
  - When it is one of the runtime's own, the runtime stops with "Internal CLR error".

This matches what the held targets show: a thread in an ordinary wait, with no debugger state left on it.

## The change (ADR-016 6.6): the pump runs until the detach

`53bbda6`. A detach now leaves no callback un-continued:

- **The order.** Until then `Dispose` closed the callback pump first, waited 50 ms (borrowed), and only then synchronized
  the target. Now the session synchronizes a live target with its own `Stop`, then asks the pump to continue until the
  detach (`CallbackPump.ContinueUntilDetached`).
- **What the pump does then.** It continues the stop it is parked at and every callback that still comes. It publishes no
  stop, binds no breakpoint, holds at no module load and serves no pause.
- **When it closes.** After `Detach`, and before `Terminate`. With the session's `Stop` outstanding, each `Continue`
  returns at once and leaves the target synchronized, as the detach requires.
- **A target that has gone** keeps the old order: the pump is joined first. With no `Stop` of the session's own, a
  `Continue` would send the gone target a request that waits for an answer (finding 105).

The tests:

- **`CallbackPumpTests`, two tests.** One for the parked stop and later callbacks, continued with no stop published; one
  for no bind, no hold and no pause. Each of five mutations, each removing one guard, fails one of them.
- **`InformationalFloodTest`, a new borrowed test.** It detaches in the middle of a sustained flood: the MTP target's new
  `RunThreadFlood`, probe 102's flood for 2 s.
  - It asserts no `LateCallback`. How the target ends is not asserted, since on Windows a detach can hold or end it
    (above).
  - At the mutation with the old teardown (`mut66`, never for `main`) on omega-ubuntu, it failed with one `LateCallback`.
  - The owned variants assert no `LateCallback` too, but passed at the mutation: an owned `Dispose` synchronizes right
    after closing the pump.
- **`PauseDisposeTest`.** It asserted exactly one `WorkerSilentBreak`, the old teardown's signature: the worker parked at
  the Pause stop died on the closed queue. Both variants now assert none, and failed at the mutation with one each.

| Machine | DrHook.Engine.Tests | Integration suite |
|---|---|---|
| omega | 246 passed, 3 skipped | not run: drhook-mcp runs from this source |
| omega-ubuntu | 246 passed, 3 skipped | 36 passed, 1 skipped |
| omega-windows | 224 passed, 25 skipped (POSIX-only) | 34 passed, 3 skipped |

**Probe 102 at the change** ([`102-natural-end/at-6.6-2026-11-24-*.json`](102-natural-end/)) returned RESULT(0) on omega,
omega-ubuntu and omega-windows. It is probe 102's first RESULT(0) anywhere. Every session ended naturally, and no detach
session dropped a callback, on every read path. On 11-01 almost every detach session dropped one.

| Machine | .NET | Read paths | Detach sessions dropping a callback |
|---|---|---|---|
| omega | 10.0.9 | transport (dotnet, apphost) | 0 of 80 |
| omega-ubuntu | 10.0.14, Microsoft's | direct, transport | 0 of 80 |
| omega-windows | 10.0.15 | direct | 0 of 40 |

omega's run was at `063b5f8`, whose engine is `53bbda6`'s: the amend added only an integration test's assertion.

**What it costs.**

- **omega.** A detach's `Dispose` rose from a median of 60 to 70 ms (11-01) to 92 to 95 ms. The old teardown found the
  target held at the dropped callback, so its `Stop` returned at once. Now the target runs until the session's `Stop`
  synchronizes it.
- **omega-windows.** In probe 118 the median fell, from 88 to 63 ms.

## On the runners (2026-11-28)

Probe 118 at `6b39d13`, with the change, on GitHub's two Windows runners (CI run 46128839394):

| | Windows x64 (Windows 10.0.26100, 4 cores) | Windows arm64 (Windows 11 26200, 4 cores) |
|---|---|---|
| Sessions | 400 | 400 |
| Callbacks dropped at the detach | 0 | 0 |
| DrHook's other anomalies | 0 | 0 |
| Held targets | 0 | 2 |
| Targets that ended with an unhandled exception (`0xE0434352`) before reaching `done`, as the failed writes did on omega-windows | 5 | 4 |

The records are `118-held-after-detach/runner-windows-x64.json` and `runner-windows-arm64.json`.

## What this establishes

- **The change.** ADR-016 6.6's teardown change leaves no callback un-continued: none dropped in 1,200 detaches on
  omega-windows, nor in 800 on the two Windows runners, x64 included, nor in probe 102's 200 detach sessions on three
  machines. The suites pass on all three, and on all six runners (CI run 46128873899).
- **The hypothesis.** The dropped callback is not what holds a Windows target: 2 held in 400 before the change, 4 in 1,200
  after. Findings 102 and 112's hypothesis, that the held target is held at the dropped callback, is refuted.
- **What holds it.** One of the target's own threads, the flood's child, in an alertable wait of the runtime's. Nothing
  suspends it, and no debugger state is left on it. A fresh attach and detach does not release it.
- **The cause.** It is consistent with finding 119, a handle of the target's closed at the detach by mscordbi. So are the
  failed writes (26 of 1,600 sessions) and the fatal runtime errors (4).

## Not established

- **Which handle the held child waits on, or which one its waker uses.** That needs the thread's native stack with
  coreclr's symbols, and nothing on omega-windows reads that. The three dumps are kept on omega-windows,
  `C:\Users\bemafred\diag\p118-5842fe4b-out\after.json-dumps\`.
- **Whether the change moved the rates of the failed writes and the fatal errors.** Before and after differ, but by
  counts too small to tell.

## Next (proposed, not decided)

1. ~~Run probes 118 and 119 on both Windows runners.~~ Done 2026-11-28 (above, and finding 119).
2. ~~Read the held child's native stack from a dump.~~ Not pursued (decided 2026-11-28): the cause is known (finding 119),
   and which handle a held target lost changes nothing in DrHook (ADR-016 6.6).
