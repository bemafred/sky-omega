# Finding 70: ADR-008 Increment 2 — probe target redesign for natural exit

**Status:**   ADR-008 Increment 2 deliverable. Three substrate-supporting probe targets (`07-target.cs`, `42-target.cs`, `48-target.cs`) redesigned from Layer-1-violator shapes (`while (true)` / `Thread.Sleep(Timeout.Infinite)`) to bounded finite work that exits naturally. Five remaining probe targets (`47`, `51`, `52`, `53`, `54`) annotated as intentional-violator-by-design because their probe-purpose IS to validate substrate behavior against violators (Layer 2 guard validation). All affected probes (07, 42, 43, 44, 45, 48) PASS regression with redesigned targets. Substrate code unchanged.
**Date:**     2026-05-26

## What changed

### Redesigned targets (supporting infrastructure, no probe-purpose for infinite duration)

#### `07-target.cs`

Probes affected: 07, 43, 44, 45. Used as a "continuous managed-event generator" — substrate attaches, observes Thread.Start/Join + throw/catch callbacks for as long as probe needs.

Before:
```csharp
while (true)
{
    Thread t = new(static () => { });
    t.Start();
    t.Join();
    try { throw new InvalidOperationException("drhook-smoke"); }
    catch { }
    Thread.Sleep(20);
}
```

After:
```csharp
const int Iterations = 3000;  // ~30 ms per iteration × 3000 ≈ 90 s natural runtime

for (int i = 0; i < Iterations; i++)
{
    Thread t = new(static () => { });
    t.Start();
    t.Join();
    try { throw new InvalidOperationException("drhook-smoke"); }
    catch { }
    Thread.Sleep(20);
}
// Falls through to natural exit.
```

90 s default natural runtime — substrate-correctness probes 07/43/44/45 all complete in well under that. If a future probe takes longer, target exits naturally and substrate handles via finding 66 death-detection routing (no crash, no anomaly).

#### `42-target.cs`

Probes affected: 42 (redesigned). Informational-only flood (Thread.Start/Join, no exceptions).

Before:
```csharp
while (true)
{
    Thread t = new(static () => { }) { IsBackground = true };
    t.Start();
    t.Join();
}
```

After:
```csharp
const int Iterations = 500_000;  // empirically calibrated; ~60 s+ natural runtime
                                 // covers probe 42's ~27 s observed runtime with margin
for (int i = 0; i < Iterations; i++) { /* same body */ }
// Falls through to natural exit.
```

**Iteration count calibration**: probe 42 produces ~3000 callbacks/sec under substrate continuous attach (1500 iterations/sec). Native un-attached rate is higher (~6000 iterations/sec). Probe runtime ~27 s with substrate attached ~50% of the time. Initial guess of 100,000 iterations expired at probe cycle 37 of 50 (~17 s runtime) — first run after redesign FALSIFIED ("target died: process exited before cycle 37 began"). Bumped to 500,000 (~60 s natural runtime); second run 50/50 PASS in 29.6 s.

#### `48-target.cs`

Probes affected: 48 only. Multi-session test infrastructure — substrate spawns N fresh targets, attaches briefly, Disposes each.

Before:
```csharp
Thread.Sleep(Timeout.Infinite);
```

After:
```csharp
const int Iterations = 10;
const int PauseMs = 450;
for (int i = 0; i < Iterations; i++)
{
    Thread t = new(static () => Thread.Sleep(50)) { IsBackground = true };
    t.Start();
    t.Join();
    Thread.Sleep(PauseMs);
}
// Falls through to natural exit (~5 s total runtime).
```

Brief observable work (10 Thread.Start/Join × 450 ms pause ≈ 5 s) plus natural exit. Each probe-48 cycle attaches for ~200 ms; target outlives the observation window comfortably.

### Annotated as intentional-violator-by-design

These five targets remain Layer 1 violators because their probe-purpose is precisely to validate substrate's Layer 2 guard against discipline violators. The violator shape IS the test variable. Each got a clarifying header comment.

