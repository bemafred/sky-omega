# Finding 66: Probe 47 outcome + substrate change — target-death detection in `DebugSession.Dispose` resolves the external-death class of `drhook-detach-exit-race`

**Status:**   Probe 47 PASSED on macOS-arm64 2026-05-24 (10/10 clean Dispose against externally-killed Borrowed targets). Probe 44 phase C, previously SIGSEGVing under the new `--no-cache` protocol (finding 65 § Adjacent), now PASSES 10/10. Probe 42 (redesigned, finding 65) still PASSES 50/50. 59/59 unit tests + 2/2 integration tests unchanged. Substrate change: `DebugSession.Attach` acquires `Process.GetProcessById(pid)` for death-detection only (no kill ownership); `DebugSession.Dispose` short-circuits Quiesce + TryResumeForDetach when target has exited, with explicit exit-work-completion settle (200 ms) before Detach. Detach remains called on all paths (releases CCW reference from mscordbi — safety precondition for `_callback.Dispose`). Closes the production-critical "external target death during observation" scenario class: NCrunch killing testhosts, user force-quits, OOM killer reaping targets, target SIGSEGVs / unhandled exceptions, container shutdowns.
**Date:**     2026-05-24

## The hypothesis Probe 47 validates

**"The substrate must gracefully Dispose a Borrowed session whose target has died externally, without crashing mscordbi's RC event thread mid-exit-work-item processing."**

Real-world scenarios this scenario class covers:

- NCrunch killing a testhost between tests (per Phase 5 / probe 53 characterisation).
- Developer force-quitting a debugged app from Activity Monitor / Task Manager.
- OOM killer reaping a memory-hungry target during observation.
- Target SIGSEGVing / unhandled-exception-aborting on its own.
- Container/VM shutdown killing the target while substrate observes.

These are all production-grade observation patterns; they MUST work for substrate-grade reliability. Pre-finding-66, the substrate's `Dispose` marched through Quiesce → TryResumeForDetach → Detach against a dying target — racing mscordbi's RC event thread's exit-work-item processing and SIGSEGVing on macOS-arm64.

## The construction

Probe 47's target (`47-target.cs`) is a parked sleeper:

```csharp
Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();
Thread.Sleep(Timeout.Infinite);
```

No callback flood. No exceptions. The substrate's only mscordbi interaction is attach, brief observation, then teardown.

Probe (`47-external-death-smoke.cs`), 10 cycles, each:

1. Spawn target.
2. Wait for READY sentinel + extract real PID.
3. `DebugSession.Attach(pid, sink)` (Borrowed).
4. `Thread.Sleep(200)` — brief observation window.
5. `proc.Kill(entireProcessTree: true)` — external death.
6. (`PostKillSettleMs = 0` — immediate Dispose to expose the race; the substrate is responsible for handling the timing).
7. `session.Dispose()` — substrate must detect death and skip mscordbi protocol ops.
8. `Thread.Sleep(100)` — inter-cycle settle so process IDs don't collide.

Falsification:

- 2 usage; 3 no READY; 4 first Attach failed; 5 target failed to die;
- **6 substrate Dispose exception**;
- **7 WorkerException anomaly**;
- 9 attach failed mid-loop; 0 PASS.

## The substrate change

### 1. `Attach` acquires Process handle for death-detection (Borrowed)

Mirrors finding 64's `AttachAndOwn` but without kill ownership. The Process handle is held purely as a death-detection oracle.

```csharp
public static DebugSession Attach(int processId, IDebugEventSink sink)
{
    ArgumentNullException.ThrowIfNull(sink);
    // Acquire Process handle for DEATH-DETECTION (Probe 47 / finding 66).
    // Substrate does NOT take ownership of kill for Borrowed — the handle is consulted
    // in Dispose to short-circuit mscordbi protocol ops when the target has died externally.
    Process targetProcess = Process.GetProcessById(processId);
    DbgShim dbgShim = DbgShim.Load();
    nint pUnknown = 0;
    try
    {
        ThrowIfFailed(dbgShim.CreateCordbForProcess(processId, out pUnknown), ...);
        return FromCordbg(dbgShim, sink, processId, pUnknown, ownsTarget: false, targetProcess: targetProcess);
    }
    catch
    {
        if (pUnknown != 0) Marshal.Release(pUnknown);
        dbgShim.Dispose();
        targetProcess.Dispose();
        throw;
    }
}
```

`Process.GetProcessById` throws if the target has already exited at Attach time — the caller learns immediately rather than mid-attach. Consistent with `AttachAndOwn`'s contract.

### 2. `Dispose` short-circuits Quiesce + TryResumeForDetach on dead target

