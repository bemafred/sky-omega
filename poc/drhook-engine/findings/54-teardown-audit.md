# Finding 54: Teardown audit — DebugSession / CallbackPump / ManagedCallbackHost / DbgShim Dispose paths

**Status:**   Audit (Phase 1b of [ADR-007](../../../docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md)).
Walks every Dispose path end-to-end across eight session states and produces the reproducer
matrix for ADR-007 Probes 42–45. Resolves the ICorDebug detach drainage contract from
[finding 53](53-threading-memory-model-audit.md) into two distinct sub-contracts (queued-callback-flush
and exit-work-item) and maps the engine's current evidence against each.
**Date:**     2026-05-23
**Method:**   For each session state {running, stopped, eval, pause, child-exit, attach-mid-flight,
concurrent-Dispose, kill-coincident}, walk `DebugSession.Dispose` step-by-step against the running
substrate state and identify what is safe by contract, what is racy, and what depends on an
external contract whose evidence-state is summarized at the end. Cross-references finding 53's
field-level race-window register; this audit's units are *Dispose-path scenarios*, not fields.

## Historical evidence — the two limit docs synthesized

[`docs/limits/drhook-clean-detach.md`](../../../docs/limits/drhook-clean-detach.md) (**Resolved** 2026-05-21):

mscordbi's RC event thread races `Detach` while flushing **queued-callback backlog** under load. Crash signature:
`CordbRCEventThread::ThreadProc → FlushQueuedEvents → DispatchRCEvent → ShimProxyCallback::CreateThread::Dispatch → EXC_BAD_ACCESS`.
Resolved by `Quiesce()` (`ICorDebugController.Stop(0)`) before `Detach()` — `Stop` blocks until dispatch is quiet. Probe 08: 3/3 clean under continuous flood. The minimal fix sufficed; the proposed `SetAllThreadsDebugState(SUSPEND)` + `HasQueuedCallbacks` drain loop was NOT needed on CoreCLR 10 / macOS-arm64.

[`docs/limits/drhook-detach-exit-race.md`](../../../docs/limits/drhook-detach-exit-race.md) (**Triggered**):

mscordbi's RC event thread races `Detach` while processing the **exit work item** when target exits coincident with Dispose. Crash signature:
`CordbRCEventThread::ThreadProc → ExitProcessWorkItem::Do → EXC_BAD_ACCESS, garbage address`.
Quiesce does **not** cover this — Stop drains queued callbacks but the exit work item is a distinct dispatch path that fires when the debuggee dies around detach time. Probe 12 (active breakpoint + dispose-then-kill): intermittent segfault. Probe 12 with kill-first teardown: 6/6 clean. The stopped-at-breakpoint state widens the race window (probe 09 with no breakpoints was 3/3 clean dispose-then-kill).

**Synthesis:** the ICorDebug detach drainage contract is actually **two contracts**:

- **C-DRAIN-CB:** Detach is safe with respect to queued callbacks iff Stop precedes Detach. *(Validated, in code today.)*
- **C-DRAIN-EXIT:** Detach is safe with respect to the exit work item iff the target has already exited cleanly before Detach starts (kill-first / wait-for-ExitProcess), OR the target is left running through Detach (no exit work item fires). *(Partially mitigated for Launched sessions; uncovered for Attached sessions.)*

Both contracts must hold for clean teardown; the engine today has one robust, one not.

## The Dispose paths walked (mechanical reference)

For per-scenario reasoning each path is reduced to its ordered side-effects. Code references are line numbers in the current substrate.

### DebugSession.Dispose (DebugSession.cs:847–886)

```
gate _disposed                                      // DS-1a if concurrent
_pump.Dispose()                                     // see below — CP-1 inside
Quiesce() = controller.Stop(0)                      // C-DRAIN-CB enforcement
Detach() = controller.Detach() if not _detached    // gated on _detached
_cordbg.Terminate()                                 // releases ICorDebug
foreach _breakpoints: release Breakpoint/Function/Module nints
_breakpoints.Clear()
foreach _symbols.Values: reader?.Dispose()
_symbols.Clear()
if _pProcess != 0: Marshal.Release(_pProcess); _pProcess = 0
if _pUnknown != 0: Marshal.Release(_pUnknown); _pUnknown = 0
_callback.Dispose()                                 // MCH-1 hazard if any callback still in flight
_dbgShim.Dispose()                                  // DBG-D if concurrent
GC.KeepAlive(_cordbg); GC.KeepAlive(_controller)
```

