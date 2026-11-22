# Finding 116 — how long a debugger worker takes to answer a health request (ADR-016 6.8): under 20 ms, against a 5 s bound

**Date:** runs and record 2026-11-21; the runs after the fix 2026-11-22
**Status:** **RECORDED** on omega (macOS arm64), omega-ubuntu (Linux arm64) and omega-windows (Windows 11 arm64).
Probe 116 ran at `e0dfa5cb`, once on each machine, each in a clean clone at that commit (RESULT(0) on each). The
machines ran one at a time, and nothing else of ours ran on omega or its two virtual machines meanwhile. After the fix
of what it found, it ran again at `b71d9cce`: once on each machine, and once more on omega-windows with a watcher of
its targets (RESULT(0) on each). Not run: zbook, macbook, the six runners, x64.

ADR-016 6.8 has drhook-mcp ask a worker running a request how it is every 5 s, and suspect it when the answer has not
come within 5 s (`Supervisor.HealthBound`). The design says: "That bound is checked against the first measured round
trips before it is kept." The worker's stdin thread answers a health request at once (`WorkerLife.AnswerHealth`); this
measures how long that takes, seen from drhook-mcp's side of the pipes.

## What was run

[`116-health-round-trip.cs`](../116-health-round-trip.cs) starts drhook-worker as drhook-mcp does (the dotnet host of
its runtime, `exec worker/drhook-worker.dll`, from the worker drhook-mcp's build carries) and speaks WorkerProtocol to
it directly. It sends a health request every 20 ms and times each from the write to the answer's arrival, 150 or more
per phase:

1. **Idle:** no request running.
2. **During launches:** `drhook_launch` of the snapshot target to its breakpoint, each ended by `drhook_kill`, repeated
   until 150 health requests had been sent during launches. Each launch must answer `status: launched` with
   `stoppedReason: breakpoint`.
3. **Waiting for the target:** after a last launch and `drhook_break_clear`, a `drhook_continue` that waits for a stop
   while the target runs its loop (600 beats of 50 ms). The requests start 500 ms into the wait.

Each answer names the phase the worker was in, and the probe counts them, so a phase is what the worker said it was.
After the third phase the worker is asked to exit, the continue still waiting, and the time to its exit is reported.

- **The hypothesis:** every round trip is under 500 ms, a tenth of the bound, in every phase.
- **Refuted** by a round trip of 500 ms or more, or by a health request left unanswered.

| Machine | OS | .NET (probe and worker) |
|---|---|---|
| omega | macOS 27.0.1, arm64 | 10.0.9 |
| omega-ubuntu | Ubuntu 26.04.1, arm64, a virtual machine on omega; Microsoft's .NET in `~/.dotnet` | 10.0.14 |
| omega-windows | Windows 10.0.26200, arm64, a virtual machine on omega | 10.0.15 |

The logs: [`omega.txt`](116-health-round-trip/omega.txt) (written by the probe alone; the clone's `git status -sb`,
`## HEAD (no branch)` at `e0dfa5c`, was read before the run), [`omega-ubuntu.txt`](116-health-round-trip/omega-ubuntu.txt)
and [`omega-windows.txt`](116-health-round-trip/omega-windows.txt). In the Windows log each en dash reads `ΓÇô`: PowerShell
took the probe's UTF-8 output for code page 437 and wrote it as UTF-8.

## Results

Round trips in ms, median [min–max], and the number of answers:

| Machine | Idle | During launches | Waiting for the target |
|---|---|---|---|
| omega | 0.03 [0.01–2.18], 150 | 0.05 [0.02–0.21], 154 | 0.05 [0.03–0.12], 150 |
| omega-ubuntu | 0.12 [0.06–2.32], 150 | 0.10 [0.05–1.66], 151 | 0.10 [0.06–0.25], 150 |
| omega-windows | 0.15 [0.06–3.32], 150 | 0.09 [0.04–0.54], 150 | 0.13 [0.05–1.19], 150 |

**The hypothesis holds.** Every health request was answered, and the longest round trip, 3.32 ms, was idle on
omega-windows: about 1/1,500 of the 5 s bound. The bound is kept as it is: a worker whose stdin thread runs answers
two to three orders of magnitude inside it (17.16 ms at most in any run, below), so an answer missing at 5 s means that
thread is not running.

Earlier runs of the probe, before it counted its launches and checked their stops (the same measurement otherwise),
agree: three on omega, longest round trips 2.24, 2.28 and 3.25 ms; one on omega-ubuntu, 2.40 ms. At `ffb78e50`, one run
on each machine: longest 2.25, 2.43 and 3.88 ms. Their logs are not in the repository.

**The phases the worker named.** Idle and waiting answered only their own phase, 150 of 150. During launches:

| Machine | Launches | Each, from request to answer, median [min–max] | Debugger work | Waiting for the target | Idle |
|---|---|---|---|---|---|
| omega | 7 | 581 ms [570–591] | 140 | 7 | 7 |
| omega-ubuntu | 56 | 42 ms [18–62] | 35 | 60 | 56 |
| omega-windows | 36 | 96 ms [89–290] | 78 | 37 | 35 |

About one idle answer per launch on each machine. That fits the probe's construction: it sends a health request right
after each launch request, which the stdin thread can answer before the worker has begun the launch. The launch times
are not this probe's question. That a launch to a breakpoint takes 14 times as long on omega as on omega-ubuntu is
recorded, not explained.

## A second observation: the worker's end waits for a continue that waits

The probe asks the worker to exit while the continue still waits. The worker runs its end after the request running
(`WorkerLife` queues `End` behind it), and a waiting `drhook_continue` returns only at a stop, at the target's end, or
after 2 minutes (`EngineSteppingSession.ContinueAsync`). The worker waits for its end up to `EndBound`, 30 s, and then
exits anyway.

| Machine | Exit after the exit request | The session |
|---|---|---|
| omega | 29,683 ms | ended: the continue returned at the target's end |
| omega-ubuntu | 28,480 ms | ended: the continue returned at the target's end |
| omega-windows | 30,122 ms | not ended: the worker wrote "the request running did not let the session end within 30 s; exiting" |

On omega-windows the worker wrote that line in both runs, at `ffb78e50` and at `e0dfa5cb`. The target's loop
is 600 beats of 50 ms, so on omega and omega-ubuntu it ended just inside the bound, and on omega-windows just outside
it. Nine seconds after the second Windows run no `dotnet` process was left there; whether the target ended at its
loop's end or because its worker had gone, the probe does not show.

ADR-016 6.7 says of drhook-mcp's end (a reconnect): "A worker in session ends the session as `drhook_stop` does, then
exits." The build keeps that only when the request running ends within 30 s. A reconnect during a `drhook_continue`
that waits for a target which neither stops nor ends within 30 s leaves the session not ended, on every platform.
`ContinueAsync`'s cancellation token is not observed by its wait, and the worker passes `none`. This is recorded for a
decision; nothing is changed here.

**Decided 2026-11-22 (Martin), and built the same day:** the worker's end cancels the wait of the request it runs, and
the session then ends as `drhook_stop` ends it. Building it showed a second gap at drhook-mcp's end: the pending
continue's health request could not be sent to a worker whose stdin drhook-mcp had just closed, and was read as no
answer, so the worker was discarded and its owned target killed. That is closed too. See ADR-016, Step 7, "The end of
a wait at the worker's end".

## After the fix, at `b71d9cce`

The probe is unchanged; the worker's end now cancels the continue's wait. The logs:
[`b71d9cc-omega.txt`](116-health-round-trip/b71d9cc-omega.txt),
[`b71d9cc-omega-ubuntu.txt`](116-health-round-trip/b71d9cc-omega-ubuntu.txt),
[`b71d9cc-omega-windows.txt`](116-health-round-trip/b71d9cc-omega-windows.txt), and the watched Windows run,
[`b71d9cc-omega-windows-watched.txt`](116-health-round-trip/b71d9cc-omega-windows-watched.txt) with its
[`targets`](116-health-round-trip/b71d9cc-omega-windows-targets.csv).

| Machine | Exit after the exit request | Longest round trip |
|---|---|---|
| omega | 155 ms | 2.53 ms, idle |
| omega-ubuntu | 144 ms | 2.33 ms, idle |
| omega-windows | 265 ms | 5.91 ms, idle |
| omega-windows, watched | 84 ms | 17.16 ms, idle |

No worker wrote the `EndBound` line. The hypothesis holds in all four runs.

**The watched run.** A PowerShell job beside the probe listed the snapshot targets with their creation times, through
CIM, about every half second (each listing took longer than its 250 ms sleep). That load is the likely cause of the
17.16 ms round trip; it is not shown. What the watcher saw:
- 17 of the run's 39 targets (38 launches and the last one). Each of the 16 seen during the launches was seen in one
  listing only, and none was left after: `drhook_kill` ended each.
- The last target, from the launch before the continue, was seen until 63 s after it was created. Windows has no exit
  request yet (ADR-016 6.4), so `drhook_stop`, the worker's end, detaches and leaves an owned target running, and it
  ran on after its worker had exited. Its end came by itself, with nothing of ours acting on it; that it came at its
  loop's end is likely and not shown.

In the first run at `b71d9cce`, one target was listed 2 s after the run and none 22 s later. In the watched run the
last target was created 11 s before the probe's end; taking the same spacing, the first run's lived about 33 s or less.
That is an estimate. The watched run shows which target is left, not why the two lives differ.

## Not established

- zbook, macbook, the runners, and x64.
- A loaded machine. The machines were quiet except in the watched run, where a CIM listing every half second
  coincided with the longest round trip of all, 17.16 ms. The bound's margin is wide, and a machine loaded enough to
  delay the stdin thread by seconds was not tried.
- A worker in native debugger work that the stdin thread waits on. As read in the code, the stdin thread reads the
  engine's marks without a lock (`CallbackPump.IsWaiting` and `WaitingChangedAt` are volatile reads), and writes its
  answer under `WorkerChannel`'s lock, which the request thread and the engine's callback and console-drain threads
  hold only while each writes one line. No construction yet holds a native call, which 6.8's 30 s debugger-work bound
  also waits for.
- What becomes of a target when its worker exits with the session not ended. Since the fix that happens only when
  debugger work, or an evaluation within its own 30 s bound, holds the end past `EndBound`.

## What this establishes

On omega, omega-ubuntu and omega-windows, a worker answered every health request in every run and every phase (idle,
during launches and while waiting for its target): within 6 ms on quiet machines, and within 18 ms with a process
watcher running. The 5 s bound is kept. As first built, the worker's end waited behind a running request for up to
30 s, so a reconnect during a long wait left its session open. Since `b71d9cce` the end cancels the wait, and the
worker exits 84 to 265 ms after its exit request.
