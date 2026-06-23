# Finding 82 — Argument-name fidelity: frame arguments carry real parameter names; static-method arg 0 no longer mislabelled `this`

**Date:** 2026-06-23
**Status:** Engine + unit + probe validated on macOS/arm64; live MCP (`drhook_locals`) re-check pending a DrHook MCP reconnect.
**Probe:** `poc/drhook-engine/argname-fidelity-smoke.cs` + `argname-fidelity-target.cs`
**Unit test:** `tests/DrHook.Engine.Tests/MethodMetadataTests.cs`

## Context

Surfaced while validating, for the Minerva (local-inference) readiness arc, that DrHook source
breakpoints bind in .NET file-based (single-file) apps. Binding works (live-validated; the PDB
records the original absolute source path and the post-`4f254ef` PDB-walk resolver matches it). But
`drhook_locals` at a stop inside a top-level-program local function showed the function's only
argument as `name = "this"`, value `7` — value correct, **name wrong** (it is `x`).

## Root cause

`src/DrHook.Mcp/EngineSteppingSession.cs` named arguments **positionally**:

```csharp
["name"] = index == 0 ? "this" : $"arg{index}",
```

No `IsStatic`/`HasThis` check and no real parameter-name resolution. So **every static method** — and
in a top-level program *every* local function and static helper is static — mislabelled argument 0 as
`this`, and every other argument showed as `arg1`/`arg2` instead of its declared name. (Locals were
fine: `LocalValue.Name` is resolved from the PDB local scopes via `SymbolReader.GetLocalNames`; only
arguments used placeholders, because `ArgumentValue` carried no name at all.)

Parameter names and instance-vs-static are **not** in the Portable PDB — they live in the main
assembly metadata (the `Param` table + the method signature's `HasThis` calling-convention bit).

## Fix

Mirror the locals path for arguments:

- **`src/DrHook.Engine/MethodMetadata.cs`** (new) — `ArgumentNames(modulePath, methodToken)` reads the
  PE `Param` table + signature header via `System.Reflection.Metadata` (pure managed, BCL-only,
  file-based — the same testability bar as `SymbolReader`). Returns argument names aligned to
  `ICorDebugILFrame.GetArgument(i)` indices: `this` at 0 iff the method is instance, then declared
  parameters by sequence number.
- **`ArgumentValue`** gains a `Name` field (default `""`, appended so the value-carrier sites —
  `Eval.GetResultValue`, `Variables.ReadValue` — compile unchanged).
- **`DebugSession.GetArguments`** walks to the top frame (as `GetLocals` does), resolves names via
  `MethodMetadata.ArgumentNames`, and threads them into `Variables.ReadActiveFrameArguments`, which
  sets each `ArgumentValue.Name` (positional `argN` fallback when a name is unavailable).
- **`EngineSteppingSession.ArgumentValueToJson`** renders `a.Name` — the `index == 0 ? "this"` hack is
  removed.
- **`EngineSteppingSession.ExpandAsync`** (the `drhook_expand` handler) resolves the target to an
  argument by its *real* name → index (then the positional `argN` fallback, else a local). This was a
  **regression the naming change introduced and the first probe missed** (it asserted names only): the
  old handler routed only `"this"`/`"argN"` to `ExpandArgument`, so once an object argument displayed as
  its real name (`d`, `seed`, …) it fell through to `ExpandLocal` and found nothing. `this` itself kept
  working (still named `"this"`). The eval/member-call path is unaffected (it resolves receivers against
  *locals* by name; `this`-as-an-argument was never a valid member-eval receiver — a pre-existing gap).

The conditional-breakpoint evaluator is untouched: `CSharpCondition.Schema.From` still binds arguments
positionally (`arg0`/`arg1`), so adding `Name` is purely additive (zero risk to existing conditions).
Enabling by-name argument references in conditions is a separate future enhancement.

## Construction & outcome

`MethodMetadataTests` reads this test assembly's own metadata for a static fixture
`StaticFixture(int seed, long factor)` and an instance fixture `InstanceFixture(int delta, int times)`
— deterministic, CI-safe, no debuggee:

- static → `[seed, factor]`, no `this`; instance → `[this, delta, times]`; non-method token / missing
  module → empty. **4/4 PASS.** Full `DrHook.Engine.Tests`: **123/123** (119 prior + 4 new), no regression.

`argname-fidelity-smoke.cs` builds a **file-based** target to a DLL (build-first), launches it under
`DebugSession`, takes the `Debugger.Break` setup stop, arms source breakpoints in a static method and an
instance method, and asserts `GetArguments()` names at each hit:

```
bound      : static bp id=1, instance bp id=2 (file-based binding OK)
static args: [seed, factor]
inst args  : [this, d]
this[Box]  : inline _base==100 True; expand _base==100 True  (fields: _base=100)
d[Delta]   : By=5, Times=2
PROBE PASSED
```

This pins **file-based source-breakpoint binding** (the Minerva-critical path, previously without
regression coverage); validates the argument-name fix through the real ICorDebug stop → `GetArguments`
path; and validates that **`this` and a non-`this` object argument are inspectable** — `this`[Box]`._base`
read both at depth 1 (the `drhook_locals` view) and via `ExpandArgument(0)` (the `drhook_expand` path),
and the object parameter `d`[Delta]`.{By,Times}` via `ExpandArgument(1)`. All read by INDEX in the engine
(the value/field reads are unchanged by the naming fix). Independent of the MCP server (`#:project`).

## Validation status

- Engine + unit + end-to-end probe: **green** on macOS/arm64.
- `DrHook.Mcp` + `DrHook.Engine.IntegrationTests` build clean (0 warnings under `TreatWarningsAsErrors`).
- **Pending (needs a DrHook MCP reconnect — it runs the pre-fix build until restarted):** live
  `drhook_locals` showing real names, and live `drhook_expand` by an argument's real name (e.g.
  `drhook_expand "d"`). Both MCP changes build clean; the engine pieces they compose onto
  (`GetArguments` names, `ExpandArgument` by index) are probe-validated above.

## References

- DRHOOK.md — file-based-app launch + cache considerations.
- `SymbolReader.GetLocalNames` — the PDB-side counterpart this mirrors.
- Mercury session graph `https://sky-omega.dev/sessions/2026-06-23-drhook-filebased-breakpoints/`.
