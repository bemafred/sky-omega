# Finding 53: Threading + memory-model invariant audit — DrHook.Engine + EngineSteppingSession

**Status:**   Audit (Phase 1a of [ADR-007](../../../docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md)).
The substrate prerequisite for everything downstream in Phase 1: a substrate with intermittent
teardown bugs makes every subsequent test ambiguous. This finding records the cross-thread
contracts the engine relies on, the primitives that enforce them, and the race windows still
uncovered. Output is consumed by 1b (teardown audit, [finding 54](54-teardown-audit.md))
and 1c (stack-budget audit, [finding 55](55-stack-budget-audit.md)), and by probes 42–45 plus
the new probes this audit surfaces.
**Date:**     2026-05-23
**Method:**   Bottom-up audit (every field, then writers/readers) as primary; top-down (every
thread, then what it touches) as cross-check. Inventory denominator is mechanical-grep over
`src/DrHook.Engine/` + `src/DrHook.Mcp/`. Scope includes the engine's interaction with external
threads owned by ICorDebug, libdbgshim, EventPipe, and the MCP SDK — the thread-contract surface
is where teardown races live.

## Inventory denominator (completeness proof)

The audit must cover everything in this set. Anything outside it is out of scope or surfaced by
the top-down cross-check.

**Synchronization primitives** — `grep -rn -E "(BlockingCollection|ManualResetEventSlim|ManualResetEvent|SemaphoreSlim|AutoResetEvent|CountdownEvent|Barrier|ReaderWriterLockSlim|ConcurrentDictionary|ConcurrentQueue|ConcurrentBag|ConcurrentStack|TaskCompletionSource)\b" src/DrHook.Engine/`:

- `CallbackPump.cs:30–32` — `BlockingCollection<CallbackEvent>` `_events`, `BlockingCollection<ResumeKind>` `_resume`, `BlockingCollection<StopInfo>` `_stops`.
- `Interop/DbgShim.cs:283` — `ManualResetEventSlim Signaled` in nested `StartupContext`.

**Atomic primitives** — `grep -rn -E "Interlocked\." src/DrHook.Engine/`:

- `Interop/ManagedCallbackHost.cs:45–46` — `Interlocked.Increment/Decrement(ref _refCount)`.
- `Diagnostics/StackInspector.cs:80` — `Interlocked.Increment(ref contentionCount)` (local).

**Critical sections** — `grep -rn -E "\block\s*\(|\bMonitor\.|\bSpinLock\b|\bReaderWriterLock"`:

- `BoundedLogSink.cs:42, 55, 71` — all three accesses to `_buffer`/`_dropped` are under `lock (_lock)`.

**Volatile** — `grep -rn -E "\bvolatile\b"`: zero occurrences.

**Thread-creation sites** — `grep -rn -E "(new Thread\b|Task\.Run\b|ThreadPool\.|QueueUserWorkItem)"`:

- `CallbackPump.cs:73` — `new Thread(Pump) { IsBackground = true, Name = "DrHook.CallbackPump" }`.
- `Diagnostics/StackInspector.cs:93` — `Task.Run(() => source.Process(), ct)`.

**External thread entries** — top-down identification of threads we do NOT create that enter our code:

- `mscordbi event thread` (ICorDebug-owned) — enters via `ManagedCallbackHost`'s `[UnmanagedCallersOnly]` vtable thunks (38 callback methods, lines 168–209) → `_sink.OnCallback` → `CallbackPump.OnCallback` (line 49).
- `libdbgshim startup thread` (dbgshim-owned, one-shot per Launch) — enters via `DbgShim.StartupCallbackThunk` (line 287) once `RegisterForRuntimeStartup`'s registered callback fires.
- `EventPipe processing thread` (TraceEvent-owned, one-shot per `StackInspector.CaptureAsync`) — runs inside `Task.Run(() => source.Process(), ct)`, dispatches event-handler delegates synchronously on that thread.
- `MCP request thread(s)` (MCP SDK-owned) — enter via `EngineSteppingSession`'s async methods, which call into `DebugSession`'s public API.

**Static fields** — narrowed from `grep -rn -E "\bstatic\s+(readonly\s+)?"`: every match is either a Guid constant, a static class with no mutable state, a static-readonly immutable singleton (`DebugSession.Wrappers = StrategyBasedComWrappers`; `StackInspector.DefaultProviders = IReadOnlyList<EventPipeProvider>`), or a JsonSerializerOptions cache. **No mutable static state** in `DrHook.Engine`. `DrHook.Mcp.EngineSteppingSession.Indented` is also immutable. Safe by construction; no further audit required.

**DI singletons** — `grep -rn -E "(AddSingleton|AddScoped|AddTransient|AddHostedService)" src/DrHook.Mcp/`:

- `Program.cs:77` — `AddSingleton<EngineSteppingSession>()`. Implication: one `EngineSteppingSession` instance for the entire MCP server lifetime. Concurrent MCP requests all share its instance state.

That is the denominator. Eight classes hold cross-thread state; the remainder are static-state-free or single-thread-by-construction.

## Thread graph

Five threads can be live simultaneously during a typical session:

