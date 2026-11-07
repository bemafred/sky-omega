# Limit: DrHook's snapshot has no call stacks and no thread states

**Status:**        Latent
**Surfaced:**      2026-11-07, by ADR-016 Step 5 (probe 106): a worker stuck in the attach could be seen only from outside the engine, and the HOTSPOT heuristic turned out to measure the number of threads (fixed in `21f80fb`).
**Last reviewed:** 2026-11-07
**Promotes to:**   A DrHook ADR, when a debugging task needs to know where a running thread is, or why it is not running, without stopping the process. The first candidate is a stuck debugger worker (ADR-016 Step 6).

## Description

`drhook_snapshot` (`StackInspector.CaptureAsync`) observes a running process through EventPipe without stopping it. For each thread it reports how many of its samples it was sampled in and how many of them ran managed code (the sample's `ClrThreadSampleType`). It also reports exception, GC and lock-contention counts, and anomaly flags.

Two things are missing:

- **Call stacks.** The snapshot says *which* threads ran managed code, not *where*. A thread spinning in a loop is named, but its loop is not.
- **Thread states.** A sample that is not `Managed` is `External`, which means only "not running managed code". The snapshot cannot say whether that thread was waiting on a lock, blocked in a read, or running native code.

Until 2026-11-07 both were promised, in the class comment ("collapsed stacks, thread state overview"), in the MCP tool description ("thread states"), and in DRHOOK.md and README.md ("thread/stack snapshot"). Those texts now describe what the snapshot gives. The class name, `StackInspector`, still promises stacks.

## Trigger condition

A debugging task whose answer is where a running thread is, or what a non-running thread waits on, when stopping the process is not an option: stopping would change what is observed, or no debug session can be had. Probe 106 was one. A worker stuck inside the attach was suspected only from outside. To look at it, the supervisor needed a view from outside the engine: macOS `sample`, and the threads in `/proc`. Those were workarounds for this missing capability and have been removed.

## Current state

- With a debug session, the engine walks the call stacks exactly (ICorDebug), but only while the target is stopped. That breaks the snapshot's passive contract, and needs a session the snapshot does not have.
- Without one, there is the per-thread managed share and the anomaly flags above.
- EventPipe sees managed threads only. The native threads where finding 105's hang lives (mscordbi's PAL) are invisible to it whatever is added. Managed stacks would show the worker's main thread inside the attach P/Invoke, which says where it waits in managed code, not why.

## Candidate mitigations

None verified; each is a hypothesis to test before engineering.

1. **The samples' own stacks.** The SampleProfiler's samples carry a stack of instruction addresses. To name the methods behind them needs the runtime's method-load or rundown events, then a mapping (TraceEvent's `TraceLog` does this for a trace file). Collapse the stacks per thread, and report the top frames of the busiest stacks.
2. **Thread states from the runtime's wait events.** Wait and contention events, or the thread pool's, can say what an `External` thread waits on. What CoreCLR 10 emits per platform has to be checked first.
3. **Native frames** need a platform-specific mechanism outside EventPipe. That is a separate question, and likely its own entry.

## References

- [`StackInspector.cs`](../../src/DrHook.Engine/Diagnostics/StackInspector.cs), [`StackInspectorTests.cs`](../../tests/DrHook.Engine.Tests/StackInspectorTests.cs)
- [ADR-016](../adrs/drhook/), Step 5: probe 106, [`poc/drhook-engine/106-supervisor.cs`](../../poc/drhook-engine/106-supervisor.cs)
- Commit `21f80fb` (HOTSPOT from each sample's type)