### CallbackPump.Dispose (CallbackPump.cs:186–196)

```
gate _disposed                                      // CP-1 if concurrent
_events.CompleteAdding()                            // future OnCallback Adds throw → caught
_resume.CompleteAdding()                            // parked Take throws → worker breaks foreach
_worker?.Join(TimeSpan.FromSeconds(2))              // 2s timeout — TR-2 hazard if worker is in pauseHandler
_events.Dispose()
_resume.Dispose()
_stops.Dispose()
```

### ManagedCallbackHost.Dispose (Interop/ManagedCallbackHost.cs:135–143)

```
if _block != 0: NativeMemory.Free(_block); _block = 0
if _v1 != 0: NativeMemory.Free(_v1); _v1 = 0
... (v2, v3, v4) ...
if _self.IsAllocated: _self.Free()
```

Idempotent ✓ (every step is `if not-already-freed`). But it does NOT defend against a callback being in-flight at the moment of Free — that's the MCH-1 hazard from finding 53, gated by C-DRAIN-CB and C-DRAIN-EXIT both holding.

### DbgShim.Dispose (Interop/DbgShim.cs:301–305)

```
if _lib != 0: NativeLibrary.Free(_lib)
// NOTE: _lib is NOT zeroed after Free
```

**Bug — DBG-D:** the field is not zeroed after `Free`. A second `Dispose` call on the same instance re-enters the branch and double-frees. Single-thread Dispose is unaffected (no second call from the same thread); the race window is concurrent-Dispose (T7).

### EngineSteppingSession.CleanupSession (EngineSteppingSession.cs:584–607)

```
if _session is not null:
    if _launchedProcess is not null:
        try: _launchedProcess.Kill(entireProcessTree: true) if not HasExited
        catch: swallow
        _launchedProcess.Dispose()
        _launchedProcess = null
    try: _session.Dispose()
    catch: swallow
    _session = null
_lineBreakpoints.Clear()
_functionBreakpoints.Clear()
_exceptionFilters.Clear()
_stepCount = 0; _targetPid = 0; _targetVersion = "unknown"; _sessionHypothesis = ""
```

**Kill precedes Dispose** for launched sessions — implementing the validated mitigation from `drhook-detach-exit-race`. **Attached sessions have no kill step** — the exit-race is exposed for them.

## Scenario-by-scenario teardown analysis

State at the moment `DebugSession.Dispose` enters. Each scenario walks the Dispose path identifying hazards. Race-window tags refer to finding 53; new hazard tags introduced here are TR-* (teardown).

### T1 — Dispose while RUNNING (no stop pending, no eval, no pause)

**Substrate state:** debuggee executing managed code; mscordbi may fire any informational/stopping callback at any moment. CallbackPump worker is parked at `_events.GetConsumingEnumerable`'s `MoveNext` (no event pending).

**Walk:**

1. `_disposed` gate passes (single-Dispose contract upheld).
2. `_pump.Dispose()`:
   - `CompleteAdding(_events)` — atomic. Future `OnCallback` invocations from mscordbi see a completed collection: `Add` throws `InvalidOperationException`, caught and dropped at CallbackPump.cs:55. **Safe by BlockingCollection contract.**
   - `CompleteAdding(_resume)` — affects nothing in T1 (worker is not parked at `_resume.Take`).
   - `_worker?.Join(2s)` — worker exits `foreach` as `MoveNext` returns false (CompleteAdding propagated). Joins in <100ms typical. ✓
   - `_events.Dispose()` and the other two: any mscordbi callback now arriving sees `ObjectDisposedException` instead of `InvalidOperationException` — same catch handles it. ✓
