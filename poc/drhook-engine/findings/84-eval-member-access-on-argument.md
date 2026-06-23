# Finding 84 — Member-access on an argument receiver in eval (conditions / logpoints)

**Date:** 2026-06-23
**Status:** Engine + probe validated (live func-eval) on macOS/arm64.
**Builds on:** [Finding 83](83-eval-argument-access.md) (bare-identifier argument access) and
[Finding 82](82-argument-name-fidelity.md) (argument-name fidelity).
**Probe:** `poc/drhook-engine/eval-arg-receiver-smoke.cs` + `eval-arg-receiver-target.cs`

## Context

Finding 83 let a condition reference an argument by name as a *bare identifier* (`index % 2 == 0`).
Member-access *on* an argument receiver still failed: `DebugSession.TryEvalMemberCall` (the live
condition/logpoint member path) and `TryEvalInstanceCall` resolved the receiver via `ResolveLocalSlot`
— **locals only** — so a condition like `w.Area == 200` where `w` is a parameter faulted with a
`conditionError`. This was the residual half of the 2026-05-21 limitation "eval resolves locals only,
not arguments".

## Fix

New `DebugSession.ResolveReceiverValue(name)` resolves a receiver to an OWNED `ICorDebugValue` from a
named local slot (`GetActiveFrameLocalValue`) or — failing that — an argument by its metadata name
(`MethodMetadata.ArgumentNames` → `GetActiveFrameArgumentValue(index)`). A local shadows a same-named
argument, matching the condition evaluator's identifier resolution (finding 83). **Both**
`TryEvalMemberCall` (live condition/logpoint path) and `TryEvalInstanceCall` now use it — uniform
receiver resolution, not a one-path patch. (`TryEvalInstanceCall` also shed its inline local-slot
lookup in the process.)

The getter is still resolved on the receiver's runtime type (`MemberResolver.ResolveGetter`), so this
covers **properties**; reading a plain field through member-access remains the inspector's job
(`drhook_locals` / `drhook_expand`), not the eval path.

## Validation

`eval-arg-receiver` probe: `Describe(Widget w)` — `w` is an argument; `Widget` has explicit `Width` /
`Height` fields and a computed `Area => Width * Height` property. A conditional breakpoint
`w.Area == 200` is armed; the target calls `Describe(new Widget(5,5))` (Area=25, FALSE) then
`Describe(new Widget(10,20))` (Area=200, TRUE). A clean `Breakpoint` stop (not `conditionError`) on the
10×20 call proves the getter func-evaluated on the argument receiver on **both** calls and **gated**
correctly; inspecting `w` confirms `Width=10, Height=20`. Full `DrHook.Engine.Tests` **130/130**, no
regression; `DrHook.Mcp` builds clean.

## Closure

With findings 82 + 83 + 84, argument access across DrHook inspection **and** evaluation is complete:
arguments carry real names (display), conditions/logpoints reference arguments by name, and
member-access calls getters on argument receivers. The substrate no longer has a "locals work,
arguments don't" seam in the breakpoint/inspection surface.

## References

- `src/DrHook.Engine/DebugSession.cs` (`ResolveReceiverValue`, `TryEvalMemberCall`, `TryEvalInstanceCall`)
- Mercury session graph `https://sky-omega.dev/sessions/2026-06-23-drhook-filebased-breakpoints/`.
