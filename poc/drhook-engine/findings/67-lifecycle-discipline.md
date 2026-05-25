# Finding 67: Substrate lifecycle discipline — natural process endings by default; debugger guards for lifecycle violators

**Status:**   Discipline clarification + substrate-design direction (no substrate code changes in this finding). Surfaced 2026-05-25 during Phase 8 mass-promotion attempt. Key insight from session: the substrate's MCH-RE-2 / drhook-detach-exit-race work (findings 63/64/65/66) was honest engineering against real symptoms — but those symptoms only existed because integration test patterns and target designs were already violating process lifecycle discipline. The cleaner answer is upstream: don't violate. As a *debugger* substrate specifically, an additional dimension applies — we must guard for targets that themselves violate lifecycle rules, since misbehaving-target observation is the substrate's actual job.
**Date:**     2026-05-25

## What this session revealed

The Phase 8 mass-promotion attempt (integration-test enforcement of Phase 1 substrate-correctness probes) surfaced a chain of symptoms that I initially mis-framed as substrate accumulation bugs (`MCH-RE-3`). Probes 48, 48b, 48c systematically eliminated each hypothesis:

- Probe 48 (10 fresh targets, AttachAndOwn, lightweight spawn): **10/10 PASS**. Substrate handles many sessions in same probe-host.
- Probe 48b (10 fresh targets, AttachAndOwn, heavyweight `dotnet test` spawn): **10/10 PASS**. Heavyweight child-process tree is not the cause.
- Probe 48c (Borrowed + Pause-stop + Dispose-without-Resume + explicit kill of leave-running-detached target): **SIGBUS** at cycle 1 cleanup or cycle 2 spawn — but only with the explicit kill in place. Without the kill (let target outlive the probe): PASS.

The chain of cause:

```
Test code calls Kill on a "leave-running"-detached Borrowed target
  → mscordbi RC thread is still mid-cleanup for that detached session
  → Kill triggers ExitProcess work item in mscordbi RC thread
  → Dispatched into our CCW... which substrate has freed via _callback.Dispose
  → UAF → SIGBUS on macOS-arm64
```

**This is the substrate behaving correctly given the inputs.** The bug is in the inputs — the caller violated the Borrowed contract by terminating a target the substrate had explicitly detached-from-and-left-running.

## The lifecycle discipline (two layers)

### Layer 1 — Default discipline: natural process endings, like COM's AddRef/Release

Process lifecycle is a contract between creator and consumer, similar to COM reference counting:

- The process **creator** owns lifecycle responsibility — spawns, manages, ensures graceful shutdown.
- **Consumers** (observers, attachers, dependents) acquire handles for their purposes but DO NOT terminate.
- A well-implemented process **ends naturally** when its work completes — Main returns, async work finishes, signals are honored.
- Kill is the **OS-level escape hatch** for processes that won't end on their own. It's a sign of improper implementation (eternal loops, missing signal handlers, deadlocks). It is NOT a normal cleanup mechanism.

Applied to Sky Omega DrHook.Engine substrate:

| Substrate API | Today | Discipline-aligned |
|---|---|---|
| `Attach` (Borrowed observer) | Doesn't kill (correct) | Doesn't kill (correct) — wait for natural exit |
| `AttachAndOwn` (Owned observer) | **Kill-first on Dispose** | **Wait for natural exit; kill only as escape hatch** |
| `Launch` (Owned creator) | **Kill-first on Dispose** | **Wait for natural exit; kill only as escape hatch** |

Finding 64's `AttachAndOwn` kill-first closure of MCH-RE-2 was solving the *consequence* of a discipline violation (test code calling `Kill` after Dispose). The cleaner upstream fix: don't `Kill` at all, design test targets to complete naturally, the race never exists.

### Layer 2 — Debugger-specific guard: substrate must handle lifecycle violators

A debugger is in the business of dealing with processes that misbehave. We CANNOT assume targets are well-implemented. Real-world examples Martin invoked: Claude Chat App on macOS that can't quit normally; a process stuck in an infinite loop; a service deadlocked waiting on a peer that crashed; a test that never returns because of a hang.

For a *debugger* substrate, the discipline-aligned design is therefore:

1. **Default**: wait for natural exit on Dispose. Respect lifecycle, the substrate trusts well-behaved targets.
2. **Escape hatch**: provide an explicit `Abandon()` / forced-kill operation, named to signal *"this is the extraordinary measure for a target that won't behave"*. Caller must opt in.
3. **Robustness under violation**: substrate behavior under external/forced kill (probe 47, probe 44 phase C scenarios) is STILL a substrate-correctness concern — because targets DO misbehave, and OS-level kills DO arrive. The substrate's responsibility is to not crash when the OS does what only the OS can: terminate a process the substrate didn't expect to die.

The two layers are complementary, not in tension. Layer 1 sets the default; Layer 2 is the guard for inputs that violate Layer 1.

## What this changes — proposed direction (not implemented yet)

### Substrate API

Three options (in order of discipline-purity):

**Option 1: Behavior change to existing `AttachAndOwn` / `Launch`.**
- `Dispose` waits up to a timeout (e.g., 5s) for natural target exit.
- If timeout elapses (target stuck), emit an anomaly (`TargetStuckAtDispose` or similar) and fall back to kill — extraordinary measure, surfaced as substrate signal.
- New explicit method `Abandon(timeout)` for callers that *know* the target needs forced termination.

**Option 2: New API names, deprecate the implicit kill behavior.**
- `AttachAndObserve(pid, sink)` — Owned observer, Dispose waits for natural exit.
- `AttachAndManage(pid, sink)` — explicit "I will kill this if needed" semantics.
- Existing `AttachAndOwn` either retired or aliased to one of the above.

**Option 3: Keep current API, document AttachAndOwn's kill as a "managed-lifecycle" specialty.**
- Less disciplined, easiest to ship from here.
- Still requires per-call-site discipline review.

Recommended: **Option 1** — change the default behavior, add explicit `Abandon` as the escape hatch. Discipline-aligned, doesn't fork the API surface.

### Target design

Probe targets and integration target test methods must EXIT NATURALLY:

| Target | Today | Discipline-aligned |
|---|---|---|
| `07-target.cs` (probe 42 supplier) | `while (true) { ...; Thread.Sleep(20); }` | Finite loop (e.g., 100 iterations) then return |
| `42-target.cs` (informational flood) | `while (true) { Thread.Start/Join }` | Finite loop then return |
| `47-target.cs` (parked sleeper) | `Thread.Sleep(Timeout.Infinite)` | Listens for "exit" signal or completes brief work |
| `48-target.cs` / `48b-target.cs` | Same as 47 | Same fix |
| MTP `IdleForDebuggerObservation` | `Thread.Sleep(30s)` | Finite observable work (~50–500 ms) then return |
| VSTest `IdleForDebuggerObservation` | `Thread.Sleep(30s)` | Same fix |

Targets that must run "long enough for substrate to observe" should achieve that via meaningful observable work, not via arbitrary sleeps that effectively require kill.

