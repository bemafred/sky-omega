# Finding 110 — `drhook_stop` asked a launched target to exit while the debugger was still attached: the cause of finding 100's cascade in the integration suite

**Date:** 2026-11-15 (reproduced and measured); 2026-11-16 (fixed)
**Status:** **Reproduced** on demand on macbook and **fixed** in `DebugSession.Dispose`, by Martin's decision of
2026-11-16 ("Yes, detach first"). The fix was measured on macOS arm64 (macbook) and Linux x64 (zbook) only.
[Finding 100](100-orphaned-transport-read-holds-dac-lock.md)'s mechanism in mscordbi is unchanged: a target that dies
by itself during a request still hangs the debugger, and the worker process (ADR-016 Step 5) is still needed.

## Summary

When a session with a target DrHook had launched or attached-and-owned ended, `DebugSession.Dispose` resumed the
target with every breakpoint active and the debugger attached, and then sent it SIGTERM. A target that passed through
its breakpoint, or ended a thread, while it died gave mscordbi's event thread a read of the dead target to wait on for
ever. That thread holds the DAC lock, so every later attach and launch in the process hung: finding 100's cascade.

Detaching first removes it. On macbook under CPU load the integration suite hung in 15 of 40 runs with the old order
and in 0 of 40 with the new one.

The same order had a second effect that no test checked. A target stopped at a breakpoint on a line it passes often
never handled the SIGTERM: DrHook waited the full natural-exit timeout (2 s) and then killed it.

## The machine

macbook joined the network on 2026-11-15:

- Apple M1 Pro, 8 cores, 16 GB, macOS 26.4.1;
- System Integrity Protection on, Developer Mode **off** (`DevToolsSecurity -status`);
- .NET runtime 10.0.15, SDK 10.0.404.

The whole suite passed there once at `d4f11f2a`: 6,952 tests, 0 failed, 159 skipped. So DrHook's debugging works on a
Mac with Developer Mode off; every Mac recorded in [finding 101](101-platform-facts.md) had it on. The hang
watcher's samples below show mscordbi reading the target over the transport (`ShimRemoteDataTarget::ReadVirtual` →
`DbgTransportSession::ReadMemory`), the path finding 100 hangs in.

## Reproduced

Each run is `dotnet test --project tests/DrHook.Engine.IntegrationTests` in a clone at a named commit, beside
`tools/watch-drhook-hang.sh`. The load is eight `yes > /dev/null`, one per core.

| Commit | Load | Runs | Hung |
|--------|------|------|------|
| `d4f11f2a` | none | 25 | 0 (each run about 28 s) |
| `d4f11f2a` | 8 busy loops | 2 | 2: 6 and 15 of 24 tests timed out at 120 s |
| `d4f11f2a` | 8 busy loops, stopped at 150 s and sampled | 10 | 6 |

A loaded run that does not hang takes 40 to 49 s. In the narrowing runs the host was sampled at 150 s. A sample with a
thread waiting in `DacDbiInterfaceImpl::FlushCache` counted as a hang, and the host and its targets were then ended so
the next run could start.

## Where it hangs

Every hang had the same two threads in the test host:

- One `CordbWin32EventThread` in a callback, waiting for a read of the target to be answered:
  `ShimProxyCallback::Breakpoint` (or `::ExitThread`) → `DacDbiInterfaceImpl::RequestSyncAtEvent` →
  `ShimRemoteDataTarget::ReadVirtual` → `DbgTransportSession::ReadMemory` → `SendRequestMessageAndWait`.
- The next session's `CordbWin32EventThread` in `AttachProcess` → `CordbProcess::Init` → `ForceDacFlush` →
  `DacDbiInterfaceImpl::FlushCache`, waiting for the DAC lock the first one holds.

These are finding 100's stacks. Across the 15 sampled hangs of the old order (6 at `d4f11f2a`, 9 in the confirmation
below):

- **The callback the thread was in:** `Breakpoint` in 10, `ExitThread` in 3, none named in the sample in 2.
- **The test that had finished just before the first test that hung:**
  - in 10, a test that stops a launched target at a breakpoint on a line it passes every 50 ms and ends it there:
    `SpanLengthCondition_CompilesEvaluatesAndGates_WithoutFuncEval` (4),
    `CaptureState_AtBreakpoint_AssemblesSelfContainedSnapshot` (4), `Stop_OfAnOwnedTarget_ReportsHowItEnded` (2);
  - in 2, `AttachAndOwn_MtpTarget_DisposeDuringFlood_NoWorkerExceptionOrSilentBreak`, a session disposed while its
    target floods the debugger with events (both `ExitThread`);
  - in 3, the two launch tests whose target never reaches the debugger. The session that hung in those runs is
    presumably the launch before them, `LaunchInitialBreakpoint_IsRemovableById`, which ends a launched target at its
    breakpoint; that is not established.

