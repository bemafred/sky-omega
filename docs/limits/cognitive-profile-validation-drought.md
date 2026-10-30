# Limit: Cognitive profile validation drought

**Status:**        Resolved (cycle 10 Phase 0, 2026-05-09)
**Surfaced:**      2026-04-30, via the public-claims sweep prompted by `memos/2026-04-30-latent-assumptions-from-qlever-comparison.md`. ADR-008 makes profile-attribution mandatory for validations; the sweep revealed that all measured validations from 2026-04-19 onward exercise the Reference profile. The Cognitive profile's last measured wall-clock at 1B was 2026-04-19 (`docs/validations/full-pipeline-gradient-2026-04-19.md`), which predates ADR-031 (Dispose gate, 1.7.32), ADR-032 (radix external sort, 1.7.39+), ADR-033 (bulk-load radix, 1.7.43), ADR-034 (SortedAtomStore — Reference-only), ADR-035 Phase 7a (metrics), ADR-036 Phase 7b (bz2 streaming), and the 1.7.46/1.7.47 property-path hardening.
**Last reviewed:** 2026-10-30 (evidence at 1.8.39, below)
**Resolved by:**   [docs/validations/cycle10-phase0-cognitive-gradient-2026-05-09.md](../validations/cycle10-phase0-cognitive-gradient-2026-05-09.md) — Cognitive bulk-load gradient at 1 M / 10 M / 100 M against `latest-all.ttl.bz2` on 1.7.50 substrate. All three falsification triggers cleared (wall-clock no regression vs 1.7.22; max probe distance = 4 at 25 M atoms; correctness smoke pass). Cognitive throughput rises with scale — 256 K/sec at 100 M vs Reference Sorted's ~350 K/sec at the same scale (~30 % overhead attributable to 88 B vs 32 B entries).

## Evidence at 1.8.39 (2026-10-30)

The status above records what the 1.7.50 gradient closed: Cognitive bulk-load throughput up to 100 M triples. This
section records what validates the Cognitive profile at 1.8.39, area by area. Mercury's semantic memory runs on
Cognitive, the default profile, so the published record leans on Reference measurements. These are the limits of
reasoning from those measurements to Cognitive.

### What a Reference result says about Cognitive

The two profiles share some code and differ in their storage (`QuadStore.cs`, ADR-029):

| | Cognitive | Reference |
|---|---|---|
| Indexes | GSPO, GPOS, GOSP and TGSP: `TemporalQuadIndex`, 88-byte entries with valid time and transaction time | GSPO and GPOS: `ReferenceQuadIndex`, 32-byte entries |
| Atom store | `HashAtomStore` | `SortedAtomStore`, with a minimal perfect hash (ADR-039) |
| Write-ahead log | Yes | No |
| Writes | SPARQL Update and the session API at any time, and bulk load | Bulk load only, then sealed |

Shared by both: the parsers, the SPARQL engine (one execution path since ADR-047) and its expression evaluator, the
B+tree page file (`BTreeFile`) and the trigram index.

So a Reference result is evidence for Cognitive only where it exercises shared code: parsing, query planning and
operators, B+tree paging. It says nothing about the temporal indexes, the hash atom store, the write-ahead log or
recovery, which Reference does not have. The 21.3 B Reference runs do not show that a Cognitive store reaches that
size, or how one behaves under writes.

### Temporal semantics: tested at test size; the tests missed one defect for six months

- Cognitive is the default profile, so the test store pool, and with it the W3C SPARQL suites, run on Cognitive
  stores.
- Temporal behavior has its own tests: `TemporalQuadIndexTests` (32), `DeleteEndsValidTimeTests` (17), and
  `TemporalSparqlTests` (11) for `AS OF`, `DURING` and `ALL VERSIONS`.
- Those tests did not catch the deepest temporal defect found so far. A SPARQL DELETE removed a fact's history instead
  of ending its valid time. A census of a copy of the memory store found 297 entries recorded this way, the oldest
  from 2026-04. It was found while designing ADR-053, fixed in 1.8.23 (ADR-054), and the live store was repaired.
- **Not shown:** temporal queries at scale. No `AS OF`, `DURING` or `ALL VERSIONS` query has been timed on a store
  larger than a test's.

### Durability: not established; an open defect

- The write-ahead log is fsynced at commit (see Durability Design in
  [mercury-internals.md](../architecture/technical/mercury-internals.md)).
- `TransactionalIntegrityTests` (9) and `WriteAheadLogTests` (34) test recovery. Their "crash" tests dispose the
  store, which is a clean close. None kills a process.
