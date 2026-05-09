# Cycle 10 Phase 0 — Cognitive validation gradient (2026-05-09)

**Status (2026-05-09):** Cycle 10 Phase 0 complete. Cognitive bulk-load gradient at 1 M / 10 M / 100 M against `latest-all.ttl.bz2` on 1.7.50 substrate. **All three falsification triggers cleared.** Cognitive profile substrate has not regressed across the 1.7.22 → 1.7.50 trajectory; it has *gained* throughput. The cognitive-profile-validation-drought ([`docs/limits/cognitive-profile-validation-drought.md`](../limits/cognitive-profile-validation-drought.md)) is Resolved.

## What this closes

Most recent prior Cognitive measurement was 1.7.22 (April 2026 full-pipeline gradient at 1 B). Since then 10+ ADRs have shipped against shared infrastructure: ADR-028 rehash, ADR-031 Dispose gate, ADR-035 Phase 7a metrics, ADR-036 bz2 streaming, property-path grammar refactor (1.7.46/47), cancellation gate (1.7.46), 1.7.49 cleanup hook. None measured against Cognitive. Phase 0 closes this gap before cycle 10's Phase 1+2+3 ship more changes.

## Run command

Per [docs/roadmap/cycle-10-multi-fix-plan.md](../roadmap/cycle-10-multi-fix-plan.md) Phase 0:

```bash
mercury --store wiki-{N}m-cogp0 \
        --bulk-load latest-all.ttl.bz2 \
        --profile Cognitive \
        --limit {N}_000_000 \
        --metrics-out cycle10-phase0/{N}m.jsonl \
        --metrics-state-interval 30 \
        --no-http --no-repl
```

Sequenced 1 M → 10 M → 100 M via launch script (`/tmp/cycle10-phase0/launch.sh`), detached via `nohup`.

## Headline numbers

| Scale | Triples loaded | Wall-clock | Avg rate | Atom count | Load factor | Max probe |
|---|---:|---:|---:|---:|---:|---:|
| 1 M   | 1,000,000   | **5.5 s**     | **183,008/sec** | (not sampled at 1M) | — | — |
| 10 M  | 10,000,000  | **51.0 s**    | **196,153/sec** | 1,816,770   | 0.7 %  | **2** |
| 100 M | 100,000,000 | **6 m 30 s**  | **256,588/sec** | 24,960,288  | 9.3 %  | **4** |

Throughput rises with scale — typical Cognitive bulk-load profile (atom-store warmup costs amortize over more triples). 256 K/sec at 100 M Cognitive is fast: comparable to 100 M Reference Sorted on 1.7.50 (~350 K/sec) within ~30 % overhead, attributable to the full 88 B Cognitive schema vs Reference's 32 B.

## Atom-store health (ADR-028 rehash regression check)

The load-bearing metric for ADR-028 health is *probe distance* — if rehash had silently regressed, probe distance would climb pathologically as the atom-store fills. Phase 0 measurement at 100 M:

```
atom_count:       24,960,288
bucket_count:     268,435,456
load_factor:      0.093 (~9.3 %)
probe_distance:   p50=0  p95=0  p99=1  p999=2  max=4
```

**max probe distance = 4 across 25 M atoms is excellent** — well below the > 50 trigger. Hash function quality + ADR-028 rehash both healthy at this scale.

Zero `atom_rehash` events fired during 1 M and 10 M; 1 `atom_file_growth` event during 100 M (expected — the file pre-allocation grows as the atom set fills). No pathological rehash storm.

## Correctness smoke test

Post-bulk-load smoke test against the 100 M Cognitive store via `SparqlEngine.Query`:

```
[open]               16 ms     Profile=Cognitive AtomStore=Hash IndexState=PrimaryOnly
[stats]              <1 ms     quads=99,166,092
[bounded SELECT 5]   18 ms     5 rows
[Dump-subject query] 1 ms      9 rows
```

