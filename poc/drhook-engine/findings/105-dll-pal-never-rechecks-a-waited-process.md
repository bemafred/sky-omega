# Finding 105 — mscordbi's PAL never notices that a target died while a thread waits for it, so a request in flight at the death waits forever

**Date:** 2026-11-06
**Status:** **Mechanism located** in the runtime source (`release/10.0`) and consistent with every hung process
sampled on omega (macOS 27.0.1 arm64, runtime 10.0.9) and omega-ubuntu (Ubuntu 26.04.1 arm64, runtime 10.0.14),
and with the named stacks of every thread on zbook (Ubuntu 26.04.1 x64, runtime 10.0.14);
Windows, which has no PAL, did not hang. A target DrHook launched, the debugger's own child, hangs the same way
(2026-11-07, as predicted). **Not reproduced as
a minimal case. Not fixed**, here or upstream. It explains the hangs of findings
[100](100-orphaned-transport-read-holds-dac-lock.md), [103](103-terminate-waits-on-error-write.md) and
[104](104-crash-boundary.md).

## The question

Findings 100, 103 and 104 each record mscordbi's Win32 event thread waiting forever inside
`DbgTransportSession::SendRequestMessageAndWait`, for a target that is gone. That function also waits on the
target's process handle, which should wake it when the target dies (read below). So why does it not wake?

## What the source says (release/10.0)

- **The wait includes the target's death.** On the debugger side, `SendRequestMessageAndWait`
  (`shared/dbgtransportsession.cpp`, ~738) waits with no timeout on `{ hReplyEvent, m_hProcessExited }`. On the
  second it returns `CORDBG_E_PROCESS_TERMINATED`. (When the transport thread has already taken the message, it then
  waits on the reply alone; the recorded stacks are in the two-handle wait, so that branch is not the one seen.)
- **That handle is a PAL process handle.** `m_hProcessExited` is a duplicate of the handle that
  `DbgTransportTarget::GetTransportForProcess` (`di/dbgtransportmanager.cpp`) gets from `OpenProcess`.
- **A PAL process handle is checked once when a wait starts, then only by monitoring.**
  `CSynchWaitController::CanThreadWaitWithoutBlocking` (`pal/src/synchmgr/synchcontrollers.cpp`) checks the process
  with `HasProcessExited` before blocking; if it is alive the thread blocks, and `RegisterWaitingThread` hands the
  process to `RegisterProcessForMonitoring`. Monitoring is done by the synchronization manager's worker thread: its
  poll timeout drives `DoMonitorProcesses`, which calls `HasProcessExited` for each monitored process
  (`pal/src/synchmgr/synchmanager.cpp`). `HasProcessExited` handles a process that is not a child: `waitpid` fails
  with `ECHILD`, and `kill(pid, 0)` failing with `ESRCH` means it has exited.
- **mscordbi's PAL has no worker thread.** `DbgDllMain` (`debug/di/cordb.cpp`) calls `PAL_InitializeDLL()`. In
  `pal/inc/pal.h`, `PAL_INITIALIZE_DLL` is `PAL_INITIALIZE_NONE`, commented "don't start any of the helper
  threads"; only `PAL_INITIALIZE` includes `PAL_INITIALIZE_SYNC_THREAD`. mscordbi links its PAL from
  `libmscordaccore` (the DAC), which exports `PAL_InitializeDLL` and `PAL_RegisterModule` and no other initializer
  (`nm` on every runtime installed on omega, 6.0.10 to 11.0 preview 5; the hung processes loaded 10.0.9).

## What was observed

- **The hung waits are the two-handle wait.** The stacks of finding 100 (`macos-x64-host-14374-write-excerpt.txt`)
  and finding 103 show the event thread in `SendRequestMessageAndWait` → `WaitForMultipleObjectsEx` →
  `InternalWaitForMultipleObjectsEx` → `BlockThread` in `libmscordaccore`.
- **No worker thread in the debugger's PAL.** omega, 2026-11-06 11:32–12:27 UTC: probe 100's `measure-*` modes left
  16 hung probe children. Each was sampled with `sample` once it had lived 45 s (a usable run ends in about 15 s).
  In all 16, the event thread was in a transport request, and no thread ran
  `CPalSynchronizationManager::WorkerThread` in `libmscordaccore`. The host runtime's own PAL (`libcoreclr`) had its
  worker, in `ReadBytesFromProcessPipe` → `kevent`, in all 16. That is a different PAL with its own handles.
