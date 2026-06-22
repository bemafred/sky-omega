# ADR-014: DrHook inspection robustness — value-type read safety and fault containment

**Status:** Completed — 2026-06-22 (Engineering) — *Proposed → Accepted → Completed same day; the D1 probe overturned the ADR's own span-specific hypothesis (see Root cause), the checked-read fix landed and is validated on the synthetic + the real `BindingTable`, and the D2 backstop was deliberately deferred.*

## Context

[ADR-013](ADR-013-value-type-refstruct-inspection.md) (D1/D2, commit `1fe8dcc`) extended the lazy inspection to VALUETYPE (`0x11`) structs and BYREF (`0x10`) ref-structs, and was validated for *simple* shapes (`poc/drhook-inspection-robustness/adr013-vt-*`: a `Box` struct + `ReadOnlySpan<char>` expand cleanly).

The ADR-050 dogfood finale (2026-06-22) then surfaced the boundary. With a breakpoint stopped in `BindingTable.EnsureStringCapacity` (a ref-struct method, so `this` is BYREF), `drhook_locals` — which now expands `this` → the `BindingTable` value type → its fields, including `Span<Binding>` and `Span<char>` — **crashed the DrHook engine with `SIGABRT` in `libmscordbi`** (crash report `drhook-mcp-2026-06-22-040558.ips`). The debugged target survived; the engine (MCP server) died. (Recorded: `ck:obs-drhook-adr050-finale`.)

So inspecting a *complex* ref-struct is not yet safe, and that exposes **two distinct gaps**:

1. **Value-type / span read safety.** Some value-type shapes are read cleanly (`ReadOnlySpan<char>`), but others abort mscordbi when read via the ICorDebug path D1/D2 uses (`BindingTable`, whose fields include `Span<Binding>` — a span *of structs* — and a `Span<char>` whose `_reference` is a managed byref). The boundary is shape-specific and uncharacterized.
2. **No fault containment.** An inspection fault — whether `SIGSEGV` (the original depth-2 `QuadStore` crash, ADR-007:65) or `SIGABRT` (this one) — **takes the engine down** instead of degrading to a surfaced anomaly. A debugger inspecting arbitrary frames must never let a frame's shape crash the debugger. ADR-007's inspection item already named this ("a walk fault surfaces as an anomaly, not process death"); it is now **demand-proved a second time**, by a different signal.

These faults are **native** (in mscordbi, below the managed plane), so a managed `try/catch` cannot contain them — the same wall the original hunt hit.

## Root cause (characterized 2026-06-22 — the probe overturned the hypothesis)

The Context above hypothesised a **shape-specific** boundary: that some value-type/span shapes (`Span<struct>`, a `Span<char>` with a live `_reference`) abort mscordbi while others read cleanly. The D1 probe (`poc/drhook-inspection-robustness/adr014-faultrepro-*` — three receiver shapes reached via the byref `this`, each field expanded in its own process) **refuted that** and pinned the real cause:

- **It is not shape-specific and not span-specific.** Reading *any* field — even an `int` — aborts when the receiver struct is large; reading the same fields off a smaller struct is safe. The boundary is **size**, not kind.
- **The crash signature is an access violation, not a stack-canary trip.** The crash report's faulting thread is `abort ← PROCAbort ← TerminateProcess ← EEPolicy::HandleFatalError ← FailFastIfCorruptingStateException ← SfiInitWorker ← HandleHardwareException ← [JIT'd inspection code] ← RuntimeMethodHandle_InvokeMethod`. A hardware AV in our unsafe COM-vtable walk, which the CLR classifies as a **Corrupting State Exception** and fail-fasts — so a managed `try/catch` genuinely cannot contain it (confirming the ADR's premise). This **unifies** the new SIGABRT with the original ADR-007 depth-2 `QuadStore` SIGSEGV: both are AVs in the native inspection walk.
- **The mechanism:** `Variables.ReadValue` copied the *whole* value via `ICorDebugGenericValue.GetValue` into a fixed **8-byte** stack `long buffer`, for **any** value whose generic-value QI succeeds — including a multi-field `VALUETYPE`. A `Span<T>` is 16 bytes; `BindingTable`/the synthetic `Mimic` is ~48. The copy overran the buffer by tens of bytes, corrupting the frame. Whether the corruption crashes depends on the surrounding stack layout — which is exactly why it presented as a **Heisenbug**: adding *any* code (even a no-op trace call) between the native calls shifted the layout and masked it.
- **Size law, confirmed by the probe:** `PlainRef` (≈8 B) safe; `NormalBox`/`ReadOnlySpan<char>` (≈16 B) overflow-but-harmless → safe (which is why ADR-013's simple shapes "validated"); `Mimic`/`BindingTable` (≈48 B) → crash.

`ck:obs-adr014-crash-is-av-not-overflow`, `ck:obs-adr014-root-cause`.

## Decision

### D1 — Value-type read safety: make the read a *checked* operation (DONE)

`Variables.ReadValue` now queries `ICorDebugValue.GetSize` (vtable slot 4 — a metadata read that does **not** copy) **before** the value copy, and only reads into the 8-byte buffer when the value fits (`size <= 8`). Primitives and enums still read; a large value type renders via its fields with a null raw and `HasChildren = true` — **navigable, not amputated**. This is the precise non-amputating fix: the finale's whole purpose — reading `this._stringBuffer.Length` and the other `BindingTable` fields — is preserved.

A read-surface **audit** confirmed `ReadValue`'s `GetValue` was the *only* unchecked value-byte read: `StringInspector` already sizes its buffer from `GetLength`, and `Eval`'s `SetValue` writes a matched 4-byte I4 (`ck:obs-adr014-read-surface-audited`). So D1 closed the *class*, not just the one site. "Reads are checked operations" is now the standing rule for the inspection surface.

### D2 — Fault containment backstop: **deferred** (decision 2026-06-22)

With the read surface audited-clean, the uncatchable-CSE-fail-fast fault class has **no remaining demonstrated trigger**. Building the out-of-process backstop now would be engineering against a hypothetical. Decision (Martin, 2026-06-22): **defer** the heavyweight out-of-process isolation — option (b) — to a dedicated production-suitability effort (ADR-007 Phase 9 / a successor ADR); it is the right backstop for *unforeseen* ICorDebug AVs but is a large architectural lift (the debug session is owned by the engine process, so it means MCP-supervises-engine IPC + session recovery). Option (c), a native Mach handler, is rejected as primary: the CLR owns hardware-exception handling and escalates AVs to a fail-fast, so a handler fights the runtime and is fragile. The defer is reversible — if a native inspection fault recurs despite checked reads, that is the evidence that promotes (b).

### Scope discipline

Kept the ADR-013 lazy/bounded model. This ADR was robustness, not new expansion surface. Out of scope: D3 span-aware *conditions* (still ADR-013); the out-of-process backstop (deferred, above).

## Validation — met

**Proposed → Accepted (2026-06-22):** the mscordbi-abort cause was characterized by the D1 probe (a size-overflow, *not* shape-specific) and the D2 approach was chosen (defer the out-of-process backstop).

**Accepted → Completed (2026-06-22):**
- ✅ A probe inspects arbitrary ref-structs — `Mimic` (a `BindingTable`-shaped ref struct), `PlainRef`, `NormalBox` — and every field is read or rendered navigable-opaque, **never a crash**: 35/35 runs across 7 cells (was 6/6 abort pre-fix). `DrHook.Engine` unit 119/119 + integration 12/12 green; no regression.
- ✅ The **real** `BindingTable` frame (`adr014-finale-*`): a real Mercury `SELECT` breaks at `EnsureStringCapacity`; `GetArguments(1)` expands `this` (all 5 fields), `additional = 1102`, `this._stringBuffer.Length = 65536`, `target` reads — **no engine crash**. This is the exact frame that aborted the engine pre-fix (`ck:obs-adr014-finale-real-bindingtable`).
- ↪ "An *injected* unreadable shape degrades to a surfaced anomaly" — **reframed by the D2 defer.** D1 makes the read *checked*, so the shape no longer faults (prevention, not containment). A CSE fail-fast cannot be surfaced in-process; the anomaly-on-native-fault backstop for *unforeseen* shapes is the deferred out-of-process work.

## Consequences

- The size-overflow gap is closed: a large value type (a real `BindingTable`, any ref struct with span fields) no longer drops the engine when inspected. The checked read is general — every value, not just the demand-proved one.
- **Reads are checked operations** is now established discipline for the inspection surface (audit done), closing the class the two crashes (depth-2 SIGSEGV, complex-ref-struct SIGABRT) both belong to — both were AVs in the native walk.
- An `EngineAnomaly` *inspection-fault kind* (surfacing a native fault instead of dying) belongs to the deferred out-of-process backstop, not here: prevention (D1) is the in-process answer; isolation (deferred (b)) is the backstop, evidence-gated on a recurrence.
- DrHook is materially closer to the substrate promise ("safe to point at *any* frame"); the residual is unforeseen ICorDebug AVs.
- Kept as regression artifacts: `poc/drhook-inspection-robustness/adr014-faultrepro-*` (synthetic repro/localizer) and `adr014-finale-*` (real-`BindingTable` dogfood); `DEBUGGING.md` documents how to run them.

## References

- [ADR-013](ADR-013-value-type-refstruct-inspection.md) — the expansion (D1/D2) that surfaced this boundary.
- [ADR-007](ADR-007-teardown-concurrency-test-debug.md) inspection-at-scale item — first called for fault containment; the original depth-2 `QuadStore` SIGSEGV.
- Mercury [ADR-050](../mercury/ADR-050-growable-result-binding-buffer.md) — the dogfood finale that demand-proved this.
- `ck:obs-drhook-adr050-finale` — the finding (SIGABRT in mscordbi; target survived).
- `drhook-mcp-2026-06-22-040558.ips` — the crash report (`SIGABRT`, `libmscordbi`).
- `poc/drhook-inspection-robustness/adr013-vt-*` — the simple shapes that DO inspect cleanly (the contrast).
