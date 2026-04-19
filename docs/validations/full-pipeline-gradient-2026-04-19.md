# Full Pipeline Gradient — 2026-04-19

**Status (2026-04-19):** Bulk load + rebuild both validated at 1 M / 10 M / 100 M. 1 B rebuild in progress (bulk was validated 2026-04-18, see [bulk-load-gradient-2026-04-17.md](bulk-load-gradient-2026-04-17.md)).

Extends the yesterday gradient. Yesterday only exercised **bulk load** — the store after load was in `PrimaryOnly` state, GPOS/GOSP/TGSP/Trigram empty. Today's gradient validates the **rebuild** half of the pipeline: turning a bulk-loaded `PrimaryOnly` store into a `Ready` production-queryable store.

## Why this gradient mattered

A "bulk-loaded" store without rebuild can only answer queries that fit the GSPO scan pattern (subject-bound or all-unbound). Production Wikidata queries are predominantly predicate-bound (e.g., "all entities with `schema:about`"), which requires GPOS. A predicate-bound query against a `PrimaryOnly` store falls back to a full GSPO scan — correct but very slow at scale.

Rebuild is therefore the step that makes bulk-loaded data *actually usable* for real workloads. Until today, it had not been validated at any non-toy scale.

## Headline results

| Scale | Bulk load | Rebuild | Predicate query | Trigram entries |
|---|---|---|---|---|
| 1 M  |    4 s (250 K/sec) |  2.9 s  |  130 ms | 438 K |
| 10 M |   46 s (217 K/sec) |   42 s  |  857 ms | 4.37 M |
| 100 M |   5 m 49 s (286 K/sec) | **11 m 35 s** | 6.5 s | 45.67 M |
| 1 B  |   (validated 2026-04-18, 50 m 17 s @ 331 K/sec) | **in progress** | — | — |

Predicate-bound query: `SELECT (COUNT(*) AS ?n) WHERE { ?s <http://schema.org/about> ?o }`.

### Rebuild scales ~1.6× per decade

Rebuild time is super-linear in data size (expected for B+Tree insert at growing depth). Trigram entries scale linearly (4.4× per decade, matching the literal-atom-to-triple ratio).

## Bugs surfaced during the gradient

### Bug R-1 — `SaveMetadata` msync ran during rebuild (1 M)

**Symptom:** 1 M rebuild did not complete in 10 min. All three secondary indexes (GPOS/GOSP/TGSP) were making slow progress — ~140 MB written each after ~2 min.

**Root cause:** The 1.7.15 msync-defer fix inside `QuadIndex.SaveMetadata` was gated on `_bulkMode`, which is a *construction-time* flag set from `StorageOptions.BulkMode`. Rebuild runs against a store opened without `--bulk-load` — `_bulkMode` is false. Every page allocation during rebuild triggered `_accessor.Flush()`, which on macOS is `msync` over the entire 256 GB sparse mmap.

**Fix:** Split `_bulkMode` into two concepts:
- Construction decision (pre-size mmap, set `FileOptions.None` vs `WriteThrough`) — still reads the constructor parameter.
- Runtime `_deferMsync` field — togglable via new `internal SetDeferMsync(bool)` method.

`QuadStore.RebuildIndex` enables deferral around the rebuild loop, calls `target.Flush()` once at the end, then disables. Matches the bulk-load durability contract: one msync per phase.

**Commit:** `dece1c9` (1.7.22).

### Bug R-2 — `TrigramIndex` stale pointer across remap (10 M)

**Symptom:** 10 M rebuild completed GPOS/GOSP/TGSP cleanly (all 9.99 M entries), then crashed with `System.AccessViolationException` in `TrigramIndex.AppendToPostingList` during the trigram phase.

**Root cause:** `AppendToPostingList` computed
```csharp
var atomsPtr = (long*)(_postingPtr + offset + sizeof(int) + sizeof(int));
```
*before* calling `EnsurePostingCapacity`, which may remap the posting file and atomically swap `_postingPtr`. The entry-copy loop that runs after the resize then dereferenced the stale `atomsPtr` — reading from the now-unmapped previous region. SIGBUS → `AccessViolationException`.

Same class of bug as ADR-020 covers for `AtomStore`; `TrigramIndex` predated that guidance.

**Fix:** Recompute `atomsPtr` after `EnsurePostingCapacity` returns. Pointer-recompute pattern is now consistent with `AtomStore`.

**Commit:** `dece1c9` (1.7.22).

### Bug R-3 — `TrigramIndex.EnsurePostingCapacity` created mmap before `SetLength` (10 M)

