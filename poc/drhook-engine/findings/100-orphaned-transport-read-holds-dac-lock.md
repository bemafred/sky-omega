# Finding 100 — a session disposed while mscordbi reads target memory leaves the DAC locked; every later attach and launch hangs (macOS)

**Date:** 2026-10-30
**Status:** Mechanism **located** from stacks of the hung process on a GitHub macOS runner and the runtime source
(`release/10.0`). **Not reproduced** on omega, **not fixed**. Why mscordbi never sees the target's exit is not
traced.

## Symptom

On the GitHub macOS runners, DrHook integration tests hit the suite's 120 s limit: in 1.8.38 (macOS x64) and
1.8.39 (macOS arm64) one test each, and from 4b9812a on both runners, where every test after the first timed out
too (8 timeouts, about 16 minutes). They pass on omega in about a second.

The timeline added in 4b9812a showed where: each VSTest-target test printed `testhost pid N`, then nothing. The
call that did not return was `DebugSession.AttachAndOwn`. The first test to hang always ran right after its MTP
sibling, which disposes a session mid-flood (InformationalFloodTest) or while paused (PauseDisposeTest).

## Where it hung

`tools/watch-drhook-hang.sh` recorded the integration-test host on macOS x64 (run 41068258696) 93 s and 271 s
into the suite, with `sample` (native stacks) and `dotnet-stack` (managed stacks). Both records show the same state.

- **The hung attach.** The test thread is in `ICorDebug.DebugActiveProcess` → `ShimProcess::DebugActiveProcess` →
  `CordbWin32EventThread::SendDebugActiveProcessEvent` → `WaitForSingleObject` (no timeout). Its Cordb's Win32 event
  thread is in `AttachProcess` → `CordbProcess::Init` → `ForceDacFlush` → `DacDbiInterfaceImpl::FlushCache`,
  waiting on a mutex in `libmscordaccore`, the DAC, which is loaded once per process.
- **The thread inside the DAC.** The previous session's Win32 event thread (the MTP flood test's) is dispatching a
  runtime event: `CordbProcess::HandleRCEvent` → `RawDispatchEvent` → `DacDbiInterfaceImpl::IsThreadMarkedDead` →
  `DacReadAll` → `ShimRemoteDataTarget::ReadVirtual` → `DbgTransportSession::ReadMemory` →
  `SendRequestMessageAndWait`. It is the only thread inside the DAC.
- **Why its read never ends.** That session's transport thread is in `TwoWayPipe::Connect` → `open()`: it is
  reconnecting to the dead target's named pipe.
- **The cascade.** In the second record the launch test's session waits in `FlushCache` too. Every new session
  needs the DAC, so every later attach and launch in the process hangs.
- **A leak beside it.** Seven transport threads are blocked in `TwoWayPipe::Connect` → `open()`, one per earlier
  session whose target was killed. The host on omega shows the same after 10 s of a normal run.

## Mechanism (runtime source, `release/10.0`)

- `ShimRemoteDataTarget::ReadVirtual` (`debug/di/shimremotedatatarget.cpp`) reads target memory with
  `PAL_ReadProcessMemory` when `PAL_OpenProcessMemory` succeeded, and over the debugger transport otherwise. On macOS
  `PAL_OpenProcessMemory` needs `task_for_pid` (`pal/src/debug/debug.cpp`); on Linux it opens `/proc/<pid>/mem`.
  Both the runner's stacks and omega's (Developer Mode on) show reads going over the transport.
- `DbgTransportSession::SendRequestMessageAndWait` (`debug/shared/dbgtransportsession.cpp`) waits with no timeout
  for the reply or `m_hProcessExited`.
- When the connection breaks, the debugger side's transport worker loops `while (m_eState != SS_Closed)` calling
  `TwoWayPipe::Connect`, which opens the target's pipe with `open(O_WRONLY)` (`debug/debug-pal/unix/twowaypipe.cpp`).
  On a named pipe that call blocks until a reader opens the other end. A killed target never removes its pipes;
  `TwoWayPipe::CleanupTargetProcess` removes them when the debugger detects the exit. Omega's `$TMPDIR` held 954
  `clr-debug-pipe-*` files (2026-10-30).
- So the read ends only if `m_hProcessExited` signals, and here it never did. Not traced: how mscordbi's PAL
  detects a target's exit. For a process that is not its child, the PAL's check is `waitpid` (ECHILD) then
  `kill(pid, 0)` (ESRCH), run by the synch manager's worker thread (`pal/src/synchmgr/synchmanager.cpp`). A PAL
  started in DLL mode (`PAL_INITIALIZE_DLL`, which is `PAL_INITIALIZE_NONE`) does not start that thread, and the
  sample shows no worker thread in `libmscordaccore`, only `libcoreclr`'s.

## What DrHook does that opens the window

`DebugSession.Dispose` of an owned session that is still running: `TryResumeForDetach` continues the target, so
it runs and raises callbacks, which mscordbi's Win32 event thread dispatches, reading target memory; then
`RequestExit` sends SIGTERM and, after the natural-exit timeout, `TryKillTargetAndSettle` kills it. A target that
dies while a dispatch has a read outstanding orphans that read. The faster the machine, the smaller the window:
it hit 5 of the 12 macOS jobs CI has run since it began (653f1b0), and none of 23 runs on omega under CPU load (20 of the
flood pair, 3 of the whole solution).

## Consequence

A test host or a `drhook-mcp` server process that hits the window can never attach or launch again: the call
hangs until the process is restarted. Each killed target also leaves one transport thread blocked for the life
of the debugger process, and its two pipe files in `$TMPDIR`.

## Next

A deterministic probe before any fix: stop the target with SIGSTOP so a transport read cannot be answered, start a
read, kill the target, then attach to a second target. Fix options depend on what it shows, among them: leave the
target synchronized, not running, when it is killed; detach before killing; or end the session's transport when
the target is known dead.
