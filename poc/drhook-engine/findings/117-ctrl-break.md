# Finding 117 — CTRL+BREAK reaches a target on a console of its own (ADR-016 6.4): 80 of 80, from a sender that attaches to it

**Date:** 2026-11-22
**Status:** **RECORDED** on omega-windows (Windows 11 arm64), one run of probe 117 at `20b5121d`, in a clean clone at that
commit (RESULT(0)). Nothing else ran on omega or its virtual machines meanwhile. Not run: the Windows runners, x64.

ADR-016 6.4 decided Windows' exit request: after the detach, CTRL+BREAK to the target's own process group. The event
reaches only processes on the sender's console (`GenerateConsoleCtrlEvent`), and probe 112's sender shared the target's
console by construction. DrHook's does not: `WindowsSpawn` starts a target with `CREATE_NO_WINDOW`, a console of its
own, and drhook-worker is on drhook-mcp's console (`WorkerProcess.Start` sets no `CreateNoWindow`). The ADR: "Whether a
helper attached to it (`AttachConsole`) can send the event is to be built and measured."

## What was run

[`117-ctrl-break.cs`](../117-ctrl-break.cs), one program in four roles, with no debugger:

- **The parent**, in drhook-mcp's place. It counts any CTRL+BREAK it receives (a SIGQUIT handler that cancels it), and
  starts the sender as `WorkerProcess.Start` starts a worker: the dotnet host of its runtime, `exec`, stdin and stdout
  redirected, stderr inherited. So the sender is on the parent's console.
- **The sender**, in drhook-worker's place, with the worker's handlers for SIGTERM, SIGINT and SIGHUP and none for
  SIGQUIT, so a CTRL+BREAK that reached it would end it. Per session it starts a target as `WindowsSpawn` starts one
  (suspended, stdin `NUL`, stdout and stderr a pipe, only those handles inherited, `CREATE_NO_WINDOW`), with
  `CREATE_NEW_PROCESS_GROUP` added, so the target is the root of its own group. Once the target says it is ready, the
  sender sends CTRL+BREAK to the group one of two ways:
  - **worker:** itself. It leaves its console (`FreeConsole`), attaches to the target's (`AttachConsole`), sends
    `GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, target PID)`, leaves the target's console, returns to its parent's
    (`AttachConsole(ATTACH_PARENT_PROCESS)`) and sets its standard handles back.
  - **helper:** a process it starts with no window (`CreateNoWindow`, so on a console of the helper's own; stdin
    redirected and closed) leaves that console, attaches to the target's, sends, and exits.
- **The targets.** break-handler: a SIGQUIT handler cancels the event's default, writes `signal`, and the program returns
  0. break-default: no handler; a ProcessExit handler writes `process-exit` if it runs.

20 sessions per way and kind, 80 in all.

- **The hypothesis:** every call succeeds; a break-handler target runs its handler and returns 0, and a break-default
  target ends with `0xC000013A` (finding 112's detached end), each within 5 s of the send; neither the sender nor the
  parent receives the event; and the worker way leaves the sender back on its parent's console with its standard
  handles as they were.
- **Refuted** by a call that fails, a target that does not end so, a signal the sender receives, a CTRL+BREAK the parent
  receives, or a sender not back on its parent's console or with its standard handles changed.

| Machine | OS | .NET (all four roles) |
|---|---|---|
| omega-windows | Windows 10.0.26200, arm64, a virtual machine on omega | 10.0.15 |

The log: [`omega-windows.txt`](117-ctrl-break/omega-windows.txt). Windows PowerShell 5.1 appended the probe's output to
the runner's UTF-8 lines as UTF-16; the file here is that log decoded to UTF-8 and nothing else (the probe writes ASCII
only). The raw log is omega-windows `C:\Users\bemafred\diag\probe117-20b5121.log`, sha256 `8f15e4b5…139a1f3c`.

## Results

**The hypothesis holds,** 80 of 80. Every `FreeConsole`, `AttachConsole` and `GenerateConsoleCtrlEvent` succeeded, every
target ended as stated, and no session had a deviation. The sender received no signal and exited 0; the parent received
no CTRL+BREAK.

| Way | Target | Ended as stated | Exit | Handler's `signal` line after the send, ms, median [min–max] | Sender off its console, ms, max | Helper, start to exit, ms, median [min–max] |
|---|---|---|---|---|---|---|
| worker | break-handler | 20 of 20 | 0 | 0.40 [0.36–0.48] | 1.12 | — |
| worker | break-default | 20 of 20 | `0xC000013A` | — | 0.70 | — |
| helper | break-handler | 20 of 20 | 0 | 0.40 [0.36–0.46] | — | 43.2 [42.1–44.6] |
| helper | break-default | 20 of 20 | `0xC000013A` | — | — | 43.3 [42.6–52.2] |

- **Who was on the target's console.** Only the target and the process that had attached to it, in every session.
- **The standard handles.** `AttachConsole` replaced none of them, in either way. They were pipes in every role
  (`pipe/pipe/pipe`), before, during and after.
- **The return.** In every worker session the sender was back on its parent's console: five processes on it, as at its
  start, the parent among them.
- **break-default** ended with `0xC000013A` without running its ProcessExit handler, as in finding 112's detached end.
- **Send to end** (in the log), from the send to the sender's sight of the end: worker 1.3 to 2.7 ms. For the helper
  the sender looks only after the helper has exited, so its 10.1 to 12.0 ms is when the sender looked, not when the
  target ended; the `signal` lines show the two ways deliver alike.

## Not established

- The Windows runners and x64; one run on one machine.
- A sender whose standard handles are console handles. Here every one was a pipe, as drhook-mcp's are under an MCP
  client; what `FreeConsole` does to a console handle was not tried.
- A sender with no console, or alone on its console. The worker way returns to its parent's console, so a worker
  started with a console of its own, or whose parent has gone, has none to return to. Neither is how drhook-mcp starts
  a worker today.
- Whether the event needs its sender attached until it arrives. The sender left the target's console right after the
  send, and the helper exited soon after; neither leave was timed against the handler.
- A console event from drhook-mcp's console (CTRL+C, the console's close) during the sender's 1 ms away from it.
- A target that started a process of its own: that child is on the target's console and in its group unless it made
  its own, so CTRL+BREAK reaches it too. On Unix, SIGTERM to the target's PID does not reach its children.
- Any of this under a debugger, and after a detach. Finding 112 measured a detached target's answer to CTRL+BREAK; the
  whole path, detach then request, is for the build's tests.
- `S_FALSE` for `ControlCTrap` (6.4's other half).

## What this establishes

A process off a target's console can send it CTRL+BREAK by attaching to that console. Both ways delivered every event
(a handler ran within half a millisecond of the send), neither the sender nor its parent received it, and nothing
recorded about the sender changed:
- **The worker itself** leaves drhook-mcp's console for about a millisecond.
- **A helper process** leaves the worker's console alone, and costs a process start, about 43 ms here.

So 6.4's exit request can be built without changing how a target is launched, beyond `CREATE_NEW_PROCESS_GROUP`.
