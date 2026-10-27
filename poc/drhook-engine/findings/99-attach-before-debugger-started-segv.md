# Finding 99 — attaching before the target's runtime debugger had started crashed the debugger host (Linux, macOS)

**Date:** 2026-10-27
**Status:** Mechanism **PROVEN** (probe 66, deterministic on Linux arm64). **Fixed**: an attach waits until the
target's runtime has started its debugger helper thread. A runtime defect in mscordbi; DrHook now avoids the state.

## Symptom

The second host crash of [finding 98](98-dbgshim-unload-tls-destructor-segv.md): with that fix in, the DrHook
integration test host on omega-ubuntu (Linux arm64, distro .NET 10.0.13) still died with SIGSEGV in 2 of 12 full
runs, and in 1 of 40 runs of only the six VSTest-testhost tests. Some runs instead failed "no stop within 5 s of
Pause".

## Where it crashed (three cores, one signature)

Symbols came from Ubuntu's debuginfod (`debuginfod.ubuntu.com`). gdb asserts on these libraries' DWARF for `/s`,
`ptype` and field access, so the analysis read member offsets with `readelf -wi` and decoded the signal frame from
the raw stack.

- The faulting thread is mscordbi's RC event thread, running a queued `SendAttachProcessWorkItem::Do` →
  `Cordb::SendIPCEvent` → `CordbRCEventThread::SendIPCEvent`, at process.cpp:10057. That line is
  `process->GetEventChannel()->SendEventToLeftSide(...)`, and `CordbProcess::m_pEventChannel` (offset 912) is NULL.
- The `CordbProcess` is not detached, terminated or neutered (its shim is live). Its DAC is initialized,
  `m_initialized = 0`, and the shim has had no first managed event.
- The managed stacks put all three crashes in VSTest-testhost tests (`VstestAttachDisposeTest`,
  `ConcurrentPauseStopTest`). Those tests attach as soon as vstest.console prints the testhost's PID, which it does
  when it *launches* the testhost ([finding 91](91-vstest-host-debug-attach-race.md)).

## Mechanism (runtime source, v10.0.0)

- `Cordb::DebugActiveProcessCommon` (rsmain.cpp) on the transport platforms (`FEATURE_DBGIPC_TRANSPORT_DI`: Linux,
  macOS) calls `pProcess->QueueManagedAttach()` **unconditionally** once `DebugActiveProcess` succeeds. Elsewhere the
  attach is queued only if `CordbProcess::Init` found the left side initialized: then it opens the IPC channel and
  sets `m_fDoDelayedManagedAttached`. `QueueManagedAttach` only asserts that flag, which is a no-op in release builds.
- `CordbProcess::Init` asks the DAC `IsLeftSideInitialized()`. If false, it opens no channel and expects a startup
  notification later.
- In the target, `Debugger::Startup` marks the left side initialized (the inlined `RaiseStartupNotification`, an
  `InterlockedIncrement`) on the instruction right after `DbgTransportSession::Init` creates the debug transport.
  The transport's own thread answers the debugger's handshake.

So if the attach's transport session opens before the startup thread executes that next instruction, mscordbi
queues an attach on a process with no channel, and its RC event thread dereferences NULL. On an idle machine the gap
is a few instructions. On a loaded 4-vCPU VM, preempting the startup thread for the few milliseconds the attach
takes is rare but possible, which matches about 1 crash in 40.

A target whose debugger never starts is **not** this crash: with no transport, no session opens and mscordbi gives
up after 10 s with `CORDBG_E_TIMEOUT` (`DbgTransportPipeline::DebugActiveProcess`, `WaitForSessionToOpen(10000)`).

## Probe 66

`66-early-attach-smoke.cs` runs each attempt in a child process, so a host crash becomes an exit code.

- Two constructions missed the state, and the probe header records why:
  - Attaching as early as possible from spawn always found the transport FIFOs present. The window is about 1 ms
    after libcoreclr loads.
  - Freezing the whole target with SIGSTOP stops its transport thread as well, so the attach timed out instead.
- **held** reproduces it. The target runs under gdb in non-stop mode with a breakpoint on the marking instruction
  (`libcoreclr + 0x29d9a8` in this build), so only the startup thread stops; the transport thread keeps running.

