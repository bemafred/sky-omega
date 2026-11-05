# Finding 103 — Terminate waits forever on mscordbi's event thread, which is writing an error into a target that is gone (macOS)

**Date:** observed 2026-11-04 (CI); written 2026-11-05
**Status:** **OBSERVED once**, on a GitHub macOS arm64 runner, with native and managed stacks of the hung process.
Where it holds is read from those stacks and the runtime source (`release/10.0`). **Which error started it is not
identified. Not reproduced. Not fixed.** It is the first stack record of the "single timeout, then recovery" that
[finding 100](100-orphaned-transport-read-holds-dac-lock.md) set aside as a different failure of unknown cause.

## Symptom

`test.yml` run 41889846674, attempt 1, at `f4bd82e` (which includes the Detach change `7f5965e`), job macOS arm64
(`macos-latest`, macOS 26.7.2, runtime 10.0.14):

- `AttachAndOwn_VstestTestHost_DisposeDuringFlood_NoWorkerExceptionOrSilentBreak` hit the suite's 120 s limit. Its
  timeline ends at `18:44:17.043  flood started; disposing`: `DebugSession.Dispose` never returned.
- Every later test in the integration suite passed; the suite ended 15 s after the timeout with this one failure. Of
  the job's 6,660 tests, 6,491 passed, 168 were skipped, and this was the only failure.
- So it was a single timeout, not finding 100's cascade.

The other jobs:

- **Linux x64 and Linux arm64** passed.
- **macOS x64:** the integration suite passed (2 min). The watcher records any host that has run 90 s; its record
  (18:48:53) shows a test waiting normally for a stop, not a hang.
- **Windows x64, Windows arm64 and macOS x64** were cancelled at 19:01, during later tests, by a push to `main`
  (`19793c3`). The pipeline has since been changed to queue runs instead of cancelling them.
- Martin reran the four unfinished jobs on 2026-11-05 at 08:08 (attempt 2). They are not part of this finding.

The two earlier single timeouts, at 1.8.38 (macOS x64, this same test) and 1.8.39 (macOS arm64, the VSTest
PauseThenDispose test), left no stacks. Whether they had this cause is not known.

## Where it hung

`tools/watch-drhook-hang.sh` recorded the test host (pid 2116) at 18:45:24, 67 s after the timeline's last line. It
found no `testhost.dll` process, so the target had already exited. An excerpt of the record is in
[`103-terminate-waits-on-error-write/`](103-terminate-waits-on-error-write/macos-arm64-host-2116-excerpt.txt).

- **The test thread** is in `DebugSession.Dispose` → `ICorDebug.Terminate` → `Cordb::Terminate` → `Cordb::Neuter` →
  `CordbProcess::Neuter` → `ShimProcess::Dispose` → `CordbWin32EventThread::Stop` → `WaitForSingleObject`. It is
  waiting for the session's Win32 event thread to finish.
- **The Win32 event thread** (Thread_10724) is in `CordbWin32EventThread::Win32EventLoop` →
  `CordbProcess::UnrecoverableError` → `DbgTransportSession::SetDCB` → `SendRequestMessageAndWait`, waiting for an
  answer from the target.
- **The next thread created after it** (Thread_10725), presumably the same session's transport thread, is in
  `TwoWayPipe::Connect` → `open()`: reconnecting to the gone target's pipe.
- **Six more transport threads**, one per earlier session, are blocked the same way. finding 100 records the same
  pattern.
- **Missing, compared with finding 100:** no thread is inside the DAC (`DacDbiInterfaceImpl`), and no attach waits in
  `FlushCache`.

All 2,392 samples of each of these threads show the same stack.

## Mechanism (runtime source, `release/10.0`)

- `CordbWin32EventThread::Stop` (`debug/di/process.cpp`) sets `m_run = FALSE`, signals the thread's control event,
  and waits for the thread with no timeout. The event loop checks `m_run` only at the top of each turn, so `Stop`
  returns only when the current turn ends.
- `CordbProcess::UnrecoverableError` does four things, in this order:
  1. It returns at once if the process object is already neutered.
  2. It marks the process unrecoverable.
  3. It writes the error into the target's debugger control block (`m_errorHR`, `m_errorCode`, through
     `UpdateLeftSideDCBField` → `DbgTransportSession::SetDCB`).
  4. Only then does it send the `DebuggerError` callback.
- The write in step 3 is a transport request. `SendRequestMessageAndWait` waits for the reply with no timeout (finding
  100). The target is gone, so no reply comes, and the transport thread loops trying to reopen the target's pipe.
- The write is wrapped in `EX_TRY`/`EX_CATCH`, with a comment that expects "an unexpected disconnection from the
  target". But a disconnection shows up as a wait that never ends, not as an exception, so the catch never runs.
