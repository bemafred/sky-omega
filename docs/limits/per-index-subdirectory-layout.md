# Limit: Flat store layout makes per-file symlinks fragile under file-replace patterns

**Status:**        Latent
**Surfaced:**      2026-04-28, during a forward-looking discussion of two-SSD utilization on future consumer hardware (256-512 GB unified RAM, 16 TB SSD split on two physical drives)
**Last reviewed:** 2026-04-28
**Promotes to:**   ADR when a workload genuinely binds on per-volume bandwidth — concretely: (a) a target deployment wants WAL on one SSD and data on another to decouple fsync latency from read bandwidth, OR (b) an index-pair deployment wants GSPO and GPOS on separate spindles to parallelize multi-join random reads, OR (c) a backup/replication layout wants a subset of indexes mirrored independently. None of these have fired yet.

## Description

Mercury's `QuadStore` takes a single `baseDirectory` and creates all index files as flat siblings:

```
<store>/
  gspo.tdb
  gpos.tdb
  gosp.tdb
  tgsp.tdb
  atoms.data
  atoms.offsets
  atoms.hash    (or atoms.atoms / atoms.offsets for SortedAtomStore)
  wal.log
  store-schema.json
```

The OS already provides the indirection layer for placing files on different filesystems — symlinks, APFS firmlinks, Linux bind mounts. mmap traverses symlinks transparently (open() → fd → mmap is symlink-aware), so a user wanting two-SSD utilization can in principle do:

```
<store>/gpos.tdb -> /Volumes/SSD-B/wd/gpos.tdb
<store>/wal.log  -> /Volumes/SSD-B/wd/wal.log
```

…and Mercury never knows. This is the right division of responsibility — storage placement is an OS concern.

**The fragility**: several Mercury code paths create or replace files at literal paths:

- ADR-034 Phase 1B-5b — `QuadStore.CommitBatch` (Reference + Sorted) closes the placeholder `SortedAtomStore` and opens a fresh one over the just-built `atoms.atoms` / `atoms.offsets`. Implemented today as in-place file replacement at the same paths.
- `Mercury.Pruning` copy-and-switch — creates `*.new` files alongside existing ones and renames them over the originals.
- Index rebuild paths — `RadixSort` external-sort spills to `Path.GetTempPath()` (already correctly off-store), but the final write back to the `.tdb` files happens at the literal path.
- Future Reference seal/reopen (`reference-readonly-mmap.md`) — would close the RW mmap and reopen Read at the same path.

When the literal path is a symlink to another filesystem, two failure modes appear:

1. **`File.Create` against an existing symlink** — the new file lands on the symlink's *target* filesystem, which is the desired behavior. This case is fine.
2. **`File.Create` of a new sibling file followed by atomic `File.Replace`** — the new file is created in the *directory's* filesystem (because the new sibling has no symlink yet), then `File.Replace` becomes a cross-filesystem rename, which on POSIX falls back to copy + unlink. Atomicity is lost. The new file ends up on the wrong SSD.

Failure mode (2) is the common pattern in copy-and-switch and several rebuild paths.

The robust layout is **subdirectory-per-index**:

```
<store>/
  gspo/data.tdb
  gpos/data.tdb
  gosp/data.tdb
  tgsp/data.tdb
  atoms/
  wal/log
  store-schema.json
```

With this layout, `ln -s /Volumes/SSD-B/wd-gpos /store/gpos` is durable across rebuilds, file replaces, atomic swaps — anything Mercury does to files inside the symlinked directory stays on the target filesystem. The symlink boundary is the directory, not the file.

## Trigger condition

Promote to an ADR when any of the following holds:

- A real deployment wants WAL on its own SSD (classic DB pattern; fsync queue isolation matters at high-throughput write workloads — currently not characteristic of Reference, possibly relevant for future Cognitive heavy-write profiles).
- A real deployment wants index-pair separation across physical SSDs to parallelize read-side bandwidth on multi-join queries (interesting for query optimization rounds when WDBench-style multi-BGP latency becomes the binding constraint).
- A backup/replication strategy wants per-index granularity (e.g., snapshot only `gspo` independent of `gpos`).
- A profile-specific layout (e.g., Reference seals atoms differently from Cognitive) wants subdirectory isolation for atomicity.

None of these have fired yet. Single-SSD on a 128 GB laptop has not made volume-level placement a binding constraint at any scale Mercury has been validated against, including 21.3 B Wikidata.

## Current state

- All paths are flat siblings (verifiable: `grep -E "\\.tdb|\\.log|atoms\\." src/Mercury/Storage/QuadStore.cs` near line 133).
- mmap+symlink transparently works for read-mostly files (existing index files where the user has set up the symlink before opening the store).
- Cross-filesystem `File.Replace` will fail or silently fall back to non-atomic copy in current code paths.
- `EnsureSufficientSpace` reports the directory's filesystem free space, not the actual write target's — a separate, smaller issue worth flagging in the same ADR if and when this gets promoted.

## Candidate mitigations

The promotion-time edit is small and mechanical:

1. Change file path constants in `QuadStore` to subdirectory-rooted (`gspo/data.tdb` instead of `gspo.tdb`).
2. `Directory.CreateDirectory` per index subdirectory at store-create time.
3. Migration shim: on opening a flat-layout store, detect via presence of `gspo.tdb` at the root and either (a) rewrite layout in-place via `File.Move` into subdirs as a one-time op, or (b) read both layouts and write the new one only when next written. Backward compat is non-negotiable for existing wiki-21b-ref users.
4. Update copy-and-switch, `CommitBatch` reopen, and any other file-replacement paths to confirm new files are created inside the same subdirectory (already true if subdirectory becomes the unit of placement; just verify).

Estimated cost: a day of work, well-scoped. The reason this is in the limits register and not on the immediate ADR backlog is that **no one has asked for two-SSD utilization yet**. When they do — or when an internal optimization round identifies per-index placement as a measurable win — this becomes a fast-to-execute ADR.

A useful interim posture for users wanting to experiment: symlink the *whole store directory* to one SSD (works trivially today, gives single-SSD placement freedom), or use APFS-stripe across multiple physical SSDs (zero Mercury changes, gives passive bandwidth aggregation without per-index control).

## References

- `src/Mercury/Storage/QuadStore.cs` — flat-layout path construction
- `src/Mercury/Storage/ReferenceQuadIndex.cs` — index file lifecycle
- `src/Mercury.Pruning/` — copy-and-switch file-replacement pattern
- `docs/limits/reference-readonly-mmap.md` — related lifecycle work that would interact with subdirectory layout if promoted
- ADR-026, ADR-029 — store profile and bulk-load model that set the lifecycle context
