# Finding 104 — A target that crashes by itself can leave the debugger unusable; ending the session at its unhandled-exception stop avoided it

**Date:** 2026-11-05
**Status:** **OBSERVED** on omega (macOS 27.0.1 arm64, SDK 10.0.301) and omega-ubuntu (Linux arm64 VM, Ubuntu's and
Microsoft's .NET 10.0.14), 10 runs per mode on each. Not run on Linux x64 or Windows. **Not fixed:**
DrHook continues every stop it is asked to, including the unhandled-exception one. ADR-016 Step 4, probe 100
(`poc/drhook-engine/100-orphaned-read-smoke.cs`, modes added in `5984ad0` and `f072f0c`).

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

Ubuntu 26.04.1 arm64 (Parallels VM, 4 vCPU), `ptrace_scope=1`, at `f072f0c`, 2026-11-05 19:22–19:45 UTC. The same
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

## Not covered

- Crashes the debugger is not told about: a native fault, possibly a stack overflow. Ending the session at the stop
  needs a stop.
- Linux x64 (zbook is on Mercury work; elite is excluded) and Windows (no debugger transport; a different
  construction).
- Which transport request the event thread was in, in the hung runs; on Linux, where it was at all.
- What an operator loses by not continuing the unhandled-exception stop. Continuing it is how a debugger shows the
  program's own death; the trade-off is for Step 6.

## For ADR-016

Input to Step 6, not a decision:

- The hang has a managed-crash route DrHook can avoid: at an unhandled-exception stop of a target it is about to lose,
  end the session (detach, or kill if the target is owned) instead of continuing.
- The route it cannot avoid that way, a crash with no warning, keeps the Step 5 worker in question on macOS and
  Linux.

Where the probes ran: ADR-016 keeps anything that could freeze a machine off omega and zbook. These modes hang only
the probe's own child process, which the parent ends from outside; Martin approved running them on omega on
2026-11-05.