- So the event thread never returns to the top of its loop, `Stop` never returns, and neither does `Terminate`.
- `DebuggerError` is sent after the write, so DrHook never received it. Nothing reached DrHook's sink before the
  hang.

**Which error.** In `Win32EventLoop` the frame calls `UnrecoverableError` directly. Code from other functions can be
compiled into a function (inlined), so the call could come from the loop's own code or from a function it calls.

- The only call to `UnrecoverableError` written in the loop's own code is `SetUnrecoverableIfFailed(m_pProcess,
  hrShim)`. It runs after `m_pShim->HandleWin32DebugEvent(&event)` returns a failure.
- The functions the loop calls that could be inlined contain no such call: `CreateProcess`, `AttachProcess`,
  `ExitProcess` and `HandleCanDetach`.
- So the likeliest start is that the shim failed to handle a debug event as the target ended. **Not established:**
  which event it was, which HRESULT it returned, and that the frame's offset (`Win32EventLoop + 384`) is that call.
  The runner's `libmscordbi` is 10.0.14 (UUID `C1773E77-C8A7-3831-BC39-E397FC1936F6`); omega has 10.0.9, so the
  offset cannot be checked against omega's copy.

**Order.** `UnrecoverableError` returns early on a neutered process, and DrHook's `Detach` neuters it. So the event
thread must have entered `UnrecoverableError` before `Detach` ran. That fits DrHook's owned teardown, in which the
target ends before the detach:

1. The event pump is stopped.
2. The target is resumed.
3. SIGTERM, with up to 2 s for the target to exit; it is killed if it doesn't.
4. A 200 ms wait, then `Detach`.
5. `Terminate`.

`Detach` returned, since the stack is past it, but what it returned is not recorded: the test prints anomalies only
after `Dispose` returns.

## How it differs from finding 100

| | Finding 100 | Finding 103 |
|---|---|---|
| The Win32 event thread waits in | a target-memory **read** inside the DAC (`IsThreadMarkedDead` → `ReadMemory`), while dispatching an event | an error **write** to the target (`UnrecoverableError` → `SetDCB`), after a failed event |
| What else it holds | the DAC's lock, which every new session needs | no lock any other session needs |
| What hangs | the next attach or launch (`FlushCache`), and every one after it | `Terminate`, in the session's own `Dispose` |
| Seen as | a cascade: every later test times out | a single timeout; later tests pass |

What they share: in both, the Win32 event thread itself makes a transport request to a target that is gone. That
request has no timeout, and the thread making it is the one that would handle the target's exit, so nothing can ever
release it.

## Was the Detach change involved?

The run included `7f5965e`. In this path (an owned target, already gone when `Detach` runs) the change adds one
thing: a `WaitForExit` on an exited process, which returns at once. That is read from the code, not measured.
Single timeouts happened before the change (1.8.38, 1.8.39). So the change is unlikely to be the cause, but no test
rules it out.

## Consequence

- **In the test host,** the hang left three threads blocked for the rest of the process: the test thread, the event
  thread and its transport thread. MSTest gave up on the test after 120 s, and the host exited normally when the
  suite was done.
- **Later sessions** in the same process attached, ran and ended normally.
- **In `drhook-mcp`,** the request that disposes such a session would never return. Later sessions should still work,
  as later tests did here. That is not tested.

## What this means for ADR-016

- **`DebuggerError` cannot be the only sign that a debugger is unusable.** ADR-016's API table plans to treat
  `DebuggerError` as "debugging services are disabled". Here the debugger reached that state, and the callback was
  never sent, because the write that comes before it never finished.
- **A second native call blocks forever after the target vanishes.** ADR-016 Step 4 asks "whether a native debugger
  operation stays blocked after the target vanishes". This is a second such operation, `Terminate`, at a different
  place from finding 100's.
- **The rule the ADR cites, seen in practice.** The documentation says `ICorDebug::Terminate` should not be called
  before `ExitProcess` has arrived for every process. Whether `ExitProcess` arrived here is not recorded.

## Next

1. **Find the trigger.** Disassemble `libmscordbi` 10.0.14 for osx-arm64 at `Win32EventLoop + 384`, to confirm the
   call site. This needs that runtime on omega, which is a download.
   - The HRESULT is held in the hung process's memory (`m_errorHR`, set before the write). A core dump of the host
     taken by the watcher would hold it. The watcher takes none today.
2. **Reproduce it.** Probe 100's flood-kill mode samples until the event thread is inside `ReadMemory`. A variant
   would sample for `UnrecoverableError` instead, or would run the VSTest flood test in a loop until it hangs
   ([ADR-015](../../../docs/adrs/drhook/ADR-015-run-until-anomaly.md)). Neither has been run.
3. **Report upstream together with finding 100.** It is the same kind of problem: a request with no timeout made by
   the thread that would handle the exit, here on an error path whose catch expects an exception that never comes.
