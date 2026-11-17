# Finding 112 — cancellation (ADR-016 Step 3): a program that handles SIGTERM, or a Generic Host, ends cleanly under the debugger; SIGTERM without a handler, while attached, loses the debugger in 6 of 14 sessions

**Date:** runs and record 2026-11-16
**Status:** **RECORDED** on zbook (Linux x64) and macbook (macOS arm64). Both machines show the same result. Windows
is not run: its construction (CTRL+BREAK to a console process in its own group) is to be built and reviewed before
it runs. Not run on omega, omega-ubuntu or the runners.

## What was run

[`112-cancellation-smoke.cs`](../112-cancellation-smoke.cs) runs each construction in a child process of its own:
20 sessions against 20 fresh targets ([`112-cancellation-target.cs`](../112-cancellation-target.cs)), then one more
attach to an idle target, which must succeed. Every session is borrowed (`DebugSession.Attach`). The target floods
thread events, as in probes 100 and 102. The probe sends `kill(pid, SIGTERM)` itself, at a random moment 0 to 500 ms
into the flood, after the debugger has seen 10 CreateThread events.

| Target | How it ends |
|--------|-------------|
| term-handler | its own SIGTERM handler (`PosixSignalRegistration`) cancels the signal and the flood; `Main` returns 0 |
| term-default | no handler: the runtime's default handling of SIGTERM |
| host-term | a Generic Host: SIGTERM reaches its console lifetime, which stops the host and its `BackgroundService` |
| host-self-stop | the same host stops itself (`StopApplication`) 1.5 s into the flood; no signal |

| Order | What the probe does |
|-------|---------------------|
| attached | signals while attached, waits up to 30 s for the debugger's `ProcessExited` stop, then disposes |
| detached | disposes (synchronize, then detach), then signals: `drhook_stop`'s order since finding 110 |

host-self-stop runs attached only, since it has no signal to order. Each construction runs on both read paths
(finding 101):

| Machine | OS, architecture | .NET | Paths |
|---------|------------------|------|-------|
| zbook | Ubuntu 26.04.1, x64, metal, 8 cores | 10.0.14, Microsoft's (`~/.dotnet`) | descendant (reads direct), reparented (transport) |
| macbook | macOS 26.4.1, arm64 (M1 Pro), 8 cores, SIP enabled | 10.0.15, Microsoft | `dotnet` and apphost, both over the transport |

Both ran the probe files committed with this finding, on `86af6a1b`. macbook ran them as experiment commit
`f2753ecb`, which is not on `main`. zbook ran them uncommitted, before that commit, with the same contents. The
machines were otherwise idle. The records are in [`112-cancellation/`](112-cancellation/) (`zbook.json`,
`macbook.json`). A smoke run on zbook (2 sessions per child) is in zbook's `~/diag/p112-smoke-zbook.log` (sha256
`ec52c49d…b99898`).

## A program that ends through its own shutdown code leaves the debugger usable

term-handler, host-term and host-self-stop, attached: **240 of 240 sessions ended cleanly**. That is 120 on each
machine, 180 of them over the transport.

- Each target ran its whole shutdown and exited 0, writing all its markers: `signal cancelled`, or
  `stopping service-stopped host-stopped`.
- The debugger saw the exit every time and saw no other stop. No anomaly was reported, and no debug pipe was left.
- All 12 final attaches succeeded.
- From the signal to the exit:

  | | zbook | macbook |
  |---|---|---|
  | term-handler | 34 to 87 ms | 40 to 161 ms |
  | host-term | 44 to 157 ms | 75 to 269 ms |

- `Dispose` after the exit took 250 to 286 ms. As in finding 102, that is DrHook's settle waits.

So a cancellable target stopped mid-flood behaves like Step 2's natural end.

## After a detach, every target is fine

All three signalled targets, detached first: **240 of 240 sessions usable**, and all 12 final attaches succeeded.

- Each target then ended as it would without a debugger, 20 to 57 ms after the signal.
- Every session reported exactly one `LateCallback`, finding 102's dropped callback, and nothing else apart from
  the two below. That one callback is why none of them counts as clean.
- **The first `GoneTargetAlive`.** It came on zbook, term-handler, descendant, session 5:
  - `Detach` returned `CORDBG_E_PROCESS_TERMINATED` (`0x80131301`) for a live target, and 500 ms later the target was
    still running.
  - `Dispose` took 561 ms, which includes that 500 ms wait.
  - The target then received the signal, handled it, and exited 0.

  This is finding 102's zbook case, a "terminated" target that was not gone. The check decided on 2026-11-04
  reported it instead of accepting it. Until now, that check had never run.
- **term-default left the target's debug pipes** (`clr-debug-pipe-<pid>-*`) in 40 of 40 detached sessions on each
  machine, and in every attached session. No other construction left any. The pipes are left only when the target
  dies by the signal, debugger or not. The runtime source was not read for why.

## SIGTERM without a handler, while attached, loses the debugger

term-default ends the same way under the debugger as without one: exit 143, and its `ProcessExit` handler does not
run. That is .NET 10's default for an unhandled SIGTERM, checked on zbook without a debugger. To the debugger, it is
an abrupt end. Attached, each child stopped at its first loss, since the next attach then blocks. So these are
counts of sessions that ran, not 20 per child:

| Machine, path | Sessions run | Exit seen (signal to exit) | Lost | Then |
|---------------|--------------|----------------------------|------|------|
| zbook, descendant | 1 | 0 | session 1 | session 2's attach blocked |
| zbook, reparented | 2 | 1 (26 ms) | session 2 | session 3's attach blocked |
| zbook smoke, descendant | 2 | 1 (25 ms) | session 2 | the final attach blocked |
| zbook smoke, reparented | 2 | 1 (25 ms) | session 2 | the final attach blocked |
| macbook, `dotnet` | 3 | 2 (35 ms) | session 3 | session 4's attach blocked |
| macbook, apphost | 4 | 3 (35 ms) | session 4 | session 5's attach blocked |

**6 of 14 sessions lost the debugger**, on both machines and on both read paths, direct as well as transport. Every
lost session looked the same:

- **No `ProcessExited` within 30 s.** The target had ended: it was gone, and on zbook's descendant path its exit code
  was 143.
- **No error from `Dispose`.** It returned in 260 to 283 ms. `Detach` reported `CORDBG_E_PROCESS_NOT_SYNCHRONIZED`
  (`0x80131302`), and `Terminate` reported `CORDBG_E_ILLEGAL_SHUTDOWN_ORDER` (`0x80131C15`). This is the pair
  finding 89 recorded.
- **The next attach blocked past its 30 s bound.** The parent then ended the child process, the only recovery the
  API allows. The blocked attach's target ended by itself, as it is built to: after 60 s in the 20-session runs,
  and after 33 s for the smoke's idle target.

**Finding 105's detector held in all 14 sessions.** Its rule is that once the target is gone, `ProcessExited` either
comes within milliseconds or not at all. Here it came 25 to 35 ms after the signal, or not at all. Every session that
saw the exit was followed by an attach that succeeded, and every session that did not was followed by an attach that
blocked.

Where the blocked attach waits is not recorded, since ADR-016 Step 0 rules out an outside sampler on the debugger
process.

- **On Linux,** the watchdog lists the child's threads:
  - five threads under the process's own name in `futex_do_wait`. Unnamed threads take that name, mscordbi's
    among them;
  - one `.NET DebugPipe` thread more than the probe's own runtime has (descendant child), or two more (reparented
    child), waiting to open a pipe (`wait_for_partner`, `openat`).

  That has the shape of finding 103's transport thread reopening a gone target's pipe. Finding 100's cascade, the
  next attach waiting in `FlushCache` behind the event thread, fits too, but it is not shown.
- **On macOS,** the watchdog lists no threads.

This fits [finding 105](105-dll-pal-never-rechecks-a-waited-process.md)'s race, though it does not show it. mscordbi's
PAL never notices a target that dies while one of its threads waits for it, so a death with a request in flight
waits forever. Findings 100, 104 and 105 recorded that for SIGKILL, FailFast, a stack overflow and native faults. A
death between requests is noticed, which would explain why some sessions survive. **New here:** SIGTERM, the polite
request, is the same abrupt end for any program without a handler for it. A program that handles it, itself or
through a Generic Host, chooses its own end. So the debugger's fate depends on the target's code, not on the signal.

## What this means for DrHook

- **`drhook_stop` (owned, detach first) is safe for each target here.** The detached order is its order, and it was
  usable 240 of 240. The probe's sessions were borrowed, so the owned path itself was not run.
- **`drhook_kill` (`DebugSession.Abandon`) ends the target while attached.** For an owned target it sends SIGTERM,
  waits 200 ms, then sends SIGKILL; for a borrowed target it sends SIGKILL at once. This is read from the code
  together with this record; the probe did not call Abandon.
  - On a running target with no handler, the owned SIGTERM is the attached term-default case.
  - Its SIGKILL is finding 104's kill.
  - On a target with a handler or a Generic Host, the SIGTERM alone ends it cleanly.
- **A borrowed target ended by its owner while DrHook is attached** can lose the debugger, when it is a program with
  no SIGTERM handler. Examples: `kill <pid>`, a service manager's stop, a test runner ending its host. ASP.NET Core
  and other Generic Host programs, or a program with its own handler, end cleanly. Which kind a test host is was not
  measured.
- **For Step 6 (not decided):** Abandon's own documentation explains why it does not detach first. A detach needs
  the target to answer a synchronize request, and Abandon is for a target that may not answer. What this record adds:
  for a target that does answer, a detach first, as `drhook_stop` does, would avoid the loss. So would a stop first:
  finding 104 recorded a kill while synchronized clean 30 of 30, at a Pause stop. Whether Abandon should try either,
  bounded, before it ends the target while attached is a teardown change, and the ADR leaves that to Step 6.

## Not established

- Where the blocked attach waits, and what the lost session's event thread is doing.
- Why a signal death loses the debugger in some sessions and not others. The signal's moment in the flood is the
  obvious variable, but 14 sessions cannot show it.
- Windows: the CTRL+BREAK construction.
- The owned path: a launched target, and `drhook_kill` itself.
- A target signalled while stopped. Here every target was running, mid-flood.
- The other machines and the runners.

## Next (proposed, not decided)

1. Build the Windows construction for review: a console target launched in its own process group
   (`CREATE_NEW_PROCESS_GROUP`), and a sender on its console that raises CTRL+BREAK (`GenerateConsoleCtrlEvent`).
2. Run probe 112 on the six runners through `drhook-probe.yml`. This needs the probe on `main` and a workflow change,
   reviewed when proposed.
3. Decide whether a macOS run may `sample` the stuck child once, to see where the attach waits. Step 0 rules out an
   outside sampler.
4. Take the Abandon order into Step 6.