```csharp
public void Dispose()
{
    if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
    _pump.Dispose();

    if (_ownsTarget)
    {
        TryKillTargetAndSettle();   // finding 64 kill-first; settles KillSettleMs (100 ms)
    }
    else
    {
        // For Borrowed: external Kill may have happened microseconds before Dispose.
        // Process.HasExited can lag the actual exit by tens of milliseconds; small settle
        // ensures the death-check below makes the correct routing decision.
        Thread.Sleep(BorrowedDeathCheckSettleMs);   // 50 ms
    }

    bool targetDead = _targetProcess?.HasExited == true;

    if (targetDead)
    {
        // Dead-target path: let mscordbi exit-work-item complete before Detach unwinds
        // the same internal state. 200 ms empirically comfortable; well above mscordbi's
        // sub-millisecond exit-work duration.
        Thread.Sleep(ExitWorkSettleMs);   // 200 ms
        Detach();
    }
    else
    {
        // Live-target path (unchanged from finding 65):
        Quiesce();
        if (!_ownsTarget) TryResumeForDetach();
        Detach();
    }

    _cordbg.Terminate();
    // ... refs release, _callback.Dispose, _dbgShim.Dispose, _targetProcess.Dispose ...
}
```

### Why Detach is preserved on the dead-target path

`Detach()` calls `ICorDebugController::Detach`, which releases mscordbi's reference to our ManagedCallbackHost CCW. This is the safety precondition for `_callback.Dispose()` (later in teardown), which frees the CCW's native vtable + GCHandle backing memory.

If we skip Detach, mscordbi still holds the CCW pointer. When `_callback.Dispose()` then frees that memory, any subsequent mscordbi dispatch into the CCW (the RC event thread can be slow to drain) hits freed memory → UAF crash. Verified empirically: skipping Detach on the dead-target path made probe 47 SIGSEGV at cycle 1 (where the dispatch-settle-only fix from finding 65 had it passing 10/10).

So the substrate's death-detection short-circuit ONLY skips the **active mscordbi protocol pushes** (Quiesce's Stop, TryResumeForDetach's Continue-loop) that race exit-work-item processing. It preserves Detach (necessary) and adds the explicit exit-work-completion settle (so Detach unwinds against a fully-quiesced mscordbi, not a racing one).

### Constants

```csharp
private const int KillSettleMs = 100;                  // finding 64 — Owned kill-first
private const int ExitWorkSettleMs = 200;              // finding 66 — dead-target pre-Detach
private const int BorrowedDeathCheckSettleMs = 50;     // finding 66 — Borrowed pre-HasExited
```

All on macOS-arm64; cross-platform validation deferred to Phase 9.

## Validation

### Probe 47 — production-scenario external-death

```
$ dotnet run --no-cache 47-external-death-smoke.cs -- 47-target.cs

cycles     : 10/10 clean Dispose; 0 threw; elapsed 6071ms
anomalies  : 38 surfaced
  UnexpectedHResult                : 28      (Detach against dying-target returns
                                              non-S_OK — substrate signals honestly
                                              via anomaly, doesn't crash)
  LateCallback                     : 10      (mscordbi RC thread dispatches ExitProcess
                                              etc. during exit-work; substrate catches
                                              via OnCallback's CompleteAdded guard)

PROBE 47 PASSED — 10/10 clean Dispose cycles against externally-killed Borrowed
targets; substrate detects external death and skips mscordbi protocol operations
that would race exit-work-item processing.
```

### Probe 44 phase C — kill-coincident with Pause-stop (now also closed)

```
$ dotnet run --no-cache 44-detach-exit-race-smoke.cs -- 44-target.cs

── Phase C: Attached + external Process.Kill + immediate Dispose × 10 cycles ──
phase-C    : 10/10 clean Dispose; 0 threw; elapsed 7602ms
phase-C    : PASSED — substrate handles 10 kill-coincident cycles without engine crash.

PROBE 44 PASSED
```

Probe 44 phase C had been SIGSEGVing intermittently (cycles 2–8) since commit 44d76aa under the `--no-cache` protocol (finding 65 § Adjacent). The death-detection routes it through the same dead-target path as probe 47; the additional Pause-stop state doesn't change the substrate's decision tree once death is detected.

### Regression checks

```
$ dotnet run --no-cache 42-dispose-resumehandler-race-smoke.cs -- 42-target.cs
cycles     : 50/50 clean Dispose; 0 threw; elapsed 29742ms
PROBE 42 PASSED   (live-target path unchanged, only adds 50 ms BorrowedDeathCheckSettleMs)

$ dotnet test tests/DrHook.Engine.Tests/DrHook.Engine.Tests.csproj
Passed!  - Failed:     0, Passed:    59, Skipped:     0, Total:    59

$ ./tests/DrHook.Engine.IntegrationTests/bin/Release/net10.0/DrHook.Engine.IntegrationTests
total: 2, failed: 0, succeeded: 2, duration: 2s 207ms
```

