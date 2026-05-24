# Finding 55: Stack-budget audit — DrHook.Engine stackalloc/recursion sites + per-thread stack budgets

**Status:**   Audit (Phase 1c of [ADR-007](../../../docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md)).
Closes the Phase 1 audit triplet (with [finding 53](53-threading-memory-model-audit.md) /
[finding 54](54-teardown-audit.md)). Inventories every `stackalloc` + `Span<T>`/`fixed` site
in `DrHook.Engine`, walks the mutual recursion in the inspection path (`FieldEnumerator` ↔
`Variables.GetChildren` ↔ `ArrayInspector`), and computes the per-recursion-level stack cost
against per-platform thread stack defaults. Produces explicit `Thread(…, maxStackSize)`
declarations for engine-owned threads and a `MaxInspectionDepth` constant matching Mercury's
substrate discipline.
**Date:**     2026-05-23
**Method:**   Mechanical grep over `src/DrHook.Engine/` for `stackalloc`, `Span<`, `fixed*`,
`NativeMemory.Alloc*`. Cross-reference Mercury's stack discipline (DefaultMaxDepth, bounded
stackalloc with heap fallback, ref-struct parsers). Per-recursion-level cost computed by adding
stackalloc sizes + local-variable estimates + COM-call-frame overhead. Per-platform thread
defaults sourced from documented runtime conventions (Windows 1MB, macOS main 8MB / secondary
512KB, Linux 8MB).

## Inventory denominator

**stackalloc sites in DrHook.Engine (5 total):**

- `Interop/MetadataResolver.cs:79` — `char* nameBuf = stackalloc char[512]` (1024 bytes). In `ResolveMethodToken`'s field-walking path; called once per name lookup.
- `Interop/MetadataResolver.cs:107` — `char* nameBuf = stackalloc char[512]` (1024 bytes). Sibling resolve path.
- `Interop/FieldEnumerator.cs:194` — `char* nameBuf = stackalloc char[256]` (512 bytes). In `GetFieldName`, called per-field inside `EnumAndReadFields`, which is itself called per-type-level inside the **mutually-recursive** field walk.
- `Interop/ManagedCallbackHost.cs:67` — `stackalloc nint[] { _v1, _v2, _v3, _v4 }` (32 bytes on 64-bit). One-shot at `Build`; not on any recursive path.

**`fixed` pin sites (10 total, no allocation — pin existing managed memory):**

- `Interop/StringInspector.cs:76`, `MetadataResolver.cs:42/55/152`, `RuntimeNavigation.cs:119` — pin a `char` buffer/string for a single COM call.
- `Interop/FieldEnumerator.cs:155` — pin `uint[] tokens` (16 elements) for a single EnumFields iteration.
- `Interop/DbgShim.cs:82/125/132/233/234` — pin `char*` for module-name + command-line ICorDebug calls.

None of these allocate stack space themselves; they pin already-allocated managed memory for native consumption. Lifetime is bounded by the `fixed` block. No stack-budget concern, but several appear inside iterative call paths (FieldEnumerator.cs:155 is inside `while (true)` enum loop) — pin lifetime is per-iteration, not per-walk, so this is correct.

**Native heap allocations (1):**

- `Interop/ManagedCallbackHost.cs:133` — `NativeMemory.AllocZeroed(slots, sizeof(nint))`. The CCW block + 4 vtables. Heap-allocated by design (lifetime spans the entire debug session); freed in `Dispose`. **Outside stack-budget scope.** (Lifetime concern is MCH-1 from finding 53.)

**Recursive call sites (mutual recursion):**

The inspection layer recursion is mutual, terminating on `depth <= 0`:

```
FieldEnumerator.GetFields(pValue, depth)         // entry from DebugSession.GetLocals/GetArguments
  └─ AppendFieldsAtLevel
      └─ EnumAndReadFields              // per type-level (the GetBase walk is ITERATIVE inside)
          └─ stackalloc char[256]       // per field name resolved
          └─ Variables.GetChildren(fieldValue, elementType, depth - 1)
              ├─ FieldEnumerator.GetFields(...)         // if CLASS/OBJECT (0x12/0x1C)
              └─ ArrayInspector.TryReadElements(...)    // if ARRAY/SZARRAY (0x14/0x1D)
                  └─ Variables.GetChildren(elementValue, elementType, depth - 1)
                      └─ FieldEnumerator.GetFields(...) // or back to ArrayInspector
```

