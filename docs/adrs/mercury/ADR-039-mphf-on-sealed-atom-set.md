# ADR-039: Minimal Perfect Hash Function (MPHF) over the sealed atom set

## Status

**Status:** Accepted — 2026-05-09. Reviewed pre-implementation; four implementation-detail clarifications added below (verification cost, concurrency contract, construction memory budget, file-format magic bytes).

## Context

ADR-034 SortedAtomStore (shipped 1.7.30 → 1.7.48) gave the Reference profile a *finite, sealed, dense-ID* atom set: at 21.3 B Wikidata, exactly 4,005,235,528 unique atoms with IDs 1..N, fixed at bulk-load finalize. The current `GetAtomId(string)` lookup path is binary search over `atoms.atoms` via `atoms.offsets`:

```
log₂(4 B) ≈ 32 comparisons
each comparison ≈ 1 cache miss (offsets read) + 1 cache miss (atom bytes read) + 1 memcmp
total ≈ 32 cache misses + 32 byte-comparisons per lookup
```

This is the production cost of every SPARQL query that binds a string term: `?s ?p "Stockholm"@sv`, `?s rdfs:label "..."`, every `text:match(...)` trigram-prefilter resolution. Cycle 8's smoke test measured `text:match("Stockholm")` cold at 11 s end-to-end; some fraction of that is string-to-ID resolution overhead.

The sealed-set property — atoms cannot be added after `Finalize` — is structurally new in 1.7.48. It enables a class of optimizations that weren't accessible under the prior HashAtomStore (which allowed live insertions and required online structure). One such optimization is **Minimal Perfect Hash Function (MPHF) lookup**: compute the atom ID directly from the string with no probing, no comparisons, no log-N traversal.

ADR-034's Phase 2 sketch named "BBHash MPHF on top — O(1) lookups with zero collisions at ~1.6 bits/key overhead." This ADR scopes that work.

### The MPHF property

A minimal perfect hash function over a fixed set S of N strings maps S → {0, 1, …, N−1} bijectively: every string in S maps to a unique integer in the dense range; no collisions. Construction is over the static set; once built, lookup is O(1) — typically 1–3 hash computations plus a small table read.

**Important: MPHF does not validate input.** For a string s ∉ S, `mphf(s)` returns *some* integer in [0, N), colliding with a real atom. Lookup must therefore be:

```
id = mphf(query_string)
if id < 0 or id ≥ N: return NotFound
atom_bytes = SortedAtomStore.GetAtomString(id)   // O(1) — direct offset read
if atom_bytes != query_string: return NotFound
return id
```

The verification step (read `atoms.atoms[id]`, compare to `query_string`) costs **2 cache misses + 1 memcmp**: first the offset lookup in `atoms.offsets[id]`, then the byte read in `atoms.atoms` at that offset. Both reads land in OS page cache after warmup. Vs binary search's 32 of each. Still a big win — the constant factor improvement is ~10–16×, plus the algorithmic O(1) vs O(log N) shape benefits at scale.

## Hypothesis (falsifiable)

**H — `GetAtomId(string)` is 10–30× faster with MPHF than with binary search at 4 B-atom scale**, measured as median wall-clock per call.

The 10–30× range comes from cache-residency assumption variance:
- Lower bound (10×): cold cache, every binary-search step is a hard memory fetch.
- Upper bound (30×): partially warm cache for small lookups; MPHF's hash + 1 verify still wins.
- Real workloads sit in between.

**Falsified if** the measured median speedup at 100 M atoms on warm cache is < 5×, OR the MPHF construction time at 4 B keys exceeds 4 hours on M5 Max (making the build cost unrecoverable for typical workloads).

A secondary metric — query-side wall-clock for SPARQL queries with bound string terms — should improve proportionally to the fraction of query time spent in `GetAtomId`. If `GetAtomId` is 5 % of query wall-clock, MPHF saves ~5 %; if it's 50 %, MPHF saves ~50 %. Either way, the per-call speedup is the primary metric.

## Decision

### Algorithm: BBHash (Limasset et al.)

BBHash is the MPHF family fit for our scale and constraints:

- **O(N) construction** via multiple hash-and-bitvector iterations. ~minutes-to-hours at 4 B keys.
- **~1.6 bits/key storage overhead**. At 4 B keys → **~800 MB** total MPHF size.
- **2–3 hash computations + 1 bit-vector lookup per query**, all in CPU + L1/L2 cache. No comparisons, no string memcmp during lookup itself.
- **Pure-C# implementable** — uses bit vectors, hash function (xxHash3 from `System.IO.Hashing`), and rank-select bitvector ops. BCL-only fit.
- **Algorithm shape** is well-published, has many reference implementations.

Alternatives considered:

