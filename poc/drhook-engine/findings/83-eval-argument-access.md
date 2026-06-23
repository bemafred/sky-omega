# Finding 83 — Argument access in condition / logpoint evaluation (CSharpCondition)

**Date:** 2026-06-23
**Status:** Engine + unit validated on macOS/arm64; live MCP before/after pending a DrHook reconnect (the server runs the pre-fix build until restarted).
**Builds on:** [Finding 82](82-argument-name-fidelity.md) (argument-name fidelity — the prerequisite).

## Context

DrHook conditional breakpoints and logpoints share one `BreakpointPolicy` (condition / hitCount /
logMessage / suspend); the condition and template fragments are C# expressions compiled by
`CSharpCondition` (Roslyn → `Expression.Compile`). A live capability check through the MCP —
`drhook_break_source` with `condition "i % 250 == 0"` + `logMessage "loop i={i} doubled={doubled}"`
+ `suspend none` — confirmed conditions, `%`-gating, and non-stopping logpoints all work over
**locals** (logged at i=0,250,500,750 without stopping). But a condition referencing an **argument**
faulted: `stoppedReason: conditionError`, fault log `"condition fault: local 'index' not found at this
stop"`. This is the limitation recorded 2026-05-21 ("eval resolves locals only, not arguments").

## Root cause

`CSharpCondition.ReadLocalRaw` scanned `ctx.Locals` only; `Schema.From` keyed arguments positionally
(`arg0`/`arg1`) and `BuildIdentifier` consulted only `Schema.Locals`. An identifier could resolve a
local by name but never an argument. (Until the same-day argument-name fidelity work — finding 82 —
arguments carried no real names to resolve against; that work unblocked this.)

## Fix

- `Schema.From` keys arguments by their real `Name` (`this` + declared parameters); a local shadows a
  same-named argument (locals are the inner scope).
- `BuildIdentifier` consults `Locals` **or** `Arguments` for the typed unbox.
- `ReadLocalRaw` → `ReadIdentifierRaw`: scans locals then arguments by name; the not-found message is
  now `"identifier '…' not found at this stop (no local or argument)"`.

**Scope:** bare identifiers in conditions **and** logpoint templates — `index % 2 == 0`, `seed == 7`,
`logMessage "idx={index}"`. **Not** included: member-access *on* an argument receiver (`d.Times > 1`),
which needs the func-eval receiver to resolve from an argument slot (`TryEvalMemberCall` /
`ResolveLocalSlot` are locals-only) — a deeper, separately-scoped follow-on.

## Validation

- `CSharpConditionTests` +6 (argument equality; `%`-gating over a parameter; local-shadows-argument;
  identifier-not-found) and `CSharpConditionTemplateTests` +1 (logpoint template interpolates an
  argument). Full `DrHook.Engine.Tests` **130/130**, no regression. `DrHook.Mcp` builds clean under
  `TreatWarningsAsErrors`. The evaluator is pure logic over `IEvalContext`, so unit tests are its
  primary validation (its live integration path is the same one probes 22–25 exercised).
- **Pending (DrHook reconnect):** live before/after — the demo's `index == 3` condition that returned
  `conditionError` pre-fix should now stop cleanly.

## References

- `src/DrHook.Engine/Expressions/CSharpCondition.cs`
- Mercury session graph `https://sky-omega.dev/sessions/2026-06-23-drhook-filebased-breakpoints/`
  (`obs/conditional-bp-logpoint-validated`).