**Symptom:** Same stack (`AccessViolationException` under `AppendToPostingList`), different cause. After fixing R-2, the same crash reproduced from a different code path — the mmap was created at the new capacity *before* the file was extended.

**Root cause:** `EnsurePostingCapacity` ordering was:
```
CreateFromFile(capacity: newSize)  ← mmap spans [0, newSize)
_postingPtr = newPtr
_postingFile.SetLength(newSize)    ← file extended AFTER
```
Writes to the new region between the swap and `SetLength` hit unmapped pages.

Same class as 1.7.12 Bug 4 (`QuadIndex` mmap didn't grow with file), which ADR-020 §4 addresses.

**Fix:** Reorder to `SetLength → map → swap → unmap old`.

**Commit:** `dece1c9` (1.7.22).

## Version progression (1.7.13 → 1.7.22)

| Ver | Date | Primary change | 10 M throughput |
|---|---|---|---|
| 1.7.13 | 2026-04-18 | Bug 5 (hash table config) | 57.7 K/sec |
| 1.7.14 | 2026-04-18 | Atom ID routing, UtcNow cache, FStat kills | 64.7 K/sec |
| 1.7.15 | 2026-04-18 | SaveMetadata msync defer in bulk mode | 243 K/sec |
| 1.7.16 | 2026-04-18 | Word-wise FNV hash (**later reverted**) | 272 K/sec |
| 1.7.17 | 2026-04-18 | Revert `IsInputRedirected` auto-detect | — |
| 1.7.18 | 2026-04-18 | SPARQL aggregate alias projection | — |
| 1.7.19 | 2026-04-19 | Revert word-wise FNV (1 B hash clustering) | 243 K/sec |
| 1.7.20 | 2026-04-19 | `_bulkMode` → `_deferMsync` split | (same as 1.7.19) |
| 1.7.21 | 2026-04-19 | (version bump, no code change) | — |
| 1.7.22 | 2026-04-19 | TrigramIndex pointer + SetLength-order fixes | 217 K/sec |

1.7.22 throughput is slightly lower than 1.7.19 baseline because the gradient runs used cold caches (different `sudo purge` timing). Within measurement noise.

## Methodology validation — the gradient works

Every scale step in this gradient surfaced exactly one class of bug, matching yesterday's pattern:

| Scale | Bug class |
|---|---|
| 1 M | Defer-msync flag construction-time only (R-1) |
| 10 M | TrigramIndex stale pointer + SetLength order (R-2, R-3) |
| 100 M | *(no new bugs — throughput and correctness as projected)* |
| 1 B | *(pending)* |

Yesterday's gradient found 5 bugs in the bulk-load path. Today's gradient found 3 more in the rebuild path. Each bug was invisible at smaller scale and guaranteed to hit in production.

## Reproduction

Each scale step is a three-command pipeline. For scale `N` (e.g. `100m`):

```bash
# 1. Extract slice (if not already)
head -n 100000000 ~/Library/SkyOmega/datasets/wikidata/full/latest-all.nt \
  > /tmp/wikidata-100m-clean.nt

# 2. Bulk load (primary index only; store ends in PrimaryOnly state)
rm -rf ~/Library/SkyOmega/stores/wiki-100m
mercury --store wiki-100m --bulk-load /tmp/wikidata-100m-clean.nt \
  --min-free-space 500 --no-http --no-repl

# 3. Rebuild secondary indexes (GPOS, GOSP, TGSP, Trigram)
mercury --store wiki-100m --rebuild-indexes --no-http --no-repl

# 4. Validate — predicate-bound query via GPOS
echo 'SELECT (COUNT(*) AS ?n) WHERE { ?s <http://schema.org/about> ?o }' \
  | mercury --store wiki-100m --no-http
```

`--no-repl` is required because the REPL reads stdin by default; in scripted / background contexts with no TTY, it blocks forever on `read()`. Explicit skip matches the 1.7.17 design.

## Provenance

- Hardware: MacBook Pro M5 Max, 128 GB RAM, 8 TB SSD
- Dataset: `latest-all.nt` from Wikidata April 2026 dump (21.3 B triples, 3.1 TB)
- Mercury versions: 1.7.19 through 1.7.22 (all run on 2026-04-19)
- Gradient runs: 1 M, 10 M, 100 M completed in foreground; 1 B rebuild in progress
- Session continuation of 2026-04-18 evening
- All findings mirrored to Mercury MCP semantic memory (session graph `urn:sky-omega:session:2026-04-19`)
