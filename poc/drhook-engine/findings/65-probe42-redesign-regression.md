# Finding 65: Probe 42 redesigned — hypothesis-aligned construction surfaces a real substrate regression introduced at 44d76aa

**Status:**   Probe 42 retired and replaced. New probe construction matches the stated hypothesis ("Dispose during the worker's `_resumeHandler(...)` call") by using an informational-only callback target (Thread.Start/Join, no exceptions) so the pump worker actually spends time inside `_resumeHandler`'s synchronous `controller.Continue(0)` COM call. At **824e642 baseline 50/50 clean Dispose**; at **HEAD SIGSEGV** after the sentinel Dispose. The TryResumeForDetach Continue-loop introduced by commit 44d76aa is the regression-introducing substrate change, and the regression hits a **production-relevant race**, not just the original probe's constructed STOPPING-unconsumed pattern. Supersedes finding 57.
**Date:**     2026-05-24

## What the original probe 42 actually constructed

The original `42-dispose-resumehandler-race-smoke.cs` (committed at `824e642`) targeted `07-target.cs`, whose loop is:

```csharp
Thread t = new(static () => { });
t.Start(); t.Join();                  // CreateThread + ExitThread (Informational)
try { throw new InvalidOperationException("drhook-smoke"); }
catch { /* swallow */ }               // Exception (STOPPING)
Thread.Sleep(20);
```

The Exception fires on every iteration. Per `CallbackPump.Pump` (CallbackPump.cs:188–214):

- Informational callbacks → `_resumeHandler!(Continue, 0)` (worker briefly in `controller.Continue`).
- **Exception is STOPPING** → push to `_stops`, **park at `_resume.Take()`**.

The probe never set a breakpoint, never consumed `_stops`, never called `Resume`. Within milliseconds of attach, the worker parked at `_resume.Take()` against the first Exception and **stayed parked** until `_pump.Dispose` did `CompleteAdding(_resume)`, unparking it via the `WorkerSilentBreak` catch.

So the worker was **never inside `_resumeHandler`** when Dispose hit — it was parked at `_resume.Take()` waiting for a consume that never came. The 20 `WorkerSilentBreak` anomalies finding 57 reported (one per cycle) were the substrate's honest signal of this. The stated hypothesis (Dispose-during-`_resumeHandler`) and the constructed scenario (Dispose-while-parked-at-Take-with-unconsumed-stop) named different races.

The probe's PASS at 824e642 was real for what the probe actually tested, but the probe did not test what its name / docstring / ADR-007 entry claimed.

## What the redesigned probe constructs

The new `42-target.cs`:

```csharp
while (true)
{
    Thread t = new(static () => { }) { IsBackground = true };
    t.Start();   // CreateThread (Informational)
    t.Join();    // ExitThread (Informational)
}
```

No exceptions. No breakpoints. No module dynamics. Only Informational callbacks. The pump worker therefore takes each event and goes straight to `_resumeHandler!(Continue, 0)` → `controller.Continue(0)`. It never enters the STOPPING branch, never pushes to `_stops`, never parks at `_resume.Take()`. The worker is always either consuming the next event from `_events.GetConsumingEnumerable` or inside `_resumeHandler`'s synchronous COM call — exactly the surface the stated hypothesis names.

The new probe (`42-dispose-resumehandler-race-smoke.cs`) attaches, sleeps a randomized delay in `[20, 500] ms` (sampling the race window across `controller.Continue` duration distribution), disposes, repeats 50 cycles. Falsifies on:

- Any process crash.
- `WorkerException` anomaly (`controller.Continue` threw through the pump boundary — substrate bug).
- `WorkerSilentBreak` anomaly (stop fired against a no-stops target — substrate classification bug).
- Dispose threw.

`LateCallback` anomalies are expected: under flood, mscordbi dispatches callbacks after `_events.CompleteAdding`; the substrate's OnCallback catch turns them into structured signal rather than silent loss.

## Validation

### Baseline (substrate source at 824e642 — pre-`TryResumeForDetach`)

```
$ dotnet run --no-cache 42-dispose-resumehandler-race-smoke.cs -- 42-target.cs

runtime    : .NET 10.0.0
target pid : 95551
flood @1s  : 2934 events   (≈3000/sec — much heavier than 07-target's 24/sec)
cycles     : 50/50 clean Dispose; 0 threw; elapsed 17406ms
target     : alive (resumed un-debugged)
anomalies  : 10 surfaced (0 dropped to capacity)
  LateCallback                     : 10

PROBE 42 PASSED — 50/50 clean Dispose cycles under continuous informational flood;
substrate's Quiesce + Interlocked gates handle Dispose-during-_resumeHandler
without crashes, without WorkerException, without WorkerSilentBreak, and without
killing the target.
```