- **CHM/CHD MPHF families** — slightly different construction; comparable storage; comparable lookup. BBHash chosen for simpler implementation and smaller per-key overhead.
- **RecSplit** — finer-grained splits, ~1.5 bits/key. More complex implementation; marginal benefit at our scale. Reject for now; revisit if BBHash overhead is binding.
- **Cuckoo / open-addressing hash table** — *not* an MPHF; allows collisions; needs probing. Equivalent to today's HashAtomStore. Rejected.
- **Trie-based string-to-ID lookup** — O(string length) lookup, no hashing. Slower than MPHF; rejected.

### Storage: sibling file `atoms.mphf`

A new file alongside `atoms.atoms` and `atoms.offsets`:

```
atoms.atoms     ← prefix-compressed atom bytes (existing)
atoms.offsets   ← per-atom offset into atoms.atoms (existing)
atoms.mphf      ← BBHash blob (new, ~800 MB at 4 B atoms)
```

**File format (concrete spec):**

```
[ 4 bytes ]  magic:    0x4D504846 ("MPHF" big-endian)
[ 4 bytes ]  version:  0x00000001 (uint32, schema version; bumped on incompatible change)
[ 8 bytes ]  num_keys: uint64
[ 8 bytes ]  hash_seed: uint64 (xxHash3 seed)
[ 4 bytes ]  bit_vector_count: uint32 (BBHash iteration count, typically 3-5)
[ 4 bytes ]  reserved
─── per bit-vector (repeated bit_vector_count times) ───
[ 8 bytes ]  bv_bit_count: uint64
[ 8 bytes ]  bv_byte_count: uint64 (= ceil(bv_bit_count / 8))
[ 8 bytes ]  rank_table_byte_count: uint64
[ ... ]      bit_vector_bytes (raw bit array)
[ ... ]      rank_table_bytes (precomputed rank lookup, every 512 bits)
```

Magic bytes catch schema drift and partial-write corruption immediately at open: any mismatch → fall back to binary-search path with a warning, do not silently use a corrupt MPHF. Version bump is the discipline if the format ever changes.

### Construction: integrate into `MergeAndWrite` post-write

After `MergeAndWrite` completes the dedup+write of `atoms.atoms` and `atoms.offsets`:

```csharp
// New step inside MergeAndWrite or as a chained call after it:
if (profile == StoreProfile.Reference) {
    var mphf = BBHashBuilder.Build(
        atomCount,
        i => GetAtomString(i),    // iterate keys via atoms.atoms
        seed: stableHashSeed
    );
    File.Copy(mphf.SerializeToBytes(), $"{baseFilePath}.mphf");
}
```

Construction cost is one-time, lives inside the bulk-load run. Adds to total wall-clock but is *not on the per-query path*; amortizes immediately.

Cycle 10 should measure construction time at 4 B atoms. Projected: 30 min – 2 h on M5 Max based on BBHash papers' published throughput (≈1–4 M keys/sec).

### Lookup: opt-in via `IAtomStore.TryGetAtomIdMphf`

Add a method to `IAtomStore`:

```csharp
public interface IAtomStore {
    long GetAtomId(string s);                  // existing — binary search fallback
    long GetAtomId(ReadOnlySpan<char> s);
    bool TryGetAtomIdMphf(ReadOnlySpan<char> s, out long id);  // new — null returns -1 if MPHF unavailable
}
```

Default `GetAtomId` keeps binary search as the correctness baseline. Callers that benefit (SPARQL bound-term resolution, trigram FILTER lookups) opt into the MPHF path. Falls back to binary search if `atoms.mphf` doesn't exist (e.g., older cycle 8 stores or Cognitive profile).

### Concurrency / thread-safety contract

After construction, the MPHF blob is **immutable**. Bit-vectors and rank tables are read-only; lookup performs no mutation. Multiple threads can call `TryGetAtomIdMphf` concurrently without coordination. The mmap-backed read in `atoms.atoms` (verification step) is also read-only and thread-safe — same contract as the existing `GetAtomString(id)`.

No locks, no atomics, no `volatile` reads. Readers see consistent state because the file is read-only after creation, and the single-process-writer contract for atoms persists from ADR-034 (Reference profile is sealed at finalize).

### Construction memory budget

BBHash's iterative algorithm needs working memory for two parallel bit-vectors per iteration: a *collision* bit-vector and an *output* bit-vector. At 4 B keys with ~3 iterations and ~1.6 bits/key per iteration, peak working memory is approximately:

| | Approximate peak |
|---|---:|
| 1 M atoms | ~1 MB |
| 10 M atoms | ~10 MB |
| 100 M atoms | ~100 MB |
| 1 B atoms | ~1 GB |
| 4 B atoms (21.3 B Wikidata) | **~1.5–2 GB peak** |

Comfortable on a 128 GB host. Construction also performs N reads from `atoms.atoms` (one per key) → ~99 GB of sequential disk reads at 4 B-atom scale. Sequential scan is fast on NVMe (~30 s at 3 GB/s), so disk I/O is not the binding cost.

### Profile applicability