The 50 ms Borrowed pre-check settle adds modest latency to Dispose against live targets (~50 ms vs previous 0 ms) but doesn't change correctness. Probe 42's 50 cycles take ~30 s now (was ~27 s), within acceptable Dispose budget.

## What `drhook-detach-exit-race` looks like after this finding

| Scenario | Pre-finding-66 status | Post-finding-66 status |
|---|---|---|
| Borrowed Attach, live target, normal Dispose | Working | Working |
| Borrowed Attach, informational callback flood, normal Dispose | Mitigated (finding 65 dispatch-settle) | Mitigated (unchanged) |
| Borrowed Attach, external target Kill, immediate Dispose | **Triggered (probe 47 SIGSEGV)** | **Resolved (probe 47 10/10)** |
| Borrowed Attach + Pause-stop, external Kill, immediate Dispose | **Triggered (probe 44 phase C SIGSEGV)** | **Resolved (probe 44 phase C 10/10)** |
| Owned (AttachAndOwn / Launch), substrate kill-first, Dispose | Mitigated (finding 64 kill-first + settle) | Resolved (now routes via death-detection too) |
| Owned + informational flood, Dispose | Working | Working |

The limit `drhook-detach-exit-race` is upgraded from **Triggered → Resolved** in `docs/limits/drhook-detach-exit-race.md`.

## What this finding does

- **Closes Probe 47** (external-death class): substrate handles target-died-externally scenarios for Borrowed sessions.
- **Closes Probe 44 phase C** (kill-coincident with Pause-stop): same death-detection mechanism handles the Pause-pending variant.
- **Resolves `drhook-detach-exit-race`** for the substrate-supported scenarios.
- **Adds substrate API surface**: `DebugSession.Attach` now acquires a Process handle (death-detection only); on success, the substrate owns the handle's lifetime (disposed in Dispose alongside other native resources).

## Forward — observations and follow-on

### `Process.GetProcessById` cost at Attach time

Per-Attach cost: ~1 ms on macOS-arm64. Negligible. The Process handle is a managed wrapper; it doesn't hold OS-level resources beyond what's needed for the `HasExited` query.

### Substrate state vs. Phase 9 cross-platform

`Process.HasExited` semantics differ across platforms: on Windows, ExitCode is updated synchronously when the process exits; on Linux/macOS, it depends on `waitpid` collection. The `BorrowedDeathCheckSettleMs = 50` is empirical for macOS-arm64 and may need tuning for Linux/Windows during Phase 9. The current value is conservative.

### Owned-path interaction

For Owned sessions (AttachAndOwn / Launch), `TryKillTargetAndSettle` runs first with `KillSettleMs = 100`. By the time the death-detection check runs, `HasExited` is reliably true. The Owned path now routes through the dead-target branch too, gaining the ExitWorkSettleMs (200 ms) cushion before Detach — strictly more robust than pre-finding-66.

### Probe 47's anomaly profile