Fixture: `fixtures/42-dispose-resumehandler-race-osx-arm64-20260524T154446Z.txt`.

### Regression (substrate source at HEAD — `TryResumeForDetach` in place)

```
$ (dotnet run --no-cache 42-dispose-resumehandler-race-smoke.cs -- 42-target.cs; echo RAW_EXIT=$?) 2>&1 | tail

runtime    : .NET 10.0.0
target pid : 95460
flood @1s  : 2971 events   (sentinel attach + drain succeeded)
RAW_EXIT=139                (SIGSEGV — silent native crash)
```

No further probe output. Sentinel Dispose returned (the `flood @1s` line printed). Then SIGSEGV before cycle 0 completed.

## What changed at 44d76aa

Commit `44d76aa` ("Probe 44 (drhook-detach-exit-race resolution, Attached path) PASSED + substrate change") added `TryResumeForDetach()` to `DebugSession.Dispose` for the Attached path:

```csharp
Quiesce();                           // controller.Stop(0) — synchronize target
if (!_ownsTarget)
    TryResumeForDetach();            // NEW — Continue(0) loop until S_FALSE
Detach();
_cordbg.Terminate();
```

`TryResumeForDetach` loops `_controller.Continue(0)` up to 10 attempts, expecting `S_FALSE` (target running, mscordbi Stop-counter drained). The intent — balance mscordbi's Stop counter so the next Attach doesn't hit `CORDBG_E_DEBUGGER_ALREADY_ATTACHED` — is documented in `DebugSession.cs:893–933` and is correct for the Attached path's general case.

### Why probe 44's substrate change claimed not to regress probe 42

The 44d76aa commit message says: *"Adjacent: re-ran Probe 42 to confirm substrate change didn't regress — 20/20 still clean (fixture 42-dispose-resumehandler-race-osx-arm64-20260524T074821Z.txt)."*

Per `feedback_filebased_app_stale_cache`: file-based-app re-runs without `--no-cache` execute a cached build that does not see engine source edits. The 2026-05-24T07:48 fixture was produced by the pre-44d76aa cached binary — a false PASS. The substrate change shipped without genuine probe-42 revalidation. Confirmed today by:

- Running today's (`--no-cache`) source at 824e642 → 20/20 PASS (real baseline behavior).
- Running today's source at 44d76aa → SIGSEGV (real regression behavior, masked by stale cache at original ship time).

The new probe's redesigned shape would have caught this on the original re-run had `--no-cache` been in use.

## The regression manifests in production-relevant code paths

The original probe's `WorkerSilentBreak`-flooded pattern is non-production: real MCP consumers consume stops. So I initially framed the 44d76aa regression as a corner-case-only issue against a constructed scenario.

**The redesigned probe disproves that framing.** Its informational-only target reflects a perfectly ordinary debugger scenario:
- Production caller attaches to observe a target that is generating module/thread/AppDomain churn.
- Worker is mid-`controller.Continue(0)` when caller disposes.

This is exactly the *stated* race window the substrate's Quiesce + Interlocked gates were designed to handle, and exactly the scenario `TryResumeForDetach`'s Continue-loop now breaks. The regression is real and affects normal usage.

## Hypothesis for the SIGSEGV mechanism (not yet pinned to a line)

Probe 42's flood produces ~3000 callbacks/sec. During the sentinel attach + 1s drain + Dispose:

1. mscordbi RC event thread has tens-to-hundreds of CreateThread/ExitThread callbacks queued.
2. `_pump.Dispose` calls `CompleteAdding` on `_events` + `_resume`, joins worker (≤2s).
3. `Quiesce` calls `_controller.Stop(0)` — synchronize target. Stop counter increments.
4. **`TryResumeForDetach` loops `_controller.Continue(0)`.** Each `Continue` resumes the target one step. Resumed target → mscordbi dispatches queued OR newly-generated callbacks via `ManagedCallbackHost.OnCallback` → `CallbackPump.OnCallback`. The pump queue is CompleteAdded; OnCallback's `InvalidOperationException` catch fires the `LateCallback` anomaly.
5. The Continue-loop and mscordbi's RC event thread interact while `_callback` (CCW) is still alive. mscordbi may dereference RC-thread state that `Detach` / `Terminate` tear down moments later.
6. Result: asynchronous native crash on mscordbi's RC thread shortly after sentinel Dispose returns, or synchronous crash on cycle 0's `dbgshim.CreateCordbForProcess` encountering inconsistent mscordbi internal state.

