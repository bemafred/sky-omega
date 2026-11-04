# Finding 102 — the natural end (ADR-016 Step 2): a target that ends by itself leaves DrHook usable on every machine; a detach drops one callback, and once, on Windows, the target stayed held

**Date:** runs 2026-11-01; written 2026-11-03
**Status:** **RECORDED** on our machines (omega, omega-ubuntu with both .NET builds, zbook, omega-windows) and on the
six GitHub runners (workflow run 41314425334), probe 102 at `6bb7ef8` everywhere. Every run RESULT(1): the two
constructions that end naturally under the debugger had no deviation anywhere; the two that detach first had a
deviation in almost every session (below). elite is excluded (ADR-016 Step 0; it is offline, suspected RAM).

## What was run

[`102-natural-end-smoke.cs`](../102-natural-end-smoke.cs) runs each construction in a child process of its own:
20 sessions against 20 fresh targets ([`102-natural-end-target.cs`](../102-natural-end-target.cs)), then one more
attach to an idle target, which must succeed. Every session is borrowed (`DebugSession.Attach`): DrHook never ends
the target, and nothing is stopped, evaluated, signalled or killed. The target floods thread events (probe 100's
flood: one empty thread started and joined after another), so the debugger is always dispatching CreateThread and
ExitThread and reading the target's memory to do it.

| Construction | What happens |
|--------------|--------------|
| quiet | the target floods for 1.5 s, stops the flood and ends 300 ms later; the debugger sees the exit, then disposes |
| mid-flood | the target ends while a thread still floods; the debugger sees the exit, then disposes |
| detach-then-end | the debugger disposes (borrowed: synchronize, then detach) at a random moment in the flood; the target floods 1 s more and ends |
| exit-at-detach | as detach-then-end, but the target ends as soon as it sees the debugger gone (finding 63's case, without the kill) |

Each construction runs on both read paths where the platform has two (finding 101): on Linux a descendant target
(read directly) and a reparented one (read over the transport); on macOS a `dotnet`-hosted target and one started
through its own apphost (on the runners direct and transport; on omega both transport). Windows has one path. The
target ends itself 60 s after it starts whatever it sees of the debugger, so a target still alive after that is
held. 1,520 sessions in 76 child processes; the facts of each run are in [`102-natural-end/`](102-natural-end/).

| Machine | OS, architecture | .NET (probe and target) |
|---------|------------------|-------------------------|
| omega | macOS 27.0.1, arm64, metal, 18 cores | 10.0.9, Microsoft |
| omega-ubuntu | Ubuntu 26.04.1, arm64, Parallels VM, 4 cores | 10.0.14, Ubuntu's (`/usr/lib/dotnet`) |
| omega-ubuntu | same | 10.0.14, Microsoft's (`~/.dotnet`) |
| zbook | Ubuntu 26.04.1, x64, metal, 8 cores | 10.0.14, Microsoft's (`~/.dotnet`) |
| omega-windows | Windows 11 (26200; it was 26100 for finding 101), arm64, Parallels VM, 4 cores | 10.0.14, Microsoft |
| runner `macos-latest` | macOS 26.6.2, arm64, 3 cores | 10.0.14, Microsoft |
| runner `macos-26-intel` | macOS 26.6.1, x64, 4 cores | 10.0.14, Microsoft |
| runner `ubuntu-24.04` | Ubuntu 24.04.5, x64, 4 cores | 10.0.14, Microsoft |
| runner `ubuntu-24.04-arm` | Ubuntu 24.04.5, arm64, 4 cores | 10.0.14, Microsoft |
| runner `windows-latest` | Windows (26100), x64, 4 cores | 10.0.14, Microsoft |
| runner `windows-11-arm` | Windows 11 (26200), arm64, 4 cores | 10.0.14, Microsoft |

omega is the only machine on 10.0.9, its newest .NET 10 runtime.

## A target that ends by itself under the debugger

quiet and mid-flood: **760 of 760 sessions ended naturally**, on every machine and both read paths, 360 of them over
the transport (omega 80; the Linux reparented targets 200; the apphost targets on the macOS runners 80). Every target
ended and wrote its marker, no anomaly was reported, no call blocked, and the final attach succeeded in every child
process (76 of 76). mid-flood is the race Step 2 named as open, the program's end while the debugger dispatches
another thread's event: 380 of 380.

So on these machines a natural end did not produce finding 100's failure, on the path finding 100 hangs in or the
other. Finding 100 follows a forced end; this is the first measurement of the same flood ending by itself.

`Dispose` after the target's exit took 250 to 390 ms (up to 570 on the macOS runners, once 1.9 s on the macOS x64
runner): DrHook waits 50 ms and then 200 ms by design on that path (`BorrowedDeathCheckSettleMs`,
`ExitWorkSettleMs`).

