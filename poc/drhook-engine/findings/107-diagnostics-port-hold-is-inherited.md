# Finding 107 — The runtime's diagnostics-port pause holds a launched target before its first managed code, but every .NET child inherits it, and a child with nobody listening waits forever

**Date:** 2026-11-08
**Status:** **Observed** on zbook (Ubuntu 26.04.1 x64, Microsoft's runtime 10.0.14, dbgshim 9.0.661903), probe 107
rev 5, 2026-11-08 05:31–05:36 UTC, exit 0, every prediction held. **Linux only**: the probe reads `/proc`, and was not
run on macOS or Windows. **Decided** (Martin, 2026-11-08): the shell stays in DrHook's default Linux launch. Nothing in
the engine changed.

## The question

On Linux, DrHook holds a launched target with `/bin/sh -c 'kill -STOP $$; exec "$0" "$@"' <program>` (`LinuxSpawn`).
The hold is needed because glibc's `posix_spawn` cannot start a process suspended, dbgshim's
`CreateProcessForLaunch` takes no stdio handles, and the hold must exist before the runtime checks dbgshim's startup
semaphores. Martin asked whether the shell is needed at all (2026-11-07). The candidate replacement is the runtime's
own pause: with `DOTNET_DiagnosticPorts=<path>,connect,suspend` the runtime dials `<path>` early in `EEStartup` and
waits for a `ResumeRuntime` command.

Questions (the probe's header has the full predictions):

- **Q1.** Is the pause before the runtime's check for dbgshim's startup semaphore? Registering 2 s into the pause must
  still catch the startup.
- **Q2.** Does any managed code run before the resume?
- **Q3.** Do the target's .NET children inherit the pause, and what does a child do with nobody listening?
- **Q3b.** Is a runtimeconfig property a channel the children would not inherit?
- **Q5.** Does dbgshim treat a paused runtime as already loaded, and call back during the pause?
- **Q6.** Does the default diagnostics port exist during the pause, and does a `ResumeRuntime` over it release the
  pause?

## What the source says (release/10.0)

- **The pause comes before the debugger's startup.** `EEStartupHelper` (`vm/ceemain.cpp`) calls
  `PauseForDiagnosticsMonitor`, then `JitHost::Init`, then `InitializeDebugger`, under which
  `PAL_NotifyRuntimeStarted` runs.
- **The startup handshake.** `NotifyRuntimeUsingSemaphores` (`pal/src/thread/process.cpp`) opens the startup
  semaphore (`/clr` + `st` + pid as 8 hex digits + process start time as 16, from `/proc/<pid>/stat`). If it is
  absent, nothing happens. If it exists, the runtime posts it, then waits on the continue semaphore with
  `while (sem_wait(continueSem) != 0)`, retrying only on `EINTR`, with no timeout.
- **An S_OK startup callback leaves the runtime waiting for a debugger.** The end of `Debugger::Startup`
  (`debug/ee/debugger.cpp`) is `if (PAL_NotifyRuntimeStarted()) MarkDebuggerAttachedInternal();`. The probe never
  attaches (no `DebugActiveProcess`), so a held target was predicted to stop before main and be killed at its bound.
- **The wire** (`src/native/eventpipe/`): a 20-byte header (`ds-protocol.h`); `ResumeRuntime` is command set `0x04`,
  id `0x01`, no payload. Both the OK (`0x00`) and the error (`0xFF`) replies are 24 bytes, the header plus an int32
  code (`ds_ipc_message_send_success`). `ipc-protocol.md` says the OK reply has no payload; that is wrong. The
  runtime advertises itself on connect with 34 bytes (`ADVR_V1\0`, a cookie, the pid, two zero bytes).
- **Re-dial timing** (`ds-ipc-pal-types.h`): after a failed connect the runtime waits 10 ms, growing by ×1.25, up to
  500 ms.

## What was run

`107-launch-hold-smoke.cs` (902 lines) with `107-launch-hold-target.cs` and `107-launch-hold-hook.cs`, written by
sky-zbook and reviewed by sky-omega before each run (revisions 2–5). It speaks to libdbgshim and the diagnostics IPC
directly; it does not use DrHook.Engine. Each mode spawns `dotnet <target.dll>` with its own socket path.

| Mode | Runs | What it does |
|------|------|--------------|
| hold | 10 | spawn paused; accept the dial; wait 2 s; `RegisterForRuntimeStartup`; hold 2 s; Q6 in runs 1–5; `ResumeRuntime` |
| hold-control | 10 | no pause; register 2 s after the spawn |
| child-listener | 3 | the target starts one `dotnet` child; the child's dial is held 5 s, then resumed |
| child-no-listener | 3 | the listener is closed and its path removed before the child starts; the child is watched 30 s, then the path returns |
| runtimeconfig-property | 1 | `DOTNET_DiagnosticPorts` as a runtimeconfig property instead of an environment variable |

The full log is [`107-diagnostics-port-hold-is-inherited/zbook-rev5.txt`](107-diagnostics-port-hold-is-inherited/zbook-rev5.txt).

## What was observed

- **Q1: the pause holds the startup, 10 of 10.**
  - The runtime dialled 0–12 ms after the spawn: 3 threads, `libcoreclr` mapped.
  - Registering 2 s into the pause returned S_OK and created the semaphore pair. No callback came during the pause.
  - The reverse `ResumeRuntime` replied OK (code 0), and one S_OK callback with an `ICorDebug` followed.
- **The held target stopped before main, as predicted, 10 of 10.** No marks came, and 15 s after the resume it had not
  ended. The main thread was in `futex_do_wait`, `.NET DebugPipe` in `wait_for_partner`, `.NET Debugger` in
  `futex_do_wait`. It was killed at its bound. Run 1 had 6 threads and no `.NET Finalizer`; runs 2–10 had 7, so where
  it stops varies slightly.
- **Without the pause the startup is missed, 10 of 10** (hold-control). Main came 34–48 ms after the spawn. A
  registration 2 s later still returned S_OK, and the callback came with the target already past main; it exited 0.
- **Q2: no managed code ran before the resume.** No module-initializer mark and no main mark appeared before the resume,
  in any held run. The startup-hook arm tested nothing: the target is a .NET 10 file-based app, and its generated
  runtimeconfig sets `System.StartupHookProvider.IsSupported` to false (also `EventSource.IsSupported`,
  `IsDynamicCodeSupported` and others), so `DOTNET_STARTUP_HOOKS` was ignored. No hook mark appeared in any run, even
  after main.
- **Q5: dbgshim waits for the runtime's startup notification.** It did not treat the paused runtime as loaded; the
  callback came only after the resume.
- **Q6: the default port's `ResumeRuntime` does not release the pause, 5 of 5.** The default port
  (`/tmp/dotnet-diagnostic-<pid>-<start>-socket`) existed during the pause. `DiagnosticsClient.ResumeRuntime` over it
  returned in 2–20 ms, and 2 s later the target was still held. Only the suspending port's resume released it.
- **Q3: every .NET child inherits the pause, 3 of 3.** The child dialled the same socket 12 ms after the probe told the
  target to start it. Its environment held `DOTNET_DiagnosticPorts` unchanged: the runtime does not unset it, and the target saw the
  same value. The child produced no mark during its 5 s hold, then ran and exited 0 once resumed.
- **A child with nobody listening waits, 3 of 3.** With the path removed, the child stayed in startup for 30 s: state
  S, 3 threads, no mark. When the path was bound again it re-dialled within 380–387 ms, and ran to exit 0 once resumed.
- **Q3b: a runtimeconfig property is not a channel.** The target ran to exit 0 without dialling, and saw the variable
  unset.
- **Containment.** 33 processes tracked, none alive at the end; 30 files removed, all from the ten killed held
  targets (their `clr-debug-pipe` pairs and diagnostic sockets). No unexpected leftovers.

## Reading

The pause does what the shell does for the target: it holds the runtime before its first managed code and before
dbgshim's semaphore check, so a registration made during it catches the startup. But the hold travels in the
environment. Every .NET program the target starts, for its whole life, pauses at startup and dials DrHook. A child
started after DrHook stops, detaches or crashes waits forever, and the 30 s runs show nothing ends that wait but a
listener. Clearing the variable inside the target would stop the spread, but it changes the program under
observation. A listener that resumes every child automatically works only while DrHook is attached (sky-zbook,
2026-11-08).

So the shell stays for the default launch. The shell's hold ends at `exec` and touches nothing the target starts.
The diagnostics-port hold suits only a mode the operator chooses for catching children, with this limit written down
([ADR-017](../../../docs/adrs/drhook/ADR-017-debugging-a-process-tree.md)). Commit `b570e31` (a POSIX launch finds
the program itself) is unaffected.

## The rev 4 stop

Rev 4 took the reply layout from `ipc-protocol.md` and stopped on the 24-byte OK reply in hold run 1, with exit 2. Its
exit-2 path skipped containment, and left a target stuck in startup with its `sem.clr*` pair, pipe pair and diagnostic
socket. sky-zbook recorded their state at 05:21:54 UTC, killed the target (its start time matched) and removed the
files. sky-omega's review had checked the layout against the doc rather than the source, and had missed the exit-2
path. Rev 5 takes every layout from the source and runs containment in a `finally` on every end.

## Side findings

- **The startup handshake's continue wait has no bound.** A registrar that dies between the runtime's post of the
  startup semaphore and its own post of the continue semaphore leaves the target in startup forever. This applies to
  today's shell-based launch too: a DrHook crash at that moment. Not observed; read from the source.
- **A killed runtime leaves its `/tmp` files.** Each killed held target left its `clr-debug-pipe-<pid>-<start>-in/out`
  pair and its `dotnet-diagnostic-<pid>-<start>-socket`. That matches finding 100's pipe files on macOS, and adds the
  diagnostic socket on Linux. zbook's `/tmp` holds 382 stale pipe pairs from earlier sessions, left untouched.
- **.NET 10 file-based apps run with AOT-style feature switches off**, startup hooks among them, even when not
  published for AOT. A probe that relies on a startup hook, EventSource or dynamic code must not be a file-based app.

## Not established

- **macOS and Windows.** The probe ran only on Linux x64. Whether the same variable pauses and is inherited the same
  way there is untested.
- **A full DrHook attach after the resume.** The probe stops after the startup callback; it never calls
  `DebugActiveProcess`, sets a breakpoint or continues to main. That is the next step, with DrHook.Engine, if the
  child-catching mode is built.
- **Startup hooks before the resume.** Untested (above). By the source, a startup hook is managed code
  (`System.StartupHookProvider`), and no managed code runs before `EEStartup` returns, so the hook must come after the
  resume. That is an inference, not an observation.
- **Whether the default port could carry the resume** in some other configuration. Only the case above was tried.
