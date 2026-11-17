# Finding 106 — the debugger in a worker process (ADR-016 Step 5): the supervisor recovered from every lost debugger; a target held at a stop when its worker dies stays held on Unix and dies on Windows

**Date:** runs and record 2026-11-17
**Status:** **RECORDED** on zbook (Linux x64), macbook (macOS arm64) and omega-windows (Windows 11 arm64). Probe 106
ran at `81859423`, 20 runs per mode on each machine. Every machine gave RESULT(0): each mode's prediction held.

The machines are those Martin approved for Step 5 on 2026-11-17 (ADR-016 Step 0), plus zbook, where Step 4's probes
also ran. Not run yet:

- omega-ubuntu, which zbook cannot reach yet;
- the six runners, which need a `drhook-probe.yml` option;
- so macOS x64, Windows x64 and Linux arm64 are not covered.

## What was run

[`106-supervisor.cs`](../106-supervisor.cs) never loads DrHook.Engine. It drives [`106-worker.cs`](../106-worker.cs),
which hosts the engine for one session per process, over NDJSON on stdin and stdout. Before the supervisor discards a
worker, it writes its intent and the target's last known state to a journal, then ends the worker through the
operating system. The probe's header has each mode's hypothesis and construction. In short:

| Mode | Construction |
|------|--------------|
| induce-and-recover | Each attempt is a worker that launches probe 100's crash target (`flood-segv`: it floods thread events and faults in native code 0.5 to 3 s after the attach) and continues every stop. There are up to 8 attempts. The first one in which the worker reports its debugger lost is discarded, and a fresh worker runs a session: launch, a Break stop, locals, a natural end with `ProcessExited`. |
| supervisor-detects | The same, except that the supervisor ignores the worker's verdict. After each crash it asks the same worker to attach to a fresh target, with a 30 s bound. No answer within the bound means the worker is suspected, discarded, and replaced by a fresh session. The decision is then compared with the worker's own facts. |
| held-launched, held-attached | A worker launches the target ([`106-worker-target.cs`](../106-worker-target.cs)), or attaches to one the supervisor started. It reads locals at a Break stop, and is then discarded while healthy. The supervisor watches the target's beat file for 10 s, attaches a fresh worker if the target is alive, and gives it 60 s to finish. |

| Machine | OS, architecture | .NET |
|---------|------------------|------|
| zbook | Ubuntu 26.04.1, x64, metal, 8 cores | 10.0.14, Microsoft's |
| macbook | macOS 26.4.1, arm64 (M1 Pro), 8 cores, SIP enabled | 10.0.15, Microsoft |
| omega-windows | Windows 11 (26200), arm64, Parallels VM on omega | 10.0.15, Microsoft |

macbook and zbook ran at the same time. omega-windows ran alongside both, and omega itself was building and testing
Mercury at times (sky-omega). Nothing in the probe measures speed beyond its bounds. The logs and zbook's journal are
in [`106-worker-process/`](106-worker-process/).

## Results

| Mode | zbook | macbook | omega-windows |
|------|-------|---------|---------------|
| induce-and-recover | lost and recovered, 20 of 20 | lost and recovered, 20 of 20 | never lost, 20 of 20; fresh session ok |
| supervisor-detects | suspected and recovered, 20 of 20 | suspected and recovered, 16 of 20; never lost in 4 | never lost, 20 of 20; fresh session ok |
| held-launched | **held**, reattach failed, 20 of 20 | **held**, reattach failed, 20 of 20 | **died**, 20 of 20 |
| held-attached | **held**, reattach failed, 20 of 20 | **held**, reattach failed, 20 of 20 | **died**, 20 of 20 |

## Recovery from a lost debugger

**On Linux and macOS, a crash during a request lost the worker's debugger often.** A run stops at its first loss,
so each count below includes the attempt that was lost:

| Machine | Lost, induce-and-recover | Lost, supervisor-detects |
|---------|--------------------------|--------------------------|
| zbook | 20 of 44 attempts | 20 of 54 |
| macbook | 20 of 64 attempts | 16 of 91 |
| omega-windows | 0 of 160 attempts | 0 of 160 |

That fits findings 104 and 105: Windows has no PAL, and never hung.

**The worker's own detection is fast and has a wide margin.** The worker calls its debugger lost when the target is
gone and no `ProcessExited` comes within 2 s.

- It reported the loss 2.0 s after it saw the target gone, every time (2,003 to 2,031 ms).
- When the debugger was not lost, `ProcessExited` came 0 to 26 ms after the target was gone on zbook, 0 to 35 ms on
  macbook, and 0 to 138 ms on omega-windows.