## A detach drops one callback

detach-then-end and exit-at-detach: **754 of 760 sessions reported exactly one `LateCallback`**, and nothing else
except the cases below; 6 reported none (all on the `macos-latest` runner). **No session reported two.** The dropped
callback was a CreateThread (467) or an ExitThread (287), arriving after the queue was completed (310) or disposed
(444). In 759 of 760 sessions the target then ran on and ended by itself: detach-then-end 0.9 to 2.5 s after
`Dispose` returned (median 1.0 s), exit-at-detach within 0 to 109 ms.

Where it comes from, read in the code (not observed in mscordbi): a borrowed `Dispose` of a live target
(`DebugSession.Dispose`) first disposes the callback pump (completes its queue, joins its worker, disposes the
queue), waits 50 ms, and only then synchronizes the target (`Quiesce`, `ICorDebugController.Stop`) and detaches. In
that window the flooding target keeps raising events; `CallbackPump.OnCallback` cannot queue them and drops them,
reporting a `LateCallback`, without a `Continue`. The pump's own header says each callback holds the target until the
debugger continues it. That explains why there is never a second: the target is held at the first dropped callback
until the detach. Finding 59 saw the same late callbacks after the pump's disposal and recorded them as expected at
a clean teardown. Finding 54's contract (C-DRAIN-CB: a detach is safe with respect to queued callbacks if the `Stop`
precedes it) is enforced by that `Stop`, which comes after the pump is gone, so a callback in the window is neither
queued nor continued. **Hypothesis, not established:** the dropped callback holds the target until `Detach`
releases it, and the release is what failed once (next section). Finding 42 (probe 33, macOS, 2026-05-23) recorded
the precedent: a `Detach` of a target held at a breakpoint stop that was never continued left it held indefinitely.

## Once, on Windows, the target stayed held

omega-windows, detach-then-end, session 15 of 20 (target pid 25888): the attach, the flood and `Dispose` looked like
every other session (detach 106 ms into the flood, `Dispose` 83 ms, the one late callback, a CreateThread after the
queue was disposed). The target then did not end within the child's 70 s wait and never wrote its marker. The child
watched it through its own `Process` handle, so the pid is not a reused one. When the child had finished (sessions
16 to 20 and the final attach ran normally), the parent found the target still alive more than 75 s after it started
and ended it.

The target cannot run past 60 s by its own construction: its wait for the debugger to leave and its flood loop both
stop at 60 s. So it was held, in one of two ways the record cannot tell apart: suspended, or a thread blocked, such as
the flood thread waiting on a thread start or join whose debugger event was never continued (the main thread then
waits for the flood thread forever). Windows runs capture no target state.

Rates: 1 of 20 on omega-windows (detach-then-end; exit-at-detach 0 of 20), 0 of 40 on each Windows runner, 0 on
every other machine. DrHook itself was not affected. This is the reverse of ADR-016's rule: not DrHook failing with
its target, but a borrowed target held by DrHook leaving it, where a borrowed `Dispose` is meant to leave the target
running un-debugged (`DetachSynchronized`).

## Detach reports "process terminated" under the wrong name

