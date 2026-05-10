# Sky Omega Latest Version Review

**Reviewed artifact:** `/mnt/data/sky-omega.zip`  
**Review date:** 2026-05-10  
**Basis:** Actual ZIP contents inspected in this session. No claims below rely on memorized repository state.

---

## 1. Repository Snapshot

| Item | Observed value |
|---|---:|
| Git branch | `main` |
| HEAD commit | `8c3557f` |
| `.csproj` files | 20 |
| C# source files | 434 |
| New substrate project | `src/SkyOmega.Bcl` |
| New test project | `tests/SkyOmega.Bcl.Tests` |
| Latest changelog entry | `1.7.53 — 2026-05-10` |
| Active validation theme | Cycle 10 Phase 3: 21.3B Wikidata production-scale bulk-load |

I could not run `dotnet test` in this execution environment because the `dotnet` CLI is unavailable. The repository changelog states that all `4395` Mercury tests and all `12` SkyOmega.Bcl tests pass, but that was not independently verified here.

---

## 2. Executive Assessment

The latest version has moved materially beyond the previous Cycle 9 / `1.7.50` state.

The important shift is `1.7.53`: the production-scale MPHF construction failure around 4B atoms exposed ordinary BCL `int`-indexed collection limits, and the fix moves the missing primitives into a new substrate-level `SkyOmega.Bcl` project.

That is architecturally correct.

The current version is not merely proving that Mercury can ingest Wikidata. That was the Cycle 9 proof. This version is converting the ingestion path into a production-scale substrate with explicit limits, measured mitigations, and scale-correct primitives.

Current status:

- Early Phase 3 ingestion/spill behaviour looks strong.
- The MPHF/int32 blocker has been addressed in the right layer.
- The ADR-038 B1 compressed intermediate direction appears to be working.
- The ADR-038 B2 readahead path should not yet be trusted as production-safe.
- Documentation is trailing implementation, which is acceptable during validation but should be consolidated before external presentation.

---

## 3. What Changed in `1.7.53`

The latest changelog entry describes a production-scale crash in Cycle 10 Phase 3 around chunk `004045`, approximately 4B atoms, caused by an `OverflowException` from:

```csharp
new List<long>(checked((int)keyCount))
```

The correction introduced or moved several substrate primitives:

- `SkyOmega.Bcl`
- `ChunkedList<T>`
- `ChunkedArray<T>`
- `BitVector`
- `SplitMix64Hash`

The MPHF builder was corrected away from several `int` traps, including:

- `List<long>(checked((int)keyCount))`
- `long[keyCount]`
- `Dictionary<long, int>(remaining.Count)`
- `long[remaining.Count]`
- large temporary collectors based on normal arrays/lists

The most important correction is replacing the collision dictionary shape with two `BitVector`s:

- `seen`
- `collided`

That is a scale-correct move. It removes both the `int` ceiling and the pathological memory shape of a dictionary at billions of keys.

---

## 4. Phase 3 Production-Run Evidence

The checked-in validation log inspected was:

```text
docs/validations/cycle10-phase3-21b-bulk-2026-05-10.jsonl
```

Observed state from the file:

| Metric | Observed value |
|---|---:|
| Last captured triple count | `641,700,000` |
| Approximate progress vs 21.316B triples | `~3.01%` |
| Average load rate | `~657,983 triples/sec` |
| Recent load rate | `~724,028 triples/sec` |
| Spill events captured | `111` |
| Spill queue depth at handoff | `0` |
| Approximate compressed spill bytes so far | `~17.9 GB` |
| Disk free at last sample | `~6.81 TB` |

Assessment:

The early parser/spill phase looks healthy. Spill queue depth remaining at zero is a strong signal that the spill worker is not backing up. Compressed intermediates are dramatically smaller than raw pressure would imply, so ADR-038 B1 appears to be doing useful work.

However, this JSONL evidence does not yet validate:

- the full merge phase,
- readahead stability,
- MPHF construction at full 21.3B scale,
- final index rebuild behaviour,
- queryable-store correctness after the full run.

The current checked-in evidence validates the early bulk-load path, not the entire Cycle 10 stack.

---

## 5. Major Finding: Readahead Concurrency Hazard

The highest-risk issue found is in the ADR-038 B2 readahead implementation.

`ChunkReadAheadDispatcher` uses `BoundedFileStreamPool` from multiple worker tasks. But `BoundedFileStreamPool` declares a single-threaded contract and internally uses mutable `Dictionary` / `LinkedList` state without synchronization.

That means the readahead path violates the abstraction’s own stated contract.

At the projected 21.3B scale, compressed chunk count likely stays below the hard pool cap of `8192`, so stream eviction may not occur. But concurrent mutation can still occur even without eviction. That means the B2 path can behave nondeterministically or corrupt internal pool state under pressure.

Recommendation:

For the immediate production validation run, disable B2 readahead and validate B1 + B3 first:

```bash
MERCURY_MERGE_READAHEAD=0
```

This should preserve the compressed-intermediate benefit while avoiding the unsafe concurrent stream-pool path.

---

## 6. Related Finding: Stream Reopen / Position Recovery Risk