| Target | Probe(s) | Why violator-by-design |
|---|---|---|
| `47-target.cs` | 47 | External-kill scenario testing requires target alive when probe sends kill |
| `51-target.cs` | 51, 55 | Ignoring-handler scenario testing requires target to catch + Cancel=true |
| `52-target.cs` | 52 | Tight-CPU-loop testing requires while-true with no async safepoints |
| `53-target.cs` | 53 | Default-disposition testing requires no signal handlers |
| `54-target.cs` + `54-child-target.cs` | 54 | Process-tree testing requires parked tree for signal propagation observation |

Each gets a comment header that explicitly says "INTENTIONAL LAYER-1-VIOLATOR" + reasoning + cross-reference to ADR-008 / finding 67.

## Validation

All probes that use the redesigned or annotated targets:

| Probe | Target(s) | Result |
|---|---|---|
| 07 | 07-target (redesigned) | PASSED — 24 callbacks drained from live stream |
| 42 redesigned | 42-target (redesigned) | PASSED 50/50 — 29.6 s elapsed |
| 43 | 07-target (redesigned) | PASSED — 10 Pause + 10 Exception stops, 0 anomalies, target alive |
| 44 phase B + C | 44-target | PASSED — phase B 1/1 + phase C 10/10 |
| 45 | 07-target (redesigned) | PASSED — 1 WorkerException, dead-worker behavior, Dispose clean, target alive |
| 48 | 48-target (redesigned) | PASSED 10/10 — 6.9 s elapsed |

Plus regression validation:
- 59/59 unit tests still PASS (substrate code unchanged; probe-target changes don't affect unit tests)
- Probes using intentional-violator targets (47, 51, 52, 53, 54, 55) not re-run since they got documentation-only changes; substrate validation unchanged

## What this does NOT cover

- **Integration targets** (`tests/DrHook.Engine.IntegrationTargets.Mtp/IdleTarget.cs` and `tests/DrHook.Engine.IntegrationTargets.Vstest/IdleFact.cs`): both currently `Thread.Sleep(30s)`. These are Increment 3 scope per ADR-008.
- **Integration tests** themselves: still use `Process.Kill(entireProcessTree:true)` in `finally` blocks for VSTest variant. Increment 3 will redesign these to use `WaitForExit` assertions.

## Discipline observation

Substrate code untouched in this increment — the changes are purely in probe targets and target documentation. Yet the *discipline alignment* is substantial: substrate-supporting targets now demonstrate the lifecycle pattern they expect production targets to follow. The substrate is no longer in the position of "we depend on the OS to kill our test infrastructure for us"; targets exit naturally on completion.

The intentional-violator-by-design targets explicitly document their role. Future readers (and future me, on rediscovery) won't be tempted to "fix" them by redesigning for natural exit — they're testing the substrate against violators by design.

## Cross-references

- [ADR-008](../../../docs/adrs/drhook/ADR-008-process-lifecycle-discipline.md) Increment 2 — this finding's deliverable.
- [finding 67](67-lifecycle-discipline.md) — discipline articulation.
- [finding 68](68-process-lifecycle-ground-truth.md) — empirical signal-handling ground truth; informs target design.
- [finding 69](69-increment1-substrate-api.md) — substrate API change that makes natural-exit targets workable (Stage 1 SIGTERM honored by CoreCLR default → target exits cleanly in tens of ms).

## What's next

- **Increment 3** — integration target + existing integration tests redesign. MTP `IdleForDebuggerObservation` and VSTest `IdleFact.IdleForDebuggerObservation` move from `Thread.Sleep(30s)` to finite observable work; integration tests use `WaitForExit` assertions instead of explicit `Process.Kill`.
- **Increment 4** — Phase 8 mass promotion (14 substrate-correctness integration tests using the natural-exit pattern).
- **Increment 5** — ADR-007 amendment + ADR-008 closure.