- [ADR-058](../adrs/mercury/ADR-058-wal-replay-restores-consistent-store.md) (Proposed, 2026-10-29): a scripted
  `kill -9` and restart of `mercury-mcp` lost three triples whose commit had been acknowledged and fsynced. The next
  write then gave new atoms the IDs of atoms the surviving index entries still pointed to. The cause is in the code:
  the checkpoint truncates the log without persisting the B+tree entry counts or the atom store's next ID. The same
  state was found in elite's memory store after a routine MCP reconnect.
- **Until ADR-058 is fixed,** a Cognitive store that stops without a clean shutdown can lose acknowledged writes,
  and its next write can corrupt atoms.

### Latency: bulk load measured to 1 B; queries barely

- Bulk load: 256 K triples/s at 100 M on 1.7.50 (2026-05-09). Bulk load and index rebuild at 1 B on 1.7.22
  (2026-04-19): rebuild 3 h 7 m, and a predicate-bound `COUNT` over the 1 B store in 49.3 s.
- Queries: only smoke queries on the 100 M store at 1.7.50 (a bounded SELECT in 18 ms, a subject lookup in 1 ms).
- Since 1.7.50 the SPARQL engine has been replaced (ADR-045, ADR-047, ADR-049), and Update's WHERE runs through it
  (ADR-052). The new engine was measured at scale on Reference only
  ([WDBench differential, 2026-06-13](../validations/wdbench-differential-breadth-2026-06-13.md)). On Cognitive it
  runs the same operators over 88-byte entries, four indexes and the hash atom store, so the Reference timings do not
  carry over.
- The memory stores are small: omega's holds 6,878 quads. Their use says nothing about latency at scale.
- **Not shown:** query or update latency on a Cognitive store above test size, on the current engine.

### Operational behavior: used daily, at small size

- The Mercury MCP server sets no profile, so each machine's memory store is Cognitive. Omega's holds 78 named graphs,
  with session graphs dated from 2026-03-29 to 2026-10-30: 6,878 quads, written in 782 transactions (`mercury_stats`,
  2026-10-30).
- That use has found defects the tests missed, and each became an ADR and tests: a chained `;` update ran only its
  first operation (ADR-046, 1.8.12); an Update WHERE failed to parse (ADR-052, 1.8.22); the delete defect above
  (ADR-054); the lost writes on elite (ADR-058, open).
- **Not shown:** stores beyond thousands of quads under daily writes, more than one writing process, and restarts
  after an unclean stop (ADR-058).

### What this means

- **Mitigated:** SPARQL and temporal semantics on Cognitive. The conformance suites run on it, daily use finds what
  they miss, and with one execution path a query defect found on Reference is fixed for Cognitive too.
- **Not mitigated:** durability across an unclean stop (ADR-058 is open), and query latency, temporal queries
  included, at any size above the tests, on the current engine.
- **Claims:** a Reference number is a Reference claim (ADR-008). It is not evidence of Cognitive durability, temporal
  behavior, latency or operation.

### What would close the gaps

1. ADR-058, with its restart conformance test (D3): kill a process after acknowledged commits, restart it, and compare
   the store with what was acknowledged.
2. A Cognitive gradient on the current engine, at 1 M, 10 M and 100 M, timing queries as well as the load, with
   temporal queries among them: candidate mitigations 1 and 3 below.

## Description

Sky Omega has two workload profiles per ADR-008: Reference (large mostly-static, ad-hoc-SPARQL-shaped) and Cognitive (smaller working set, write-heavy, bitemporal-dense, James-driven query patterns). The substrate work since 2026-04-19 has overwhelmingly targeted Reference: all of ADR-029 through ADR-036, plus the property-path hardening arc, plus the 21.3 B Wikidata Phase 6 run. The validation evidence in `docs/validations/` reflects this.

The most recent Cognitive validation is `full-pipeline-gradient-2026-04-19.md` — a 1B Cognitive bulk + rebuild run at version 1.7.22, before:

- **ADR-031 Dispose gate** (1.7.32) — collapsed read-only Dispose from 14 min to 0.84 s on 1 B Cognitive
- **ADR-032 radix external sort for rebuild** (1.7.39+) — 10.5× faster rebuild at 100M (Reference; Cognitive impact unmeasured)
- **ADR-033 bulk-load radix** (1.7.43) — 3.92× faster combined at 1B (Reference; Cognitive shape similar but unmeasured)
- **Property-path hardening** (1.7.46/1.7.47) — 12 cancellation token sites, 12 grammar gap fixes, 1 Case 2 binding bug, all in shared SPARQL execution path used by both profiles

Cognitive is the workload that motivates Sky Omega 2.0 (Lucy long-term memory, James orchestration, Sky language layer). Without recent Cognitive measurements, we cannot answer:

1. Did the recent substrate work regress or improve Cognitive performance?
2. What is the current Cognitive write throughput? The 1.7.22 baseline was ~50K triples/sec; that number is now stale.
3. How does Cognitive perform on the bitemporal-dense access patterns the cognitive layers will produce (AS OF, DURING, ALL VERSIONS)?
4. Where are the binding bottlenecks in Cognitive — same as Reference (write amplification, atom-store hash drift), or different (WAL contention, per-session-graph cardinality)?

The drought is a Triggered limit (not Latent) because it actively constrains decisions: any architectural choice between "ship Cognitive feature X" vs "ship Reference feature Y" is currently weighted by the implicit assumption that Cognitive is OK. We don't have the data to defend that assumption.

## Trigger condition

Already triggered. Effects:

- ADR-008 attribution discipline reveals the gap when applied to existing validation runs — every row on the STATISTICS scale-validation table from 2026-04-19 onward needs the Reference profile tag, with no Cognitive counterpart.
- External comparison conversations (the QLever discussion) cannot be scoped to Cognitive workloads with current evidence.
- Sky Omega 2.0 cognitive-component readiness reviews (when they begin) will bottleneck on this.

## Current state

The Cognitive profile is still load-bearing in the codebase — `TemporalQuadIndex`, the bitemporal `LogRecord`, valid-time + transaction-time storage in B+Tree leaves, the AS OF / DURING / ALL VERSIONS query parser branches, the predicate-statistics maintenance during Cognitive Dispose, all live and exercised by the test suite (1,586 SPARQL tests pass on Cognitive). The substrate is correct.

What is missing is performance measurement at scale against the current 1.7.47 substrate. A small set of gradient runs (1M / 10M / 100M Cognitive bulk + rebuild + query) would close the gap without committing to a full 1B Cognitive run (which is the rough analogue of Phase 6 for Cognitive).

## Candidate mitigations

In rough order of cost / payoff:

1. **Small gradient: 1M / 10M / 100M Cognitive against 1.7.47.** Mirrors the historical pattern (`docs/validations/bulk-load-gradient-2026-04-17.md`, `full-pipeline-gradient-2026-04-19.md`). ~12-24 hours wall-clock for the three scales combined. Validates current Cognitive performance, identifies regressions if any, produces an artifact for ADR-008 attribution.
2. **WDBench against Cognitive.** Run the same WDBench paths+c2rpqs queries against a Cognitive-profile store of the same Wikidata data. Direct comparison of executor cost on the two profiles. Substantial wall-clock (Cognitive bulk-load of full Wikidata is the analogue of Phase 6's 85h, almost certainly longer because Cognitive carries the bitemporal columns).
3. **Cognitive-shaped synthetic benchmark.** A workload generator producing James-style query patterns (small named graphs, AS OF queries with varying time anchors, mutation churn at session boundaries). More representative of the actual Cognitive workload than ad-hoc Wikidata SPARQL. Takes design effort upfront.
4. **Defer until Sky Omega 2.0 cognitive-component work begins.** The cognitive components (Lucy, James) will exercise Cognitive workloads naturally. Wait for that work to drive the measurement. The cost: every architectural decision between now and then is made on stale Cognitive data.

Path (1) is recommended as the immediate mitigation. Path (3) is the right long-term direction but requires substrate work in `benchmarks/Mercury.Benchmarks/`. Path (4) is the wrong choice — too much Reference-targeted substrate work has shipped to defer Cognitive measurement further.

## Why this matters beyond the metric gap

Two architectural risks are currently latent:

1. **Cognitive may have regressed unnoticed.** Property-path hardening, walker rewrite, Case 2 binding fix — all touch the shared SPARQL execution path. The Reference WDBench rerun confirms correctness on Reference. Cognitive correctness is asserted by the 1,586 SPARQL test suite, but performance-shape-changes are not caught by unit tests.
2. **The "Sky Omega" claim drifts.** Without Cognitive numbers, public-facing claims about the project cite Reference numbers exclusively. ADR-008 mandates profile-tagging; this entry surfaces the asymmetry that mandate exposes.

## References

- ADR-008 — Workload Profiles and Validation Attribution (companion ADR; this entry is the first triggered limit it surfaces)
- `docs/validations/full-pipeline-gradient-2026-04-19.md` — most recent Cognitive measurement (1.7.22)
- `docs/validations/adr-031-dispose-gate-2026-04-21.md` — most recent Cognitive-relevant validation (Dispose-only, narrow scope)
- ADR-031 (mercury) — Dispose gate; the only post-2026-04-19 ADR that touches Cognitive directly
- `memos/2026-04-30-latent-assumptions-from-qlever-comparison.md` — the meta-pattern memo that prompted the sweep that surfaced this
