# Finding 30: Probe 22 Outcome — PASSED: standard-C# conditional breakpoints (the Roslyn front end)

**Status:**   **PASSED, 2/2 — the capability that started this whole thread.** A breakpoint with a
condition written in ordinary C#, evaluated against the live debuggee, stopping only when it holds.
Roslyn parsed `"value == 3"`; a tree-walk interpreter evaluated it against the engine's
`IEvalContext` (the frame's named locals); `DebugSession.WaitForConditionalStop` surfaced the stop
**exactly** on the iteration where `value == 3`, skipping the other six. **netcoredbg could not do
conditional breakpoints on macOS/ARM64 (func-eval deadlock); DrHook.Engine does.**
**Date:**     2026-05-22
**Probe:**    `poc/drhook-engine/22-conditional-bp-smoke.cs` + `22-cond-target.cs`

## Architecture (substrate stays lean; Roslyn lives above it)

The engine stayed **BCL-only**. It exposes:
- `IEvalContext` — a read-only snapshot of a stop's named locals + arguments.
- `DebugSession.WaitForConditionalStop(Func<IEvalContext,bool> condition, timeout)` — loops
  `WaitForStop`; at each **breakpoint** hit it snapshots the frame and evaluates the predicate; if
  false it `Resume`s and waits for the next hit; if true it surfaces. The breakpoint marks WHERE; the
  predicate decides WHETHER. The predicate is a plain delegate — **the engine knows nothing about C#
  or Roslyn.**

The **Roslyn front end lives in the probe** (`#:package Microsoft.CodeAnalysis.CSharp@4.11.0`): a ~40-line
`CSharpCondition.Compile` parses the expression with `SyntaxFactory.ParseExpression` and walks the tree
(literals, local identifiers → `IEvalContext.Locals`, comparison + boolean operators) into a
`Func<IEvalContext,bool>`. Roslyn restored and ran cleanly. This validated the front end **without
adding Roslyn to the core engine** — the dependency-placement decision (a separate
`DrHook.Engine.Expressions` package vs caller-side) can now be made on evidence.

```
condition  : "value == 3" at 22-cond-target.cs:32
running    : breakpoint set; resuming until "value == 3" …
stopped    : condition held — value = 3
PROBE 22 PASSED — stopped exactly when it held (value=3).
```

## What this proves

1. **Standard C# in, correct stop out.** The LLM consumer writes ordinary `value == 3` — no custom
   dialect — and the engine stops precisely when it holds. The whole product rationale, demonstrated.
2. **The eval substrate composes with a front end.** Roslyn parse → tree-walk → `IEvalContext` reads →
   boolean → conditional-stop loop. Each layer was already proven; this wires them.
3. **No func-eval re-entrancy for primitive conditions.** The predicate reads locals synchronously at
   the stop — no nested Continue — so the conditional-stop loop is clean.

## Scope / next

- **Slice done:** conditions over primitive locals/args (`value == 3`, `a > 5 && b < 100`).
- **Member-access conditions** (`s.Length > 3`, `list.Count == 0`) need func-eval *inside* the
  predicate — and func-eval uses `Resume`/`WaitForStop`, which the conditional-stop loop is already
  using, so naive nesting re-enters. The eval substrate is proven (probes 19–21); wiring a func-eval
  into the predicate path without re-entrancy is the next increment.
- **Multiple conditional breakpoints** — today the predicate applies at any breakpoint hit; per-breakpoint
  conditions need breakpoint identity at the stop.
- **Front-end home** — extract `CSharpCondition` to `DrHook.Engine.Expressions` (engine + Roslyn) so the
  core engine stays BCL-only and the C# layer is an optional package.

## References

- Probe: `poc/drhook-engine/22-conditional-bp-smoke.cs`, `22-cond-target.cs`
- Fixture: `fixtures/22-conditional-bp-osx-arm64-20260522T144204Z.txt`
- Engine: `IEvalContext` (`ArgumentValue.cs`), `DebugSession.WaitForConditionalStop` + `EvalContext`
- Findings 27–29 (func-eval works + breadth — the substrate the member-access slice will use), 26 (locals), 16 (stopping model)
- ADR-006 Open Question 2 — the conditional-breakpoint/eval question, now answered in the affirmative
- Mercury session 2026-05-21 observation `probe-22-conditional-bp`