```
                                                                       mscordbi event thread
                                                                       (external, ICorDebug-owned)
                                                                                │ vtable callback
                                                                                ▼
   MCP request thread(s)                                              ManagedCallbackHost (CCW)
   (external, MCP SDK-owned)        ┌─────────────────────────────┐         _sink.OnCallback
            │                       │                             │              │
            │  Launch/RunAsync ─────│  EngineSteppingSession      │              ▼
            │  (one-shot setup)     │  (singleton — shared)       │       CallbackPump.OnCallback
            │                       │                             │              │ _events.Add
            │  Step/Resume/Pause ───┤   _session: DebugSession?   │              ▼
            ├───────────────────────│                             │       ┌──────────────────┐
            │  Set/RemoveBreakpoint │                             │       │   _events queue  │
            │                       └──────────┬──────────────────┘       └────────┬─────────┘
            │                                  │                                   │
            │                       (calls into)│                                   │ Take
            │                                  ▼                                   ▼
            │                     ┌─────────────────────────────┐    ┌──────────────────────────┐
            │                     │  DebugSession               │    │  CallbackPump.Pump       │
            │                     │   - mutable lists/dicts     │    │  (worker thread —        │
            │                     │   - StopThread (read)       │    │   DrHook.CallbackPump)   │
            │                     │   - WaitForStop  ───────────┼────┤   - reads _events        │
            │                     │   - Resume       ───────────┼────┤   - writes _stops        │
            │                     └─────────────────────────────┘    │   - calls _resumeHandler │
                                                                     │     (= Stepping.Arm +    │
                                                                     │      controller.Continue)│
                                                                     └──────────┬───────────────┘
                                                                                │
                                                                                ▼
                                                                     ICorDebugController
                                                                     (sole caller invariant —
                                                                      only the pump worker)


   libdbgshim startup thread (external, one-shot per Launch)
            │
            │ fires StartupCallbackThunk once the runtime has initialized
            ▼
   DbgShim.StartupCallbackThunk → StartupContext.{PCordb, HResult, Signaled.Set()}
            │
            │ ManualResetEventSlim release barrier
            ▼
   DbgShim.LaunchWithDebugger (waiter thread = MCP request that called Launch)


   EventPipe processing thread (external, one-shot per CaptureAsync)
            │
            │ inside Task.Run(() => source.Process()), delivers events sequentially
            ▼
   StackInspector handler closures (Dictionary<int,int>, List<…>, int contentionCount)
            │
            │ closes the EventPipe session; awaiter thread reads aggregated state
            ▼
   StackInspector.CaptureAsync (after `await processTask`)
```

Five threads, three external-into-our-code entry points (`ManagedCallbackHost` vtable, `DbgShim.StartupCallbackThunk`, EventPipe handler delegates), one internal worker (`CallbackPump.Pump`), one or more MCP request threads.

## Per-field record

Per-field shape: **(class)** — `field: type` — writer thread(s) → reader thread(s); declared contract; enforcing primitive; race-window status.

### CallbackPump (instance fields)

The continue-loop core. Owns the queue-rendezvous between mscordbi, the worker, and the MCP caller.

- **`_events: BlockingCollection<CallbackEvent>`** — *readonly, ctor-init.* mscordbi event thread WRITES (`Add` from `OnCallback`); pump worker thread READS (`GetConsumingEnumerable` in `Pump`). Contract: producer-consumer queue with happens-before via the BCL primitive. Primitive: `BlockingCollection<T>` over `ConcurrentQueue<T>` — published memory writes before `Add` are visible after the matching `Take`. **Safe.**

- **`_resume: BlockingCollection<ResumeKind>`** — *readonly, ctor-init.* MCP request thread WRITES (via `Resume`/`StepInto`/`StepOver`/`StepOut`, all funnel through `Enqueue` → `_resume.Add`); pump worker thread READS (`_resume.Take()` after publishing a stop). Same primitive contract. **Safe.**

- **`_stops: BlockingCollection<StopInfo>`** — *readonly, ctor-init.* Pump worker thread WRITES (`Add` from `Pump`); MCP request thread READS (`TryTake` from `WaitForStop`). Same primitive contract. **Safe.**

- **`_userSink: IDebugEventSink`** — *readonly, ctor-init.* Pump worker thread READS only (calls `OnEvent`/`OnLog`). The interface contract ([IDebugEventSink.cs:18–23](../../src/DrHook.Engine/IDebugEventSink.cs)) explicitly states `OnLog` may be called from the MCP thread (via `WaitForPolicyStop`), so implementations holding both `OnEvent` and `OnLog` must be thread-safe. `BoundedLogSink` honors this via `lock(_lock)`. **Safe — contract documented and honored.**

- **`_resumeHandler: Func<ResumeKind, nint, int>?`**, **`_pauseHandler: Action?`** — *non-readonly, nullable.* MCP request thread (the one that called `Attach`/`Launch`) WRITES once in `Start` BEFORE invoking `_worker.Start()`; pump worker thread READS via `_resumeHandler!`/`_pauseHandler!`. Contract: set-exactly-once before publication; never reset. Primitive: `Thread.Start` is a publication barrier per the .NET memory model (operations before `Start` happen-before operations on the started thread). **Safe by Thread.Start publication semantics. Contract: never re-assign after Start.**

