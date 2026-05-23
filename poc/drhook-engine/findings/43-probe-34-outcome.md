# Finding 43: Probe 34 Outcome — PASSED: persistent exception filter (`Arm` / `List` / `Remove` / `Clear`)

**Status:**   **PASSED, 2/2** (clean exit 0 both runs; 47 unit tests still pass; probe 26 regression
re-validated — no backward-compat drift). Fourth Phase 3 substrate gap closed:
`DebugSession.ArmExceptionFilter(typeName, phaseFilter)` registers a filter ONCE; subsequent
`WaitForStop` calls auto-resume non-matching exception stops and only surface matching ones. Backs
`drhook_step_break_exception` as a steady-state arm-once primitive.
**Date:**     2026-05-23
**Probe:**    `poc/drhook-engine/34-exfilter-smoke.cs` + `34-exfilter-target.cs`

## API

```csharp
public sealed record ExceptionFilterInfo(int Id, string TypeName, ExceptionStopKind PhaseFilter)
{
    public const string AnyType = "*";
    public bool Matches(string actualType, ExceptionStopKind actualPhase);
}

public int ArmExceptionFilter(string typeName, ExceptionStopKind phaseFilter = None);
public IReadOnlyList<ExceptionFilterInfo> ListExceptionFilters();
public bool RemoveExceptionFilter(int id);
public int ClearExceptionFilters();
```

- `TypeName == "*"` (= `ExceptionFilterInfo.AnyType`) matches any thrown type.
- `PhaseFilter == None` matches any phase; otherwise must equal `FirstChance` / `UserFirstChance` /
  `CatchHandlerFound` / `Unhandled`.
- Multiple armed filters are OR-composed; an exception passes when ANY armed filter matches it.
- Type match is **exact** today; subclass walking (`extends` chain via metadata) is a follow-on.

Mirrors the breakpoint registry (probe 32) in shape: monotonically increasing positive ids, typed
`Info` records, `List` returns a snapshot array, `Remove(id)` is idempotent (false on miss), `Clear`
returns the cleared count.

## How it integrates — `WaitForStop` filter-awareness with a backward-compat default

When **no filters** are armed, `WaitForStop` is a one-liner delegate to the pump (unchanged from
the legacy behavior — probes 26/27 rely on this). When **at least one** filter is armed, the wait
becomes a deadline-bounded loop:

```csharp
public StopInfo? WaitForStop(TimeSpan timeout)
{
    if (_exceptionFilters.Count == 0) return _pump.WaitForStop(timeout);   // legacy path
    DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
    while (true)
    {
        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return null;
        StopInfo? stop = _pump.WaitForStop(remaining);
        if (stop is null) return null;
        if (stop.Reason != StopReason.Exception) return stop;
        if (ExceptionMatchesAnyFilter(stop.ExceptionKind)) return stop;
        _pump.Resume();                                                    // non-matching: drop
    }
}
```

Probe 26 was re-run after this change to confirm the no-filter path is **unchanged** — exception
stops surface as they did before. Zero behavioral drift for direct-engine consumers.

## End-to-end

Target throws `ProbeException` and `OtherException` alternately, caught. The probe has two phases:

```
arm        : id=1  filter=ProbeException@FirstChance
phase A    : 3 WaitForStop cycles, all must be ProbeException …
   cycle 1: stop=Exception  type=ProbeException
   cycle 2: stop=Exception  type=ProbeException
   cycle 3: stop=Exception  type=ProbeException
remove     : filter removed; list empty
phase B    : up to 8 WaitForStop cycles, must observe BOTH types …
   cycle 1: stop=Exception  type=ProbeException
   cycle 2: stop=Exception  type=OtherException
PROBE 34 PASSED
```

**Phase A** proves three things in one go: the filter persists across waits (3 consecutive surfaces);
non-matching exceptions are *auto-resumed silently* (the `OtherException`s alternating between the
visible stops never appear); and the filter applies to the existing `WaitForStop` (no new API the
consumer must learn).

**Phase B** proves removal restores default. The `OtherException` surfaces within 2 cycles — if the
filter were silently kept armed, only `ProbeException`s would still appear.

## Design notes

- **No-behavior-flag-in-spirit.** WaitForStop's "filter-aware loop vs one-liner delegate" looks like
  a state flag, but it's session state about the filter list (count > 0), not a mode toggle inside a
  class with two implementations. The behavior is naturally derived from configuration: an empty
  filter list IS the no-filter case, and the loop falls out for free.
- **Filter is a property of the WAIT, not a property of an exception breakpoint.** Compared with the
  per-call `WaitForExceptionPolicyStop`, this persists. Both forms coexist — `WaitForExceptionPolicyStop`
  is the explicit-per-call form (probe 30); persistent filters are the arm-once form (this probe).
  They do not interfere with each other: `WaitForExceptionPolicyStop` does not consult the persistent
  filter list, and `WaitForStop` does not consult an inline argument. Each consumer picks the
  pattern that fits.
- **WaitForPolicyStop unchanged.** It still returns non-Breakpoint stops as-is. The persistent
  exception filter feature is scoped to `WaitForStop` — the natural surface for MCP `step_continue`.

## Scope / next

ADR-006 Phase 3 status:

- [x] AsyncBreak (probe 31, finding 40)
- [x] Breakpoint registry (probe 32, finding 41)
- [x] Launch (probe 33, finding 42)
- [x] **Persistent exception filter** (this finding)
- [ ] Object inspection (depth ≥ 1) — `ICorDebugType` / `GetFieldValue` / string + array rendering
  (the longest pole)
- [ ] `SteppingSessionManager` rewrite + regression suite + netcoredbg retirement
- [ ] Polish: Terminate-on-dispose for Launched sessions; stdout/stderr capture pipes;
  subclass-aware exception-type matching (walk `extends` chain via `GetTypeDefProps`'s `ptkExtends`).

Four of five substrate gaps closed; only **object inspection** remains before the host rewrite.

## References

- Probe: `poc/drhook-engine/34-exfilter-smoke.cs`, `34-exfilter-target.cs`
- Fixture: `fixtures/34-exfilter-osx-arm64-…`
- Engine: `ExceptionFilterInfo.cs` (new public type + `Matches`), `DebugSession.ArmExceptionFilter
  / ListExceptionFilters / RemoveExceptionFilter / ClearExceptionFilters` (new),
  `DebugSession.WaitForStop` (filter-aware loop when filters > 0),
  `DebugSession.ExceptionMatchesAnyFilter` (private helper)
- Findings 26/27 (the exception-stop mechanism; re-run for regression), 30 (per-call exception policy
  — complementary, both forms coexist), 40/41/42 (the Phase 3 arc)
- Mercury session 2026-05-22 observation `probe-34-persistent-exception-filter`