Either way: the substrate's Continue-loop pumps queued callbacks through a downstream-dead callback pipeline during a teardown window. That's the suspect.

## What this finding does

- **Retires the original probe 42 + finding 57**: stated hypothesis didn't match construction; PASS was for a different (non-production) scenario.
- **Establishes new probe 42**: hypothesis-aligned construction; baseline behavior validated at 824e642 (50/50 clean).
- **Confirms real substrate regression at HEAD**: `TryResumeForDetach` introduced at 44d76aa breaks the *stated* Probe 42 race against a production-relevant target shape.
- **Documents the stale-cache false-PASS mechanism**: 44d76aa's "re-ran probe 42, still clean" was a stale-cache artifact, not real revalidation.

## Forward — what comes next

The regression is substrate-grade and warrants its own substrate work. Options under consideration (deferred to a separate session decision):

1. **Move `TryResumeForDetach` after `Detach`**: counter rebalancing might still achieve its goal post-Detach (no longer pumping callbacks through a live CCW).
2. **Drain queued callbacks before Continue-loop**: explicitly let mscordbi's RC thread complete its current backlog before initiating Continue-loop; e.g., a brief synchronization read or `Thread.Sleep` between Quiesce and TryResumeForDetach.
3. **Replace Continue-loop with a different counter-rebalancing mechanism**: if mscordbi exposes a direct counter-query / counter-reset, prefer that over a side-effecting Continue.
4. **Accept the trade-off**: keep TryResumeForDetach for the Attached-path counter-balance benefit (preventing CORDBG_E_DEBUGGER_ALREADY_ATTACHED on re-attach), document that it has a known race against informational floods, characterise the rate envelope.

This is for a future probe (likely probe 47 or similar, characterising the fix) and a future substrate change. Not in scope for this finding — which establishes the regression cleanly and supersedes the false-PASS provenance of finding 57.

## ADR-007 implications

- **Phase 1 Probe 42 status changes**: the PASS recorded against finding 57 was for a constructed pattern, not the stated hypothesis. The replacement probe + this finding establish that the substrate handled the stated race at 824e642 but does NOT at HEAD. Phase 1 closure cannot rest on the old PASS.
- **Phase 8 promotion**: today's `AttachAndOwn`-based integration tests use 1–2 sessions, no informational flood. The MTP + VSTest integration tests' PASS at HEAD (finding 64) is genuine — those scenarios don't exercise the `TryResumeForDetach` × backlog-callback interaction this finding exposes. Phase 8 remains unblocked.
- **Phase 1 substrate-correctness arc**: re-opened. Probe 42 needs PASS at HEAD for the stated hypothesis before the arc can close. That requires substrate work on `TryResumeForDetach` (one of the options above), validated by the replacement probe.

## Cross-references

- [ADR-007](../../../docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md) Phase 1 Probe 42 — entry updated to point at this finding; finding 57 marked superseded.
- [finding 53](53-threading-memory-model-audit.md) — MCH-1 race (the stated hypothesis Probe 42 names).
- [finding 54](54-teardown-audit.md) — T1/T2 teardown scenarios.
- [finding 57](57-dispose-resumehandler-race-outcome.md) — **superseded** by this finding. Original probe's construction did not match its stated hypothesis. PASS was for a different (non-production) scenario.
- [finding 59](59-detach-exit-race-outcome.md) — Probe 44 substrate change (`TryResumeForDetach` introduced at commit 44d76aa).
- [`feedback_filebased_app_stale_cache`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_filebased_app_stale_cache.md) — the discipline rule the 44d76aa "still clean" claim violated.
- [`feedback_check_help_first`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_check_help_first.md) — `dotnet run --no-cache` is the one-flag fix; today's protocol uses it by default.
- Commit `824e642` — probe 42 + finding 57 (original), pre-`TryResumeForDetach` substrate.
- Commit `44d76aa` — Probe 44 substrate change introducing `TryResumeForDetach`; regression-introducing commit.
- `src/DrHook.Engine/DebugSession.cs:893–933` — `TryResumeForDetach` implementation (regression source).
- `src/DrHook.Engine/CallbackPump.cs:148–215` — `Pump` worker logic; Informational vs STOPPING branches.