| mode | without the guard | with the guard |
|---|---|---|
| held (startup thread held after the transport is up) | **3/3 SIGSEGV of the host** | 3/3 refused (`CORDBG_E_TIMEOUT`) |
| released (held, let go 2 s into the attach) | **3/3 SIGSEGV** | 3/3 stopped on Pause |
| unready (TMPDIR read-only: no transport, no debugger) | 3/3 `CORDBG_E_TIMEOUT` after 10 s | 3/3 refused |
| ready (normal target) | 3/3 stopped | 3/3 stopped |

The core from a `held` run has the test-host cores' signature: same thread and function, same process state.

## Fix

`DebugSession.Attach` and `AttachAndOwn` wait, on Linux and macOS, until the target has a thread named
**`.NET Debugger`** (`DebugSession.AwaitTargetDebugger`, bounded at 10 s, the bound mscordbi gives the same attach).
That is the runtime's debugger helper thread. `Debugger::Startup` starts it with `DebuggerRCThread::Start`, on the
same thread and after the mark, and it names itself, so the name proves the target is past the window. A target that
doesn't get there in time (still starting, or debugging disabled with `DOTNET_EnableDiagnostics=0`) is refused with
`CORDBG_E_TIMEOUT` and a message that says why. Windows doesn't queue the attach unconditionally and skips the wait.
`DebugSession.Launch` is unaffected: its startup callback fires after the runtime has started its debugger.

`Interop/TargetThreads.cs` reads thread names:

- Linux: `/proc/<pid>/task/*/comm`.
- macOS: `proc_pidinfo(PROC_PIDLISTTHREADS)` and `PROC_PIDTHREADINFO`, unentitled for a same-user target. The
  layout was checked against the macOS 27 SDK by compiling a probe.

Two candidates were rejected:

- **The FIFOs' existence**: the window starts *after* they exist.
- **System.Private.CoreLib being mapped**: it does follow the mark (`SystemDomain::Init` runs after
  `InitializeDebugger`, 8 of 8 samples), but in a single-file app CoreLib lives inside the bundle, so it never
  appears as a mapped file.

## Validation

- Probe 66 as in the table, before and after.
- `TargetThreadsTests`: the test host's own `.NET Debugger` thread is found on Linux and macOS, which also checks the
  macOS struct offsets against a live process.
- omega-ubuntu, the six VSTest-testhost tests in a loop: before, 1 host crash in 40 runs. With the guard, 0 in 40
  (2 runs still failed the `ConcurrentPauseStopTest` exit check below). With the guard and that test fixed, 40 of
  40 clean. Full integration suite 3/3 clean, twice.
- omega (macOS): DrHook.Engine.Tests 186 passed, 1 skipped; integration 18 passed, 1 skipped in 3 of 3 runs.
- elite (Linux x64): unit 186 / 1 skipped; integration 18 / 1 in 3 of 3.
- omega-windows: unit 179 / 8 skipped (5 of 5 runs); integration 17 / 2 in 3 of 3. The guard is a no-op there.

## Found along the way

- **`ConcurrentPauseStopTest` did not sync on its target.** It paused 100 ms after the attach. An early attach then
  paused the testhost *before* the throw/catch loop, so the "Pause amid a STOPPING flood" assertion passed without a
  flood. Dispose then SIGTERMed the testhost mid-handshake, and the `dotnet test` bootstrap missed its 10 s exit:
  1 in 20 runs on omega-ubuntu, and twice on omega under host load. This is finding 94's failure in a test that the
  clock-to-target fixes of findings 94 and 95 didn't reach. It now runs to a breakpoint inside the loop (`FLOOD_RUNNING_HERE`) before it pauses.
- **`WindowsSpawnTests` asserted the scheduler.** A `CREATE_SUSPENDED` thread is suspended by a kernel APC that runs
  when it is first scheduled, so until then it reads `Ready`, not `Wait/Suspended`. That failed once on
  omega-windows under load. The test now checks behavior: the child doesn't run until it is resumed.
- **Open: a debugger thread leaks per ended session on Linux.** A host that has run many sessions has one mscordbi
  `DbgTransportSession::TransportWorker` thread per past session, each blocked in `TwoWayPipe::Connect` → `open()`
  on a FIFO (6–12 per test-host core). A target killed with SIGKILL never removes its `clr-debug-pipe-*` FIFOs
  (omega-ubuntu's `/tmp` held 402). The transport worker's reconnect then opens a FIFO that nobody will ever open
  from the other side, and blocks forever. This is bounded by the number of sessions, harmless so far, and not yet
  addressed.
