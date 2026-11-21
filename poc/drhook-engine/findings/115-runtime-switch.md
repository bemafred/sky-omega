# Finding 115 — finding 86's lock, with a debugger worker per session (ADR-016 6.7): gone between sessions

**Date:** run and record 2026-11-21
**Status:** **RECORDED** on omega (macOS arm64). Probe 115 ran at `aee6e218`, once, in a clean clone at that commit
(RESULT(0)). Not run: omega-ubuntu, omega-windows, zbook, macbook and the six runners.

Finding 86 found that a debugger process loads the debug components (mscordbi and the DAC) of the first target's
runtime version, for the whole process and for good, so that a later target on another version fails. DebugSession now
refuses that up front (`GuardRuntimeLock`). ADR-016 6.7 decided a debugger worker per session, and gave as one of its
reasons: "Finding 86's lock goes away". The worker and drhook-mcp's relay to it were built the same day (`688a156`).
This checks that reason.

## What was run

[`115-runtime-switch.cs`](../115-runtime-switch.cs) builds its target,
[`115-runtime-switch-target.cs`](../115-runtime-switch-target.cs), for .NET 9 and for .NET 10. A file-based app's two
builds share one output directory, so each build's output is copied to a directory of its own, and each copy's
`runtimeconfig.json` must name its framework. (The probe's first run, before that copy, built .NET 10 twice and showed
nothing; its log was not kept.) Then:

1. **Through drhook-mcp's supervisor,** as drhook-mcp's tools drive it: `drhook_launch` of the .NET 9 target to a
   breakpoint in its loop, then `drhook_kill`; then the same for the .NET 10 target. Each launch starts a fresh worker.
2. **The control, in the probe's own process:** `DebugSession.Launch` of the .NET 9 target to its entry-module hold,
   then `Abandon`; then the same for the .NET 10 target.

The control runs second, so that it does not lock the probe's process before the supervisor's turn. The supervisor's
sessions load no debugger into the probe's process.

- **The hypothesis:** through the supervisor, both launches stop at the breakpoint, each locked to its own target's
  version (`lockedRuntime` .NET 9, then .NET 10). In one process, the second launch is refused with finding 86's
  message.
- **Refuted** if a launch through the supervisor fails, or reports another `lockedRuntime`. The run shows nothing if
  the control's second launch is not refused.

omega: macOS 27.0.1, arm64; the probe and drhook-mcp on .NET 10.0.9 (SDK 10.0.301); the only .NET 9 runtime installed is 9.0.4 (`dotnet --list-runtimes`; the log gives the major version only).
The log is [`115-runtime-switch/omega.txt`](115-runtime-switch/omega.txt).

## Results

| | .NET 9 target | .NET 10 target |
|---|---|---|
| Through the supervisor | launched, stopped at the breakpoint, `lockedRuntime` .NET 9, 802 ms | launched, stopped at the breakpoint, `lockedRuntime` .NET 10, 630 ms |
| Then `drhook_kill` | killed while stopped | killed while stopped |
| In one process | launched, at its entry-module hold; the process locked to .NET 9 | refused: "This DrHook debugger process is locked to .NET 9 …" |

**The hypothesis holds.** Each session's worker locked only itself, to its own target's version. The next session, in
a fresh worker, debugged a target on another version, with no reconnect. In one process the lock held, as finding 86
found.

The launch times include starting the worker.

## Not established

- Within one session the lock still holds: one worker debugs one runtime version. The probe did not try a second
  target in one session; a session has one target.
- .NET 11, and versions other than 9 and 10.
- Linux and Windows, and x64.
- Attach. Only launch was run.

## What this establishes

On omega, with a worker per session, a session on one .NET version can follow a session on another without a
reconnect. Before this, drhook-mcp needed a reconnect between them. A process that drives `DebugSession` itself, such as
a probe or the integration suite, is still locked for its whole life.
