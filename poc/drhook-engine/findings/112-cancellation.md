# Finding 112 — cancellation (ADR-016 Step 3): a program that handles SIGTERM, or a Generic Host, ends cleanly under the debugger; SIGTERM without a handler, while attached, loses the debugger in 6 of 14 sessions; on Windows, DrHook takes a CTRL+BREAK the target does not handle, and the target runs on

**Date:** runs and record 2026-11-16; Windows 2026-11-17; Windows with `S_FALSE`, and the six runners, 2026-11-23
**Status:** **RECORDED** on zbook (Linux x64) and macbook (macOS arm64), which show the same result, and on
omega-windows (Windows 11 arm64), with the Windows construction: CTRL+BREAK to a console process in its own group
(section [Windows](#windows-ctrlbreak-to-the-targets-own-process-group)), and on the six runners (section
[The operator is told, and the six runners](#the-operator-is-told-and-the-six-runners-2026-11-23)). Not run on omega
or omega-ubuntu. Since `4e72de5` DrHook answers `ControlCTrap` with `S_FALSE` (ADR-016 6.4). On omega-windows and
both Windows runners, a target without a handler, attached, now ends as it does without a debugger: 80 of 80 usable
(section [Windows, with DrHook answering `S_FALSE`](#windows-with-drhook-answering-s_false-2026-11-23)). The title's
last clause is the `S_OK` record.

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

## Windows: CTRL+BREAK to the target's own process group

Windows has no SIGTERM. A console program is asked to end with a console control event. Of those, only CTRL+BREAK
can be sent to a single process group: CTRL+C reaches every process on the console. So on Windows:

- the probe starts the target in a process group of its own (`CreateNewProcessGroup`);
- the probe's child, which shares the target's console, sends `GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, pid)`;
- .NET maps CTRL+BREAK to `PosixSignal.SIGQUIT`, which a handler can cancel on Windows.

| Target | How it ends |
|--------|-------------|
| break-handler | its own SIGQUIT handler (`PosixSignalRegistration`) cancels the event and the flood; `Main` returns 0 |
| break-default | no handler: Windows' default for CTRL+BREAK |
| host-break | a Generic Host: CTRL+BREAK reaches its console lifetime, which stops the host and its `BackgroundService` |
| host-self-stop | as on Linux and macOS; no signal |

The orders are those above. Windows has a single read path, with no transport, so each construction ran once, on the
`dotnet` host.

| Machine | OS, architecture | .NET |
|---------|------------------|------|
| omega-windows | Windows 11 (26200), arm64, Parallels VM on omega, 4 cores | 10.0.15, Microsoft |

The probe ran at `b08b8604`, the commit that added the Windows construction, carried to the VM as a bundle. There
were three runs:

- a smoke run, 2 sessions per child;
- 20 sessions per child;
- break-handler detached alone, 100 sessions.

Meanwhile, omega-ubuntu was running probe 106, and Mercury's tests ran on omega at times. The child shared the
target's console every time (5 processes on it), so it never had to allocate one, and every `GenerateConsoleCtrlEvent`
succeeded. The records are in [`112-cancellation/`](112-cancellation/): `omega-windows.json` (20 sessions) and
`omega-windows-break-handler-detached.json` (100). The smoke run is in zbook's `~/diag/p112-omega-windows-smoke.json`
(sha256 `2c955a91…719661`).

| Target, order | Usable | Clean | How the target ended |
|---------------|--------|-------|----------------------|
| break-handler, attached | 20 of 20 | 20 | exit 0, 38 to 282 ms after the signal, `signal cancelled` |
| host-break, attached | 20 of 20 | 20 | exit 0, 52 to 341 ms after the signal, `stopping service-stopped host-stopped` |
| host-self-stop, attached | 20 of 20 | 20 | exit 0, `stopping service-stopped host-stopped` |
| break-default, attached | 20 of 20 | 0 | not by the signal: at its 60 s cap, exit 0, `cap process-exit` |
| break-handler, detached | 19 of 20; 99 of 100 | 0 | exit 0, `signal cancelled`; once in each run, held (below) |
| break-default, detached | 20 of 20 | 0 | exit `0xC000013A` (`STATUS_CONTROL_C_EXIT`), no marker |
| host-break, detached | 20 of 20 | 0 | exit 0, `stopping service-stopped host-stopped` |

- No session lost the debugger.
- Every final attach succeeded, also in the two runs with a held target, whose outcome the probe counts as not
  usable because of that target.
- As on Linux and macOS, every detached session reported exactly one `LateCallback`, finding 102's dropped callback,
  so none counts as clean.
- `Dispose` after an attached exit took 262 to 529 ms: DrHook's settle waits.

**Its own shutdown code: clean, as on Linux and macOS.** A handler or a Generic Host, signalled while attached, gave
40 of 40 clean sessions; host-self-stop gave 20 of 20.

**No handler, attached: DrHook takes the CTRL+BREAK, and the target runs on.** In all 20 sessions:

- the debugger received one `ControlCTrap` callback, and no exit;
- the target went on flooding until its cap, 60 s after it started, and then returned normally, its `ProcessExit`
  handler running;
- the probe waited its 30 s for an exit, then disposed: an ordinary detach.

Read in dotnet/runtime `release/10.0`:

- At startup the runtime adds a console control handler of its own, `DbgCtrlCHandler` (`vm/ceemain.cpp`, Windows
  only).
- While a managed debugger is attached, that handler passes CTRL+C and CTRL+BREAK to it
  (`Debugger::SendCtrlCToDebugger`, `debug/ee/debugger.cpp`). It stops the runtime's threads and waits for the
  debugger's answer. If the answer was `S_OK`, it returns that the event is handled.
- mscordbi delivers the event as `ControlCTrap` and sends the callback's own return value back as the answer
  (`ShimProxyCallback::ControlCTrap` in `debug/di/shimcallback.cpp`, and `CordbProcess::HandleControlCTrapResult`).
- DrHook's `ControlCTrap` returns `S_OK`, as every DrHook callback does (`ManagedCallbackHost.Fire`).

So under DrHook the event counts as handled, and Windows never reaches its default handler, the one that ends the
process.

A target with a handler of its own never gets that far. Windows calls the most recently added handler first, the
target's own was added after the runtime's, and it handles the event. That fits the `ControlCTrap` count: 20 in
break-default attached, and none in any other construction.

So where Linux and macOS lose the debugger in 6 of 14 sessions, Windows does not end the target at all.

**No handler, detached: an abrupt end, as on Linux and macOS.** The exit code is `0xC000013A`, `ProcessExit` did not
run, and the target was gone within 139 ms of the signal.

**After a detach, twice in 120 sessions, the target stayed held: finding 102's Windows case.**

- **Which sessions.** break-handler detached: session 11 of 20 (pid 48740), and session 32 of 100 (pid 16744).
- **Up to the end, both looked like every other session.** `Dispose` took 96 and 77 ms, the signal followed 292 and
  140 ms later, and each had its one late callback, a CreateThread.
- **Then the target hung.** It ran its handler (`signal` was written), but it never wrote `cancelled` and did not end.
  75 s after it started, the parent ended it.

The target's main thread waits for the flood thread without a bound, while the flood loop itself stops at the cap.
So either the flood thread was stuck on a thread start or join, or the process was suspended: the two possibilities
finding 102 named. The handler's thread ran managed code after the detach, so the runtime was not held in a debugger
stop as a whole.

Rates:

| Where | Held |
|-------|------|
| omega-windows, break-handler detached | 2 of 120, plus 0 of 2 in the smoke run |
| omega-windows, break-default and host-break detached | 0 of 40, plus 0 of 4 in the smoke run |
| omega-windows, finding 102 | 2 of 80 |
| the two Windows runners, finding 102 | 0 of 160 |
| Linux and macOS, here | 0 of 240 |

No stacks were taken. Finding 102's first next step, recording a held target's state, is still open.

## Windows, with DrHook answering `S_FALSE` (2026-11-23)

ADR-016 6.4 decided that DrHook answers `ControlCTrap` with `S_FALSE`, to be measured first with this probe's
no-handler target, attached. `4e72de5` makes that change and nothing else. The probe ran at that commit on
omega-windows, carried as a bundle into a clone of its own, on .NET 10.0.15, the same machine and runtime as the
`S_OK` runs above. There were two runs:

- break-default attached alone, 20 sessions
  ([`omega-windows-sfalse-break-default-attached.json`](112-cancellation/omega-windows-sfalse-break-default-attached.json));
- every Windows construction, 20 sessions each
  ([`omega-windows-sfalse.json`](112-cancellation/omega-windows-sfalse.json)).

The answer reaches the target. This was read in dotnet/runtime `release/10.0` before the change:

1. mscordbi's shim queues the event, and calls DrHook's callback when it dispatches it.
2. It sends the callback's return value back to the target (`ControlCTrapEvent::Dispatch` in `ShimProxyCallback::ControlCTrap`, `debug/di/shimcallback.cpp`, then
   `CordbProcess::HandleControlCTrapResult`, `debug/di/process.cpp`).
3. The target's runtime counts the event as handled only for `S_OK` (`debug/ee/debugger.cpp`,
   `DB_IPCE_CONTROL_C_EVENT_RESULT`: `SetDebuggerHandlingCtrlC(pEvent->hr == S_OK)`).
4. `DbgCtrlCHandler` returns that, and for `FALSE` Windows goes on to the next handler.

| Target, order | `S_OK` (`b08b8604`, above) | `S_FALSE` (`4e72de5`) |
|---------------|----------------------------|-----------------------|
| break-default, attached | usable 20, clean 0: ran on to its 60 s cap, exit 0, `cap process-exit` | usable 40 of 40, clean 40: exit `0xC000013A`, no marker, 25 to 211 ms after the signal |
| break-handler, attached | usable 20, clean 20 | usable 20, clean 20 |
| host-break, attached | usable 20, clean 20 | usable 20, clean 20 |
| host-self-stop, attached | usable 20, clean 20 | usable 20, clean 20 |
| break-default, detached | usable 20, `0xC000013A` | usable 20, `0xC000013A` |
| break-handler, detached | usable 19 of 20 (one held) | usable 19 of 20 (one held) |
| host-break, detached | usable 20 | usable 20 |

- **break-default attached now ends as it does without a debugger.**
  - Each of the 40 sessions had one `ControlCTrap`, and then the debugger saw the exit.
  - The exit code was `0xC000013A`, and `ProcessExit` did not run. That is break-default detached's end.
  - There was no other stop, no anomaly and no debug pipe left.
  - `Dispose` took 267 to 526 ms, DrHook's settle waits as before. Every final attach succeeded.
- **Nothing else changed.** No other construction had a `ControlCTrap`. The attached ones with a handler or a host
  stayed 20 of 20 clean. The detached ones had their one `LateCallback` each, as before.
- **The one session not usable is the held target above.** It was break-handler detached, session 20 (pid 9824).
  - It looked like the earlier two: `Dispose` 130 ms, one late CreateThread, `signal` written but not `cancelled`.
  - 75 s after it started, the parent ended it.
  - No debugger was attached when the event came, so DrHook's answer did not reach that target.
  - Held targets, break-handler detached on omega-windows: 3 of 140 in this finding, counting the 2 of 120 above.

## The operator is told, and the six runners (2026-11-23)

6.4's other half is built in `0a6d01a`. When the pump dispatches `ControlCTrap`, it records a `ConsoleControlEvent`
anomaly, and drhook_drain_anomalies and the views show it. `ba0d865` adds probe 112 to `drhook-probe.yml`. Both are on
the branch `drhook-6.4-sfalse` with `4e72de5`, and both runs below are at `ba0d865`.

**omega-windows, break-default attached, 20 sessions**
([`omega-windows-report-break-default-attached.json`](112-cancellation/omega-windows-report-break-default-attached.json)):

- Usable 20 of 20.
- Each session carried exactly one anomaly: `ConsoleControlEvent in ControlCTrap: CTRL+C or CTRL+BREAK reached the
  target while it was debugged; DrHook passed it on to the target`. It was each session's only deviation, so the probe
  counts none as clean.
- Exit `0xC000013A`, 25 to 109 ms after the event; every exit seen; `Dispose` 277 to 372 ms; the final attach
  succeeded.

**The six runners**, run 45252424492, .NET 10.0.15, 20 sessions per construction (`runner-<platform>.json`).

| Windows runner | break-default attached | Every other construction |
|----------------|------------------------|--------------------------|
| x64 (Windows 10.0.26100, 4 cores) | usable 20 of 20; one `ControlCTrap` and one `ConsoleControlEvent` each; exit `0xC000013A`; final attach ok | usable, but for one held target (below) |
| arm64 (Windows 10.0.26200, 4 cores) | the same, 20 of 20 | all usable |

- **6.4's measurement is in.** omega-windows and both Windows runners ran the no-handler target, attached, with
  `S_FALSE`: 80 of 80 usable, and every final attach succeeded.
- **A held target after a detach, for the first time on a runner.**
  - Windows x64, host-break detached, session 4 (pid 2804).
  - `Dispose` took 52 ms. The host wrote `stopping`, but its service and host never stopped.
  - It was still alive 75 s after it started, and the parent ended it.
  - Runners, Windows, detached: 1 of 120 here, against 0 of 160 in finding 102. The case is that of omega-windows above:
    no debugger is attached when the event comes, so DrHook's answer does not reach it.
- **Linux and macOS: SIGTERM without a handler, while attached, loses the debugger on every runner.** Everything else was usable, 20
  of 20 per construction and read path.

  | Runner | term-default attached: sessions usable before the loss, per read path |
  |--------|----------------------------------------------------------------------|
  | Linux arm64 | descendant 20 (no loss); reparented 1 |
  | Linux x64 | descendant 6; reparented 0 |
  | macOS arm64 | dotnet 2; apphost 2 |
  | macOS x64 | dotnet 13, then the child ended with nothing on stderr; apphost 5 |

  - Seven of eight such children lost the debugger.
  - In six, the engine refused the next call with `DebuggerLostException`:
    - five for a target the OS shows as gone, with no ExitProcess from mscordbi within 2 s (finding 105);
    - one for `DebuggerError` `0x80004005` (macOS arm64, dotnet).
  - The probe does not catch that exception, so the child ends at the first loss, and the sessions it had left do
    not run. On zbook and macbook the child stopped at its first loss too, there because its next attach blocked. So
    the counts above are sessions before a loss, not a rate.
  - Why one macOS x64 child ended without a word is not established.

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
- **On Windows, a CTRL+BREAK to a target with no handler does nothing while DrHook is attached.** DrHook answers
  the runtime's `ControlCTrap` with `S_OK`, so the event counts as handled. The debugger stays usable, but the
  program does not end, whether a person pressed Ctrl+Break in its console or an owner sent the event. It ends
  only once DrHook has detached. With a handler or a Generic Host, the target ends cleanly, attached or not.
  - Answering `S_FALSE` would pass the event on, so the target would end abruptly, as it does detached.
  - Which answer DrHook should give is a choice for Step 6.
  - *Decided in Step 6 (ADR-016 6.4): `S_FALSE`. Measured 2026-11-23 on omega-windows, 40 of 40 clean (section
    [Windows, with DrHook answering `S_FALSE`](#windows-with-drhook-answering-s_false-2026-11-23)).*
- **On Windows, `drhook_stop` kills an owned target, since it has no exit request yet** (DRHOOK.md, ADR-016 Step 3).
  This record shows what CTRL+BREAK after a detach would do. A program that handles it ends cleanly. One that does
  not ends abruptly, as SIGTERM's default does on Linux and macOS. Using it would need the target launched in its
  own process group and a sender that shares its console. Whether `drhook_stop` should do this is not decided.
- **A borrowed detach on Windows can leave the target held**: 2 of 120 here, and 2 of 80 in finding 102, all on
  omega-windows. DrHook itself was unaffected each time. This bears on ADR-016's Target preservation gate.

## Not established

- Where the blocked attach waits, and what the lost session's event thread is doing.
- Why a signal death loses the debugger in some sessions and not others. The signal's moment in the flood is the
  obvious variable, but 14 sessions cannot show it.
- Windows: what holds a target after a detach. No stacks, suspend counts or dump were taken. Whether the dropped
  callback is the cause: in both held sessions it was a CreateThread, but so it was in 83 of the 118 that ended.
- Windows: CTRL+C, which cannot be sent to one group.
- The owned path: a launched target, and `drhook_kill` itself.
- A target signalled while stopped. Here every target was running, mid-flood.
- The other machines: omega and omega-ubuntu.

## Next (proposed, not decided)

1. ~~Build the Windows construction for review: a console target launched in its own process group
   (`CREATE_NEW_PROCESS_GROUP`), and a sender on its console that raises CTRL+BREAK (`GenerateConsoleCtrlEvent`).~~
   Built and run on omega-windows, 2026-11-17 (section Windows).
2. ~~Run probe 112 on the six runners through `drhook-probe.yml`. This needs the probe on `main` and a workflow change,
   reviewed when proposed.~~ Run 2026-11-23 from the branch `drhook-6.4-sfalse`, run 45252424492 (section The operator
   is told, and the six runners).
3. Decide whether a macOS run may `sample` the stuck child once, to see where the attach waits. Step 0 rules out an
   outside sampler.
4. Take the Abandon order into Step 6, and with it, on Windows, DrHook's answer to `ControlCTrap` and an exit
   request for `drhook_stop`. *Decided 2026-11-20 (ADR-016 6.2 to 6.4). The exit request was built 2026-11-22; the
   answer, `S_FALSE`, on 2026-11-23, measured on omega-windows (section Windows, with DrHook answering `S_FALSE`).*
5. On Windows, record a held target's state before the parent ends it: its threads' stacks and suspend counts, or a
   dump. That is finding 102's first next step. The runtime's `createdump.exe` is on omega-windows, but nothing that
   reads a Windows dump is. Step 0's rule is about the debugger process, not the target.
6. Record a lost debugger as the session's outcome in the probe, instead of letting the child end. On the runners the
   engine refused the session's next call with `DebuggerLostException`, which the probe does not catch; on zbook and
   macbook the child's next attach blocked. Either way a child stops at its first loss, so term-default attached gives
   sessions before a loss, not a rate.