3. `Quiesce()` → `controller.Stop(0)`. **C-DRAIN-CB enforcement.** Synchronizes the debuggee; blocks until mscordbi's queued-callback dispatch is quiet. Probe 08 validated this 3/3 under flood load.
4. `Detach()` → `controller.Detach()`. **C-DRAIN-EXIT exposure:** if the target is about to exit (e.g., target crashes between Quiesce-stopping-it and Detach-resuming-it, or external SIGKILL coincident), mscordbi's exit work item fires during Detach. The drhook-detach-exit-race window. Otherwise clean.
5. `_cordbg.Terminate()` — releases ICorDebug. No further callbacks should fire from this point. The MCH-1 hazard for step 8 (below) depends on this guarantee holding.
6. Release breakpoint nints (4 per entry × N entries) — pure refcount drops. Safe.
7. Dispose SymbolReaders, release `_pProcess` / `_pUnknown` — refcount drops. Safe.
8. `_callback.Dispose()` — `NativeMemory.Free` the CCW block and the 4 vtable allocs. **MCH-1 hazard:** if Terminate did not fully drain pending mscordbi callbacks, a late callback dereferences freed memory. The two limit docs both target this same root cause: the RC event thread can have queued OR exit-work-item dispatch in flight that Terminate doesn't synchronously join.
9. `_dbgShim.Dispose()` — `NativeLibrary.Free`. Safe in T1 (single-thread Dispose).