For substrate-correctness probes that NEED a long-running target (e.g., probe 42's flood race needs sustained callback rate), the discipline-correct shape is: target generates the load via finite repetition (`for (int i = 0; i < 1000; i++)`) and exits naturally after, not via `while (true)`. The probe completes within the target's natural lifetime; if probe runs faster than target, probe exits cleanly and target dies naturally.

### Integration test pattern

Tests no longer call `Process.Kill` on targets. The pattern becomes:

```csharp
[TestMethod]
public void Borrowed_TargetSurvives_NaturalExit()
{
    using Process bootstrap = TargetSpawn.Mtp(targetExe);
    int pid = TargetSpawn.ExtractPid(bootstrap, TimeSpan.FromSeconds(30));

    using (DebugSession session = DebugSession.Attach(pid, new NullSink()))
    {
        Thread.Sleep(200);
        // ... substrate-correctness assertions ...
    }  // Dispose: detach-leave-running

    // Don't kill. Don't even check HasExited inside the test method body —
    // target's finite work completes ~50-500ms after we Detached, target exits naturally.
    bool exitedNaturally = bootstrap.WaitForExit(TimeSpan.FromSeconds(5));
    Assert.IsTrue(exitedNaturally, "Target did not naturally exit within 5s after Borrowed Dispose — substrate may have left it stuck, or target is mis-implemented (eternal loop).");
}
```

The `WaitForExit` assertion actively *validates* lifecycle discipline — if the target fails to exit naturally, the test fails, and we know either the substrate left it stuck OR the target is mis-implemented.

### Probe 47 / probe 44 phase C / probe 48c — still valuable

These probes explicitly model **lifecycle-violating scenarios** (external kill mid-session, target dying coincident with substrate Dispose, etc.). Under the Layer 2 discipline, they validate the substrate's *guard* against misbehavior. Their value increases under this framing — they're not testing happy paths but testing the substrate's robustness when the world misbehaves.

Specifically:

- **Probe 47**: validates substrate handles external death cleanly during Borrowed observation (OOM kill, user force-quit, target SIGSEGV). Real production scenarios.
- **Probe 44 phase C**: validates substrate handles kill-coincident-with-Pause scenario. Real production scenario (NCrunch killing testhost during pause).
- **Probe 48c (now-PASS variant with no explicit kill)**: validates substrate's Borrowed leave-running detach against VSTest testhost.

The substrate's death-detection (finding 66) is precisely the Layer 2 guard for these scenarios. Discipline-aligned.

## What's NOT changing in this finding

This finding does not modify substrate code. It documents the discipline understanding the session reached. Substrate changes (Option 1's `AttachAndOwn` semantics shift + new `Abandon` API + target redesigns) are scoped to a separate substrate-evolution effort — likely an ADR-007 amendment or successor ADR.

## What's deferred to next session

1. **Substrate API redesign**: implement Option 1 — `AttachAndOwn` / `Launch` Dispose waits for natural exit; new `Abandon` method as escape hatch. Probe + finding for the redesign.
2. **Target redesign**: rewrite `07-target.cs`, `42-target.cs`, `47-target.cs`, `48-target.cs`, `48b-target.cs`, MTP `IdleForDebuggerObservation`, VSTest `IdleForDebuggerObservation` to exit naturally.
3. **Integration test redesign**: rewrite Phase 8 promotion patterns to use natural-exit assertions (`WaitForExit`) instead of explicit `Kill`.
4. **Phase 8 mass promotion**: proceed once 1+2+3 are aligned. Should be straightforward with discipline-aligned semantics in place.

## What ADR-007 should reflect after this session

ADR-007's Phase 1 "complete on macOS-arm64" claim from finding 66 was premature in a specific way: substrate-correctness probes pass individually, finding-65/66 substrate changes are real and validated, but the *integration enforcement* layer (Phase 8) requires the lifecycle-discipline rework above before it becomes a CI-enforceable contract.

I'd suggest ADR-007 gets an explicit *Phase 0.5* or *prerequisite to Phase 8* entry: "Lifecycle discipline alignment — substrate semantics + target designs + integration test patterns must respect process lifecycle (default natural exit, explicit Abandon as escape hatch). See finding 67."

Phase 1 substrate-correctness remains validated at the probe level; the integration-enforcement promotion is gated on the discipline rework.

## Cross-references

- [ADR-007](../../../docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md) — needs amendment per "What ADR-007 should reflect" above.
- [finding 63](63-mch-re-2-investigation.md) — initial MCH-RE-2 investigation (was about caller's dispose-then-kill ordering, fixed at substrate level).
- [finding 64](64-substrate-owned-lifecycle.md) — substrate-owned lifecycle (AttachAndOwn). Today's kill-first protocol is the work this finding rethinks as Layer-1 violation.
- [finding 65](65-probe42-redesign-regression.md) — dispatch-settle in TryResumeForDetach. Substrate-correctness for live-target informational flood.
- [finding 66](66-target-death-detection.md) — target-death detection. This is the Layer 2 *guard* for misbehaving / dying-target scenarios — still valuable under the new discipline framing.
- Probes 48, 48b, 48c (this session) — empirical evidence chain that surfaced the discipline gap.
- `feedback_no_helpers`, `feedback_no_behavior_flags`, `feedback_resource_limit_class_audit` — adjacent discipline memories.
- Martin's framing: "We should rely on natural process endings, but being a debugger — we should *guard* for misbehaving targets that *violates* process lifecycle rules" (2026-05-25 conversation).

## The principle, restated

> A well-implemented process ends naturally. Kill is for the rare case where it doesn't.
>
> A debugger is in the business of observing processes that may or may not be well-implemented. The substrate's default discipline must respect the well-implemented case; its escape hatches must handle the misbehaving case; and the two must be clearly named, documented, and separated so callers know which one they're invoking and why.