- **The same on Linux.** omega-ubuntu (Ubuntu 26.04.1 arm64), 2026-11-06 12:30–13:30 UTC, the same modes 10 runs
  each with two builds of runtime 10.0.14 (Ubuntu's package and Microsoft's): 12 of 80 measured runs hung. A
  watcher read the thread names (`/proc/<pid>/task/*/comm`) of each hung probe child once it had lived 45 s. All 12
  had exactly one `.NET SynchManag` thread, a PAL worker. Each PAL names its worker the same, and the host runtime's
  PAL starts one, so one thread means none in the DAC's PAL; the names alone do not say which library owns it.
  No `ProcessExited` stop came within 30 s in any of the 12, and no next attach finished within 120 s; in the 68
  usable runs `ProcessExited` came 17 ms before to 25 ms after the probe saw the target gone.
- **The death is never noticed afterwards.** In the same 16 runs no `ProcessExited` stop came within 30 s of the end,
  and the next attach in the process was still pending 120 s after it began. In the 24 runs of the same modes whose
  debugger stayed usable, `ProcessExited` came 1 ms before to 34 ms after the probe saw the target gone.

- **Which requests.** In the 16 omega samples the waiting threads were in `DbgTransportSession::ReadMemory` in 11,
  `WriteMemory` in 3 and both in 2 ([summary](105-dll-pal-never-rechecks-a-waited-process/omega-hung-samples-summary.txt),
  with one full sample beside it). On omega-ubuntu only the thread names were read
  ([`omega-ubuntu-hung-threads/`](105-dll-pal-never-rechecks-a-waited-process/omega-ubuntu-hung-threads/)).
