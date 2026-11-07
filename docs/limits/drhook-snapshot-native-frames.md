# Limit: DrHook's snapshot sees no native frames and no native threads

**Status:**        Latent
**Surfaced:**      2026-11-07, by ADR-016 Step 5 (probe 106), while the snapshot's collapsed stacks and thread states were built.
**Last reviewed:** 2026-11-07
**Promotes to:**   A DrHook ADR, when a debugging task needs to see where a thread waits below its last managed frame, or a thread the runtime does not manage. The first candidate is a debugger worker stuck as in finding 105, whose wait is in mscordbi's PAL (ADR-016 Step 6).

## Description

`drhook_snapshot` names, for every managed thread, its collapsed stacks and its state, from the sample profiler's samples resolved through the method rundown (`StackInspector`, 2026-11-07). EventPipe sees managed code only. So:

- **Below the last managed frame, nothing.** A thread outside managed code shows the managed frame it left from: `Monitor.Wait`, `Thread.Sleep`, or a P/Invoke such as `Interop+Sys.Read`. It doesn't show the native frames under that frame: the kernel wait, the runtime's or a native library's code.
- **Threads the runtime doesn't manage are invisible.** mscordbi's own threads, where finding 105's hang lives, are an example.
- **P/Invoke stubs generated at run time have no name.** One was observed innermost under `SocketAsyncEngine.EventLoop`: it has no module, and the rundown doesn't name it. The stack keeps it as `[unresolved frame]`, and the thread's `at` names its caller.

No DrHook text promises any of this. The snapshot's promise is collapsed stacks and a thread state overview of the process's managed threads, and that is kept.

## Trigger condition

A hang or a stall whose answer lies in native code or a native thread, that the managed frames don't settle. For a debugger worker stuck in the attach, the managed stack shows its main thread inside the attach P/Invoke. That says where in managed code it waits, not what in mscordbi it waits on.

## Current state

- Managed frames and states: complete for every managed thread (tests: `StackInspectorTests`).
- Native frames: none. Probe 106's supervisor once looked from outside the engine (macOS `sample`, the threads in `/proc`). That view was a workaround and has been removed.

## Candidate mitigations

None verified.

1. **A platform stack walker in the engine:**
   - macOS: `task_threads` and `thread_get_state` with frame-pointer walking (arm64 keeps frame pointers);
   - Linux: `ptrace` or `/proc/<pid>/task/*/stack` for the kernel side;
   - Windows: `StackWalk64`.

   Symbols come from the loaded images. This is BCL plus P/Invoke, like the rest of DrHook.Engine. It suspends each thread long enough to walk it. The runtime's sampler already does that to managed threads: the probe saw a SuspendEE on every sampling tick, about 600 a second.
2. **ICorDebug's native frames** (`ICorDebugNativeFrame`) during a debug session. These are exact, but need a stopped target and a session, and for a stuck debugger the session is the thing that's stuck.

## References

- [`StackInspector.cs`](../../src/DrHook.Engine/Diagnostics/StackInspector.cs), [`StackInspectorTests.cs`](../../tests/DrHook.Engine.Tests/StackInspectorTests.cs)
- [ADR-016](../adrs/drhook/ADR-016-debugger-resilience-and-recovery.md), Step 5: probe 106
- [Finding 105](../../poc/drhook-engine/findings/105-dll-pal-never-rechecks-a-waited-process.md): mscordbi's PAL never re-checks a waited process
