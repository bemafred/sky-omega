# Finding 34: Probe 25 Outcome — PASSED: Roslyn member-access walker (`box.Size == 42` fully parsed)

**Status:**   **PASSED, 2/2** (clean exit 0 both runs). A breakpoint condition written in ordinary C#,
`box.Size == 42`, is parsed by Roslyn, walked, and the member access is func-eval'd on the operand's
**runtime type** each hit — nothing about `Box` is hardcoded. The target's `box.Size` cycles 40..44; the
conditional breakpoint surfaced **exactly** on the `Size == 42` iteration. This joins the three prior threads
into one end-to-end capability.
**Date:**     2026-05-22
**Probe:**    `poc/drhook-engine/25-member-walker-smoke.cs` + `25-member-target.cs`

## What this joins

- **Probe 22** (finding 30): Roslyn front end, but conditions over **primitive locals** only (`value == 3`).
- **Probe 23** (finding 31): func-eval works **inside** a conditional predicate — but the predicate was
  **hardcoded** (`s.Length`, hardcoded `String.get_Length`).
- **Probe 24** (finding 32): **general** member resolution — `TryEvalMemberCall` resolves the getter on a
  value's runtime type, no hardcoded declaring type/module.

Probe 25 wires (24) into the walker of (22), exercising the re-entrancy proven in (23): the walker's `Eval`
switch gained a `MemberAccessExpressionSyntax` arm that calls `session.TryEvalMemberCall(operand, member)`.
A parsed `box.Size == 42` is now a real conditional breakpoint, not a hand-written predicate.

```
condition  : "box.Size == 42" (Roslyn-parsed, member func-eval'd) at 25-member-target.cs:38
running    : breakpoint set; resuming until "box.Size == 42" (member func-eval'd each hit) …
stopped    : condition held — box.Size eval Completed, value=42
PROBE 25 PASSED — fully-parsed member-access conditional "box.Size == 42" stopped exactly when it held (box.Size=42).
```

## The walker arm (the whole increment)

```csharp
MemberAccessExpressionSyntax ma when ma.Kind() == SyntaxKind.SimpleMemberAccessExpression
    => ResolveMember(session, ma),
// …
static object ResolveMember(DebugSession session, MemberAccessExpressionSyntax ma)
{
    if (ma.Expression is not IdentifierNameSyntax target)        // operand must be a local identifier
        throw new NotSupportedException(...);
    string thisLocal = target.Identifier.Text;                   // "box"
    string member    = ma.Name.Identifier.Text;                  // "Size"
    EvalStatus st = session.TryEvalMemberCall(thisLocal, member, …, out ArgumentValue v);
    if (st != EvalStatus.Completed) throw …;
    return v.RawValue ?? throw …;                                // 42, fed back into ApplyBinary
}
```

The walker closes over `DebugSession` (the engine stays BCL-only; Roslyn lives in the probe). For the member
access the walker only needs the operand identifier (`box`) and the member name (`Size`) — the engine derives
the runtime class, declaring module, and getter from the live value (finding 32).

## Why the gating is real (not a one-shot)

`box.Size` cycles 40,41,42,43,44 across loop iterations, so the unconditional breakpoint hits **every**
iteration. The predicate func-evals `box.Size` at each hit and resumes when it isn't 42 — the stop surfaces
**only** at 42. This exercises the func-eval-in-predicate re-entrancy (finding 31) repeatedly under a real
gate, not just once.

## Scope / next

- **Operand is a local identifier.** `a.b.c` (chained), member-of-member, and operand-from-arguments are not
  yet handled (`this`-from-args is the same gap probe 23 hit). Chained access = recurse `ResolveMember` on a
  reference-valued result — a follow-on.
- **Strings / arrays / generics** still need the `ICorDebugType` path (finding 32 scope note): `s.Length`
  generically isn't here yet (those aren't `ICorDebugObjectValue`).
- **Fields** (vs property getters): `ICorDebugObjectValue.GetFieldValue@8` — a sibling of the getter path.
- **Logpoint convergence** (finding 33): this exact walker, rendering to string instead of comparing, is the
  message interpolator for a log action. The next engineering step is the `BreakpointPolicy` (condition +
  hit-count gates, log action, Suspend ∈ {All,None}) that makes conditions and logpoints two configs of one
  policy.
- **Engine home**: `DrHook.Engine.Expressions` (Roslyn-dependent, separate from the BCL-only core) when this
  graduates from probe to engine.

## References

- Probe: `poc/drhook-engine/25-member-walker-smoke.cs`, `25-member-target.cs`
- Fixture: `fixtures/25-member-walker-osx-arm64-20260522T161514Z.txt` (+ a second clean run, exit 0)
- Engine (unchanged this probe): `DebugSession.WaitForConditionalStop`, `TryEvalMemberCall`, `IEvalContext`
- Findings 30 (Roslyn front end), 31 (func-eval in predicate), 32 (general member resolution), 33 (breakpoint-types model)
- Mercury session 2026-05-22 observation `probe-25-member-walker`