- **On Linux x64, with named stacks.** zbook (bare metal, Ubuntu 26.04.1 x64, Microsoft's runtime 10.0.14),
  2026-11-06 15:25–16:00 UTC. ptrace_scope was 1, so the probe ran as a child of gdb (`gdb-holder.py`), which may
  attach to its descendants; it sampled every probe child at 45 s (`thread apply all bt`, shared libraries, thread
  names) and then detached. 5 children hung: 4 of the 60 runs of the second batch ([finding 104](104-crash-boundary.md))
  and 1 of 15 in a rerun without apport. As a control it sampled the 3 children of a short `measure-crash` run at 6 s,
  after the target's death, all of which stayed usable. The runtime's libraries are stripped; the frames were named
  afterwards with Microsoft's symbol files for the same build ids (`dotnet-symbol`), and the offsets are the same in
  every sample ([`zbook-linux-x64/stack-symbolized.txt`](105-dll-pal-never-rechecks-a-waited-process/zbook-linux-x64/stack-symbolized.txt),
  [`stack-summary.txt`](105-dll-pal-never-rechecks-a-waited-process/zbook-linux-x64/stack-summary.txt)).
  - **The one worker belongs to the host runtime.** In all 8 samples, hung and control, the single `.NET SynchManag`
    thread was `libcoreclr`'s, in `CPalSynchronizationManager::ReadCmdFromProcessPipe` → `ReadBytesFromProcessPipe`
    → `poll`, as on omega. `libmscordaccore` contains `CPalSynchronizationManager::WorkerThread` and `StartWorker`,
    but no thread ran them. So the debugger's PAL had no worker, in the hung runs and in the usable ones, as the
    mechanism predicts.
  - **The Win32 event thread waits forever on a write.** In each hung child, mscordbi's `CordbWin32EventThread` of the
    first session was in `pthread_cond_wait` with no deadline: `Win32EventLoop` → `ShimProcess::HandleWin32DebugEvent`
    → `CordbProcess::Filter` → `HandleRCEvent` → `RawDispatchEvent` → (a frame addr2line places in
    `ShimProxyCallback::CreateThread`) → the DAC's `DacDbiInterfaceImpl::RequestSyncAtEvent` →
    `DbgTransportSession::WriteMemory` → `SendRequestMessageAndWait` (`dbgtransportsession.cpp:743`, the two-handle
    wait) → `WaitForMultipleObjectsEx` → `InternalWaitForMultipleObjectsEx` → `BlockThread` → `ThreadNativeWait`.
    The same frames in all 5, after a native fault and after `abort()` alike: the debugger was writing into the target its
    request to synchronize at the next event (the flooding target's thread events) when the target died. On omega the hung threads were
    in `ReadMemory` in 11 of 16; on zbook all 5 were this write. In the controls the Win32 event thread had ended,
    and the only first-session mscordbi thread left was `CordbRCEventThread::ThreadProc`, in a wait with a timeout.
  - **The second attach waits on `g_dacMutex`.** In the 4 hung children whose second attach had begun, that attach's
    Win32 event thread was in `CordbWin32EventThread::AttachProcess` → `CordbProcess::OpenVirtualProcess` →
    `CordbProcess::Init` → `CordbProcess::ForceDacFlush` → the DAC's `DacDbiInterfaceImpl::FlushCache` → `DDHolder`,
    in `pthread_mutex_lock` on `g_dacMutex` (`libmscordaccore`, `.bss`), held by the first session's event thread
    inside `RequestSyncAtEvent`. The caller of the attach waited on it in
    `CordbWin32EventThread::SendDebugActiveProcessEvent` → `WaitForSingleObject`. That is finding 100's macOS chain
    (`Init` → `ForceDacFlush` → `FlushCache`, waiting on the DAC's mutex), on Linux.
  - **The transport thread was alive and retrying:** the first session's `DbgTransportSession::TransportWorker` was in
    the PAL's `Sleep` (a wait with a timeout), where finding 100's macOS stack had it in `TwoWayPipe::Connect` →
    `open`.
- **Windows, which has no PAL, never hung.** omega-windows (Windows 11 arm64), the same probe, 130 runs including
  70 crashes continued past their stop: the debugger was usable every time, and `ProcessExited` came every time, 13
  to 172 ms after the probe saw the target gone ([finding 104](104-crash-boundary.md)). There mscordbi reads the
  target's memory directly, with no transport request to wait on, and a process handle is a kernel object that
  signals at the death. This is the outcome the mechanism predicts; it does not show the mechanism.
- **A launched target, the debugger's own child, hangs the same way** (2026-11-07, [finding 104](104-crash-boundary.md)).
  - On the three Unix machines, 26 of 120 launched runs hung and 32 of 120 attached runs in the same batch. On
    omega-windows, none of 80 hung.
  - zbook sampled all 10 hung children with gdb and named their frames as on 2026-11-06
    ([`zbook-linux-x64-launched/`](105-dll-pal-never-rechecks-a-waited-process/zbook-linux-x64-launched/)).
  - The stacks are the same as before. The Win32 event thread waits in `RequestSyncAtEvent` → `WriteMemory` →
    `SendRequestMessageAndWait`; the second attach waits on `g_dacMutex`; the transport thread is in `Sleep`.
  - No thread is in `waitpid` or `waitid`. The waited child is not where the hang is.
  - 4 of the 7 launched hangs entered through `ShimProxyCallback::ExitThread`, the rest through `CreateThread`, and
    both lead to the same `RequestSyncAtEvent`.

## Mechanism (inferred from the above)

A thread of mscordbi that starts waiting while the target is alive is woken only by the reply or by monitoring, and
mscordbi's PAL does no monitoring. So when the target dies with a request in flight, nothing wakes the requester:
its wait has no timeout, and the death that should end it is checked only when a wait starts. In the Win32 event
thread this usually also holds the DAC lock (finding 100), so every later attach in the process hangs; on the macOS
CI runners it did in 20 of 22 runs with a stuck event thread, and in 2 the next attach still worked (finding 104,
which request is not recorded); in `Terminate` it
holds the teardown (finding 103). A death between requests is noticed, because the next wait that starts checks the
process first: that is the usable case, and why a kill while the target is synchronized, when no request is in
flight, was clean (finding 104).

This makes the hang a race between the target's death and an outstanding request, whatever ends the target: a
SIGKILL (finding 100), FailFast, a stack overflow, a native fault or `abort()` (finding 104). It also predicts the
hang for a launched target, the debugger's own child, since monitoring is what is missing, not the means of checking.
Tested 2026-11-07: it held (above).

## Not established

- That no other path in mscordbi re-checks a waited process (only the paths above were read).
- A minimal reproduction outside DrHook (a request held in flight while the target is killed).
- The threads of a usable run's process on macOS (on Linux x64: 3 samples, no worker in the DAC's PAL, as predicted).
- Linux arm64's stacks (omega-ubuntu: thread names only).
- Which request each hung run on omega-ubuntu was in. On zbook: all 5 in `WriteMemory` from `RequestSyncAtEvent`.
- Why, in 2 macOS arm64 CI runs, a stuck event thread did not block the next attach.

## For ADR-016

- There is no clean recovery inside the process: nothing exported lets DrHook start the PAL's worker, and the
  blocked thread cannot be woken from outside. That supports a worker process (Step 5) for the crashes DrHook cannot
  avoid.
- Avoidance follows from the mechanism: end a session only when no request is in flight (synchronized), as at the
  unhandled-exception stop.
- Detection follows too: once the target is gone, a `ProcessExited` stop either comes within milliseconds (at most
  175 ms, on the macOS CI runners) or not at all. Its absence marks the stuck event thread, not always a blocked DAC.
- Upstream: the right fix is in the runtime, a debugger-side PAL that monitors waited processes, or a transport wait
  that re-checks the target. Worth reporting with this finding's references.