Per-level stack cost (one round of `GetFields` → `EnumAndReadFields` → `Variables.GetChildren`):

| Item | Size (bytes) |
|---|---|
| stackalloc char[256] (FieldEnumerator.cs:194) | 512 |
| stackalloc char[512] (MetadataResolver.cs, if reached) | 1024 |
| COM call frames (~4 calls/level, ~64 bytes each) | 256 |
| Locals (uints, nints, parameters across the chain) | ~256 |
| **Sub-total per recursion level** | **~2 KB worst case** |

**Worst-case 50-deep recursion: ~100 KB stack.** Bounded; well within all platform defaults including macOS secondary (512KB).

**Caveat:** the per-field stackalloc is INSIDE `EnumAndReadFields`'s `for (int i = 0; i < fetched; i++)` loop, not inside the recursive call. So a single recursion frame may invoke `GetFieldName` (with its 512-byte stackalloc) once per field WHILE keeping previous fields' stackallocs alive? Actually no — the C# spec says stackalloc inside a loop releases each iteration. Re-allocates on next iteration. Stack pointer moves down, then back up. **At any moment within a level, only ONE stackalloc char[256] is live.** Worst case stays ~2 KB per level.

## Mercury cross-reference

Mercury's stack discipline patterns, identified by grepping `src/Mercury/`:

### Pattern 1: Explicit `DefaultMaxDepth` constant

[`Mercury/Sparql/Parsing/SparqlParser.cs:34`](../../src/Mercury/Sparql/Parsing/SparqlParser.cs):

```csharp
public const int DefaultMaxDepth = 10;

public SparqlParser(ReadOnlySpan<char> source, int maxDepth)
{
    if (maxDepth <= 0)
        throw new ArgumentOutOfRangeException(nameof(maxDepth), "Max depth must be positive");
    _maxDepth = maxDepth;
}

[MethodImpl(MethodImplOptions.AggressiveInlining)]
private void IncrementDepth(string context)
{
    if (_currentDepth >= _maxDepth)
        throw new SparqlParseException($"{context} nesting exceeds maximum depth of {_maxDepth}");
    _currentDepth++;
}
```

**Transfer:** *yes.* DrHook.Engine's inspection path takes `depth` from the caller without any bound — a malformed or pathological MCP request can pass arbitrary integers. Mercury's pattern (constant + ctor validation + per-increment check) is exactly what's missing here. See ENG-STK-1.

### Pattern 2: stackalloc bounded N with heap fallback

[`Mercury/Storage/HashAtomStore.cs:313/380/583`](../../src/Mercury/Storage/HashAtomStore.cs):

```csharp
Span<byte> stackBuffer = stackalloc byte[Math.Min(byteCount, 512)];
// ... heap-fallback if byteCount > 512
```

**Transfer:** *partially.* DrHook.Engine's stackallocs are all *fixed-size* (256 / 512 / 4 elements), not caller-driven. The fixed sizes match the COM API's documented name-buffer convention (256 for IL field names, 512 for method names in cor.h). No heap fallback needed because the sizes are bounded by the API contract. **No change recommended.**

### Pattern 3: Span<T> in ref-struct parsers

[`Mercury/Sparql/Parsing/SparqlParser.cs:21`](../../src/Mercury/Sparql/Parsing/SparqlParser.cs):

```csharp
internal ref partial struct SparqlParser
{
    private ReadOnlySpan<char> _source;
```

A ref struct cannot be heap-allocated, cannot cross await/yield, cannot be captured in closures — enforces stack-only lifetime by compiler.

**Transfer:** *no.* DrHook.Engine's interop layer uses *static classes* (FieldEnumerator, Variables, ArrayInspector, etc.) — no instance state, no ref struct needed. The Span<T> equivalents in DrHook are inside method scope only (fixed pins on managed strings, or inside ICorDebug call results). No cross-method Span lifetime to worry about.

