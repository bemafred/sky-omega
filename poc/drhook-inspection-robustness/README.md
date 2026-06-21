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
