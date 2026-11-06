# Finding 104 — A target that crashes by itself can leave the debugger unusable; ending the session at its unhandled-exception stop avoided it

**Date:** 2026-11-05
**Status:** **OBSERVED** on omega (macOS 27.0.1 arm64, SDK 10.0.301) and omega-ubuntu (Linux arm64 VM, Ubuntu's and
Microsoft's .NET 10.0.14), 10 runs per mode on each; crashes without the managed warning added 2026-11-06; ending at
a stack overflow's stop, the `ProcessExited` timing and omega-windows (Windows 11 arm64 VM: no hang in 130 runs)
added later on 2026-11-06; Linux x64 (zbook, bare metal, Microsoft's .NET 10.0.14) added the same day.
**Not fixed:** DrHook continues every stop it is asked to, including the unhandled-exception one. ADR-016 Step 4,
probe 100 (`poc/drhook-engine/100-orphaned-read-smoke.cs`, modes added in `5984ad0`, `04aff45` and `4da7093`).

## The question

[Finding 100](100-orphaned-transport-read-holds-dac-lock.md) showed that a target killed while mscordbi's Win32 event
thread waits on it leaves that thread waiting forever, holding the DAC lock, so every later attach in the process
hangs. The kill there was DrHook's own. ADR-016 Step 4 asks where the failure boundary is: whether a kill made
another way is safe, whether a target that ends itself abruptly does the same damage, and whether finding 100's fault
is the target's disappearance rather than the signal that caused it.

## Constructions

All four use a target that starts and joins threads without pause, so the event thread is always dispatching
`CreateThread` and `ExitThread`. Each run is a child process of its own. After the target is gone, each run waits
10 s, records whether the event thread is inside `DbgTransportSession::SendRequestMessageAndWait` (macOS only, with
`sample`), records whether the dead target's debug pipes remain, and attaches to a new target, bounded at 30 s. A
second attach that does not finish means the debugger in that process is unusable.

| Mode | How the target ends | Predicted |
|------|---------------------|-----------|
| `flood-sync-kill` | DrHook pauses it (`ICorDebugController.Stop`), then SIGKILL while synchronized | debugger usable |
| `flood-crash` | it calls `Environment.FailFast` 0.5 to 3 s after the attach; every stop is continued, as an operator would | debugger usable |
| `flood-crash-kill` | as `flood-crash`, but at the `Exception(Unhandled)` stop SIGKILL while synchronized | debugger usable |
| `flood-crash-detach` | as `flood-crash`, but at that stop `Detach`; the target dies with no debugger attached | debugger usable |

## Results on omega

| Mode | Usable | Unusable | Detail |
|------|--------|----------|--------|
| `flood-sync-kill` | 10 | 0 | event thread idle before each kill; pipes absent; second attach ~1 s |
| `flood-crash` | 8 | **2** | runs 5 and 6: event thread still in `SendRequestMessageAndWait` 10 s after the target was gone; second attach pending after 30 s |
| `flood-crash-kill` | 10 | 0 | event thread idle at the stop; target gone ~30 ms after the kill |
| `flood-crash-detach` | 10 | 0 | `Detach` returned in ~30 ms; target gone within 1 ms of it |

Also seen, in every crash run (40 of 40): an `Exception(Unhandled)` stop came before the end, and the exception the
debugger read at it was `System.ExecutionEngineException`. FailFast reaches the debugger as that. The dead target's
pipes were absent in every run of every mode, unlike `flood-kill` in finding 100. No anomaly reached DrHook's sink in
any run, including the two that hung.

The raw output is in [`104-crash-boundary/`](104-crash-boundary/): `flood-sync-kill` and `flood-crash` 2026-11-05
17:01–17:08 UTC (`omega-sync-kill-and-crash.txt`); `flood-crash-kill` and `flood-crash-detach` 18:39–18:44 UTC
(`omega-crash-kill-and-detach.txt`). The two batches were not interleaved.

## Results on omega-ubuntu

Ubuntu 26.04.1 arm64 (Parallels VM, 4 vCPU), `ptrace_scope=1`, at `f072f0c` (the same probe as `04aff45`, before a rebase), 2026-11-05 19:22–19:45 UTC. The same
probe, first with Ubuntu's .NET (`/usr/lib/dotnet`, runtime 10.0.14), then with Microsoft's (`~/.dotnet`, runtime
10.0.14); raw output in `104-crash-boundary/omega-ubuntu-all-modes.txt`. The event thread's state is not read on Linux, so an unusable debugger shows only as the second attach
hanging (`*-attach-hung`), not as `orphaned`.

| Mode | Ubuntu's .NET | Microsoft's .NET |
|------|---------------|------------------|
| `flood-sync-kill` | 10 of 10 usable | 10 of 10 usable |
| `flood-crash` | **3 of 10 hung** (runs 2, 3, 8) | **5 of 10 hung** (runs 1, 2, 3, 8, 9) |
| `flood-crash-kill` | 10 of 10 usable | 10 of 10 usable |
| `flood-crash-detach` | 10 of 10 usable | 10 of 10 usable |

As on omega: the `Exception(Unhandled)` stop came before every crash (40 of 40), the exception read at it was
`System.ExecutionEngineException`, the crashed target's pipes were absent every time, and no anomaly reached the
sink. In 4 of the 12 `flood-crash` runs that stayed usable, a `ProcessExited` stop followed the continued exception;
it never did in a run that hung.

The VM's `/tmp` held 160 pipe files afterwards: one pair for each of the 80 second targets, which the probe's
parent kills at the end of each run. They belong to the probe's cleanup, not to any crashed target, and were removed.

**When the hangs came.** Measured from the start of each run (the attach finishes at about 1 s), the 8 runs that
hung on Linux crashed at 2.8 to 4.3 s, and the 12 that stayed usable at 3.7 to 5.2 s. The 7 earliest crashes all
hung. On omega the two hung runs crashed at 1.7 and 2.2 s, among the three earliest of ten. An observation, not
established: an earlier crash, closer to the attach, may be likelier to catch the event thread mid-request.

## Crashes without the managed warning (2026-11-06)

Three more target modes crash at the same moments by other means; probe 100 at `4da7093`, every stop continued, 10
runs per mode on omega (05:15–05:29 UTC) and on omega-ubuntu with each build (05:29–05:46 UTC). The VM shares omega's
CPU, so the two ran one after the other. The resumer now waits up to 2 s after the target is gone for a
`ProcessExited` stop before the stops are printed; `flood-crash`'s construction printed them at once.

| Mode | How the target ends (macOS crash report) | Stop before the end | omega | omega-ubuntu, Ubuntu's | omega-ubuntu, Microsoft's |
|------|------------------------------------------|---------------------|-------|------------------------|---------------------------|
| `flood-stackoverflow` | recursion on one thread; `HandleHardwareException` → `EEPolicy::HandleFatalStackOverflow` → `TerminateProcess` → `PROCAbort` → `abort` | `Exception(Unhandled, System.StackOverflowException)`, 30 of 30 | **5 of 10 hung** (runs 1, 3, 7, 8, 9) | 0 of 10 hung | 0 of 10 hung |
| `flood-segv` | a write through address 16 in libc `memset`: `EXC_BAD_ACCESS`, SIGSEGV in `_platform_memset` | none, 30 of 30 | **3 of 10 hung** (2, 3, 9) | **1 of 10** (4) | **2 of 10** (7, 10) |
| `flood-abort` | libc `abort()`: SIGABRT in `__abort`, no `PROCAbort` | none, 30 of 30 | **3 of 10 hung** (1, 4, 6) | **1 of 10** (10) | 0 of 10 |

On omega every hung run also had the event thread inside `SendRequestMessageAndWait` 10 s after the end. macOS
wrote crash reports for all 10 stack-overflow and 10 segv runs and for 5 of the 10 abort runs (not checked why). The
VM's `/tmp` held 120 pipe files of second targets afterwards, as in the first batch; none belonged to a live process,
and they were removed.

- **A stack overflow is announced.** The draft grouped it with the crashes that skip `EEPolicy::HandleFatalError`;
  it goes through its sibling `HandleFatalStackOverflow`, which gives the debugger the same `Exception(Unhandled)`
  stop as FailFast. The draft's prediction for it did not hold. Ending the session at that stop is not tried here.
- **A native fault and a direct `abort()` are not announced, and they hang the debugger too:** 6 of 20 on omega, 4 of
  40 on omega-ubuntu. These are the crashes ending the session at a stop cannot avoid, because there is no stop.
- **Stack overflow hung on macOS (5 of 10) and never on Linux (0 of 20).** Not explained. (The second batch, below,
  hung it on Linux too, 1 of 20.)
- **When the crashes came** does not separate the outcomes here: hung runs crashed at 1.9 to 5.2 s, usable ones at
  1.9 to 5.0 s. The earlier observation that the earliest crashes hung is not borne out.
- **A `ProcessExited` stop after the end went with a usable debugger in all 74 runs that had one** (omega 19,
  Ubuntu's 28, Microsoft's 27). Without one, 15 of 16 runs hung; the exception is omega-ubuntu Microsoft's `flood-abort`
  run 9, usable with no `ProcessExited` within the 2 s. Inferred, not shown: the event thread is what delivers
  `ProcessExited`, so a thread stuck in a request cannot. As a detector it would be one-sided: the stop shows the
  debugger is fine; its absence within 2 s suggests, and does not show, that it is not. The 2 s bound was chosen,
  not calibrated.

**Correction (2026-11-06, second batch).** The 2 s wait above was in fact about 200 ms. The construction stopped
resuming as soon as it saw the target gone, so a `ProcessExited` stop was caught only if it came within the resumer's
next poll. The counts stand, read as "within about 200 ms".

## Ending at a stack overflow's stop, and how long `ProcessExited` takes (2026-11-06, second batch)

Probe 100 as committed with this update, 10 runs per mode: omega 11:32–12:27 UTC; omega-ubuntu with each build
12:30–13:30 UTC, one after the other; omega-windows below. Two constructions:

- **`flood-stackoverflow-kill`, `flood-stackoverflow-detach`:** as `flood-crash-kill` and `flood-crash-detach`, at the
  `StackOverflowException` stop.
- **`measure-crash`, `measure-stackoverflow`, `measure-segv`, `measure-abort`:** every stop continued, as before.
  After the target is gone the resumer keeps resuming for up to 30 s, and each stop is recorded with its time. If the
  second attach is still pending at 30 s, the run waits 90 s more for it. A watcher recorded each probe child that
  outlived a usable run (45 s): with `sample` on omega, by thread name on omega-ubuntu.

| Mode | omega | omega-ubuntu, Ubuntu's | omega-ubuntu, Microsoft's |
|------|-------|------------------------|---------------------------|
| `flood-stackoverflow-kill` | 10 of 10 usable | 10 of 10 usable | 10 of 10 usable |
| `flood-stackoverflow-detach` | 10 of 10 usable | 10 of 10 usable | 10 of 10 usable |
| `measure-crash` | **5 of 10 hung** | **1 of 10** | **2 of 10** |
| `measure-stackoverflow` | **5 of 10** | **1 of 10** | 0 of 10 |
| `measure-segv` | **2 of 10** | **3 of 10** | **2 of 10** |
| `measure-abort` | **4 of 10** | **1 of 10** | **2 of 10** |

- **Ending the session at a stack overflow's stop was clean:** 60 of 60. Continued, the same crash hung 5 of 10 on
  omega and 1 of 20 on omega-ubuntu. Stack overflow does hang on Linux; the first batch's 0 of 20 was not a platform
  difference.
- **`ProcessExited` separates the outcomes.** In every usable run it came between 17 ms before and 34 ms after the
  probe saw the target gone (omega 24 runs, −1 to 34 ms; omega-ubuntu 68 runs, −17 to 25 ms; before means the probe's
  poll saw the end later than the debugger did). In every hung run (omega 16, omega-ubuntu 12) none came within 30 s.
- **A second attach that misses 30 s never finishes:** all 28 were still pending 120 s after they began.
- **How long the target takes to die does not decide it.** From continuing the unhandled-exception stop to the target
  seen gone: omega 1 to 29 ms, hung and usable alike; omega-ubuntu 1,056 to 1,090 ms, hung and usable alike. The
  guess that a slower death explained macOS's stack-overflow hangs is wrong: Linux's is slower and hung less. Why
  Linux takes a second was not known then; it is apport (zbook, below).
- **Which request.** In the 16 omega samples the waiting threads were in `DbgTransportSession::ReadMemory` in 11,
  `WriteMemory` in 3, and both in 2, all in `SendRequestMessageAndWait`
  ([summary](105-dll-pal-never-rechecks-a-waited-process/omega-hung-samples-summary.txt)). Why the wait never ends is
  [finding 105](105-dll-pal-never-rechecks-a-waited-process.md).

The prediction, that no run both had a `ProcessExited` stop and a hung second attach and no attach finished late,
held on every machine. omega printed RESULT(1) because the probe's check matched `-exited-` inside `not-exited-`;
the check is fixed. Raw output: `104-crash-boundary/omega-stop-and-measure.txt` and
`104-crash-boundary/omega-ubuntu-stop-and-measure.txt`.

## Results on omega-windows (2026-11-06)

Windows 11 arm64 (build 26200, Parallels VM, 4 vCPU), runtime 10.0 (10.0.14 the newest installed), 14:46–15:20 UTC,
10 runs of every mode but the three that need SIGSTOP. Windows has no debugger transport: mscordbi reads the
target's memory directly and its process handle is a kernel object that signals at the target's death. The target is
the probe child's own child, killed with `TerminateProcess`; `flood-segv` and `flood-abort` use ucrtbase's `memset`
and `abort()`. Predicted: a usable second attach every run. Raw output: `104-crash-boundary/omega-windows-all-modes.txt`.

- **Usable every run: 130 of 130**, in all 13 modes, including every crash continued past its stop (`flood-crash`,
  `flood-stackoverflow`, `flood-segv`, `flood-abort` and the four `measure-*`), which hung on macOS and Linux.
- **`ProcessExited` came in all 70 continued-crash runs,** 13 to 172 ms after the probe saw the target gone; slower
  than on macOS and Linux, and always there.
- **A native fault is announced on Windows.** The access violation in `memset` gave an
  `Exception(Unhandled, System.ExecutionEngineException)` stop in 20 of 20 runs; on macOS and Linux it gave none.
  `abort()` gave no stop (20 of 20), as on the other two.
- **Ending at the stop was clean too:** kill and detach at the FailFast and stack-overflow stops, 40 of 40, and a kill
  after a Pause, 10 of 10.

So on this machine no way of ending tried here left the debugger unusable, as finding 105's mechanism predicts for a
platform without a PAL.

## Results on zbook, Linux x64 (2026-11-06)

zbook: bare metal, Intel Core i7-6700HQ (4 cores, 8 threads), Ubuntu 26.04.1, kernel 7.0.0-42, Microsoft's .NET 10.0.14
(SDK 10.0.403), probe 100 at `63a26b2`. The second batch's six modes, 10 runs each, 15:27–15:52 UTC. Raw output:
`104-crash-boundary/zbook-stop-and-measure.txt`. The probe ran as a child of gdb, which sampled every probe child that
outlived 45 s: the stacks are in [finding 105](105-dll-pal-never-rechecks-a-waited-process.md).

| Mode | zbook |
|------|-------|
| `flood-stackoverflow-kill` | 10 of 10 usable |
| `flood-stackoverflow-detach` | 10 of 10 usable |
| `measure-crash` | 0 of 10 hung |
| `measure-stackoverflow` | 0 of 10 |
| `measure-segv` | **1 of 10** |
| `measure-abort` | **3 of 10** |

- **Ending the session at a stack overflow's stop was clean,** 20 of 20, as on the other machines.
- **The continued managed crashes did not hang here** (FailFast 0 of 10, stack overflow 0 of 10); the two crashes
  without the warning did, 4 of 20. With hang rates near those of omega-ubuntu (1 to 3 of 10 per mode), 0 of 10 is
  not evidence of a difference between the modes.
- **`ProcessExited` separates the outcomes,** as before. In the 36 usable `measure-*` runs it came 12 ms before to
  25 ms after the probe saw the target gone; in the 4 hung runs none came within 30 s, and each second attach was
  still pending 120 s after it began.
- **The second the target takes to die is Ubuntu's crash reporter.** From continuing the unhandled-exception stop to
  the target seen gone: 1,150 to 1,172 ms for FailFast and 1,164 to 1,205 ms for stack overflow, about as on
  omega-ubuntu. zbook's `core_pattern` pipes every core dump to apport, and `/var/log/apport.log` has an entry for each
  dying target's pid (signal 6), with "executable does not belong to a package, ignoring" logged 1.004 to 1.008 s
  after "called for" (`105-dll-pal-never-rechecks-a-waited-process/zbook-linux-x64/apport-log-control-run.txt`, the 3-run
  control). The kernel ends the process only after the dump has gone to the pipe. Rerun with `prlimit --core=1`, which
  makes the kernel skip a pipe dump (15:54–16:00 UTC, 5 runs each of `measure-crash`, `measure-stackoverflow`,
  `measure-abort`; `104-crash-boundary/zbook-measure-no-core-pipe.txt`): 8 to 22 ms for FailFast and 11 to 23 ms for
  stack overflow, no apport entry, close to macOS's 1 to 29 ms. omega-ubuntu's second is apport too, checked there by
  sky-omega the same day: its `core_pattern` pipes to apport, and its `apport.log` has an entry for each crashing
  probe target (signal 6), with the executable line about 1.01 s after "called for".
- **The hang does not need that second:** with the dump skipped, `measure-abort` still hung 1 of 5, with the same
  stacks.
- **Which request, named.** In all 5 hung children (4 here, 1 in the rerun) mscordbi's Win32 event thread was waiting
  in `DbgTransportSession::WriteMemory` → `SendRequestMessageAndWait`, called by the DAC's
  `DacDbiInterfaceImpl::RequestSyncAtEvent` while dispatching an event, and the second attach was waiting on the
  DAC's `g_dacMutex` in `FlushCache` (stacks named with Microsoft's symbol files; finding 105).

## What this answers

- **The disappearance, not the signal.** In `flood-crash` DrHook sent no signal; the target ended itself. On omega,
  in 2 of 10 runs the debugger was left exactly as in finding 100: the event thread waiting on the dead target, and
  the next attach hanging. On Linux arm64 the next attach hung in 8 of 20. So the fault follows the target's disappearance while a request is outstanding. Which request it
  was is not recorded: the probe looks for `SendRequestMessageAndWait`, not the frame above it (finding 100 has a read
  and a write, finding 103 an error write).
- **The runtime's warning does not protect a session that continues past it.** The unhandled-exception stop came
  first every time, and the operator continued it. The target then died while the event thread was dispatching.
- **A kill while synchronized was clean** at both points tried, on both platforms: a Pause stop (30 of 30) and the
  unhandled-exception stop (30 of 30).
- **A detach at the unhandled-exception stop was clean** (30 of 30). ICorDebug allowed it, and the runtime then ended
  the process at once. Whether `Detach` returned `S_OK` or a gone-target code is not recorded: neither produces an
  anomaly line.
- **Later sessions are contaminated** when it happens: the second attach in the same process hung.
- **DrHook did not detect it.** Nothing reached the sink in the two hung runs.

## How much it shows

On omega alone, if ending the session at the stop changed nothing, so that 2 in 10 still hung, 20 clean runs would
come up about 1% of the time (0.8²⁰). On omega-ubuntu, where 8 in 20 hung, 40 clean runs would come up about once in
10⁹ (0.6⁴⁰). Ending the session at the stop avoids the hang, on these two machines, for this kind of crash. Each
batch of continued crashes ran apart from the batch that acted at the stop on omega, and in the same batch on
omega-ubuntu.

For stack overflow, on omega, where 5 in 10 hung, 20 clean runs would come up about once in 10⁶ (0.5²⁰). On
omega-ubuntu, where 1 in 20 hung, 40 clean runs would come up about 13% of the time (0.95⁴⁰): weak evidence there on
its own, and consistent with omega's.

## Not covered

- Linux x64 on a second machine (elite is excluded).
- Which request the waiting thread on Linux arm64 was in: omega-ubuntu recorded thread names only. On zbook it was
  `WriteMemory` from `RequestSyncAtEvent`, 5 of 5 ([finding 105](105-dll-pal-never-rechecks-a-waited-process.md)).
- Why the hang rate differs by machine and crash (stack overflow 5 of 10 on omega, 1 of 20 on omega-ubuntu, 0 of 15
  on zbook). The second a Linux target takes to die is apport (zbook and omega-ubuntu), and the hang does not need it.
- A target DrHook launched, rather than attached to.
- What an operator loses by not continuing the unhandled-exception stop. Continuing it is how a debugger shows the
  program's own death; the trade-off is for Step 6.

## For ADR-016

Input to Step 6, not a decision:

- The hang has a managed-crash route DrHook can avoid: at an unhandled-exception stop of a target it is about to lose,
  end the session (detach, or kill if the target is owned) instead of continuing.
- The route it cannot avoid that way is real: a native fault or a direct `abort()` gives no stop and still hung the
  debugger on macOS arm64 and Linux arm64. With finding 105, nothing in the process can recover from it; the worker
  process is decided (Martin, 2026-11-06; ADR-016).
- Avoidance covers a stack overflow too: end the session at its stop (60 of 60).
- Detection, which DrHook does not do today: once the target is gone, a `ProcessExited` stop came within 34 ms or
  not within 30 s. The detector should check the stuck state itself (a request outstanding to a target that is gone)
  rather than a time bound alone, so that a runtime that notices the death later is not taken for a hung one.

Where the probes ran: ADR-016 keeps anything that could freeze a machine off omega and zbook. These modes hang only
the probe's own child process, which the parent ends from outside; Martin approved running them on omega on
2026-11-05.