### Pattern 4: stackalloc int[N] for hash buckets / range tables

[`Mercury/Storage/RadixSort.cs:53–54`](../../src/Mercury/Storage/RadixSort.cs):

```csharp
Span<uint> histogram = stackalloc uint[256];
Span<uint> offsets = stackalloc uint[256];
```

[`Mercury/Sparql/Execution/Operators/TriplePatternScan.cs:909`](../../src/Mercury/Sparql/Execution/Operators/TriplePatternScan.cs):

```csharp
Span<int> ranges = stackalloc int[32]; // up to 16 segments — comfortable for real queries
```

**Transfer:** *no.* DrHook.Engine has no hash-build or range-table workloads — the substrate is interop, not computation.

### What DOESN'T transfer from Mercury

- **Parsing patterns:** Mercury's `ReadOnlySpan<char>` inputs are owned + bounded; DrHook reads native memory through ICorDebug RCWs — different lifetime model (the RCW owns the buffer's lifetime, not us).
- **Within-step stackalloc:** Mercury's stackallocs live within a single *non-recursive* parse step. DrHook has stackalloc INSIDE a recursive call path (`EnumAndReadFields`'s `GetFieldName` per-field allocation). The discipline transfers (small bounded N), but the *budget calculation* must account for recursion. Mercury's calculation is "one level deep × bounded N"; DrHook's is "N levels × per-level allocation."

**Audit conclusion on Mercury transfer:** the *DefaultMaxDepth* constant is the load-bearing discipline DrHook is missing. The bounded-stackalloc and ref-struct patterns are not applicable.

## DrHook-specific stack-discipline rules

Three substrate domains where Mercury's parsing-focused discipline doesn't reach:

### Rule 1: COM-interop call frames

ICorDebug calls go through `delegate* unmanaged[Cdecl]<...>` function-pointer slots. Each call adds a native call frame on top of the managed call frame. On x86_64 systems this is ~64 bytes per call (return addr + saved regs + alignment); on ARM64 similar.

The CCW thunks in `ManagedCallbackHost` cross the boundary the OTHER way — mscordbi calls our `[UnmanagedCallersOnly]` static method, which adds a managed frame on top of mscordbi's stack. **We don't own the mscordbi thread's stack budget** — mscordbi sets it. The contract: *our thunks must stay O(1) stack.*

**Audit of mscordbi-thread thunks (ManagedCallbackHost.cs:168–209):**

Every thunk has the shape:
```csharp
private static int X(nint p, ...) => Fire(p, ...);

private static int Fire(nint pThis, CallbackKind kind, string name, ...)
{
    HostOf(pThis)?._sink.OnCallback(kind, name, ...);
    return S_OK;
}
```

- 0 stackallocs.
- 1 GCHandle.FromIntPtr lookup + 1 interface dispatch + 1 BlockingCollection.Add.
- BlockingCollection.Add: under the hood does Interlocked + Volatile + possibly Monitor wait — bounded but non-trivial. **Probably <500 bytes worst case.**

**Verdict:** thunks honor the O(1) contract. Document as a substrate invariant in code (Rule 1 comment).

### Rule 2: Callback-marshalling lifetime

The `_sink` field of `ManagedCallbackHost` is read by the mscordbi-thread thunk; the `_sink` reference points to a `CallbackPump` instance whose `_events` queue lives on the managed heap. The thunk's `_sink.OnCallback` → `_events.Add` works because:

1. The mscordbi thread's stack contains a reference to `_sink` (after `HostOf` recovers it via GCHandle).
2. `_sink` is rooted by the GCHandle in `_self` — so as long as the GCHandle isn't freed (MCH-1 territory), `_sink` and `_events` stay alive.
3. `_events.Add` allocates a node on the managed heap inside `ConcurrentQueue<T>.Enqueue`. This is the only allocation on the mscordbi thread's call path — and it's heap, not stack.

**Verdict:** the mscordbi thread does ~one managed-heap allocation per callback. Acceptable. Document.

### Rule 3: Stopped-state memory reads