The direct fallback path appears to assume stable stream position. The compressed chunk sidecar index files are written, but I did not find corresponding logic that uses them to recover position after stream eviction and reacquire.

If chunk count ever exceeds the stream-pool cap, the direct path may reopen a stream and reread from the beginning with stale prefix state.

For the current dataset, projected compressed chunk count appears likely to stay below `8192`, so this may not affect the immediate run if readahead is disabled. Structurally, however, compressed chunk reading needs one of the following:

1. make `BoundedFileStreamPool` lease-based and thread-safe;
2. avoid sharing pooled streams in readahead workers and let each worker open short-lived bounded `FileStream`s;
3. implement logical offsets plus sidecar-based seek recovery;
4. assert/fail clearly when compressed direct mode would exceed stream-pool capacity.

The simplest safe B2 fix is probably option 2: worker-local short-lived `FileStream`s per refill. That bounds file descriptor pressure by worker count and avoids sharing mutable stream-pool state.

---

## 7. Readahead Memory Budget Is Understated

`ChunkReadAheadBuffer` uses front and back buffers.

With the default 4 MiB buffer size, the real maximum is closer to 8 MiB per active chunk, not 4 MiB. At roughly 3,700–4,000 projected chunks, that can imply around 30+ GiB of user-space buffer pressure if all buffers are warm.

On a 128 GB MacBook Pro this is not automatically fatal, but the true memory shape should be explicit in the ADR and operational notes.

---

## 8. MPHF Is Structurally Unblocked but Still Allocation-Heavy

The `1.7.53` change removes the hard `int` failure mode, but MPHF construction still appears allocation-heavy.

The notable pattern is:

```csharp
atoms.GetAtomSpan(sortedPos).ToArray()
```

inside MPHF construction.

At billions of atoms, this means billions of small `byte[]` allocations across MPHF levels. That is not a correctness blocker of the same class as the previous `int` trap, but it may become a serious GC and throughput pressure point.

Recommended future direction:

`BBHashBuilder` should accept a span-based hash callback or non-allocating key reader path. Do not cache all keys; just avoid materializing a fresh array per atom per level.

---

## 9. MPHF Serialization Uses Large Temporary Arrays

`BBHash.WriteTo` and `BBHash.ReadFrom` still use large temporary byte arrays for serialization.

At the first MPHF level, the bit-vector word payload may approach roughly 1 GB. That is below the 2 GB array limit, but it creates a huge LOH allocation during write/read.

This is not currently the top risk, but it is a substrate-quality concern.

Recommended future direction:

Stream words/ranks in smaller blocks instead of materializing one very large temporary byte array.

---

## 10. Test Coverage Gap

The `BoundedFileStreamPoolTests` include an “exceeds pool capacity” style scenario, but the pool hard cap has since moved to `8192`.

A test that previously exceeded capacity may no longer actually exceed capacity. That means an important eviction/reopen path may no longer be covered.

Recommended test addition:

- create a test that deliberately exceeds the effective pool cap;
- verify compressed direct-reader correctness across eviction/reopen;
- add a separate test for concurrent readahead access if B2 continues to share any mutable infrastructure.

---

## 11. Documentation State

Several docs now trail implementation:

- `README.md` still presents `v1.7.50 / Cycle 9 complete` as the headline state.
- `STATISTICS.md` still describes ADR-038/039 as sequenced rather than partially implemented.
- `docs/limits/cognitive-profile-validation-drought.md` still says the validation drought is triggered, while Cycle 10 Phase 0 says it is resolved.
- `docs/roadmap/cycle-10-multi-fix-plan.md` still reads as proposed even though phases 0–2 are complete and Phase 3 is in-flight.

This should not be treated as release-quality criticism while instrumentation-heavy validation is active. The documentation should be consolidated after the production run completes, not during the run.

---

## 12. Recommended Priority Order

1. Disable B2 readahead for the current 21.3B validation run.
2. Validate B1 compressed intermediates + B3 MPHF end-to-end first.
3. Add structured JSONL events for MPHF build stages, not just stderr lines.
4. Fix readahead so it does not share the current non-thread-safe `BoundedFileStreamPool`.
5. Add a real pool-capacity-exceeding test.
6. Add compressed direct-reader eviction/reopen correctness tests.
7. Replace MPHF `ToArray()` hashing path with span-based hashing.
8. Stream MPHF serialization/deserialization in blocks.
9. Consolidate README / STATISTICS / limits / roadmap docs after the full validation result is known.

---

## 13. Bottom Line

The project is in a strong but delicate state.

`1.7.53` is a serious production-scale correction and shows the right architectural reflex: when a substrate limit appears, the fix is not a local patch but a substrate primitive.

The early Phase 3 numbers are excellent. The ingest/spill path looks healthy. The MPHF/int32 blocker has been addressed in the right layer.

But Cycle 10 should not yet be considered fully validated.

The immediate risk is ADR-038 B2 readahead. The safest path is to validate the full dataset with readahead disabled, prove B1 + B3 first, and then harden B2 separately.

The current version is doing exactly what an empirical substrate project should do: expose hidden limits, name them, instrument them, and convert them into durable architecture.