- **Reference profile (sealed):** ✓ Perfect fit. MPHF built at finalize, valid forever after.
- **Cognitive profile (mutable):** ✗ Skip. Atoms can be added; MPHF would invalidate on every write. Cognitive's smaller scale + Hash atom store handle the lookup path adequately.
- **Graph / Minimal profiles:** Same as Cognitive — skip for now. Could revisit if those profiles gain a "freeze" semantic later.

The MPHF is a Reference-profile-specific optimization, dispatched via `_schema.Profile == StoreProfile.Reference && File.Exists($"{baseAtomPath}.mphf")`.

## Validation protocol

### Unit tests

- Round-trip: build MPHF over a small sorted set (1 K, 100 K, 1 M atoms); assert `mphf(known_string) → correct_id` for all in-set; assert `mphf(unknown_string)` returns *some* id but verification fails.
- Construction-determinism: with the same seed, build twice; bit-vectors must match exactly.
- Serialization round-trip: build → serialize → deserialize → query; results identical.
- Profile dispatch: Reference store with `atoms.mphf` uses MPHF; Reference store without it falls back to binary search; Cognitive store always uses binary search.

### Gradient (per-fix correctness, before cycle 10)

1 M / 10 M / 100 M Wikidata Reference + Sorted bulk-load with MPHF construction enabled. Measure:
- MPHF construction time (must scale roughly linearly).
- MPHF file size (must be ≤ 2 bits/key, target 1.6).
- 10,000 random in-set queries: median + p99 wall-clock, MPHF vs binary search. Target: median 10–30× faster.
- 1,000 random out-of-set queries: confirm the verification path correctly returns NotFound.

### Cycle 10 production validation (composed with ADR-038)

Full 21.3 B Wikidata Reference + Sorted bulk-load with:
- ADR-038 P1 + P2 + sidecar (merge-phase read-side)
- ADR-039 MPHF construction (atom-store query-side)

Both fixes have orthogonal metrics. Independent attribution:
- ADR-038 metric: `intermediate_volume_bytes`, merge regime distribution, long-tail rate
- ADR-039 metric: `mphf_construction_seconds`, post-build `GetAtomId(s)` latency distribution (sampled in WDBench rerun)

Cycle 10 should also re-run a subset of WDBench against the new store to confirm the query-side speedup at production scale.

## Consequences

### Positive

- 10–30× speedup on every string-to-ID lookup at the SPARQL query layer.
- Compounds with all SPARQL patterns that bind string terms (most production patterns).
- Trigram FILTER paths benefit (the `text:match` smoke test cold latency should drop measurably).
- Storage overhead is tiny (~1 % of `atoms.atoms`).
- Construction cost is one-time at bulk-load finalize; doesn't recur.
- Composes cleanly with future ADR-034 phases (Phase 2/3 work would have built MPHF anyway; this is the proper landing).

### Negative / risks

- New algorithm to implement. BBHash is well-published but non-trivial — bit-vector rank/select operations need to be correct under all input shapes. Unit tests + reference vector tests are mandatory.
- The verification step (read atom bytes, memcmp) is *required* for correctness. Any caller using the MPHF path that skips verification will return wrong IDs for unknown inputs. The interface signature (`bool TryGetAtomIdMphf` returning success indicator) makes this hard to misuse.
- MPHF construction adds bulk-load wall-clock — projected 30 min – 2 h at 4 B atoms. Trades against the amortized per-query gain.
- Cognitive profile gets no benefit — but Cognitive's smaller scale + hash atom store don't need it.

### What this does NOT do

- Doesn't help `GetAtomString(id)` (already O(1) via offsets file).
- Doesn't help bulk-load itself (atoms are *assigned* during merge, not looked up).
- Doesn't change SPARQL query semantics — MPHF is purely a faster path for the same logical lookup.
- Doesn't reduce `atoms.atoms` size — that stays 99 GB. Adds ~800 MB of MPHF.

### Limits register impact

- This ADR doesn't directly resolve any current limits-register entry, but it's a follow-on to ADR-034's Phase 2 line. May add a sibling entry tracking "string-to-ID query-side latency" if cycle 10 measurements identify cases where MPHF is binding (e.g., sub-millisecond queries dominated by binary-search overhead).

## References

- ADR-034 — SortedAtomStore for Reference profile; Phase 2 sketch named MPHF
- BBHash paper: Limasset et al., "Fast and scalable minimal perfect hashing for massive key sets," 2017
- `src/Mercury/Storage/SortedAtomStore.cs` — current binary-search implementation
- `src/Mercury/Storage/SortedAtomStoreExternalBuilder.cs` — `MergeAndWrite` is the integration point for MPHF construction
- Cycle 8 + cycle 9 runs: 4,005,235,528 atoms — the production scale this ADR targets
- `urn:sky-omega:incident:cycle9-21b-complete-2026-05-08` (Mercury) — the substrate this builds on
