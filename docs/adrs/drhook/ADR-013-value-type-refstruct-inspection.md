# ADR-013: Inspection of value types and ref structs — VALUETYPE + BYREF field expansion, and span-aware expression evaluation

**Status:** Accepted — 2026-06-21

## Context

The lazy-inspection first increment ([ADR-007](ADR-007-teardown-concurrency-test-debug.md) inspection item, commit `6fefa8b`) made `drhook_locals` return one level with a `HasChildren` flag and added `drhook_expand` for on-demand, bounded, navigable descent. It expands **CLASS (`0x12`) / OBJECT (`0x1C`) / ARRAY (`0x14`/`0x1D`)** — reference and array types. It deliberately left structs and ref-structs for a follow-on (`ADR-007:65`: *"struct (VALUETYPE) field expansion, null-ref HasChildren precision"*).

That follow-on is now **demand-proved**, not speculative. On 2026-06-21, hunting the Mercury long-literal bug (Mercury [ADR-050](../mercury/ADR-050-growable-result-binding-buffer.md)), DrHook stopped at `BindingTable.Bind:115` and we needed to read three runtime values to confirm the overflow: the span argument's `value.Length`, and the receiver's `_stringOffset` and `_stringBuffer.Length`. We could read **none** of them:

- `this` reported element type **`0x10` (BYREF)** — a `ref BindingTable` (ref-struct method receiver). Not in `IsExpandable` → `drhook_expand` returns nothing.
- The span arguments reported element type **`0x11` (VALUETYPE)** — `ReadOnlySpan<char>` is a ref struct. Not in `IsExpandable` → opaque.
- The breakpoint condition `value.Length > 1000` **faulted** (`conditionError`) — the `CSharpCondition` evaluator (ADR-010 Increment 7) can't read `ReadOnlySpan<T>.Length`.

The bug was confirmed instead via the live call stack + static analysis — which worked, but DrHook should have read the values directly. **Mercury and Minerva are span-heavy, zero-GC, ref-struct-dense substrates**; "can't inspect a struct or a span" is a recurring blindness, not a one-off. This is the natural increment 2 of the lazy-inspection arc.

## Decision (proposed)

Extend the lazy, bounded, one-level-per-call inspection to value types and ref-structs, and teach the expression evaluator to read spans. Keep the ADR-007 discipline: **no eager recursion** — each expansion is one bounded level; a struct/span is just another node with `HasChildren`.

### D1 — VALUETYPE (`0x11`) field expansion

A struct's fields enumerate like a class (`ICorDebugValue2.GetExactType` → metadata field enum → `GetFieldValue`), but the value **is** the struct — there is no reference to dereference. `FieldEnumerator` currently QIs `ICorDebugObjectValue` and, on failure, dereferences an `ICorDebugReferenceValue`; for a value type the object-value path applies directly (skip the deref). Add `0x11` to `Variables.IsExpandable` and handle the no-deref case. Covers ordinary structs and the inner struct reached through a byref (D2).

### D2 — BYREF (`0x10`) expansion

A byref (`ref T` — e.g. the receiver of a ref-struct instance method) is a reference to a value. Expansion dereferences **one level** to the target value, then expands that (which may itself be a VALUETYPE per D1). Add `0x10` to `IsExpandable`; the navigation step derefs the byref and continues. This makes `this` inspectable inside ref-struct methods — the exact case that blocked the Mercury hunt.

### D3 — Span-aware expression evaluation

Teach the `CSharpCondition` typed walker to read `ReadOnlySpan<T>` / `Span<T>` members — at minimum `.Length` (the struct's `_length` field), so conditions and logpoints like `value.Length > 1000` compile and evaluate. Spans cannot be boxed, so the evaluator reads the field directly from the frame value rather than materialising the span. This unblocks the conditional-breakpoint workflow on the hottest substrate code (every parser/executor span).

### Scope discipline

These are **increments 2–3** of the lazy-inspection arc, under the same rules as increment 1: bounded (one level), lazy (`HasChildren` + `drhook_expand`), no eager walk. `null`-ref `HasChildren` precision (`ADR-007:65`) rides along where cheap. Out of scope: multi-dimensional arrays (existing follow-on), frame selection (ADR-010 Tier 2).

## Validation

**Proposed → Accepted** on review of the VALUETYPE/BYREF interop approach (the no-deref value-type path and the byref single-deref are the pieces to verify against Rider as oracle, per `reference_rider_as_oracle`).

**Accepted → Completed** when:
- A VALUETYPE struct local expands to its fields via `drhook_expand`.
- A BYREF `this` (ref-struct method) expands to the struct, then to its fields.
- A `ReadOnlySpan<char>` expands to show `_length`, **and** a breakpoint condition `span.Length > N` compiles and evaluates (no `conditionError`).
- A probe + regression test under the file-based PoC convention.
- **The finale (dogfooding at its best):** re-run the Mercury [ADR-050](../mercury/ADR-050-growable-result-binding-buffer.md) hunt under this fix and read `value.Length`, `_stringOffset`, and `_stringBuffer.Length` *directly* at `BindingTable.Bind:115` — confirming the DrHook fix — then watch `_stringBuffer.Length` grow and the binding succeed after the Mercury fix — confirming that one. One session validates both fixes, with no custom debugging tool.

## Consequences

- DrHook becomes capable for the substrate it is meant to observe: zero-GC, span-heavy, struct-dense Mercury/Minerva code. The blindness that forced static-analysis fallback in the Mercury hunt is closed.
- The lazy-inspection arc advances from "reference types only" to "the value-type and ref-struct surface" — most of what a zero-GC substrate's hot frames actually contain.
- Demonstrates the thesis the Mercury hunt already showed: a general runtime-observation substrate, improved through its own dogfooding feedback, replaces bespoke per-bug instrumentation.

## References

- `src/DrHook.Engine/ArgumentValue.cs` — `IsExpandable` (extend with `0x11`, `0x10`).
- `src/DrHook.Engine/Interop/FieldEnumerator.cs` — value-type (no-deref) field enumeration.
- `src/DrHook.Engine/Interop/Variables.cs` — `GetChildValueByName` / byref deref step.
- `src/DrHook.Engine/Expressions/CSharpCondition.cs` — span member access (`.Length`).
- [ADR-007](ADR-007-teardown-concurrency-test-debug.md) inspection-at-scale item — the parent arc (increment 1 landed `6fefa8b`).
- Mercury [ADR-050](../mercury/ADR-050-growable-result-binding-buffer.md) — the hunt that demand-proved these gaps; the joint DrHook-validates-both finale.
- `ck:obs-mercury-update-long-literal-empty` (`ck:relatedFinding`) — the gaps recorded at discovery.
- `reference_rider_as_oracle` — Rider validates VALUETYPE/BYREF rendering correctness on macOS.
