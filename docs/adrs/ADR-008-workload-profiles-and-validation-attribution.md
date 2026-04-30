# ADR-008: Workload Profiles and Validation Attribution

**Status:** Proposed — 2026-04-30

**Context:** Sky Omega has two distinct workload profiles — Reference and Cognitive — that exercise the substrate fundamentally differently. ADR-029 (mercury) defines them as *storage* options. This ADR layers the *workload* framing on top: characterizes each profile as a workload, makes profile-tagging mandatory for every validation, and prevents the credibility-risking gap where validation runs measuring one profile are read as benchmarks of "Sky Omega" overall.

## Problem

The 2026-04-30 QLever comparison conversation surfaced a latent gap (memo: `memos/2026-04-30-latent-assumptions-from-qlever-comparison.md`). The Phase 6 article opens with:

> *Sky Omega's Mercury substrate ingested the full Wikidata `latest-all.nt` dump — 21,316,531,403 triples — into a queryable RDF store on a single MacBook Pro.*

A careful reader assumes this represents Sky Omega's primary use case. It does not. Wikidata ingestion exercises the **Reference profile** — large, mostly-static, ad-hoc-SPARQL-shaped, write-once-then-sealed. The **Cognitive profile** is a different workload: smaller working set, write-heavy, bitemporal-dense, query patterns driven by James's tail-recursive orchestration loop rather than ad-hoc SPARQL. The Wikidata numbers say nothing direct about Cognitive performance.

The same gap recurs across every validation in `docs/validations/`. None of the run files prefix their titles with the profile under test. A reader can infer it from the file content (mentions of `latest-all.nt`, `wiki-21b-ref`, etc.) but the inference is implicit.

This is a credibility risk the moment someone serious actually reads the validation runs. QLever's published Wikidata numbers are workload-comparable to our Reference numbers (both "ingest a large mostly-static graph"). They are NOT comparable to anything we'd measure on Cognitive. Without the distinction written down, comparisons drift into apples-to-oranges by default.

ADR-029 covers profile *implementation* (what the storage layer does differently). It does not cover profile *workload characterization* (what each profile is FOR) or *validation attribution* (which existing measurements pertain to which profile). Those gaps are visible to outside readers and benchmarkers but invisible from inside the build.

## Decision

This ADR defines the two workload profiles, characterizes each at the workload level, and makes profile-tagging mandatory for every validation artifact in `docs/validations/`. It is a top-level cross-cutting decision because the gap exists at the project-presentation boundary, not the storage-implementation boundary.

### Reference profile (workload definition)

| Aspect | Reference workload |
|---|---|
| **Data shape** | Large canonical knowledge graph (10⁹ – 10¹⁰ triples). Sourced from upstream artifact (Wikidata `latest-all.ttl.bz2`, DBpedia, etc.). |
| **Write pattern** | Single bulk-load at substrate-creation time. Sealed thereafter (ADR-029 Decision 7, ADR-007). |
| **Query pattern** | Ad-hoc SPARQL — both endpoints sometimes free, large fan-out predicates, transitive closures, federated queries against external endpoints. |
| **Performance dimensions** | Bulk-ingest throughput. Cold-cache query latency. Index size on disk. Memory pressure during ingest. |
| **Temporal semantics** | None. Reference drops valid-time and transaction-time — they are meaningless for sealed canonical snapshots. |
| **Comparable substrates** | QLever, Virtuoso, Blazegraph WDQS, Apache Jena Fuseki, GraphDB. Each in their respective Wikidata-or-equivalent ingest mode. |

### Cognitive profile (workload definition)

| Aspect | Cognitive workload |
|---|---|
| **Data shape** | Smaller, growing knowledge accumulating across sessions. Per-agent, per-team, per-organization. Typically 10⁴ – 10⁸ triples. |
| **Write pattern** | Write-heavy, bitemporal-dense. Every triple has implicit valid-time + transaction-time bounds. Per-session-graph isolation. |
| **Query pattern** | Driven by the cognitive orchestration loop (James, future). AS OF queries against historical states. DURING / ALL VERSIONS for evolution tracking. Smaller fan-out, more selective predicates, more named-graph filtering. |
| **Performance dimensions** | Per-query latency (tens of ms target). Dispose / Open latency for read-only sessions. Mutation throughput at session boundaries. |
| **Temporal semantics** | Full bitemporal (valid-time + transaction-time on every triple). Versioning, soft-delete, audit trails — all queryable through SPARQL with temporal extensions (AS OF, DURING, ALL VERSIONS). |
| **Comparable substrates** | None directly. Most temporal stores are valid-time-only or transaction-time-only. Bitemporal-RDF is uncommon at this engine layer. |

