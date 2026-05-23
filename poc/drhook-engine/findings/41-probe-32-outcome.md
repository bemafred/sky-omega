# Finding 41: Probe 32 Outcome — PASSED: breakpoint registry (List / Remove(id) / Clear)

**Status:**   **PASSED, 2/2** (clean exit 0 both runs; 47 unit tests still pass). Second Phase 3
substrate gap closed: `SetBreakpoint` / `SetBreakpointAtLine` return positive ids (0 = failure),
`ListBreakpoints` returns typed `BreakpointInfo` records (`LineBreakpointInfo` / `FunctionBreakpointInfo`
subtypes — pattern-matchable), `RemoveBreakpoint(id)` deactivates + releases one entry, and
`ClearBreakpoints` does the live form for all. Backs `drhook_step_breakpoint_list`/`_remove`/`_clear`.
**Date:**     2026-05-22
**Probe:**    `poc/drhook-engine/32-registry-smoke.cs` + `32-registry-target.cs`

## Round-trip

```
set        : function id=1, line A id=2, line B id=3
list       : 3 entries
             - id=1 Function Worker.Step
             - id=2 Line 32-registry-target.cs:32
             - id=3 Line 32-registry-target.cs:33
remove(2) -> True; list now 2 entries
             - id=1 Function Worker.Step
             - id=3 Line 32-registry-target.cs:33
clear      -> 2 removed; list now 0 entries
post-clear : Set returned id=4 (monotonic across clear — registry is still healthy)
PROBE 32 PASSED
```

Pure registry-semantics probe — no breakpoint is ever hit; the target just loops. The assertions check
**every observable**: id positivity & monotonicity, list count after set, subtype + descriptor of each
entry, removal flag, surviving entries after remove, idempotent remove of a missing id (returns false),
clear count, list empty after clear, and that the id allocator keeps incrementing across clear.

## Design — subtypes, not optional fields

`BreakpointInfo` is `abstract record`; concrete forms are `LineBreakpointInfo(Id, ModuleSubstring,
FilePath, Line)` and `FunctionBreakpointInfo(Id, ModuleSubstring, TypeName, MethodName)`. The MCP
list / remove-by-natural-key flows pattern-match on the concrete subtype to recover file/line or
type/method — no nullable-fields-with-a-Kind-enum (the [feedback_no_behavior_flags] anti-pattern in
data shape). Each subtype carries exactly what makes sense for its location kind.

Internal storage mirrors that:

```csharp
private sealed record BreakpointEntry(BreakpointInfo Info, nint Module, nint Function, nint Breakpoint);
private readonly List<BreakpointEntry> _breakpoints = new();
private int _nextBreakpointId;
```

`ListBreakpoints` projects `entry.Info`; `RemoveBreakpoint(id)` does the linear lookup; both run while
stopped. `Breakpoints.Deactivate(pBp)` is a new interop call (`ICorDebugBreakpoint.Activate(FALSE)`)
that runs before the native release on live removal; `Dispose` skips deactivation because Terminate
has already invalidated the breakpoints there.

## API change — `Set*` now returns `int`

Previously `bool`. Now `int`: 0 = failed (same falsy check works textually), positive = id. **All 14
existing probes updated** via a single `sed` pass:

```sed
s|if \(!session\.(SetBreakpoint(AtLine)?)\(([^)]+)\)\)|if (session.\1(\3) == 0)|
```

`if (!session.SetBreakpoint*(…))` becomes `if (session.SetBreakpoint*(…) == 0)`. Mechanical, no
semantic change for those probes (none of them needed the id). Probe 28 regression run confirms no
behavioral drift.

The `int`-as-handle convention matches platform-native debugger APIs (Win32 `SetBreakpoint` /
`DAP setBreakpoints` both use opaque ids). Considered a `BreakpointHandle` record-struct with implicit
`bool` conversion to avoid touching the probes — rejected because the conversion magic hides intent
and the mechanical update is small.

## Probe-design note — markers off header comments (again)

The first run of probe 32 PASSED but with a misleading diagnostic: both line breakpoints bound at
line 3, because `FindMarker` matched the BREAK_A / BREAK_B tokens **inside the target's header
comment** (which mentioned them descriptively) before the actual code lines. Same class of mistake
as probe 17's first run. Fixes:

1. Target header rewritten to mention "two marked code lines" without using the literal tokens.
2. Probe gained an explicit `lineA != lineB` falsifier (return 2) so this trap can't masquerade as a
   pass the next time.

The registry-correctness result was unaffected (the registry doesn't care about source-line
distinctness), but the diagnostic clarity matters and the falsifier closes a latent loophole.

## Scope / next

Closes a second Phase 3 substrate gap. Remaining per ADR-006 Phase 3:

- [x] AsyncBreak (finding 40)
- [x] **Breakpoint registry** (this finding)
- [ ] Launch — `ICorDebug::CreateProcess`; unlocks `drhook_step_run` + `drhook_step_test`
- [ ] Persistent exception filter — generalize `WaitForExceptionPolicyStop` to register-once + drive-many
- [ ] Object inspection (depth ≥ 1) — `ICorDebugType` / `GetFieldValue` / string + array rendering (longest pole)
- [ ] `SteppingSessionManager` rewrite + regression suite + netcoredbg retirement

## References

- Probe: `poc/drhook-engine/32-registry-smoke.cs`, `32-registry-target.cs`
- Fixture: `fixtures/32-registry-osx-arm64-…` (+ a second clean run, exit 0)
- Engine: `BreakpointInfo.cs` (new public types), `DebugSession.SetBreakpoint*` (int return),
  `DebugSession.ListBreakpoints / RemoveBreakpoint / ClearBreakpoints` (new),
  `DebugSession.Dispose` (storage shape adjusted), `Interop/Breakpoints.Deactivate` (new)
- Findings 17 (markers-off-header-comments lesson), 40 (AsyncBreak — same Phase 3 arc)
- Mercury session 2026-05-22 observation `probe-32-breakpoint-registry`