**Hazard summary T1:** step 4 (drhook-detach-exit-race window — narrow, only if target exits coincident); step 8 (MCH-1 — depends on Terminate's drainage guarantee). Otherwise clean.

### T2 — Dispose while STOPPED (at breakpoint, step complete, exception)

**Substrate state:** debuggee synchronized at a stop; worker parked at `_resume.Take()` inside the foreach body (line 153 or 167). `_stopThread` is set. The MCP request thread has consumed the stop via `WaitForStop` but has not yet called `Resume`/`StepInto`/etc.

**Walk:**

1. `_disposed` gate passes.
2. `_pump.Dispose()`:
   - `CompleteAdding(_events)`: caught by OnCallback as in T1. ✓
   - `CompleteAdding(_resume)`: **the parked `_resume.Take()` throws `InvalidOperationException`** (collection completed); the `catch (InvalidOperationException) { break; }` at line 154 or 168 catches it; worker exits `foreach`. **Critical:** the worker exits WITHOUT calling `_resumeHandler` for this stop. The debuggee remains synchronized (not resumed) when the worker exits.
   - `_worker?.Join(2s)`: quick.
3. `Quiesce()` → `controller.Stop(0)`. Already synchronized — returns harmless HRESULT (likely `S_FALSE` or success). ✓
4. `Detach()`: the debuggee is synchronized at the stop. `Detach` releases the debugger; mscordbi's documented contract has Detach internally resuming the target to release it. **The drhook-detach-exit-race observation:** *the stopped-at-breakpoint state widens the race window.* If the target's code about-to-run-on-resume causes immediate exit (e.g., a `throw` in finally on top of stack), the exit work item fires during Detach's internal resume. Probe 12 reproduced this intermittently.
5. Terminate, releases, _callback.Dispose, _dbgShim.Dispose — same MCH-1 hazard as T1.

**Hazard summary T2:** the breakpoint-stopped state widens the C-DRAIN-EXIT window beyond T1. ADR-007 Probe 44's bar (single-shot 10/10 → rate-envelope 50/sec) must validate this state, not just clean T1.

### T3 — Dispose while EVAL in flight (TryEvalStaticCall et al.)

**Substrate state:** `pump.Resume()` already issued (the eval-resume that runs managed code in the debuggee); MCP thread is blocked in `pump.WaitForStop(timeout)` awaiting EvalComplete/EvalException; worker is parked at `_events.MoveNext` waiting for the next event.

**Walk:**

1. `_disposed` gate passes.
2. `_pump.Dispose()`:
   - `CompleteAdding(_events)`: a future EvalComplete/EvalException Add throws → caught → callback dropped. **The eval's outcome is lost from the engine's perspective even if mscordbi managed to dispatch it.**
   - `CompleteAdding(_resume)`: affects nothing (worker is not parked at Take).
   - `_worker?.Join(2s)`: worker exits via MoveNext returning false. Quick.
   - Dispose collections. The MCP thread's blocked `WaitForStop` catches `ObjectDisposedException` → returns null (CallbackPump.cs:86–89). TryEvalStaticCall sees `EvalStatus.TimedOut` → finally Releases the ICorDebugEval. **The eval state in the runtime is undefined** — partly-executed code, suspended thread, etc.
3. `Quiesce()` → `controller.Stop(0)`. The eval is running managed code; Stop synchronizes the debuggee. The eval is suspended but not aborted.
4. `Detach()`. **Eval-with-pending-detach is undefined territory:** the runtime owns the eval state, not us. Detaching mid-eval likely leaves the debuggee with the eval-thread still in suspended state (no debugger to resume it). The debuggee may hang or crash on the orphaned eval.
5. Terminate, releases, etc. Same MCH-1 hazard.

**Hazard summary T3:** the runtime's eval-orphaning behavior is unknown. Worth a probe — could be benign (runtime auto-aborts orphaned evals on Detach) or could deadlock the post-detach target.

### T4 — Dispose while PAUSE REQUEST is mid-processing

**Substrate state:** caller invoked `pump.RequestPause()`; worker has dequeued the PauseRequest event and is somewhere in lines 144–155 of `Pump`:

- **T4a:** worker is calling `_pauseHandler!()` (line 149 — `controller.Stop(0)`). Synchronous user code; the worker is INSIDE this call.
- **T4b:** worker has returned from pauseHandler, called `_stops.Add(new StopInfo(StopReason.Pause))`, is now parked at `_resume.Take()` (line 153). Same shape as T2.

**Walk for T4a:**

1. `_disposed` gate passes.
2. `_pump.Dispose()`:
   - `CompleteAdding(_events)`: doesn't affect the worker (it's inside pauseHandler, not at MoveNext).
   - `CompleteAdding(_resume)`: doesn't affect the worker (it's not at Take yet).
   - `_worker?.Join(2s)`: **TR-2 hazard.** Worker is inside `controller.Stop(0)`. If Stop returns < 2s, worker continues past line 149 → publishes Pause stop → parks at Take → Take throws → worker exits → joined. ✓ If Stop hangs > 2s (e.g., debuggee is in a state where synchronization can't complete), Join returns BUT THE WORKER THREAD IS STILL ALIVE. Subsequent `_events.Dispose()` race with worker's still-pending work is undefined. The worker holds the only reference to `_events` and `_resume` from inside; if it next tries to `_stops.Add` on a disposed collection, ObjectDisposedException → uncaught — kills the worker thread with an unhandled exception. **Process-fatal in the IsBackground=true case if the background-thread-unhandled-exception goes unobserved, or surfaces via AppDomain.UnhandledException.**
3. If TR-2 didn't trip (Stop returned fast), proceeds to Quiesce → Detach → ... same as T2.

**Walk for T4b:** identical to T2 from step 2c onward.

**Hazard summary T4:** T4a's TR-2 — the 2s Join timeout is a workaround for an unknown-duration `controller.Stop`. If Stop can hang on a wedged debuggee, the worker outlives Dispose with disposed collections. Phase 1 Probe 43 (Concurrent PauseRequest + STOPPING callback) is adjacent; this sub-case needs an explicit T4a reproducer to confirm or refute hang behavior of Stop.

### T5 — Dispose during CHILD-PROCESS EXIT in flight

**Substrate state:** mscordbi RC event thread is processing `ExitProcessWorkItem::Do`. The ExitProcess callback may or may not have been dispatched to our vtable yet.

**Sub-cases:**

- **T5a:** ExitProcess callback already dispatched to OnCallback BEFORE Dispose entered. Worker dequeued it, surfaced `_userSink.OnEvent("ExitProcess")`, added `StopReason.ProcessExited` to `_stops`, called `_resumeHandler!(ResumeKind.Continue, 0)` which ran `Stepping.Arm(0, Continue)` (no-op) and `controller.Continue(0)` (HRESULT on exited process is implementation-defined). Worker is back at MoveNext. THEN Dispose runs: behaves as T1 with the additional concern that step 4's Detach happens against an already-exited target.
- **T5b:** ExitProcess callback has NOT yet been dispatched; mscordbi is mid-ExitProcessWorkItem when Dispose starts. **This is exactly the drhook-detach-exit-race trigger condition.** CompleteAdding catches any incoming OnCallback. Worker exits. Quiesce attempts Stop on exiting process — HRESULT impl-defined. Detach races mscordbi's still-running ExitProcessWorkItem → segfault.

**Hazard summary T5:** T5b is the original drhook-detach-exit-race. Mitigations are (per limit doc): kill-first (validated), detach-and-leave-running for unstopped target (proposed), RC-thread join handshake (no API). Probe 44 designs the engine-side resolution. T5a is essentially safe — the exit was already observed.

### T6 — Dispose during ATTACH MID-FLIGHT (FromCordbg's catch block)

**Substrate state:** `FromCordbg` (DebugSession.cs:119–156) reached line 130's `SetManagedHandler` successfully but failed at line 132's `DebugActiveProcess` (e.g., target died between cordbg.Initialize and DebugActiveProcess). The catch block at lines 150–155 runs `pump?.Dispose()` then `callback?.Dispose()`. Worker has NOT been started yet (Start happens at line 140, AFTER DebugActiveProcess succeeds).

**Walk:**

1. `pump?.Dispose()`:
   - CompleteAdding _events / _resume: future Adds throw → caught.
   - `_worker?.Join`: `_worker` is null → no-op.
   - Dispose collections.
2. `callback?.Dispose()`:
   - `NativeMemory.Free(_block)` → frees the published vtable block.
   - Free `_v1`–`_v4`.
   - Free `_self` GCHandle.

**Hazard summary T6 — MCH-1 maximally exposed:** between line 130 (`SetManagedHandler`) and line 132 (`DebugActiveProcess` failure return) mscordbi may have already dispatched callbacks against our handler. cordbg.Initialize was called on line 126 — depending on ICorDebug's startup semantics, LoadAssembly/CreateAppDomain callbacks could already be queued. Each callback dereferences `_block` and the GCHandle in `_self`. If a callback is in flight at the moment `callback?.Dispose()` runs `NativeMemory.Free(_block)`, the dereference is into freed memory.

**Reproducer:** force DebugActiveProcess to fail (invalid PID, or kill the target between the `dbgShim.CreateCordbForProcess` return and DebugActiveProcess). The probe target ships with an attach-then-die child to force the timing.

**Status:** **Not covered by ADR-007 Probes 42–45.** **New probe candidate.** This is the partial-construction teardown that ADR-007 Phase 1's "audit before fix" discipline surfaces.

### T7 — CONCURRENT Dispose from two threads (DS-1a + DBG-D + MCH-D)

**Substrate state:** two threads invoke `DebugSession.Dispose` simultaneously. Per finding 53, the `_disposed`-bool gate is non-atomic.

**Walk (two threads through DebugSession.Dispose interleaved):**

1. Both pass the `_disposed` gate (CP-1 / DS-1 race).
2. Both call `_pump.Dispose()`:
   - CP-1 racy gate inside.
   - Both call CompleteAdding on _events/_resume — these are idempotent on BlockingCollection.
   - Both call `_worker?.Join(2s)` — joining an already-completed thread is harmless (returns immediately).
   - **First thread's** `_events.Dispose()` → completes the collection's disposal. **Second thread's** `_events.Dispose()` on an already-disposed collection throws `ObjectDisposedException` (the standard BCL behavior). The second thread's Dispose chain aborts here with an uncaught exception.
3. **If both threads survived to here** (e.g., the second was past `_pump.Dispose` already because Dispose isn't a critical section): both call `controller.Stop(0)`. ICorDebug's Stop is idempotent (you can call it on an already-stopped controller). Likely safe.
4. `Detach`: gated on `_detached` (also non-atomic) — both threads may pass, both call `controller.Detach`. **Double-Detach behavior is undefined per ICorDebug docs.** In practice on CoreCLR 10 / macOS, the second Detach likely returns `CORDBG_E_PROCESS_DETACHED` or similar; not necessarily fatal.
5. `_cordbg.Terminate()`: **double-Terminate is undefined** — the second call's behavior depends on whether mscordbi cleared its internal state. Likely safe but not guaranteed.
6. Foreach releases: `Marshal.Release` on a refcounted nint is gated by `if (_pProcess != 0)` checks for the main pointers, so second thread sees zero → skip. Breakpoint refs are released via the iterator on `_breakpoints`, then `_breakpoints.Clear()` — **two threads iterating + clearing simultaneously is undefined for `List<T>`.** First thread iterates and releases, second iterates and double-releases or hits InvalidOperationException from the modified-during-iteration check.
7. `_callback.Dispose()`: idempotent (each Free is gated). ✓
8. `_dbgShim.Dispose()`: **DBG-D.** The `if (_lib != 0)` check passes in both threads (_lib never zeroed). Both call `NativeLibrary.Free`. **Double-free of a native library on macOS via `dlclose` is undefined — may corrupt the linker state, may crash on subsequent loads.**

**Hazard summary T7:** seven distinct races, of which:
- CP-1, DS-1 (gates) — *engineering fix.*
- Double Detach, double Terminate — likely benign but not guaranteed.
- `_breakpoints` foreach + Clear race — corruption.
- DBG-D (double NativeLibrary.Free) — **undefined; potentially fatal.**

**Resolution:** all races resolved by a single change — replace the `_disposed`-bool gates in DebugSession AND CallbackPump with `Interlocked.Exchange(ref _disposed, 1) != 0` early-return. Fix `_lib` zeroing in DbgShim.Dispose. ENG-CP-1 + ENG-DS-1 + ENG-DBG-D in Phase 1 alongside probe 42–45 work.

### T8 — KILL of target COINCIDENT with Dispose

**Substrate state:** for Launched sessions, `EngineSteppingSession.CleanupSession` calls `_launchedProcess.Kill(entireProcessTree: true)` BEFORE `_session.Dispose()`. The kill is asynchronous: SIGKILL → kernel reaps the target → mscordbi observes the exit → RC event thread queues ExitProcessWorkItem → eventually dispatches ExitProcess callback (or just performs the work item).

**Sub-cases:**

- **T8a (Launched):** Kill-first compresses but does not close the race window. Between `_launchedProcess.Kill` returning (kernel accepted the signal) and `_session.Dispose` starting, the kernel may or may not have completed the reap, mscordbi may or may not have noticed. Probe 12's 6/6 clean validates that the window is narrow enough on macOS for the test rate; ADR-007 Probe 44's rate-envelope (50/sec sustained) is the real validation.
- **T8b (Attached):** `CleanupSession` does NOT kill — there is no `_launchedProcess`. The user externally kills the target. `step_stop` is invoked, `CleanupSession` skips the kill block, calls `_session.Dispose()` directly. If the user's kill was a microsecond ago, T5b's exact race is exposed.

**Hazard summary T8:** Launched sessions have one validated mitigation (kill-first); Attached sessions are exposed to the full drhook-detach-exit-race window. Probe 44 must cover both — and the engine resolution likely requires *both* kill-first (for Launched) AND detach-and-leave-running (for Attached, when the user doesn't intend to terminate the target).

## Reproducer matrix

Each scenario, the probe that owns its resolution, and the reproducer shape.

| # | Scenario | State at Dispose | Reproducer shape | Owns |
|---|---|---|---|---|
| T1 | Running, no stop | Worker at `_events.MoveNext` | Attach to long-running target, no breakpoints, Dispose | Probe 44b (detach-leave-running clean case) |
| T2 | Stopped at breakpoint | Worker at `_resume.Take()` post-stop | Attach, set bp, hit bp, Dispose without Resume | Probe 44 (the original drhook-detach-exit-race target) |
| T3 | Eval in flight | Worker at MoveNext post-eval-Resume | Attach, hit bp, call TryEvalStaticCall with a long-running eval, Dispose mid-eval | **New probe candidate** — *eval-during-Dispose; runtime orphaning behavior* |
| T4a | Pause mid-handler | Worker inside `controller.Stop(0)` | Attach, RequestPause, wedge the debuggee so Stop hangs, Dispose; observe worker.Join timeout | **New sub-probe under Probe 43** — *TR-2 worker-Join timeout exposure* |
| T4b | Pause stop parked | Worker at Take post-Pause stop | Same as T2 with Pause origin | Probe 43 (existing) |
| T5a | ExitProcess processed pre-Dispose | Worker at MoveNext post-exit-resume | Launch short-lived target, await ExitProcess, Dispose | Implicit in Probe 44 (safe baseline) |
| T5b | ExitProcess mid-flight at Dispose | mscordbi RC thread inside ExitProcessWorkItem | Launch, no kill, target exits at random time, Dispose race | Probe 44 (the original) |
| T6 | Attach mid-flight failure | `FromCordbg` catch after SetManagedHandler | Custom target that exits between Initialize and DebugActiveProcess; force the race | **New probe candidate** — *partial-construction teardown; MCH-1 maximally exposed* |
| T7 | Concurrent Dispose | Two threads in DebugSession.Dispose | Spawn two threads calling Dispose simultaneously | **Engineering fix** ENG-CP-1 + ENG-DS-1 + ENG-DBG-D; no probe needed (deterministic; unit test in Phase 8) |
| T8a | Kill-coincident Launched | Kill-then-Dispose, rate-envelope | NCrunch-like 50/sec attach-kill-dispose cycle | Probe 44a (rate envelope) |
| T8b | Kill-coincident Attached | External-kill-then-step-stop | User kills target, then triggers step_stop | Probe 44 — *Attached path needs explicit design (detach-leave-running OR refuse-without-target-confirmation)* |

**New probe candidates from this audit (not in ADR-007 42–45):**

1. **Probe T3-eval** — eval-during-Dispose; runtime orphaning behavior. Small probe; could fold into Probe 45 (worker exception path) since EvalComplete delivery during Dispose is the worker's failure case.
2. **Probe T6-attach** — partial-construction teardown via FromCordbg catch. Standalone; targets the MCH-1 attach-mid-flight exposure.
3. **Probe T4a-pause** — `controller.Stop(0)` hang behavior under wedged debuggee. Sub-probe of Probe 43.

The discipline rule (per ADR-007: *"A revealed unknown unknown becomes a probe; the phase doesn't proceed until the probe records a finding."*) — these three queue into Phase 1's probe sequence. Recommendation: schedule T3-eval and T6-attach explicitly; absorb T4a into Probe 43's design.

## The ICorDebug detach drainage contract — synthesized truth

Combining finding 53's external-contract list with this audit's scenarios:

**C-DRAIN-CB (queued-callback flush during Detach):**

- *Statement:* `ICorDebugController.Detach` is safe iff mscordbi's RC event thread has no queued callbacks at the moment Detach starts. Achieved by calling `ICorDebugController.Stop(0)` immediately prior.
- *Code today:* `DebugSession.Quiesce()` at line 845. Called before `Detach()` in the Dispose path.
- *Evidence:* Probe 08 (3/3 clean detach under continuous-flood load). [`drhook-clean-detach`](../../../docs/limits/drhook-clean-detach.md) Resolved.

**C-DRAIN-EXIT (exit work item during Detach):**

- *Statement:* `ICorDebugController.Detach` is safe with respect to the exit work item iff either (a) the target has already exited and the work item has completed before Detach starts, OR (b) the target is not exiting (or about to exit) at Detach time. Quiesce/Stop does NOT cover this — Stop drains queued callbacks but does not synchronize with the exit work item.
- *Code today:* For Launched sessions, `EngineSteppingSession.CleanupSession` kills the target via `_launchedProcess.Kill(entireProcessTree: true)` before invoking `Dispose`. This achieves (a) probabilistically but not deterministically (no API to confirm exit work item completion). For Attached sessions, **no mitigation in code today**.
- *Evidence:* [`drhook-detach-exit-race`](../../../docs/limits/drhook-detach-exit-race.md) Triggered; probe 12 single-shot 6/6 with kill-first but no rate-envelope evidence; Attached path has no probe coverage.
- *Resolution path (ADR-007 Probe 44):*
  - **For Launched:** keep kill-first; validate the rate envelope at 50/sec sustained.
  - **For Attached:** design detach-and-leave-running as the default for sessions the user did not intend to terminate. The engine's API needs a distinction (probably an explicit "terminate before detach" opt-in vs. the default "leave running").
  - **Cross-cutting:** investigate whether ICorDebugProcess exposes any "wait for RC thread to drain all dispatches" primitive that ICorDebug 4.0 didn't surface; this would be the substrate-clean solution if it exists.

**Both contracts must hold for `_callback.Dispose()` (step 8 of `DebugSession.Dispose`) to be MCH-1-safe.** Today C-DRAIN-CB is solid; C-DRAIN-EXIT is partially mitigated for Launched, exposed for Attached.

## Engineering fixes surfaced (Phase 1 — no probe needed)

These resolve concurrent-Dispose hazards deterministically; unit tests in Phase 8.

### ENG-CP-1 — Atomic idempotence gate in CallbackPump.Dispose

Replace the `_disposed: bool` field and `if (_disposed) return; _disposed = true;` pattern (CallbackPump.cs:188–189) with:

```csharp
private int _disposed;
public void Dispose()
{
    if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
    _events.CompleteAdding();
    // ... rest unchanged ...
}
```

Single-line semantic change; observable behavior identical for single-thread Dispose; second concurrent Dispose returns immediately.

### ENG-DS-1 — Atomic idempotence gate in DebugSession.Dispose

Same pattern at DebugSession.cs:849–850. Plus the `_detached: bool` gate at line 836 — same treatment. Plus consideration of "block until in-flight MCP requests complete" if DS-2 / ESS-1 surface as real (depends on MCP SDK serialization characterization, see finding 53).

### ENG-DBG-D — Zero `_lib` after Free in DbgShim.Dispose

```csharp
public void Dispose()
{
    if (_lib != 0)
    {
        NativeLibrary.Free(_lib);
        _lib = 0;
    }
}
```

Minimal change; closes the double-free window in T7.

### ENG-T4a-StopBudget — Bound `controller.Stop` budget OR document the hang

The 2s worker-Join timeout (CallbackPump.cs:192) is a workaround for an unknown-duration `controller.Stop(0)` call inside the pauseHandler. Either:
- (a) Add a budget to the pauseHandler itself (`Stop(timeoutMs)`?) — check ICorDebug for any timeout-aware Stop variant.
- (b) Accept the 2s Join timeout and document that the worker may outlive Dispose; ensure subsequent collection disposes have ObjectDisposedException catches in the worker too.
- (c) Probe T4a determines whether Stop can actually hang in practice; if not, the 2s Join is fine and (a)/(b) are over-engineering.

Recommendation: Probe T4a first; engineering follows the probe.

## EngineAnomaly surfaces (additions to finding 53's seed list)

Five additional sites where the substrate today swallows or undefines during teardown:

1. **`DebugSession.Quiesce` HRESULT discard** (Quiesce returns implicitly; no HRESULT inspection). With anomaly infra: record if `Stop` returns a non-success HRESULT — e.g., on an already-exiting process, Stop may return failure.
2. **`Detach` HRESULT discard** (DebugSession.cs:837 — no inspection). Same treatment.
3. **`Terminate` HRESULT discard** (line 864). Same.
4. **`Marshal.Release` HRESULT discard** for breakpoint nints and `_pProcess`/`_pUnknown`. Refcount drops returning nonzero may indicate leaks elsewhere; worth recording.
5. **`EngineSteppingSession.CleanupSession` swallows ALL exceptions** (lines 593, 597). With anomaly infra: structured record per exception type, not swallow-and-forget.

## What this finding does NOT cover

- **Per-thread stack-budget audit** — Phase 1c ([finding 55](55-stack-budget-audit.md)) records the per-platform thread stack defaults and the explicit `new Thread(…, maxStackSize)` declarations ADR-007 Phase 1 requires.
- **Probe 42–45 implementation** — this finding sequences the work and identifies new probes (T3-eval, T6-attach, T4a-pause) that join the queue; implementation is downstream.
- **MCP SDK request serialization characterization** — surfaced by finding 53; not re-litigated here; remains a Phase 1 prerequisite for DS-2 / ESS-1 fix work.
- **The ICorDebugProcess "drain all RC dispatches" API search** — recommended in C-DRAIN-EXIT resolution above; implementation is part of Probe 44's design search.

## Summary

**Eight teardown scenarios analyzed; three new probe candidates surface (T3-eval, T6-attach, T4a-pause)** — none of these are in ADR-007's 42–45 list. T6-attach is the most exposed (MCH-1 maximally hot during partial construction).

**The ICorDebug detach drainage contract splits cleanly into two:** C-DRAIN-CB is robustly enforced today (Quiesce); C-DRAIN-EXIT is partially mitigated for Launched (kill-first) and unmitigated for Attached. Probe 44's resolution must design the Attached path as well as validate the Launched rate envelope.

**Four engineering fixes (no probe required):**
- ENG-CP-1: Interlocked gate for CallbackPump.Dispose.
- ENG-DS-1: Interlocked gate for DebugSession.Dispose + `_detached`.
- ENG-DBG-D: Zero `_lib` after Free.
- ENG-T4a-StopBudget: contingent on probe.

**Five additional EngineAnomaly seed sites** — Quiesce / Detach / Terminate HRESULT discards, breakpoint-release HRESULT discards, CleanupSession blanket-catch — augment finding 53's five.

Phase 1b is complete. Phase 1c (stack-budget audit) is next; then the audit-informed probes 42–45 + the three new candidates execute, and Phase 1 closes with the resolutions consolidated into engine substrate.
