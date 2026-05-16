# Limit: collection element count (BCL int32 cap)

**Status:**        Resolved (introduced 2026-05-10, resolved same day in 1.7.53)
**Surfaced:**      2026-05-10, via cycle 10 Phase 3 production-run crash on 1.7.52 — `OverflowException` in `BBHashBuilder.Build` at chunk-004045 (~4 B atoms)
**Last reviewed:** 2026-05-10
**Promotes to:**   N/A — resolved via `SkyOmega.Bcl.Collections.{ChunkedList,ChunkedArray}<T>` substrate

## Description

BCL collections (`List<T>`, `T[]`, `HashSet<T>`, `Dictionary<K,V>`, `ImmutableArray<T>`, `Span<T>`, `Memory<T>`, `ArrayPool<T>`) are universally bounded by `int.MaxValue` (~2.15 B) elements. Workloads that scale linearly in the substrate's atom count cross this threshold at the 4 B-atom Wikidata point; beyond that, every collection allocation is a latent crash.

`BBHashBuilder.Build(long keyCount, …)` declared a `long`-typed input but proceeded to allocate `List<long>(checked((int)keyCount))`, `Dictionary<long, int>(remaining.Count)`, and `long[keyCount]`. At 4 B atoms, all three sites overflow: the `checked` cast threw, and absent the cast the BCL collection-element cap would have thrown internally on the first `Add` past `int.MaxValue`.

This is structurally analogous to the FD trust-gap (`runtime-fd-detection.md`): the *type signature* says we accept `long`, but the *implementation* silently re-narrows to int32. The crash class is one occurrence too many.

## Trigger condition

Any single allocation whose count is bounded by atom count, triple count, or any other input that scales past 2.15 B in production.

Concretely: any callsite of the form `new T[N]`, `new List<T>(N)`, `new Dictionary<K,V>(N)`, `new HashSet<T>(N)`, where `N` is an input-derived `long` that is not statically bounded below `int.MaxValue`.

## Current state

**Resolved structurally.** Introduced `src/SkyOmega.Bcl/Collections/ChunkedList<T>` and `ChunkedArray<T>` — long-indexed, chunked storage (default 1 M elements per chunk), no doubling-on-growth element copy, no int32 cap. `BBHashBuilder` rewritten to use them; the `Dictionary<long,int>` collision counter additionally replaced by a bit-vector pair (seen + collided) — same semantics, ~32 bytes/key → ~0.5 bits/key memory.

The new project (`SkyOmega.Bcl`) is the substrate-level home for BCL extensions Sky Omega needs but cannot NuGet-ify due to the substrate-independence discipline. Future migrations queued (deferred): `Mercury.Compression.BZip2*` (BCL-extending decompression), `Varint` helpers (currently inline in `SortedAtomStoreExternalBuilder`).

## Candidate mitigations (resolved set)

The chosen mitigation:

- `ChunkedList<T>` for append-only growing collections past 2.15 B
- `ChunkedArray<T>` for fixed-length arrays past 2.15 B
- Bit-vector pairs in place of `Dictionary<long,int>` where only "0/1 vs 2+" is needed (same trick applies wherever a counting-map is used purely for collision/dedup detection)

Alternatives ruled out:

- ❌ NuGet "BigList" / "HugeArray" packages — violates substrate-independence (Mercury BCL-only)
- ❌ Sharding into multiple `List<T>` instances inline — leaks the int32-edge throughout the codebase, no central discipline
- ❌ Lift `BBHashBuilder` int into `long` only — surface-level fix; next caller would re-introduce the same trap

## References

- Crash incident: cycle 10 Phase 3 production run on 1.7.52, `OverflowException` at `BBHashBuilder.cs:62` `new List<long>(checked((int)keyCount))`, ~4 B atoms reached at chunk-004045
- Resolution: 1.7.53 introduces `SkyOmega.Bcl` project + `ChunkedList<T>` / `ChunkedArray<T>` + `BBHashBuilder` rewrite
- Discipline: [feedback_resource_limit_class_audit](.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_resource_limit_class_audit.md), [reference_resource_limits_checklist](.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/reference_resource_limits_checklist.md)
- Cross-reference: this is the *collection-class* member of the same family as `runtime-fd-detection.md` (FD-class) and `bulk-load-memory-pressure.md` (memory-class) — the OS or BCL imposes a hard ceiling that the application surface obscures
