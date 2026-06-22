# DrHook inspection-robustness isolation (ADR-007)

Investigation POCs (not permanent tools). They isolate **why** a `drhook_locals` depth-2 walk of a
wide real frame (`QueryResults.FromMaterializedWithGraphContext`) crashed the DrHook engine and dropped
the MCP server, while the debugged target survived (F-010-2).

| POC | shape inspected at depth 2 |
|-----|----------------------------|
| `DrHookProbe/`  | plain struct, `List<int>`, `List<struct>`, `ReadOnlySpan<char>` |
| `DrHookUnsafe/` | a class holding a raw `byte*` pointer field |
| `DrHookIo/`     | `FileStream` (+ `SafeFileHandle`); `MemoryMappedViewAccessor` (+ `SafeHandle`, native base) — the `QuadStore` mmap shape |

**Result:** all seven shapes inspect **safely** at depth 2 — the inspector is *per-shape robust* (pointers
and ref-structs rendered opaque, not followed). The crash is **breadth/scale**, not a type and not depth
(depth 2 is far under `MaxInspectionDepth=10`): the real frame fans 14 args into the large `QuadStore`
object graph. **Fix (ADR-007, corrected 2026-06-21):** *not* a breadth/size cap with truncation — that would **amputate
DrHook's purpose** of observing arbitrary real state (the large `QuadStore` graph is the central case, not an
edge). The fix is architectural: **lazy, navigable, on-demand expansion** — expand one node level per request
(by handle/path), bounded per-step by construction, so arbitrarily large graphs stay **fully observable**;
**page** wide nodes, never **truncate**. Protocol design deferred. Interim workaround: depth-1 or scalar
expression reads on complex frames. See `ck:obs-drhook-inspection-lazy-navigable` and
[`feedback_derive_fix_from_purpose`](../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_derive_fix_from_purpose.md).

See **ADR-007** (the "Inspection at scale — lazy navigable expansion" item under Phase 1) and the `ck:`
design-knowledge graph (`ck:lesson-runtime-inspection-scalar-over-walk`, corrected by
`ck:obs-drhook-inspection-lazy-navigable`; original isolation in `ck:obs-drhook-inspection-isolation`).

Build/run any one: `dotnet build <dir>/<name>.csproj -c Debug`, then attach via DrHook.

## ADR-013 / ADR-014 — value-type inspection and the checked-read fix

A second generation of file-based probes (not the `.csproj` POCs above):

| Probe | What it isolates |
|-------|------------------|
| `adr013-vt-target.cs` / `adr013-vt-smoke.cs`     | ADR-013: BYREF `this` → a *normal* struct (`Box`) + a `ReadOnlySpan<char>` arg expand cleanly (the simple shapes). |
| `adr014-faultrepro-target.cs` / `adr014-faultrepro-smoke.cs` | ADR-014: the fault. Three receivers via byref `this` — `NormalBox` (normal struct), `PlainRef` (ref struct, ints only), `Mimic` (ref struct shaped like `BindingTable`: `Span<struct>` + `Span<char>` + scalars + `char[]?`). One inspection step per process (the fault is an uncatchable CSE fail-fast). |
| `adr014-finale-target.cs` / `adr014-finale-smoke.cs` | ADR-014 dogfood: a real Mercury `SELECT` breaks at `BindingTable.EnsureStringCapacity`; inspect the real `this`. |

```bash
# fault localizer — <Type> = NormalBox | PlainRef | Mimic ; <step> = args0 | args1 | expand_all | f:<field>
dotnet run --no-cache --file adr014-faultrepro-smoke.cs -- adr014-faultrepro-target.cs Mimic expand_all
# real-BindingTable finale
dotnet run --no-cache --file adr014-finale-smoke.cs -- adr014-finale-target.cs
```

**Finding ([ADR-014](../../docs/adrs/drhook/ADR-014-inspection-fault-containment.md), `ck:obs-adr014-root-cause`).** The boundary is **size, not shape**: `Variables.ReadValue` copied a whole value type via `ICorDebugGenericValue.GetValue` into a fixed **8-byte** buffer, so a value > 8 bytes (a `Span<T>` is 16; `BindingTable` ~48) overflowed the stack — a layout-dependent corruption (a Heisenbug: any added code masked it; the trace literally made it vanish). Size law: `PlainRef` ≈8 B safe; `NormalBox`/`ReadOnlySpan` ≈16 B harmless; `Mimic`/`BindingTable` ≈48 B crash. **Fix:** size the value via `ICorDebugValue.GetSize` and only copy when it fits — a *checked* read. Large value types render via their fields (navigable, not amputated). Validated: synthetic 35/35, real `BindingTable` clean, unit 119/119 + integration 12/12.