`DebugSession.Detach` accepts one failure as the expected one for a target that is already gone, and names it
`CORDBG_E_PROCESS_TERMINATED = 0x8013134F`. dotnet/runtime's error table (`release/10.0`, `corerror.xml`) gives
`0x8013134F` as `CORDBG_E_OBJECT_NEUTERED` and `CORDBG_E_PROCESS_TERMINATED` as `0x80131301` (the next code,
`0x80131302`, is `CORDBG_E_PROCESS_NOT_SYNCHRONIZED`, as DrHook's own comment elsewhere says). So a `Detach` that
returns "process terminated" is reported as an `UnexpectedHResult` anomaly. It happened four times, in the detach
constructions: zbook three times (detach-then-end reparented session 4, exit-at-detach descendant 6 and reparented
8), the `ubuntu-24.04` runner once (exit-at-detach reparented 7). In zbook's detach-then-end session the target was
alive when `Detach` said so: it flooded 1 s more, ended by itself and wrote its marker. So accepting the code as
harmless is not obviously right either. DrHook is unchanged (ADR-016: no teardown change before measurement).

What the two codes mean, read in `CordbProcess::Detach` (`debug/di/process.cpp`, `release/10.0`): `OBJECT_NEUTERED`
is its first check, and means mscordbi had already closed its process object, after handling the target's exit.
`PROCESS_TERMINATED` means mscordbi still held the process but found the target gone before or during the detach: a
detach resumes the target, and if it ends mid-detach, the source comment says Detach must return this code and
"pretend" the exit came first. It is also returned when the target's helper thread is lost while mscordbi waits for
the detach reply. Detach neuters the process object on every return path, so on both codes nothing is left
connected. Which path gave zbook's TERMINATED for a live target, the source does not show.

**Decided 2026-11-04 (Martin):** `Detach` names both codes correctly and accepts both. After either, it asks the OS
whether the target has exited, waiting up to 500 ms (`GoneTargetExitConfirmMs`). The exit-at-detach targets that drew
TERMINATED were gone 0, 20 and 20 ms after `Dispose` returned, all exit-at-detach targets within 109 ms, so the wait
is needed: an instant check would have called all three alive. zbook's live target ran 1,024 ms more. A target still
running after the wait is reported as a new anomaly, `GoneTargetAlive`. This changes how a return code is reported,
not the order or content of the teardown; no probe has been run against it yet.

## Debug pipes left on the Linux x64 runner

On the `ubuntu-24.04` runner, 3 of 40 exit-at-detach sessions (descendant 15, reparented 6 and 13) left the target's
`clr-debug-pipe-<pid>-*-in/out` in the temp directory 300 ms after the target had ended. No other machine left any.
The probe removes them once recorded. Why is not known.

## The debugger's own threads across sessions

Finding 101 left open whether mscordbi's transport thread ends with the target and whether such threads accumulate in
one process. They do not accumulate. The debugger process's thread count has no trend across the 20 sessions of
any child on any machine (it varies by at most three, on omega). On Linux, where the probe lists `.NET DebugPipe` threads after each
session (the target ended, pipes removed):

- After a session that saw the target's exit (quiet, mid-flood): one such thread beyond the probe's own, in
  `futex_do_wait`. It is a new thread each session, and the previous one is gone by the next session's record, so it
  ends within seconds.
- After detach-then-end: none.
- After exit-at-detach: one, sometimes two (the previous session's not yet gone), then gone.
- The probe's own runtime's `.NET DebugPipe` thread waits in `wait_for_partner` (opening its pipe, syscall `openat`)
  throughout, the same thread for the whole child: it is the debuggee side every .NET process has, not the
  debugger's.

## Timings

Attach: median 13 to 14 ms in every construction; slowest 7.8 s and 2.4 s, both on the `macos-26-intel` runner
(quiet, `dotnet` host, sessions 1 and 8, both natural; finding 101 saw a 4.7 s attach there too). `Dispose` of a
detach: 50 to 133 ms on our machines and the Linux and Windows runners, up to 350 ms on the macOS runners.

## What this establishes

- A target that ends by itself, idle or mid-flood, leaves DrHook usable, on all six platforms, both read paths and
  both Linux .NET builds: Step 2's hypothesis held for these constructions.
- A borrowed detach of a busy target drops one debugger callback almost every time, on every platform, in a window
  DrHook's own `Dispose` opens.
- A borrowed detach can leave the target held: once in 760, on the Windows VM.

## Not established

- What held the Windows target, and whether the dropped callback is the cause; whether it happens elsewhere at a
  rate 20 sessions cannot show.
- Why `Detach` reported "process terminated" for a live target on zbook.
- Why the Linux x64 runner left debug pipes.
- The rest of Step 2: the natural end of a launched (owned) target, a test run under MTP and VSTest, and DrHook's own
  test host as the target. Probe 102 measured borrowed sessions only.

## Next (proposed, not decided)

1. On Windows, record the held target's state when a target outlives its bound: its threads' suspend counts and wait
   reasons, and a dump; then repeat detach-then-end many times on omega-windows and both Windows runners for a rate.
2. ~~Decide how `Detach` treats `0x80131301` and `0x8013134F`, with the zbook case in view.~~ Decided 2026-11-04
   (the section above). Rerunning probe 102 would show `GoneTargetAlive` in place of zbook's `UnexpectedHResult`,
   and nothing in place of the other three.
3. Step 2's remaining scenarios (above).