When the engine is at a stop, `Variables.ReadValue` calls `ICorDebugGenericValue.GetValue(&buffer)` with a stack-local `long buffer = 0;` (Variables.cs:163). The native side writes up to 8 bytes into this stack location. Lifetime: the buffer is alive for the duration of the synchronous COM call; the COM call returns; the buffer's `long` value is captured into `raw`. ✓ Safe.

This pattern appears throughout — small stack-local scratch buffers for COM out-params. All are bounded by the COM API's documented out-size convention. **No risk; document as Rule 3.**

## Per-thread stack budget records

For each engine-known thread, the platform default, the work the thread performs, and the recommended explicit budget.

### CallbackPump worker thread (`DrHook.CallbackPump`)

- **Owner:** engine.
- **Created:** `new Thread(Pump) { IsBackground = true, Name = "DrHook.CallbackPump" }` at CallbackPump.cs:73.
- **Platform default:** Windows 1MB, macOS secondary 512KB, Linux 8MB.
- **Work:** drains `_events.GetConsumingEnumerable`; invokes `_userSink.OnEvent` (user code; unknown stack); calls `_resumeHandler` (which calls `Stepping.Arm` + `controller.Continue`) or `_pauseHandler` (`controller.Stop`); publishes to `_stops`. **Non-recursive; bounded ~5 KB worst case for the user-sink + COM-controller call frames.**
- **Risk:** the `_userSink.OnEvent` callback is user-supplied (the MCP layer's `NullSink` is empty; BoundedLogSink's `OnEvent` is empty; future sinks unknown). A pathological user sink that recurses (e.g., calls back into the engine — but DebugSession's API is "valid only while stopped" and the worker is between events) could consume arbitrary stack. **Document as a Rule for IDebugEventSink implementations: OnEvent must be O(1) stack.**

**Recommended declaration (ENG-STK-2):**

```csharp
// 1 MB explicit budget — covers Windows default (no surprise on macOS secondary 512KB,
// which would risk margin compression under future sink growth or .NET runtime tail-call
// inlining variance). Stays under Linux 8MB default; consistent across platforms.
_worker = new Thread(Pump, maxStackSize: 1024 * 1024)
{
    IsBackground = true,
    Name = "DrHook.CallbackPump",
};
```

The engine work itself fits comfortably under 100 KB; the 1 MB choice is *substrate consistency* — every engine-owned thread gets the same explicit declaration so the platform-default variance doesn't surface as a "works on dev box, fails on Windows" bug.

### mscordbi event thread

- **Owner:** ICorDebug runtime (mscordbi's RC event thread).
- **Created:** by mscordbi internally; we don't see the creation.
- **Platform default:** UNKNOWN — mscordbi-internal choice. Likely platform-default-ish.
- **Work entering our code:** vtable thunk → `HostOf` → `_sink.OnCallback` → `_events.Add`. **Bounded O(1).**
- **Risk:** if a future change adds work to a thunk (e.g., synchronously formatting the callback name), the mscordbi thread's stack budget could be exceeded. Today honored by construction.

**Recommended discipline (ENG-STK-3):** add a comment at the top of `ManagedCallbackHost.cs` documenting Rule 1 (the O(1)-stack invariant on thunks) plus a unit test that asserts each thunk's IL size stays under a threshold (proxy for stack growth).

### libdbgshim startup thread

- **Owner:** libdbgshim runtime.
- **Created:** internally by `RegisterForRuntimeStartup`.
- **Work entering our code:** `StartupCallbackThunk` → `GCHandle.FromIntPtr` → field writes → `Signaled.Set()`. **Bounded O(1).**
- **Risk:** same as mscordbi — future growth in the thunk could exceed budget.

**Recommended discipline:** same as ENG-STK-3 for `DbgShim.StartupCallbackThunk`.

### EventPipe processing thread (StackInspector.CaptureAsync's `Task.Run`)

- **Owner:** TraceEvent (via Task.Run on thread pool).
- **Created:** `Task.Run(() => source.Process(), ct)` at StackInspector.cs:93.
- **Platform default:** thread pool default — Windows 1MB, macOS 512KB, Linux 8MB.
- **Work:** TraceEvent's `EventPipeEventSource.Process` invokes registered handler delegates synchronously per event. Handlers do `List.Add`, `Dictionary[]=`, `Interlocked.Increment`. **Bounded O(1) per event.**
- **Risk:** zero in current code. TraceEvent's own stack consumption inside `Process` is not under engine control; if it ever grows we'd see EventPipe crashes.

**Recommended discipline:** no change. The Task.Run thread pool budget is fine for the work today. Document Rule (StackInspector handlers must remain O(1)).

### MCP request thread(s)

- **Owner:** ModelContextProtocol.Server SDK (thread pool).
- **Created:** by the SDK on each request dispatch.
- **Platform default:** thread pool default per platform.
- **Work:** `EngineSteppingSession.LaunchAsync` etc. → `DebugSession.GetLocals/GetArguments` → recursive inspection. **This is the only engine path with caller-controlled recursion depth.**

**Per-level cost:** ~2 KB (from inventory section above).
**Worst case if no bound:** unbounded; caller can pass `depth = Int32.MaxValue`.

**Recommended change — the only load-bearing one — ENG-STK-1:**

Add to `DebugSession`:
```csharp
/// <summary>Maximum recursion depth for object/array inspection
/// (matches Mercury's SparqlParser.DefaultMaxDepth). At ~2 KB per level
/// across mutually-recursive FieldEnumerator/ArrayInspector/Variables.GetChildren,
/// 10 levels = ~20 KB stack consumption — well under macOS-secondary
/// 512 KB and Windows 1 MB defaults, even accounting for COM-call-frame growth
/// at depth. Callers requesting deeper are clamped + an EngineAnomaly is surfaced.
/// Inheritance hierarchy walking (GetBase chain inside one level) is iterative
/// and doesn't count against the depth budget.</summary>
public const int MaxInspectionDepth = 10;
```

And clamp in `GetLocals` / `GetArguments`:
```csharp
public IReadOnlyList<LocalValue> GetLocals(int depth = 0)
{
    if (depth > MaxInspectionDepth)
    {
        // ENG-ANOM hook — anomaly surfaced + clamp
        _sink.OnAnomaly(new EngineAnomaly(/* DepthExceeded */ ...));
        depth = MaxInspectionDepth;
    }
    // ... existing body ...
}
```

Mirrored for `GetArguments`. The MaxInspectionDepth value matches Mercury's `DefaultMaxDepth = 10` — substrate consistency.

### Summary table — per-thread stack budgets

| Thread | Default | Engine work | Recommendation |
|---|---|---|---|
| CallbackPump worker | macOS sec 512KB / Win 1MB / Linux 8MB | Drain + dispatch; ~5 KB | **ENG-STK-2:** explicit `maxStackSize: 1024 * 1024` |
| mscordbi event | mscordbi internal | Thunk → enqueue; O(1) | **ENG-STK-3:** doc Rule 1 + IL-size test |
| libdbgshim startup | libdbgshim internal | Thunk → Set; O(1) | doc Rule 1 |
| EventPipe processing | thread pool default | Handler delegates; O(1) per event | document Rule |
| MCP request | thread pool default | Recursive inspection (caller-driven depth) | **ENG-STK-1:** `MaxInspectionDepth = 10` clamp + EngineAnomaly |

## Engineering fixes surfaced (Phase 1 — extension of finding 54's set)

### ENG-STK-1 — Add `MaxInspectionDepth = 10` constant + clamp in DebugSession

Mercury-aligned. The only fix that's actually substrate-critical — without it, an MCP caller passing `depth = 1000` recurses 1000 × 2KB = 2MB stack, exceeding macOS secondary 512KB and crashing the request thread (process-fatal if uncaught at the boundary).

Implementation: ~10 lines in DebugSession.cs (constant + two clamp checks in GetLocals / GetArguments). EngineAnomaly hook ties into finding 53/54's anomaly seed list.

### ENG-STK-2 — Explicit `maxStackSize: 1024 * 1024` for CallbackPump worker

Substrate consistency. No bug today; future-proofs against:
- macOS secondary 512KB → Windows 1MB platform variance.
- Future user-sink implementations that consume more stack.
- Inlining variance in JIT across CoreCLR releases.

Implementation: one-line change at CallbackPump.cs:73.

### ENG-STK-3 — Document Rule 1 (O(1) thunks) + add IL-size unit test

Defensive guard against future growth. Two-line comment at top of `ManagedCallbackHost.cs` and `DbgShim.StartupCallbackThunk`. Unit test in Phase 8: assert each `[UnmanagedCallersOnly]` thunk's IL size is under N bytes (use reflection to enumerate + assert).

## Recursion-depth resource accounting (per finding 53's "no plurality" lesson)

[`feedback_resource_limit_class_audit`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_resource_limit_class_audit.md): *every* interaction with an OS-enforced resource must be accounted, characterized, bounded — at introduction time.

**Stack-as-resource is one of the categories named in the resource-limit class audit alongside FDs/memory/threads/mmap/sockets.** This audit accounts for it:

- **Inventory:** 5 stackalloc sites, 5 thread-creation contexts (engine-owned + external), 1 recursive call graph.
- **Characterization:** per-level cost ~2 KB; per-thread platform defaults documented.
- **Bound:** ENG-STK-1 introduces an explicit depth bound matching Mercury's substrate pattern. ENG-STK-2 introduces an explicit thread stack budget for the engine-owned worker. ENG-STK-3 documents the unowned-thread O(1) invariant.

**Per the resource-limit checklist:** stack-as-resource is now characterized + bounded for DrHook.Engine. Pre-existing implicit bounds (per-call-API name-buffer sizes, render-side `MaxElements = 64`) are documented in this audit. No silent stack failures remaining.

## What this finding does NOT cover

- **Inheritance-chain walk depth** (the `while (type != 0)` loop in FieldEnumerator at lines 97–103). This is iterative — bounded by the type hierarchy depth in the debuggee, not our recursion depth — so it doesn't contribute to stack-budget consumption. Render-time cost is `O(typeChainDepth × fieldsPerLevel)`, but that's a wall-clock concern, not a stack-budget concern.
- **Per-platform validation of the budgets** — Phase 9 owns running the engine on Linux x64/arm64 and Windows x64/arm64 to validate that `maxStackSize: 1MB` works under all platform defaults.
- **mscordbi's own stack** — we don't control or know it; the O(1)-thunk discipline (Rule 1) is our only handle.
- **User-sink stack contracts** — `IDebugEventSink.OnEvent` and `OnLog` implementations are user code; the engine's contract is to invoke them on threads with documented stack budgets (CallbackPump worker for `OnEvent`, MCP request thread for `OnLog`). Documented; not engine-fixable.

## Summary

**Five stackalloc sites, all bounded (256 / 512 / 32 bytes). Ten `fixed` pins of existing managed memory, no allocation.** No unbounded stack consumption from stackalloc itself.

**Mutual recursion in inspection** (FieldEnumerator ↔ Variables.GetChildren ↔ ArrayInspector) consumes ~2 KB per level, terminates on `depth <= 0`. **But caller-supplied depth has no upper bound** — this is the only substrate-critical stack-budget gap and the only Mercury pattern that transfers directly: **ENG-STK-1** introduces `MaxInspectionDepth = 10` (matching Mercury's `SparqlParser.DefaultMaxDepth`).

**Per-thread:** five threads enter engine code; one (CallbackPump worker) is engine-owned and gets an explicit `maxStackSize: 1024 * 1024` declaration (**ENG-STK-2**) for cross-platform consistency. Two external threads (mscordbi, libdbgshim) have unowned budgets and require the O(1)-stack invariant on our thunks — honored today, documented as Rule 1 (**ENG-STK-3**) + asserted by IL-size unit test in Phase 8.

**Stack-as-resource is now characterized + bounded** for DrHook.Engine per the resource-limit-class audit discipline. Phase 1c is complete; Phase 1's audit triplet (findings 53, 54, 55) is closed. Probes 42–45 + the three new probe candidates from finding 54 (T3-eval, T6-attach, T4a-pause) execute next, against a substrate whose contracts are now explicitly documented.