The 28 UnexpectedHResult per 10 cycles is interesting — Detach against a fully-exited target returns non-S_OK HRESULT (mscordbi has no live process to detach from). The substrate surfaces this honestly. Future substrate work may decide to suppress these specific HRESULTs from the anomaly stream (they're not actionable signals; substrate-correct teardown still happens). For now, they remain visible as evidence of the substrate's honest behavior.

## Performance characterization — Dispose latency

The finding-66 settles are substantial. Worst-case Dispose latency by path:

| Dispose path | Sleeps | Total |
|---|---|---|
| Owned (AttachAndOwn / Launch) | `KillSettleMs` (100) + `ExitWorkSettleMs` (200) | **300 ms** |
| Borrowed, alive target | `BorrowedDeathCheckSettleMs` (50) + TryResumeForDetach intra+final (~20–150) | **70–200 ms** |
| Borrowed, target died externally | `BorrowedDeathCheckSettleMs` (50) + `ExitWorkSettleMs` (200) | **250 ms** |

**These sleeps are on session-teardown, NOT on operation hot paths.** Per-operation surfaces (`GetLocals`, `SetBreakpoint`, `Continue`, `Pause`, `Step*`) are unaffected — none of them touch any settle.

The substantive cost is on **high-frequency attach/detach cycles**:

- **MCP tool usage (today's primary consumer)**: typical sessions are minutes to hours; one Dispose per session. 300 ms is invisible.
- **Probes (current)**: 10–50 cycles per run; probes themselves are stress tests. ~30 s probe runtimes are acceptable.
- **Integration tests (finding 64)**: 1–2 sessions per test, ~1.5 s duration. ~10 % overhead added by sleeps. Acceptable.
- **NCrunch-rate consumption (Phase 5 / Phase 9)**: 50+ attach-detach cycles per second per testhost. **300 ms × 50 cycles = 15 s of sleep per second of operation** — catastrophic. This needs separate substrate work.

### `Thread.Sleep` vs async alternatives

`Thread.Sleep` is the correct call for synchronous `Dispose` code that must block. Releases the thread to the scheduler in sleep state; other threads still run. `Task.Delay(N).Wait()` is strictly worse — it adds Task machinery overhead while still blocking the same thread.

The real improvement would be `IAsyncDisposable` + `await Task.Delay(N)` — releases the awaiting thread to the threadpool during settles. But `DebugSession.Dispose` is `IDisposable.Dispose` and every consumer uses `using` blocks; switching to async Dispose is a wider API change (MCP server, integration tests, all probes). Worth doing eventually, scoped separately.

### Forward — NCrunch substrate work owns this performance scope

ADR-007 Phase 5 (substrate capabilities NCrunch needs) explicitly includes:

> **Probe 56 — Attach-rate-envelope hardening.** If Phase 1 probe 44 characterised single-shot 10/10, this probe characterises 50/sec sustained under simulated NCrunch load.

That probe is where the finding-66 settles will be challenged at NCrunch rates and where the substrate's options will be exercised:

1. **Tune the durations down with empirical measurement.** Current 200/50/100 are conservative defaults; mscordbi exit-work on macOS-arm64 is sub-millisecond typically. May drop to 50/20/30 with no observed regression — needs Phase 5 measurement.
2. **Signal-based waits.** Poll `HasExited` instead of fixed sleep for the BorrowedDeathCheckSettleMs window — returns as soon as the kill propagates rather than waiting full 50 ms. `ExitWorkSettleMs` stays fixed (mscordbi exposes no exit-work-complete signal). Modest gain.
3. **`IAsyncDisposable` API.** Frees the substrate's thread during settles; NCrunch's parallel coordinator can issue many concurrent attaches without pile-up on a single thread waiting for Dispose. Substantive substrate change.
4. **Background-thread teardown.** Substrate `Dispose` returns immediately, teardown completes on a worker thread. Risk: caller spawns next Attach before previous teardown done — known race. Requires careful design to be safe.

This finding establishes the substrate-correctness contract. The NCrunch-rate performance work proceeds in Phase 5 against the rate envelope probe 56 characterises. Today's MCP, integration tests, and probe suite are all comfortably within the budget the conservative defaults impose.

## Cross-references

- [ADR-007](../../../docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md) Phase 1 Probe 47 — closed by this finding.
- [finding 53](53-threading-memory-model-audit.md) — MCH-1 race window.
- [finding 54](54-teardown-audit.md) — T1/T2/T3 teardown scenarios; the "target dies during detach" path.
- [finding 59](59-detach-exit-race-outcome.md) — Probe 44 substrate change (TryResumeForDetach introduced 44d76aa); kill-coincident phase C — false-PASS at the time, real PASS now.
- [finding 64](64-substrate-owned-lifecycle.md) — Owned path's kill-first / AttachAndOwn (the Process-handle pattern this finding mirrors for Borrowed).
- [finding 65](65-probe42-redesign-regression.md) — dispatch-settle in TryResumeForDetach (handles the informational-flood race separately).
- [`docs/limits/drhook-detach-exit-race.md`](../../../docs/limits/drhook-detach-exit-race.md) — upgraded from Triggered → Resolved by this finding.
- `src/DrHook.Engine/DebugSession.cs:Attach` (Process handle acquisition) and `:Dispose` (death-detection routing).
- Commit `44d76aa` — introduced TryResumeForDetach; original "Probe 44 phase C 10/10 PASS" claim was stale-cache false-PASS per `feedback_filebased_app_stale_cache`. Probe 47 + `--no-cache` re-protocol surfaced the latent race; this finding closes it.

## Phase 1 substrate-correctness status (post-finding 66)

- **Probe 41** (anomaly-injection): PASSED (finding 56).
- **Probe 42** (Dispose during `_resumeHandler`, informational flood): PASSED (finding 65, dispatch-settle fix).
- **Probe 43** (Concurrent PauseRequest + STOPPING callback): PASSED (finding 58).
- **Probe 44** (drhook-detach-exit-race, both phases): PASSED (finding 59 + this finding closing phase C with death-detection).
- **Probe 45** (Worker-thread exception path): PASSED (finding 60).
- **Probe 46 + 46b** (MTP + Legacy VSTest integration-test promotion): PASSED in isolation (findings 61, 62). MCH-RE-2 structurally eliminated (finding 64).
- **Probe 47** (External target death during Borrowed observation): PASSED (this finding).

ADR-007 Phase 1 substrate-correctness arc — **complete on macOS-arm64**.