- `quads=99,166,092` matches the expected 0.83 % RDF set-semantics dedup ratio that cycle 8 + cycle 9 measured at 21.3 B — Cognitive dedup behaves identically.
- `<http://wikiba.se/ontology#Dump>`-subject query returns 9 rows in 1 ms — matches cycle 8's smoke test result for the same query against the 21.3 B Reference store.
- No exceptions, no parse failures, no zero-row anomalies.

## Falsification triggers — none fired

Per Phase 0 plan in [cycle-10-multi-fix-plan.md](../roadmap/cycle-10-multi-fix-plan.md):

| Trigger | Threshold | Measured | Status |
|---|---|---|---|
| Wall-clock regression vs 1.7.22 1B-extrapolation | > 1.5× slower | 100 M ran in 6:30 at 256 K/sec — substantially *faster* than 1.7.22-era pace | **✓ pass** |
| Atom-store probe distance pathology | max > 50 | max = 4 at 100 M | **✓ pass** |
| Correctness on bound-term queries | wrong row counts / failures | quads count, query rows match cycle 8 substrate; 0 failures | **✓ pass** |

## What this validates

- **Shared infrastructure changes did NOT regress Cognitive.** ADR-028 rehash, ADR-031 Dispose gate, ADR-035 metrics, ADR-036 bz2, property-path refactor, cancellation gate, cleanup hook — all shipped between 1.7.22 and 1.7.50 against shared code paths Cognitive uses. Phase 0 confirms throughput preserved (and slightly improved) and atom-store health excellent.
- **Reference and Cognitive code paths are independent at the implementation level.** ADR-037 / 1.7.49 / ADR-038 / ADR-039 are all Reference-only (`SortedAtomBulkBuilder`, `MergeAndWrite`, MPHF-on-sealed-set). Phase 0's clean Cognitive numbers confirm those changes don't structurally affect the `QuadStore.AddCurrentBatched` → `TemporalQuadIndex.InsertIntoLeaf` path.
- **The cycle 10 multi-fix methodology is unblocked.** Phase 0's purpose was to validate the assumption that we can ship Reference-track perf changes without unintentionally affecting Cognitive. Confirmed.

## What this does NOT validate

- **Cognitive bulk-load + rebuild end-to-end.** Phase 0 measured bulk-load only (`IndexState=PrimaryOnly` at the smoke-test time). Cognitive rebuild path was not exercised. If a future cycle needs to validate Cognitive end-to-end, run `mercury --rebuild-indexes` against one of the Phase 0 stores.
- **WDBench at 100 M Cognitive.** Excluded per cycle 10 plan — latency distribution is uninformative at that scale, correctness signals don't justify the additional 3–4 h run.
- **Truthy-subset Cognitive.** Excluded per cycle 10 plan — publication-comparability question for QLever / Virtuoso WDBench numbers, separate decision.
- **1 B / 10 B / 21.3 B Cognitive at 1.7.50.** Out of scope. The 100 M smoke + drought-closure was the Phase 0 ask; not a full Cognitive scale-validation run.

## References

- [Cycle 10 multi-fix plan](../roadmap/cycle-10-multi-fix-plan.md) — Phase 0 specification
- [`docs/limits/cognitive-profile-validation-drought.md`](../limits/cognitive-profile-validation-drought.md) — limit being closed
- [feedback_optimization_taxonomy](../../.claude/projects/-Users-bemafred/src/repos/sky-omega/memory/feedback_optimization_taxonomy.md) — "validation IS optimization-discipline" reasoning
- 1.7.22 era Cognitive measurements:
  - [ADR-028 rehash gradient](adr-028-rehash-gradient-2026-04-20.md) — 1 M / 10 M / 100 M Cognitive with forced 16 K initial hash
  - [Full-pipeline gradient (1 B Cognitive)](full-pipeline-gradient-2026-04-19.md) — 1 B Cognitive bulk + rebuild, the most recent 1 B Cognitive measurement
- Phase 0 raw artefacts: `/tmp/cycle10-phase0/{1m,10m,100m}.jsonl` (regenerable from the launch script)
- `urn:sky-omega:obs:cognitive-drought-deferred-to-cycle10-phase0-2026-05-09` (Mercury) — the deferral that this validation closes