- **`_worker: Thread?`** — *non-readonly, nullable.* Same writer/reader and contract as `_resumeHandler`. Read again in `Dispose` (line 192) for `Join`. **Safe — single writer, later same-or-cooperating thread reader.**

- **`_stopThread: nint`** — *non-readonly.* Pump worker WRITES (lines 150, 162) BEFORE `_stops.Add`; MCP request thread READS via `StopThread` property (line 45) AFTER `WaitForStop` returns. **The contract is documented in the XML doc at line 42:** *"Set by the worker before it publishes the stop; visible to a caller after WaitForStop returns (the stop-queue hand-off establishes the happens-before)."* Primitive: piggybacks on `_stops.Add`/`TryTake`'s `BlockingCollection<T>` release-acquire. **Safe by inherited primitive contract.**

- **`_disposed: bool`** — *non-readonly.* MCP request thread READS+WRITES in `Dispose` (lines 188–189). Contract: documented intent is single-call idempotence. **Primitive: NONE.** Two concurrent `Dispose` calls both pass the `if (_disposed) return;` gate; both run the disposal; the second's `_events.Dispose()` throws `ObjectDisposedException` on the first's already-disposed collection. **Race window CP-1 — uncovered.**

### DebugSession (instance fields)

The public engine surface. Most state is conceptually MCP-thread-only and serialized by the caller, but the singleton + concurrent-MCP-requests reality makes that contract load-bearing rather than incidental.

- **`_dbgShim`, `_pump`, `_callback`, `_cordbg`, `_controller`, `_sink`** — *readonly, ctor-init.* Single-thread initialization in the factory (`FromCordbg`); read by various methods on the MCP thread. Pump's reference to its own callback host is via separate field on its side. **Safe — no cross-thread writes.**

- **`_pUnknown: nint`, `_pProcess: nint`** — *non-readonly.* Set in private ctor (single-thread, before publication); reset to 0 in `Dispose` (lines 879–880). Read by many methods (`EnumerateModules`, `Resume`, `Pause`, breakpoint methods, eval methods — all read `_pProcess`). Contract: callers don't invoke methods during Dispose. **Race window DS-1 — uncovered** if Dispose runs concurrent with another MCP request mid-call.

- **`_detached: bool`** — *non-readonly.* Set true in `Detach` (line 838); idempotence gate at line 836. Read once in `Dispose` path indirectly. Contract: same as `_disposed`. **Race window DS-1 covers this.**

- **`_disposed: bool`** — *non-readonly.* Same shape as `CallbackPump._disposed`. Read+write in `Dispose` (lines 849–850). **Race window DS-1 — uncovered.**

- **`_breakpoints: List<BreakpointEntry>`** — *readonly reference; mutable contents.* MCP-only writes (via `SetBreakpoint`, `SetBreakpointAtLine`, `RemoveBreakpoint`, `ClearBreakpoints`) and MCP-only reads (via `ListBreakpoints`, `Dispose`). Contract per XML doc on each method: *"Valid only while stopped"* — does NOT explicitly say "and from a single thread." **Race window DS-2 — uncovered** if two MCP request threads concurrently call `SetBreakpoint` etc. against the singleton session. `List<T>` is not thread-safe; concurrent mutation corrupts internal state.

- **`_nextBreakpointId: int`** — *non-readonly.* Pre-incremented in `SetBreakpoint`/`SetBreakpointAtLine`. **Race window DS-2 covers this** (concurrent pre-increment loses ids).

- **`_exceptionFilters: List<ExceptionFilterInfo>`** — *readonly reference; mutable contents.* SAME shape as `_breakpoints` PLUS read on potentially different thread: `WaitForStop` walks `_exceptionFilters.Count` and calls `ExceptionMatchesAnyFilter` which `foreach`-iterates the list (line 205) — typically on the MCP thread calling `WaitForStop`. If a second MCP thread calls `ArmExceptionFilter` concurrently, the iterator throws `InvalidOperationException` or worse. **Race window DS-2 covers this — the iteration-during-mutation case is the worst sub-case.**

- **`_nextExceptionFilterId: int`** — same as `_nextBreakpointId`. **Race window DS-2.**

- **`_symbols: Dictionary<string, SymbolReader?>`** — *readonly reference; mutable contents.* Written in `SymbolsFor` (line 387) and `Dispose`; read in `SymbolsFor`, `GetStackFrames`, `GetLocals`, `SetBreakpointAtLine`, eval methods. `Dictionary<TKey,TValue>` is not thread-safe. **Race window DS-2 covers this.**

### ManagedCallbackHost (instance fields)

The hand-rolled native CCW. Its existence is the bridge between mscordbi and our pump.

- **`_sink: IManagedCallbackSink`** — *readonly, ctor-init.* mscordbi event thread READS (via `HostOf` then `_sink.OnCallback`). No cross-thread writes after construction. **Safe.**

- **`_self: GCHandle`** — *non-readonly value type.* MCP thread WRITES in `Build` (line 123) BEFORE the host is published to native (lines 126–130, `_block` is the publication step). MCP thread frees in `Dispose` (line 142). mscordbi event thread READS the GCHandle via `*((nint*)(pThis + sizeof(nint)))` in `HostOf` (line 49). **Race window MCH-1 — uncovered:** if `Dispose` runs concurrent with an in-flight callback, `GCHandle.FromIntPtr` on a freed handle is undefined behavior. The mitigation depends on ICorDebug's detach contract — see DS-1 cross-reference and finding 54 (teardown audit). The defensive `?.` chain at `HostOf?._sink.OnCallback` only catches the case where the cast returns null; it does not prevent the prior `GCHandle.FromIntPtr` from racing.