Each launch that hung left its target alive, reparented to `launchd`, stopped inside `EEStartupHelper`. SIGTERM did
not end those targets; SIGKILL did.

## The cause in DrHook

`DebugSession.Dispose`, owned branch, before this change (`origin/main` at `b208d9ff`):

1. The callback pump is closed.
2. `TryResumeForDetach` calls `Continue` until the target runs. **Every breakpoint is still active and the debugger is
   still attached.**
3. `RequestExit` sends SIGTERM and waits up to 2 s; `TryKillTargetAndSettle` sends SIGKILL if the target lives.
4. Only after the target is dead: `Detach`, `Terminate`, release.

Between steps 2 and 3 the running target can hit its breakpoint again, or end a thread as it dies. mscordbi's event
thread then dispatches the event and reads the target's memory to do so, and the signal ends the target with that read
outstanding. The read is never answered (finding 105: mscordbi's PAL never re-checks a process it waits on).

ADR-016 recorded the order on 2026-10-31: "the debugger is still attached and handling the target's events when the
signal lands". Finding 100's Next section proposed this fix and left one doubt: that it "conflicts with finding 64's
kill-first protocol". It does not:

- **Finding 64's kill-first** fixed a crash in the host when a target was killed after a detach (MCH-RE-2,
  [finding 63](63-mch-re-2-investigation.md)).
- **[Finding 89](89-borrowed-detach-leak.md)** found what made that crash: a detach from an unsynchronized process
  fails, mscordbi keeps its process object, and the target's later exit runs against DrHook's freed callback.
- **The new order** uses the synchronized detach of finding 89 (Stop, deactivate the breakpoints, Detach). The
  borrowed path and `drhook_detach` have used it since. Dispose also waits for the target's exit before it releases
  the callback.

## The second effect: a target at a hot breakpoint was always killed

The regression test below, run against the old order, failed 3 of 3 times in 2.6 to 2.7 s. The target's SIGTERM
handler never ran. My reading, not traced:

- The resumed target hit its breakpoint again within 50 ms, and with the pump closed nobody continued it.
- So the target stayed stopped by the debugger, and the managed SIGTERM handler could not run.
- DrHook waited the natural-exit timeout, then killed the target.

So `drhook_stop` of such a target was never the graceful exit its description promised. Its result said "killed by
signal" in either case, which is why `Stop_OfAnOwnedTarget_ReportsHowItEnded` did not notice.

## The change

`DebugSession.Dispose`, owned branch:

1. `DetachSynchronized`: Stop, deactivate the breakpoints, Detach. mscordbi resumes the target on Detach, so it runs
   free and un-debugged.
2. `RequestExit` (SIGTERM, up to 2 s), then SIGKILL if the target ignores it, as before.
3. Terminate and release. The later detach is skipped: mscordbi has neutered its process object, so the target's end
   raises no callback and there is no exit work to settle.

The target's end is still reported, because `ProcessLifetime` watches the child and not the debugger. If the
debugger is lost during the detach, the target is ended with the OS and nothing more is called on mscordbi, as in
Dispose's debugger-lost branch. `TryResumeForDetach` had no caller left and was removed.

**What it costs.** Detach needs the target's runtime to answer the synchronize request. A target whose runtime cannot
answer now holds `drhook_stop`, where the old order went on to SIGTERM and SIGKILL. `drhook_kill` is the escape for that
target.

**What it does not change.** `Abandon` (`drhook_kill`) still ends the target with the debugger attached, because
detaching would need the cooperation `Abandon` cannot count on. A target killed there, or one that crashes or is killed
from outside during a request, can still leave the debugger hung. That remedy is the worker process.

## Measured

At an experiment commit (`f6414242`, `d4f11f2a` plus the variants behind an environment variable, never on `main`;
the bundle is in the evidence archive), on macbook with eight busy loops:

| Owned teardown | Runs | Hung |
|----------------|------|------|
| Old order (variable unset, same build) | 30 | 9 |
| Stop, deactivate the breakpoints, Continue, SIGTERM | 10 | 1 (the `ExitThread` case) |
| Detach first (this change) | 10 + 30 | 0 |

Counting the six of ten at `d4f11f2a`: old order 15 of 40, detach first 0 of 40. With detach first all 23 tests passed
in every run, in 40 to 43 s. On zbook (Linux x64) the experiment's detach first passed `DrHook.Engine.IntegrationTests`
and `DrHook.Engine.Tests` in 5 of 5 runs, and no target was left.

Deactivating the breakpoints alone narrows the window but cannot close it: a dying target still ends its threads under
the debugger.

## The regression test

`TeardownOrderTest.Stop_OfAnOwnedTargetAtABreakpoint_DetachesBeforeAskingTheTargetToExit` checks the order itself,
which needs no load:

- **The target:** `DrHookSnapshotTarget report-attached-on-sigterm <file>` loops through `SNAPSHOT_HERE` and records,
  in its own SIGTERM handler, whether a managed debugger was attached (`Debugger.IsAttached`).
- **The test:** it stops the target at that line, calls `drhook_stop`'s path, and expects `attached=False`.

| Code | Runs | Result |
|------|------|--------|
| Old `DebugSession.cs` in the same worktree | 3 | Failed 3 of 3: the handler never ran (above) |
| With the change | every run | Passed |

It is POSIX-only: on Windows `RequestExit` cannot signal (ADR-007 Phase 9), so DrHook kills the target and no handler
runs.

## Found while verifying the fix: two crashes of the test host

The first build of this change ran on macbook (whole suite once idle, then 20 loaded runs of the integration suite).
No run hung, but the test host crashed three times. macOS kept a crash report for each, which the CI runners never
gave (`~/Library/Logs/DiagnosticReports`).

**Two aborts (exit 134), in 2 of the 20 loaded runs: a race in the test harness.** The host printed
`Unhandled exception. System.ObjectDisposedException ... Object name: 'System.Diagnostics.Process'` at
`Process.get_StandardError()`, in `TargetSpawn.ExtractPid`. That method starts two threads that drain the target's
output, and each called the `Process` getter inside the thread. When the test had already disposed the `Process`, the
getter threw on a background thread, which ends the process. My reading of why it showed now: sessions end faster
without the old 2 s wait and kill, so under load a test can finish before a drain thread has run. `ExtractPid` now
takes both readers on the caller's thread and the threads end quietly when the stream is closed under them.

**One segfault (exit 139), 3.9 s into the host, in the idle whole-suite run with Mercury's tests in parallel: a race in
mscordbi.** The faulting thread was mscordbi's event thread:

- `CordbRCEventThread::FlushQueuedEvents` → `ShimProcess::QueueFakeAttachEventsIfNeeded` → `QueueFakeAttachEvents`
  → `QueueFakeAssemblyAndModuleEvent`, reading address 0;
- at the same time the test's thread was in `CordbProcess::Detach` → `DetachShim` → `SendDetachProcessEvent`.

The source (release/10.0) explains it:

- **When the events are queued:** an attach marks that "fake attach events" are needed (`BeginQueueFakeAttachEvents`,
  from `AttachProcess`). The event thread queues them at its first flush, after it has released the process lock.
- **The comment there:** "Because we've released the lock, at any point from here forward the CorDbProcess may
  suddenly get neutered if the user detaches the debugger".
- **Where it crashes:** `QueueFakeAssemblyAndModuleEvent` uses what `EnumerateModules` returns without checking the
  result, so a neutered process gives it a null enumerator.

`CreateProcess` is the first of those events, and it is dispatched only after the queueing is done. So
`DetachSynchronized` now waits, up to 5 s, until the session has seen its `CreateProcess` callback. The pump records
that callback before anything else, also after the pump is disposed. A session that never got one is detached after
the bound, as before, with a new `DetachBeforeAttachEvents` anomaly. The guard covers every live detach: this owned
path, the borrowed path and `drhook_detach`.

**Not established:** which test it was, and why that session had not had its first flush when it ended. A session that
has seen any stop has had it. One crash in about 61 runs of detach-first does not show whether the guard removes it.
Nor is it shown whether the unexplained exit 139 of the integration host in CI run 43683089204 (macOS arm64, old
order) was this race.

**The build with both fixes** (`c5e18598`, whose code is this commit's; only this finding's text differs) ran on
macbook:

| Check | Runs | Result |
|-------|------|--------|
| Whole suite, idle | 3 | All passed: 6,975 tests, 0 failed, 159 skipped, each in about 3 min 40 s |
| `TeardownOrderTest` alone | 3 | All passed |
| Integration suite, eight busy loops | 40 | All exited 0: no hang, no crash, no crash report |

## Not tested

- **Windows and macOS x64.** No machine reachable from zbook; CI runs the suite on both.
- **A target that ignores SIGTERM** after the detach, so that it is killed detached. The code path is the old one;
  only its order changed.
- **A target whose runtime cannot answer the synchronize request** (above).
- **Whether [finding 103](103-terminate-waits-on-error-write.md)'s hang is gone too.** Its order section places the
  failed event before the detach, as the target ends: the same window. It was not reproduced, so this is not shown.

## Evidence

On zbook, `~/diag/macbook-finding100-evidence-2026-11-15.tgz` (sha256
`2c1b0703ef432d1187916a3687b640c9ce2a9c53e86e032757be25856de0466d`) holds:

- every run's log and test output;
- the hang watcher's native samples and managed stacks;
- the samples taken at 150 s;
- the experiment bundle.

On macbook the same is in `~/diag/` (`repro-d4f11f2a-out`, `repro2-d4f11f2a-out`, `teardown-exp-f6414242-out`), with
the scripts that ran them.