So finding 105's detector held in every attempt on all three machines.

**Recovery took milliseconds to discard, and seconds to a working session.**

| Machine | Ending the worker (OS kill) | Fresh session complete, after the discard |
|---------|-----------------------------|-------------------------------------------|
| zbook | 3 to 7 ms | 2.4 to 2.5 s |
| macbook | 13 to 15 ms | 3.2 to 3.7 s |

The fresh session (a new worker, a launch, a Break stop, locals, and a natural end with `ProcessExited`) succeeded in
all 76 recoveries. On Windows, 40 of 40 fresh sessions succeeded with no loss before them.

**The supervisor detected a lost debugger from outside, without the worker's verdict.**

- Every suspicion agreed with the worker's own facts (no `ProcessExited`): 20 of 20 on zbook and 16 of 16 on
  macbook.
- No worker was suspected that had not lost its debugger.
- Detecting it from outside costs the bound, 30 s here, against the worker's 2 s. The fresh session then completed
  1.4 s (zbook) and 2.2 to 2.7 s (macbook) after the discard.

## A target held at a stop when its worker dies

Step 5's open question, recorded per platform. Launched and attached targets behaved the same.

**Linux and macOS: the target stays held, and no debugger can take it over.** This held in 80 of 80 runs, 40 on each
machine:

- The target was alive, and its beat did not move for 10 s.
- A fresh worker's attach failed after about 10 s with `CORDBG_E_TIMEOUT` (`0x80131C08`).
- The target was still held 60 s later, so the probe ended it, and recorded that in the journal.

This is what the omega smoke at `a7583ee4` showed on 2026-11-07. Such a target can be ended only by the operating
system.

**Windows: the target dies with its worker.** This held in 40 of 40 runs:

- The target was gone 0 ms after the discard.
- Its beat stayed at the round it was held at, so it never ran on. The probe's line "the target ended by itself"
  says only that it was gone.

Windows documents that a process debugged through the Win32 debugging API is ended when its debugger exits, unless
the debugger has called `DebugSetProcessKillOnExit(FALSE)`. mscordbi debugs through that API on Windows. Whether it
makes that call was not read.

So on these platforms, no worker loss preserves a target that is held at a stop. On Unix the target is left
suspended and cannot be debugged again; on Windows it is killed. That bears on ADR-016's Target preservation gate.

## What the supervisor reported

Before every discard, the journal recorded:

- the intent, the worker and the reason;
- the target's last known state;
- who observed that state, and when;
- how certain it was.

zbook's journal has 200 lines. Each of its 80 discards was journaled before the kill, and confirmed after it
(`discarded`). The 40 held targets the probe ended are recorded as `kill-target`. The target states it recorded:

| Mode | State | Certainty |
|------|-------|-----------|
| induce-and-recover | `crashed: gone` | `observed (waitid on the worker's own child)` |
| held-* | `held at a Break stop`, with its round | `observed` |
| supervisor-detects | `crashed: gone (the worker's report)` | `reported by a worker later suspected` |

Something observed the target in every case, so no entry needed `unknown`.

## What this establishes

- **The design works on the three platforms run.**
  - The supervisor detects a lost debugger, either from the worker's 2 s check or from outside by a bound.
  - It discards the worker in milliseconds, and a fresh worker's session succeeds.
  - It recovered 76 of 76 losses on Unix. On Windows the debugger was never lost.
- **Losing a worker costs a target held at a stop**, on every platform here, in opposite ways.

## Not established

- omega-ubuntu (Linux arm64, both .NET builds) and the runners.
- A target that is running, not stopped, when its worker dies. Probe 106 holds every target at a stop first.
- Why a held Unix target cannot be attached again. The runtime may still consider a debugger attached; not read.
- Whether mscordbi on Windows leaves kill-on-exit at its default.
- DrHook's real shape, with `drhook-mcp` as the supervisor. The supervisor here is a probe.

## Next (proposed, not decided)

1. Run on omega-ubuntu once zbook can reach it.
2. Add probe 106 to `drhook-probe.yml` for the six runners. That is a workflow change, reviewed when proposed.
3. Run a target that is running when its worker dies (a held-mode variant that continues the target before the
   discard).
4. Take the rest to Step 6: the worker's lifetime and protocol, and what DrHook does with a target at a stop when its
   worker must go. That last choice is the Target preservation gate.