- **`_block: nint`, `_v1: nint`, `_v2: nint`, `_v3: nint`, `_v4: nint`** — *non-readonly.* MCP thread WRITES in `Build` (single-threaded before publication); MCP thread `NativeMemory.Free`s in `Dispose`. mscordbi event thread READS via `pThis` (which points into `_block` or one of `_v1`–`_v4`) every callback. **Race window MCH-1 covers this — Dispose racing with in-flight callback dereferences freed memory.**

- **`_refCount: int`** — *non-readonly.* `Interlocked.Increment`/`Decrement` on every QueryInterface/AddRef/Release call from mscordbi. Pure atomic counter; not consulted for teardown decisions (`teardown is Dispose, not refcount-driven` per comment line 46). **Safe.**

### DbgShim + nested StartupContext

The launch path's one-shot callback.

- **`DbgShim._lib: nint`** — *readonly, ctor-init.* `NativeLibrary.Free` in `Dispose`. No cross-thread writes. **Safe.**

- **`DbgShim._enumerateCLRs` etc. (9 function pointers)** — *readonly, ctor-init.* Read by various methods on the MCP thread. **Safe.**

- **`StartupContext.PCordb: nint`, `StartupContext.HResult: int`** — *non-readonly.* `StartupCallbackThunk` (running on libdbgshim's internal thread) WRITES (lines 295–296); the MCP request thread that called `LaunchWithDebugger` READS after `ctx.Signaled.Wait(startupTimeout)` returns (lines 264–265). Contract: `ManualResetEventSlim.Set` provides a release barrier; writes before `Set` are visible after the corresponding `Wait` returns. The .NET memory model documents `ManualResetEventSlim` as containing full memory barriers in `Set`/`Wait`. **Safe — primitive contract.** Document the citation in finding 54 (cross-platform memory-model assumptions table).

- **`StartupContext.Signaled: ManualResetEventSlim`** — *readonly, ctor-init.* libdbgshim thread calls `Set`; MCP thread calls `Wait`. MCP thread also `Dispose`s in `finally` (line 272), AFTER `UnregisterForRuntimeStartup` (line 270). Contract: `UnregisterForRuntimeStartup` blocks until in-flight callbacks complete (assumption). **Race window DBG-1 — assumption unvalidated:** if `UnregisterForRuntimeStartup` only prevents future callbacks but does not synchronize with an in-flight one, the thunk's `ctx.Signaled.Set()` (line 297) races with `ctx.Signaled.Dispose()` (line 272). Worse: `handle.Free()` (line 271) races with `GCHandle.FromIntPtr(parameter)` (line 292) in the same path. Even worse than the in-flight-callback race, this also covers "delayed callback after timeout": if `Signaled.Wait(startupTimeout)` returns false (line 261), we return `E_FAIL`, hit finally, free the handle and dispose Signaled — but if libdbgshim fires the callback AFTER our timeout, the thunk dereferences a freed `GCHandle`. **Worth a probe; libdbgshim documentation does not specify Unregister's synchronization semantics.**

### BoundedLogSink (instance fields)

- **`_capacity: int`** — *readonly, ctor-init.* Read-only after construction. **Safe.**
- **`_buffer: LinkedList<LogRecord>`** — *readonly reference; mutable contents.* All access under `lock(_lock)` (lines 42, 55, 71). **Safe.**
- **`_lock: object`** — *readonly, ctor-init.* The lock object itself. **Safe.**
- **`_dropped: long`** — *non-readonly.* All access under `lock(_lock)`. **Safe.**

The contract from the class XML doc (lines 17–19) is explicit: *"appends and drains can interleave from different threads."* Honored end-to-end. **No race windows.**

### StackInspector (locals inside CaptureAsync)

- **`threadSamples: Dictionary<int, int>`** — *local variable.* EventPipe processing thread WRITES in the `Dynamic.All` handler (line 88, `threadSamples[tid] = …`); the awaiter (MCP thread) READS after `await processTask` (line 96 onwards, aggregating into `ObservationSnapshot`). Contract: TraceEvent's `EventPipeEventSource` dispatches handlers sequentially on the processing thread; after `Process()` returns, the awaiter joins and reads. Primitive: `await processTask` is a happens-before sync. **Safe by single-writer-then-single-reader serialization, but document the dependency on TraceEvent's sequential-dispatch contract.**

- **`exceptions: List<ExceptionEvent>`, `gcEvents: List<GcEvent>`** — same shape as `threadSamples`. **Safe by inherited contract.**

- **`contentionCount: int`** — *local.* EventPipe processing thread `Interlocked.Increment`s (line 80); awaiter reads after `await processTask`. Same effective serialization as the Dictionaries. **The `Interlocked` is overkill** given TraceEvent's single-threaded dispatch, but harmless — inconsistency with neighboring non-Interlocked Dictionary writes is the actual finding. Recommend either remove the Interlocked (clearer code, matches the contract) or normalize neighbors (defensive). No race window.

### EngineSteppingSession (singleton, the MCP-thread entry)

This is where the substrate-internal contract and the MCP SDK's threading model meet.

- **`_session: DebugSession?`** — *non-readonly.* WRITTEN by `LaunchAsync`/`RunAsync` (set) and `CleanupSession` (cleared). READ by every other public method (`IsActive`, `Step*`, `Set*Breakpoint*`, `RemoveBreakpoint*`, `ListBreakpoints`, `ClearBreakpoints*`, `InspectVariables`). Contract per class XML doc (line 8): *"One session at a time (the engine model — DI-injected as singleton)."* The contract is *single session*, but **single-threaded access is implicit, not enforced.** **Race window ESS-1 — uncovered:** two MCP requests concurrently calling `SetBreakpointAsync` both pass `_session is null` check, both call `_session.SetBreakpointAtLine`, both mutate `_lineBreakpoints` (a `Dictionary<string,int>`). DS-2 covers the inner racing-on-DebugSession; ESS-1 is the outer racing-on-EngineSteppingSession-state.

- **`_sessionHypothesis: string`, `_targetVersion: string`, `_stepCount: int`, `_targetPid: int`** — same shape; written by lifecycle/step methods, read elsewhere. **Race window ESS-1.**

- **`_launchedProcess: Process?`** — written by `RunAsync` / `CleanupSession`. **Race window ESS-1.**

- **`_lineBreakpoints: Dictionary<string,int>`, `_functionBreakpoints: Dictionary<string,int>`, `_exceptionFilters: Dictionary<string,int>`** — *readonly references; mutable contents.* Mutated in every Set/Remove/Clear method; iterated in `ListBreakpoints`, `ClearBreakpointsAsync`. `Dictionary<TKey,TValue>` is not thread-safe. **Race window ESS-1.**

## Memory-model contracts (the explicit invariants)

The substrate relies on five distinct memory-model primitives. Each is documented here once; the per-field records above cite them by name.

1. **`BlockingCollection<T>.Add`/`Take` release-acquire pair.** Writes before `Add` are visible after the matching `Take`. The collection's internal `ConcurrentQueue<T>` uses `Volatile.Write`/`Volatile.Read` and `Interlocked` for the queue's internal state, providing the happens-before. **Citation:** [System.Collections.Concurrent BCL docs](https://learn.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentqueue-1#thread-safety). Used by: `CallbackPump._events`/`_resume`/`_stops` and (indirectly) `CallbackPump._stopThread`.

2. **`Thread.Start` publication barrier.** Operations on the spawning thread before `Start` happen-before operations on the spawned thread. **Citation:** [ECMA-335 §I.12.6.5](https://ecma-international.org/wp-content/uploads/ECMA-335_6th_edition_june_2012.pdf) (CLI memory model); explicitly documented at [.NET memory model overview](https://learn.microsoft.com/en-us/dotnet/standard/threading/the-managed-thread-pool). Used by: `CallbackPump._resumeHandler`/`_pauseHandler`/`_worker` publication in `Start`.

3. **`ManualResetEventSlim.Set` release barrier; `Wait` acquire barrier.** Writes before `Set` are visible after the corresponding `Wait` returns true. **Citation:** [ManualResetEventSlim source documentation](https://learn.microsoft.com/en-us/dotnet/api/system.threading.manualreseteventslim) — `Set` internally calls `_lock.PulseAll` or invokes `Monitor.Pulse` (both full fences) plus updates `_combinedState` via `Interlocked`. Used by: `DbgShim.StartupContext.{PCordb,HResult,Signaled}`.

4. **`lock` statement (Monitor.Enter/Exit).** Full memory fence on entry and exit; mutual exclusion between threads holding the same lock object. **Citation:** [C# spec §8.12](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/lock); ECMA-335 §I.12.6.5. Used by: `BoundedLogSink._lock` protecting `_buffer` and `_dropped`.

5. **`Interlocked.{Increment,Decrement,Exchange,…}`.** Atomic read-modify-write; full memory fence. Used by: `ManagedCallbackHost._refCount`, `StackInspector.contentionCount`.

External contracts the substrate inherits (one each per external-thread entry):

6. **ICorDebug detach contract.** `ICorDebugController.Stop` synchronizes the process; `ICorDebugController.Detach` releases the debugger; `ICorDebug.Terminate` ends the session. The engine's `DebugSession.Dispose` orders these as `_pump.Dispose()` (joins worker) → `Quiesce` (Stop) → `Detach` → `Terminate` → `_callback.Dispose()`. **Assumption:** no further mscordbi callbacks fire on our `ManagedCallbackHost` after `Terminate` returns. **Validation status:** assumed by ADR-006 Phase 2 increment 2 (`finding 14`); the `drhook-detach-exit-race` limit ([docs/limits/drhook-detach-exit-race.md](../../../docs/limits/drhook-detach-exit-race.md)) is the historic counter-evidence that this contract has been violated under load. Phase 1b owns the deeper teardown audit; Phase 1 probe 44 owns the resolution.

7. **libdbgshim `UnregisterForRuntimeStartup` synchronization.** Assumed to block until any in-flight startup callback completes. **Validation status:** UNVALIDATED — libdbgshim documentation does not specify this. **See race window DBG-1 below.**

8. **TraceEvent `EventPipeEventSource.Process` single-threaded dispatch.** All handler delegates fire sequentially on the calling thread. **Validation status:** documented by [TraceEvent README](https://github.com/microsoft/perfview/tree/main/src/TraceEvent); honored by `StackInspector`'s closure pattern. **Safe.**

9. **MCP SDK request serialization.** Whether the `ModelContextProtocol.Server` SDK serializes concurrent tool calls — per-server, per-tool, per-session, or not at all. **Validation status:** UNVALIDATED. If the SDK serializes per-server, ESS-1 and DS-2 collapse to non-issues (the contract is upheld by the harness). If it does not, every singleton-on-MCP-stack pattern in DrHook needs an explicit serialization point. **See race window ESS-1 below.**

## Uncovered race-window register

Each entry has a tag, the failure mode, and a mapping to a Phase 1 probe (or queues a new probe).

### MCH-1 — Dispose during in-flight vtable callback

**Where:** `ManagedCallbackHost._self` / `_block` / `_v1`–`_v4` freed while mscordbi event thread is mid-callback.

**Failure mode:** `HostOf` dereferences freed memory (via `*((nint*)(pThis + sizeof(nint)))` reading the GCHandle slot in a freed allocation). Best case: `GCHandle.FromIntPtr` returns an invalid handle, the `.Target as ManagedCallbackHost` cast returns null, `_sink.OnCallback` is not invoked; the callback returns S_OK. Worst case: the freed memory has been re-allocated by another component, the GCHandle nint reads garbage, `GCHandle.FromIntPtr` may return a valid handle pointing at unrelated managed state, and the cast either crashes or invokes `OnCallback` against a stale `ManagedCallbackHost` (which holds a freed `_sink` reference).

**Mitigation in code today:** `DebugSession.Dispose` calls `_pump.Dispose()` (joins worker) → `Quiesce` → `Detach` → `Terminate` BEFORE `_callback.Dispose()`. The intent is that mscordbi delivers no further callbacks after `Terminate`, so `_callback.Dispose` is safe. **The intent is exactly the ICorDebug detach contract — see contract #6 above.**

**Status:** **Maps to probe 44** (drhook-detach-exit-race resolution). Probe 44 must validate the contract under kill-coincident-with-Dispose load (the 50/sec NCrunch rate-envelope per ADR-007 Phase 1).

### CP-1 — Idempotent CallbackPump.Dispose

**Where:** `CallbackPump._disposed` checked without a primitive.

**Failure mode:** Two concurrent `Dispose` calls both pass `if (_disposed) return;`. Both call `_events.CompleteAdding()` (idempotent), `_resume.CompleteAdding()` (idempotent), `_worker?.Join(...)` (idempotent — joining a completed thread is fine), then `_events.Dispose()`. The second `Dispose()` on an already-disposed `BlockingCollection<T>` throws `ObjectDisposedException`. Surfaces as an unhandled exception out of Dispose.

**Mitigation in code today:** none.

**Status:** **Not covered by probes 42–45.** Standard fix: replace the boolean with `Interlocked.Exchange(ref _disposed, 1) != 0` early-return, OR take a lock. Either is a one-line change. **New work item:** ENG-CP-1, fix in Phase 1 alongside probes 42–45; no probe needed (deterministic; unit test in Phase 8).

### DS-1 — Idempotent DebugSession.Dispose + lifecycle field reads racing with Dispose

**Where:** `DebugSession._disposed`/`_detached` checked without a primitive; `_pProcess`/`_pUnknown` read by many methods, set to 0 in Dispose.

**Failure mode (two sub-cases):**

- **DS-1a:** Concurrent Dispose calls — same shape as CP-1. Second `Dispose` runs `_pump.Dispose()` (which itself is CP-1-racy), `Quiesce`/`Detach` (which may fault on a torn-down `_controller`), `_callback.Dispose()` (already disposed).
- **DS-1b:** MCP request mid-flight when Dispose runs — e.g., `EnumerateModules` reads `_pProcess` after Dispose zeroed it. Either `RuntimeNavigation.ModuleNames(0)` returns empty (best case) or dereferences null inside ICorDebug COM call (worst case — undefined).

**Mitigation in code today:** none.

**Status:** **Not covered by probes 42–45.** Fix: same as CP-1 (Interlocked or lock for the gate); the field-zeroing-during-call race is more nuanced — needs Dispose to wait for in-flight requests OR a per-method gate ("session is being disposed; refuse new operations"). **New work item:** ENG-DS-1, structural fix coupled with the EngineAnomaly infrastructure (refuse-with-structured-error rather than crash).

### DS-2 — Concurrent MCP requests against the singleton DebugSession

**Where:** `DebugSession._breakpoints` / `_exceptionFilters` / `_symbols` / `_nextBreakpointId` / `_nextExceptionFilterId` — all `List<T>` / `Dictionary<TKey,TValue>` / `int` mutated by MCP-thread methods with no synchronization.

**Failure mode:** Standard `List<T>` / `Dictionary<TKey,TValue>` concurrent-modification corruption — internal node-link state corrupted, `InvalidOperationException` during iteration (especially `_exceptionFilters` iterated inside `WaitForStop`), lost id increments. The worst case is silent corruption of the breakpoint list, leading to wrong breakpoints being deactivated on `RemoveBreakpoint`.

**Mitigation in code today:** Unwritten contract — "callers serialize." If the MCP SDK serializes per-server, the contract holds incidentally. If not, the contract is violated by construction.

**Status:** **Not covered by probes 42–45.** Resolution depends on contract #9 (MCP SDK serialization). **Queue new probe — Probe 45-pre / Phase 1 extension:** *MCP SDK request-serialization characterization* — instrument `DrHookTools` to record entry/exit thread IDs across concurrent tool calls; observe whether the SDK serializes. This is a one-off measurement, not a substrate change. Outcome dictates whether DS-2 is a real race or a contract-upheld non-issue.

> Note on probe numbering: this probe slot is reserved within Phase 1; if it's needed it becomes "Probe 42-charac" or similar — assigned at the time the work is done, not pre-numbered into ADR-007's 42–45 scope. The discipline rule is *each phase isolates one unsolved thing*; this characterization probe is upstream of fixing DS-2.

### ESS-1 — Concurrent MCP requests against the singleton EngineSteppingSession

**Where:** `EngineSteppingSession._session` and all per-session state fields; the three breakpoint id maps.

**Failure mode:** Same shape as DS-2, one layer up. Two `LaunchAsync` calls could both pass `IsActive` check and both create `DebugSession` instances; the second overwrites `_session` and the first leaks. Or one thread calls `StopAsync` while another calls `StepNextAsync` — `_session` field read returns the live session, the Stop runs `CleanupSession` which calls `_session.Dispose()`, the Step continues against a disposed session.

**Mitigation in code today:** none; same unwritten contract.

**Status:** **Not covered by probes 42–45.** Same resolution dependency on contract #9. **Same characterization probe as DS-2.** If the SDK does not serialize, both DS-2 and ESS-1 need explicit serialization — the natural primitive is a `SemaphoreSlim(1, 1)` at the `EngineSteppingSession` layer enclosing every public method, OR a per-operation lock. **New work item:** ENG-ESS-1, contingent on characterization.

### DBG-1 — libdbgshim callback after UnregisterForRuntimeStartup

**Where:** `DbgShim.LaunchWithDebugger`'s `finally` block frees the `GCHandle` and disposes `Signaled` after calling `UnregisterForRuntimeStartup`. Contract assumption: Unregister is synchronous with respect to in-flight callbacks.

**Failure mode:** If Unregister only prevents future callbacks but does not synchronize, the in-flight thunk may dereference a freed GCHandle (`GCHandle.FromIntPtr(parameter)` → undefined) or call `Set` on a disposed `ManualResetEventSlim` (`ObjectDisposedException` on the libdbgshim thread, which has no managed exception handler — process crash). Also surfaces if `Signaled.Wait(startupTimeout)` returns false (timeout) and the callback eventually fires after we've torn down the context.

**Mitigation in code today:** none.

**Status:** **Not covered by probes 42–45.** Documentation gap. **New work item:** PROBE-DBG-1, characterize libdbgshim's Unregister synchronization semantics — read the libdbgshim source ([dotnet/runtime](https://github.com/dotnet/runtime/tree/main/src/coreclr/debug/dbgshim)) OR write a small probe that registers, intentionally delays the callback, calls Unregister, and observes whether the callback fires after Unregister returns. Findings feed back into the substrate's launch-path safety contract.

### SI-1 — StackInspector closure synchronization inconsistency

**Where:** `StackInspector.CaptureAsync` uses `Interlocked.Increment` for `contentionCount` while neighboring `threadSamples`/`exceptions`/`gcEvents` are mutated without synchronization.

**Failure mode:** None observed in practice — TraceEvent's documented single-threaded dispatch contract makes both patterns safe in this code. But the inconsistency hides the contract: a reader sees Interlocked and infers multi-writer concurrency, then sees the unprotected Dictionary writes and either assumes a bug or replicates the bug elsewhere.

**Status:** **Not a race; clarity issue.** Recommendation: remove the `Interlocked` (one-line change) to match the neighbors; document the TraceEvent contract in a comment. **Trivial cleanup, not Phase 1 priority.**

## Cross-references

- **Finding 54 (Phase 1b, teardown audit):** owns the per-Dispose-path matrix and the validation of contract #6 (ICorDebug detach). MCH-1 + DS-1 + CP-1 all converge there.
- **Finding 55 (Phase 1c, stack-budget audit):** owns the per-thread stack-budget records — including the per-platform defaults (Windows 1 MB, macOS secondary 512 KB) that affect `_worker` and the EventPipe processing thread. The explicit `new Thread(Pump, maxStackSize)` declarations ADR-007 Phase 1 requires land in finding 55.
- **`docs/limits/drhook-detach-exit-race.md`:** the historical evidence that contract #6 has been violated; Phase 1 probe 44 owns the resolution.
- **ADR-007 Probe 42:** Dispose during the worker's `_resumeHandler` call. Covered by MCH-1 (worker calls `controller.Continue` inside `_resumeHandler`; if Dispose races, the controller may already be tearing down).
- **ADR-007 Probe 43:** Concurrent PauseRequest + STOPPING callback. The pump's `_events` queue serializes these by construction (single consumer); the probe validates that no callback can interleave between `_stopThread` assignment (line 162) and `_stops.Add` (line 163). The audit shows the assignment-then-Add ordering and the `BlockingCollection` release barrier — but only **the probe under load** demonstrates the contract holds against mscordbi's actual delivery pattern.
- **ADR-007 Probe 44:** drhook-detach-exit-race. Covered by MCH-1 / contract #6.
- **ADR-007 Probe 45:** Worker-thread exception path. The audit identifies this gap: `Pump` calls `_resumeHandler!(ResumeKind.Continue, 0)` at line 142 and `_resumeHandler!(kind, _stopThread)` at lines 155 / 171 without any try/catch. If `Stepping.Arm` throws (e.g., a malformed stepper call) or `controller.Continue` throws (e.g., HRESULT thrown across the COM boundary), the worker thread dies. Future `WaitForStop` calls then block until the BlockingCollection times out. The probe validates the recovery design (per ADR-007: *"worker survives + surfaces, or fails session cleanly with deterministic error"*).

## EngineAnomaly surfaces identified

ADR-007 Phase 1 requires `EngineAnomaly` typed-record infrastructure — *"thread of detection, callback context, attempted operation, observed-vs-expected"*. The audit identifies five sites where the substrate today silently swallows or undefines:

1. **`CallbackPump.OnCallback` catch** (lines 55, 115): currently catches `InvalidOperationException`/`ObjectDisposedException` and drops. With anomaly infra: record `EngineAnomaly{kind:LateCallback, callbackName:e.Name, queueState:Disposed, observed:"late callback"}` — caller can decide whether to escalate.
2. **`CallbackPump.Pump` catch** (lines 154, 168): currently breaks the loop silently when `_resume.Take()` throws (queue completed during stop). With anomaly infra: distinguish *expected teardown* from *unexpected interruption mid-stop* — the latter is anomalous.
3. **`ManagedCallbackHost.HostOf` returns null** (line 49): currently the callback returns S_OK without invoking `_sink.OnCallback`. With anomaly infra: this is exactly MCH-1's silent failure path — record it.
4. **`DbgShim.StartupCallbackThunk` early-returns** (line 291): if `parameter == 0` or `h.Target` is not `StartupContext`. The first should never happen; the second can if the GCHandle was freed. Anomaly-record it.
5. **`Pump` worker-thread death** (probe 45 target): if `_resumeHandler` throws, the worker exits the `foreach` and dies. With anomaly infra: catch at the `Pump` boundary, record `EngineAnomaly{kind:WorkerThreadException, thread:Worker, op:ResumeHandler, exception:ex}`, and either restart-on-recoverable or terminate-cleanly-with-anomaly-flag.

These five sites are the seed for Phase 1's anomaly-injection probe (per ADR-007 Validation: *"capture mechanism is validated by a designed probe (intentional anomaly injection)"*).

## What this finding does NOT cover

- **Teardown ordering correctness** — Phase 1b ([finding 54](54-teardown-audit.md)) walks the Dispose paths end-to-end and validates each via probe.
- **Stack-budget correctness** — Phase 1c ([finding 55](55-stack-budget-audit.md)) audits `Span<T>`/`stackalloc` lifetimes and produces the explicit `Thread(…, maxStackSize)` declarations.
- **Cross-platform thread defaults** — Phase 1c records the per-platform defaults; per-platform validation is Phase 9.
- **MCP SDK internals** — characterization probe DS-2/ESS-1 lives in Phase 1 prep; not in scope of this audit document beyond surfacing the dependency.

## Summary

**Six classes hold cross-thread state:** CallbackPump, DebugSession, ManagedCallbackHost, DbgShim (+ nested StartupContext), BoundedLogSink, EngineSteppingSession. Plus StackInspector for the EventPipe path (closure-scoped, not cross-MCP-request).

**Cross-thread contracts (9):** five internal (BlockingCollection, Thread.Start, ManualResetEventSlim, lock, Interlocked) and four external (ICorDebug detach, libdbgshim Unregister, TraceEvent dispatch, MCP SDK serialization). Two of the four external contracts are unvalidated (libdbgshim Unregister, MCP SDK serialization).

**Uncovered race windows (six):**
- MCH-1 (Dispose during in-flight callback) → maps to probe 44.
- CP-1 (idempotent CallbackPump.Dispose) → fix work item, no probe.
- DS-1 (idempotent DebugSession.Dispose + field-zeroing-during-call) → fix work item + structural EngineAnomaly response.
- DS-2 (concurrent MCP requests against DebugSession) → new characterization probe + contingent fix.
- ESS-1 (concurrent MCP requests against EngineSteppingSession) → same.
- DBG-1 (libdbgshim callback after Unregister) → new probe.
- SI-1 (inconsistent synchronization in StackInspector closures) → clarity cleanup, not a real race.

**The biggest finding** is that two foundational external contracts (ICorDebug detach drainage; MCP SDK serialization) are load-bearing for substrate correctness and **neither has substrate-grade evidence in this code base yet**. The detach contract has Phase 1 probe 44 queued; the MCP serialization contract is queued by this audit.

Phase 1a is complete.
