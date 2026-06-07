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
object graph. **Fix (ADR-007):** bound total walk breadth/size/time with graceful truncation + a
`BreadthClamped` anomaly. Interim workaround: depth-1 or scalar expression reads on complex frames.

See **ADR-007** (the "Inspection breadth/size budget" item under Phase 1) and the `ck:` design-knowledge
graph (`ck:lesson-runtime-inspection-scalar-over-walk`, `ck:obs-drhook-inspection-isolation`).

Build/run any one: `dotnet build <dir>/<name>.csproj -c Debug`, then attach via DrHook.
