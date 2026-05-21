# Finding 10: Probe 05 Outcome — PARTIAL (attach works; callback delivery BLOCKED)

**Status:**   PARTIAL / BLOCKED → **RESOLVED by probe 06 ([finding 13](13-probe-06-outcome.md)).** The attach → detach → terminate lifecycle works end-to-end with **no macOS entitlement wall** (a major positive). Managed callbacks never reached our *ComWrappers CCW* — root cause: ComWrappers object-CCW dispatch (not the managed transition). **Resolution:** use a hand-rolled `[UnmanagedCallersOnly]` vtable for the callback (probe 06) — slot dispatch then validated (`CreateProcess` fired). The notes below are the original investigation; the library was ruled out by finding 11 and the mechanism by finding 13.
**Date:**     2026-05-21
**Probe:**    `poc/drhook-engine/05-attach-callback-probe.cs`
**Runs:**     three, against two disposable targets (a parked sleeper, then a thread-generating worker).

## What works

| Step | Result |
|---|---|
| `DebugActiveProcess(pid, FALSE, …)` | `0x00000000` (S_OK) — **attach succeeded** |
| `Detach()` from the attached state | `0x00000000` (S_OK) |
| `ICorDebug.Terminate()` | `0x00000000` (S_OK) |

**The headline positive: macOS attach needs no debug entitlement.** A plain `dotnet run` process — no codesigning, no entitlement — attached to another .NET process via `ICorDebug.DebugActiveProcess` on macOS/ARM64. This was the big open risk (probe 02 flagged that `DebugActiveProcess`, unlike enumerate+create-interface, actually attaches and might hit `ptrace`/`task_for_pid` restrictions). It does not. **DrHook.Engine can attach with an ordinary build.**

## What's blocked

**Zero managed callbacks reached our CCW**, across all three runs:
- Run 1 (parked sleeper, `Thread.Sleep(Infinite)`): 0 callbacks in 5s.
- Run 2 (worker creating a thread every ~600ms): 0 callbacks in 5s.
- Run 3 (same worker, wait widened to 15s ≈ 25 thread-creation events): **0 callbacks**. Timing ruled out.

`SetManagedHandler` returned `S_OK` (probe 04 confirmed the CCW exposes the right IIDs), the target generated ~25 live `CreateThread`-worthy events, yet `ICorDebugManagedCallback::CreateThread` (or any callback) never fired into the stub. **Slot dispatch — probe 05's goal — is unvalidated.**

## Findings along the way

1. **Post-attach the process is RUNNING, not stopped.** An explicit `Continue()` after `DebugActiveProcess` returned `0x8013132F` — `CORDBG_E_SUPERFLOUS_CONTINUE` ("a Continue not matched by a stopping event," decoded from the CORDBG error range). So my initial "attach leaves it synchronized, Continue to kick off events" model was wrong; the process runs, and there was no pending synchronized event to continue. (The superfluous `Continue` has been removed from the probe.)

2. **No catch-up Create/Load replay on attach.** Desktop .NET Framework replayed synthetic Create/Load callbacks on attach so the debugger learned existing state. Modern CoreCLR apparently does not (for our case): a parked target produced nothing, consistent with the process running free with no queued synchronized events.

3. **Live events don't deliver either.** A target actively starting managed threads generated no callbacks over 15s. So it isn't "parked target has nothing to report" — callback delivery to our CCW simply isn't happening.

## Candidate causes (hypotheses for investigation — NOT guessed fixes)

1. **Event-transport / threading model (most likely).** ICorDebug on Unix is out-of-process: `mscordbi` (loaded by dbgshim) talks to the debuggee's runtime over a transport and dispatches callbacks on a thread it manages. Delivery may require the debugger to run a proper **event loop** / service the transport in a way our single-thread `Wait()` probe does not. netcoredbg has a full event loop and a runtime-controller thread; our probe just attaches and blocks one thread. This is the first thing to read.

2. **dbgshim / mscordbi version mismatch.** ~~We used VS Code's `csharp-2.130.5` `libdbgshim.dylib`; the event transport may be version-sensitive against .NET 10's DBI.~~ **RULED OUT (finding 11):** re-ran probe 05 against the official `Microsoft.Diagnostics.DbgShim.osx-arm64` 9.0.661903 — identical result (attach S_OK, 0 callbacks in 15s). The blocker is library-independent.

3. **macOS event-transport setup.** The attach (ptrace-class) succeeded without entitlement, but the debuggee↔debugger **event channel** may need additional setup or a separate capability that a plain process lacks.

4. **Attach-completion protocol.** A required step after `DebugActiveProcess` to begin event flow (beyond `Continue`, which is superfluous) that we're missing.

## Disciplined next step (Epistemics, not iteration)

**Read how netcoredbg receives post-attach events** — its event loop, the thread `mscordbi` delivers callbacks on, and any transport servicing or attach-completion handshake. netcoredbg demonstrably gets callbacks after `DebugActiveProcess` (finding 04 showed it waits on a `m_processAttachedCV` set *by* a callback), so the mechanism is real and documented in its source. That reading tells us what our probe is missing. A version-matched dbgshim is a cheap parallel check.

I deliberately stopped after ruling out timing rather than continuing to permute waits/continues — that would be guessing. The delivery model needs to be understood first.

## Where this leaves the PoC

The substrate-independence-critical path is **largely validated**: reach `ICorDebug` (probe 02), source-gen COM both directions (probes 03/04), attach + detach + terminate with no entitlement wall (probe 05). The remaining gap is **callback event delivery**, which is an integration detail, not a fundamental blocker — netcoredbg does it on the same platform, so it's achievable; we just need to learn the mechanism. Probe 05 stays open until callback dispatch is validated (probe 05 v2 after the netcoredbg event-loop reading).

## References

- Probe: `poc/drhook-engine/05-attach-callback-probe.cs` (superfluous Continue removed; wait widened to 15s)
- Fixtures: `fixtures/05-attach-callback-osx-arm64-2026052[1]T*.txt` (three runs; all `callback-fired=False`)
- Finding 04 — netcoredbg waits on a CV set by a post-attach callback (proof the mechanism exists)
- Findings 03 (contract), 05 (COM interop), 09 (probe 04 — the V-table the runtime won't yet call)
- Mercury session 2026-05-21 finding `probe-05-partial`
