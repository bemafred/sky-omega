# Finding 113 — what a worker per session costs at its start (ADR-016 6.7): 43 to 141 ms per session at the median, less than the close of a session

**Date:** runs and record 2026-11-21
**Status:** **RECORDED** on omega (macOS arm64), omega-ubuntu (Linux arm64) and omega-windows (Windows 11 arm64).
Probe 113 ran at `0ff69282`: 10 runs on each machine, each run a fresh Debug worker and a fresh Release worker with 5
sessions each, 100 sessions per machine. Every session completed and every worker ended by itself (RESULT(0)). Not run:

- zbook, macbook and the six runners, so Linux x64, macOS x64 and Windows x64 are not covered;
- Ubuntu's own .NET build on omega-ubuntu.

ADR-016 6.7 decided a worker per session, and said its cost, "a start-up per session", "was not measured on its own,
and is measured before Step 7 is accepted". This is that measurement.

## What was run

[`113-worker-startup.cs`](../113-worker-startup.cs) drives probe 106's worker ([`106-worker.cs`](../106-worker.cs)) and
target ([`106-worker-target.cs`](../106-worker-target.cs)), both unchanged. The target runs one round: one Break stop,
then its natural end. Each run starts a fresh worker and runs 5 sessions in it, one after the other. A session is:

1. launch;
2. wait for the Break stop;
3. read locals;
4. run to the natural end (`ProcessExited`);
5. close (`Dispose`).

Each phase is timed by the probe, from the command written to the worker's answer read, so it includes the NDJSON round
trip a supervisor sees. The worker is built twice, Debug (what `drhook-mcp` runs from source) and Release (what the
installed tool runs). Each run starts a Debug worker and then a Release one, so a change in the machine's load falls on
both.

- **ready** is the worker's start: process start to its `ready` line.
- **first** is the first session in a fresh worker. **warm** is sessions 2 to 5 in the same worker, a session in a
  process that has run one before. A long-lived worker pays the warm cost; a worker per session pays the first one.
- **start-up** = ready + first − the median of that worker's warm sessions: what a worker per session adds to each
  session.

Run 1 is reported apart, since only it meets cold file caches. The tables below are runs 2 to 10, 9 workers per
configuration.

| Machine | OS, architecture | .NET (worker) |
|---------|------------------|---------------|
| omega | macOS 27.0.1, arm64 (M5 Max), 18 cores | 10.0.9 |
| omega-ubuntu | Ubuntu 26.04.1, arm64, Parallels VM on omega, 4 cores | 10.0.14, Microsoft's (`~/.dotnet`) |
| omega-windows | Windows 11 (26200), arm64, Parallels VM on omega, 4 cores | 10.0.15 |

The machines ran one after another, omega first, and nothing else ran on the VMs (sky-zbook confirmed it ran nothing on
omega or its VMs). The logs are in [`113-worker-startup/`](113-worker-startup/). On Windows, PowerShell re-encoded the
probe's output, so the en dash in its summary reads `ΓÇô`.

## Results

**What a worker per session adds to a session**, median [min–max] ms:

| Machine | Debug | Release | of which, the worker's ready (Debug) | A warm session (Debug) |
|---------|-------|---------|------------------------------|-----------------------|
| omega | 95 [83–600] | 91 [79–594] | 73 [67–74] | 882 [862–894] |
| omega-ubuntu | 50 [39–60] | 43 [34–50] | 37 [34–38] | 325 [313–342] |
| omega-windows | 128 [84–231] | 141 [32–348] | 57 [31–59] | 515 [384–717] |

Run 1, with cold caches, added 138 (Debug) and 291 ms (Release) on omega, 16 and 46 on omega-ubuntu, 166 and 129 on
omega-windows.

**Debug and Release cost the same at the start.** On omega and omega-ubuntu the two configurations' phases are within
a few ms of each other at the median. On omega-windows they differ only where its launches vary (below): a warm launch
took 132 ms at the median in Debug and 66 ms in Release. Both configurations compile the engine's code at first use, so
this does not say what compiling it costs.

**Where the first session's excess falls**, median ms, Debug (Release is alike, except that a warm launch on omega-windows
took 66 ms):

| Phase | omega first / warm | omega-ubuntu first / warm | omega-windows first / warm |
|-------|--------------------|---------------------------|----------------------------|
| launch | 560 / 550 | 24 / 15 | 176 / 132 |
| Break stop | 29 / 32 | 14 / 23 | 21 / 24 |
| locals | 9 / 3 | 5 / 1 | 7 / 1 |
| natural end | 92 / 92 | 81 / 81 | 150 / 125 |
| close | 214 / 208 | 214 / 205 | 241 / 227 |

The excess is mostly on the launch, with a few ms on the first locals. The first Break stop is no slower than a warm
one, by more than 3 ms, on any machine (omega-windows Release: 22 against 19). So the hypothesis holds for the launch
and fails for the first stop.

**The warm sessions are steady on omega and omega-ubuntu, and not on omega-windows.** There a warm launch is either
about 60 ms (36 of 72) or 119 to 257 ms (36 of 72), with no pattern by session number. A first launch is 139 to 287 ms.
The natural end varies from 81 to 273 ms. On Windows the start-up is therefore known only to within about the spread
of a warm session.

**On macOS a first launch sometimes costs about 500 ms more.** 4 of omega's 20 first launches took longer than any
warm one: 607 and 809 ms in run 1, with cold caches, then 1,059 ms (Release, run 2) and 1,068 ms (Debug, run 5). The
other 16 took 557 to 564 ms, and all 80 warm launches 524 to 557 ms. The two of about 1,060 ms are the 594 to 600 ms
upper end of omega's start-up.

## Two costs every session pays, with or without a worker

Neither is part of the worker's start-up. The close is larger than it on every machine, and on macOS the launch is too.

- **A launch on macOS takes about 550 ms**, against 11 to 26 ms on omega-ubuntu and 29 to 287 ms on omega-windows.
  Where in `DebugSession.Launch` the 550 ms goes was not measured.
- **Closing a session whose target has ended takes about 200 ms on every machine.** `Dispose` sleeps
  `ExitWorkSettleMs` (200 ms, `DebugSession.cs`) before it detaches from a dead target, so that mscordbi's event thread
  finishes the exit work first (finding 66, probe 47).

## Not established

- Linux x64, macOS x64 and Windows x64; Ubuntu's own .NET build.
- A worker that runs from its own copy of the build, as 6.7 decides: the copy itself is not timed.
- A worker with `drhook-mcp` as its supervisor and the debug-state publisher (ADR-012) in or beside it. The supervisor
  here is a probe.
- Why a first launch on macOS sometimes takes about 500 ms more, and why omega's first two workers were ready in 35 to
  37 ms and every later one in 58 to 75 ms (omega-ubuntu: 20 and 32 ms, then 28 to 38).
- Why a Windows launch takes either about 60 or about 140 ms.

## What this establishes

On these three machines a worker per session adds 43 to 141 ms to a session at the median, and at most 600 ms. The
close of a session whose target has ended costs more than that alone, about 200 ms. On omega and omega-ubuntu most of
what a worker adds is its own process start (73 and 37 ms); on omega-windows about half (57 of 128 ms, Debug). A first
session in a fresh worker is slower than a later one by its launch (about 10 ms at the median on omega and
omega-ubuntu; 44 ms in Debug and 112 ms in Release on omega-windows, where a warm launch varies by about as much) and by
a few ms on its first locals. 6.7's cost is measured here, on these three machines.
