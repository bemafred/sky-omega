# ADR-014: DrHook inspection robustness — value-type read safety and fault containment

**Status:** Proposed — 2026-06-22 (Emergence)

## Context

[ADR-013](ADR-013-value-type-refstruct-inspection.md) (D1/D2, commit `1fe8dcc`) extended the lazy inspection to VALUETYPE (`0x11`) structs and BYREF (`0x10`) ref-structs, and was validated for *simple* shapes (`poc/drhook-inspection-robustness/adr013-vt-*`: a `Box` struct + `ReadOnlySpan<char>` expand cleanly).

The ADR-050 dogfood finale (2026-06-22) then surfaced the boundary. With a breakpoint stopped in `BindingTable.EnsureStringCapacity` (a ref-struct method, so `this` is BYREF), `drhook_locals` — which now expands `this` → the `BindingTable` value type → its fields, including `Span<Binding>` and `Span<char>` — **crashed the DrHook engine with `SIGABRT` in `libmscordbi`** (crash report `drhook-mcp-2026-06-22-040558.ips`). The debugged target survived; the engine (MCP server) died. (Recorded: `ck:obs-drhook-adr050-finale`.)

So inspecting a *complex* ref-struct is not yet safe, and that exposes **two distinct gaps**:

1. **Value-type / span read safety.** Some value-type shapes are read cleanly (`ReadOnlySpan<char>`), but others abort mscordbi when read via the ICorDebug path D1/D2 uses (`BindingTable`, whose fields include `Span<Binding>` — a span *of structs* — and a `Span<char>` whose `_reference` is a managed byref). The boundary is shape-specific and uncharacterized.
2. **No fault containment.** An inspection fault — whether `SIGSEGV` (the original depth-2 `QuadStore` crash, ADR-007:65) or `SIGABRT` (this one) — **takes the engine down** instead of degrading to a surfaced anomaly. A debugger inspecting arbitrary frames must never let a frame's shape crash the debugger. ADR-007's inspection item already named this ("a walk fault surfaces as an anomaly, not process death"); it is now **demand-proved a second time**, by a different signal.

These faults are **native** (in mscordbi, below the managed plane), so a managed `try/catch` cannot contain them — the same wall the original hunt hit.

## Decision (proposed)

Make inspection robust against arbitrary frame shapes, under the lazy/bounded discipline. Two lines, read-safety first:

### D1 — Value-type / span read safety (the first line of defense)

Characterize which value-type/span shapes abort mscordbi (probe, in the `poc/drhook-inspection-robustness/` style: `Span<struct>`, `Span<char>` with a live `_reference`, `ByReference<T>`, nested ref-struct fields, the real `BindingTable`). Then, for shapes that can't be read safely, **render them opaque** — exactly as raw pointers and ref-structs are already rendered opaque at depth — *before* attempting the aborting native call, rather than calling it and dying. Reading must be a checked operation, not a leap.

### D2 — Fault containment (the backstop)

An inspection operation must not be able to crash the engine, even when D1's read-safety is incomplete (an unforeseen shape). Because the fault is native and uncatchable in-process, options to evaluate:
- **(a) Pre-validate before the native call** — the D1 line; cheapest, but only covers known-bad shapes.
- **(b) Out-of-process / isolated inspection** — perform the risky walk in a child or worker that can die without taking the engine + MCP server down, surfacing a `BreadthClamped`/`InspectionFault` anomaly on its death. Heavier; the real backstop for *unknown* shapes.
- **(c) A native (Mach) exception/signal handler** that converts an inspection-thread fault into a recoverable error. Fragile after a native AV; evaluate but likely insufficient alone.

The decision between (b) and (c) is an Open Question to resolve by probe, not up front.

### Scope discipline

Keep the ADR-013 lazy/bounded model. This ADR is robustness, not new expansion surface. Out of scope: D3 span-aware *conditions* (still ADR-013).

## Validation

**Proposed → Accepted** once the mscordbi-abort shapes are characterized (D1 probe) and the containment approach (D2: b vs c) is chosen.

**Accepted → Completed** when:
- A probe inspects an arbitrary ref-struct — including the real `BindingTable` frame — and every field is either read or rendered opaque, **never a crash**.
- An *injected* unreadable shape degrades to a surfaced anomaly (not process death) — containment proven, not assumed.
- The ADR-050 finale re-runs end-to-end under DrHook with no engine crash: `drhook_locals` at `EnsureStringCapacity` reads `additional`/`target`/`this._stringBuffer.Length` cleanly.

## Consequences

- DrHook becomes safe to point at *any* frame — the substrate promise. Today an unlucky frame shape (a span of structs) can still drop the engine; that's the gap this closes.
- The `EngineAnomaly` infrastructure gains an inspection-fault kind, extending the surprises-as-substrate-grade loop to native inspection faults.
- Establishes that inspection *reads* are checked operations, closing the class the two crashes (SIGSEGV depth-2, SIGABRT complex-ref-struct) both belong to.

## References

- [ADR-013](ADR-013-value-type-refstruct-inspection.md) — the expansion (D1/D2) that surfaced this boundary.
- [ADR-007](ADR-007-teardown-concurrency-test-debug.md) inspection-at-scale item — first called for fault containment; the original depth-2 `QuadStore` SIGSEGV.
- Mercury [ADR-050](../mercury/ADR-050-growable-result-binding-buffer.md) — the dogfood finale that demand-proved this.
- `ck:obs-drhook-adr050-finale` — the finding (SIGABRT in mscordbi; target survived).
- `drhook-mcp-2026-06-22-040558.ips` — the crash report (`SIGABRT`, `libmscordbi`).
- `poc/drhook-inspection-robustness/adr013-vt-*` — the simple shapes that DO inspect cleanly (the contrast).
