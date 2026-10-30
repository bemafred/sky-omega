# Finding 100 — a target that dies while mscordbi's event thread reads its memory leaves the DAC locked; every later attach and launch hangs (macOS)

**Date:** 2026-10-30
**Status:** Mechanism **PROVEN**: located from stacks of the hung process on a GitHub macOS runner and the runtime
source (`release/10.0`), then reproduced on omega by probe 100. **Not fixed.** A defect in mscordbi's design that
DrHook's owned teardown runs into.

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
- **Beside it.** Seven more transport threads are blocked the same way in `TwoWayPipe::Connect` → `open()`, from
  earlier sessions. The host on omega shows such threads after 10 s of a normal run.

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
- `m_hProcessExited` is a handle from `OpenProcess(pid)` in mscordbi's PAL (`DbgTransportTarget::GetTransportForProcess`,
  `debug/di/dbgtransportmanager.cpp`). Probe 100 shows it does not release the read: the Win32 event thread was still
  in it 10 s after the kill, and a second attach, which needs the DAC, was still waiting 30 s after that. (For a process that is not its child, the PAL's exit check runs on the synch
  manager's worker thread, `pal/src/synchmgr/synchmanager.cpp`; a PAL started in DLL mode, `PAL_INITIALIZE_DLL` =
  `PAL_INITIALIZE_NONE`, does not start that thread, and neither the runner's sample nor omega's shows one in
  `libmscordaccore`. That is consistent, not proven to be the cause.)
- What does release a read is the debugger handling the exit. `CordbWin32EventThread::Win32EventLoop`
  (`debug/di/process.cpp`) waits on the process handle and calls `ExitProcess`, which marks the process
  terminating, removes the target's pipes (`CleanupTargetProcess`) and queues the ExitProcess callback. That loop runs
  on the **Win32 event thread**, the same thread that dispatches the target's events (`HandleWin32DebugEvent` →
  `CordbProcess::Filter` → `HandleRCEvent`, the runner's stack), reading target memory to do it.
- So a read made by any other thread is released when the target dies, and a read made by the Win32 event thread
  itself, mid-dispatch, never is: the only thread that would handle the exit is the one waiting. It keeps the DAC's
  lock, and the target's pipes stay, so its transport thread blocks reopening them.

## Probe 100 (`poc/drhook-engine/100-orphaned-read-smoke.cs`)

Each attempt runs in a child process. The target runs under bash, not as the child's child (finding 87).

- **control:** stop the target at a `Debugger.Break`, freeze it with SIGSTOP, and read its stack and locals from a
  second thread. The read waits; after SIGCONT it finishes (about 3 s after it started). Reads go over the transport
  on omega too, and wait on the target.
- **kill:** as control, but SIGKILL instead of SIGCONT. The read returns about 20 ms after the kill, and the dead
  target's pipes are removed: exit handling ran and released it.
- **flood-kill:** the target starts and joins threads without pause, so the Win32 event thread is always dispatching
  CreateThread and ExitThread. The child freezes the target at random moments and samples itself until that thread
  is inside `DbgTransportSession::ReadMemory`, then kills the target. Ten seconds later the thread is still in the
  read, the pipes are still there, and a second attach in the same process is still pending after 30 s: the state
  on the runner.

Observed 2026-10-30, omega (macOS 27, arm64), two batches: control 5/5 completed, kill 5/5 returned, flood-kill 5/5
orphaned the read and hung the second attach, each caught on the first or second attempt. An earlier form of the probe read after a
Pause, which has no stopped thread, so it read nothing; and its first hypothesis, that any pending read is lost
when the target dies, is refuted by the kill mode.

## What DrHook does that opens the window

`DebugSession.Dispose` of an owned session that is still running: `TryResumeForDetach` continues the target, so
it runs and raises callbacks, which mscordbi's Win32 event thread dispatches, reading target memory; then
`RequestExit` sends SIGTERM and, after the natural-exit timeout, `TryKillTargetAndSettle` kills it. A target that
dies, by either signal, while the Win32 event thread has a read outstanding orphans that read. The faster the
machine, the smaller the window:
it hit 5 of the 12 macOS jobs CI has run since it began (653f1b0), and none of 23 runs on omega under CPU load (20 of the
flood pair, 3 of the whole solution).

## Consequence

A test host or a `drhook-mcp` server process that hits the window can never attach or launch again: the call
hangs until the process is restarted. Each killed target also leaves one transport thread blocked for the life
of the debugger process, and its two pipe files in `$TMPDIR`.

## Seen on the way, not investigated

- After the kill, DrHook's `GetStackFrames` / `GetLocals` threw `NullReferenceException` rather than a structured
  error (probe 100, kill mode).
- A line breakpoint set with `SetBreakpointAtLine` on the attached file-based target stayed pending and never hit,
  though its module was loaded; the probe uses `Debugger.Break` instead.

## Next

A fix must keep the Win32 event thread out of a dispatch when the target dies; probe 100's flood-kill mode is its
test. Candidates, each a change to DrHook's owned teardown:

- kill the target only while it is synchronized (stopped by the debugger), so no event is being dispatched;
- detach before terminating, so no session is left to read (this conflicts with finding 64's kill-first protocol,
  which must be re-examined);
- make reads direct, not over the transport: with `task_for_pid` allowed, `PAL_ReadProcessMemory` fails at once on a
  dead target. Needs entitlements; not explored.

The mscordbi behaviour itself is worth reporting upstream (dotnet/runtime).