### Mandatory validation attribution

Every artifact in `docs/validations/` MUST be classifiable by profile within the first paragraph or filename. Three acceptable forms:

1. **Filename prefix:** `wdbench-paths-21b-2026-04-29-1747.jsonl` — the `21b` token implies Reference (no Cognitive run reaches that scale). Acceptable when the scale tag unambiguously identifies the profile.
2. **Filename suffix:** `<topic>-<profile>-<date>.md` for new files going forward.
3. **First-paragraph attribution:** the run's prose document opens with "Reference profile" or "Cognitive profile" explicitly.

The STATISTICS scale-validation runs table MUST include a profile column, or each row's prose must name the profile. (See implementation plan below.)

### Documentation cross-references

When a public-facing claim depends on profile semantics, that dependency must be made explicit:

- **README banner numbers** that reference Wikidata or 21.3 B → tagged "Reference profile."
- **Bitemporal claims** ("temporal extensions for time-travel queries") → tagged "Cognitive profile."
- **Comparison tables** (vs QLever / Virtuoso / Blazegraph) → tag both sides' profile or workload.
- **Articles** (existing and future) → opening paragraph must name the profile.

### Cross-comparison rule

When publishing a number that is being compared to an external benchmark (QLever Wikidata, Virtuoso WDBench, etc.), the comparison must be **profile-to-comparable-workload**, not profile-to-substrate-overall. Examples:

- ✅ "Mercury Reference profile, 21.3 B Wikidata, 85h end-to-end" vs "QLever, 17.7 B Wikidata, 14.3h end-to-end"
- ❌ "Sky Omega ingested 21.3 B in 85h" vs "QLever in 14.3h" — drops the Reference framing on our side, drops the qualifications on theirs
- ✅ "Mercury Cognitive profile bitemporal write throughput" — incomparable to standard RDF benchmarks; note that explicitly
- ❌ Treating Cognitive numbers as comparable to QLever — fundamentally different workloads

## Implementation

1. **STATISTICS.md scale-validation runs table.** Add a `Profile` column (or extend the prose convention) so every existing row carries its profile attribution. All current entries from 2026-04-19 onward are Reference profile (Cognitive validation is overdue — see `docs/limits/cognitive-profile-validation-drought.md`).
2. **README banner.** Add "(Reference profile)" qualifier where Phase 6/7 numbers appear. Update bitemporal claim to scope it to Cognitive.
3. **Existing articles.** Add a one-sentence profile attribution to each article's opening paragraph in a follow-up pass: 2026-04-26-21b-wikidata-on-a-laptop.md (Reference), 2026-04-28-what-compounds.md (mixed — Reference engineering + general discipline; tag as such).
4. **MERCURY.md.** Add a "Workload profile" section pointing to this ADR for the canonical definitions, with a one-line summary of each profile.
5. **Future validation runs.** ADR-008 attribution discipline becomes a checklist item for any new `docs/validations/` entry.

## Consequences

**Positive:**
- External readers can correctly map our numbers to comparable benchmarks. Apples-to-apples comparisons become possible.
- Cognitive validation drought (currently invisible) becomes visible — drives `docs/limits/cognitive-profile-validation-drought.md` toward Engineering.
- Marketing-style overclaiming ("Sky Omega does X") becomes harder to drift into. Each numeric claim attaches to a workload, not the substrate-overall.
- Future validation work auto-formats the right way.

**Negative:**
- Slight verbosity in headlines and tables. "21.3 B Wikidata" becomes "Reference profile, 21.3 B Wikidata" or equivalent. Acceptable cost.
- Cognitive workload doesn't have a current public-facing performance claim — the absence becomes visible. This is the right outcome (forces measurement) but it's a temporary credibility ding until Cognitive validation lands.

**Neutral:**
- ADR-029's storage-level decision unchanged. This ADR layers on top.

## Status transitions

- **Proposed** — 2026-04-30. Pending review.
- **Accepted** — when the implementation plan above lands.
- **Completed** — when (1) STATISTICS scale-validation table has profile attribution, (2) README banner is profile-tagged, (3) MERCURY.md Workload profile section ships, (4) the discipline is in AI.md as a standing practice.

## References

- `memos/2026-04-30-latent-assumptions-from-qlever-comparison.md` — the surfacing memo
- ADR-029 (mercury) — Store Profiles (storage-level decision)
- ADR-007 — Sealed Substrate Immutability (Reference profile seal generalization)
- `docs/limits/cognitive-profile-validation-drought.md` — companion limits entry surfacing the validation gap
- `MERCURY.md`, `README.md`, `STATISTICS.md` — implementation sites
