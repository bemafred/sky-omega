# Memo: Three latent assumptions surfaced via QLever comparison + limits-discipline self-catch

**Date:** 2026-04-30
**Audience:** Claude Code
**Source:** Higher-level thinking session investigating QLever’s reported Wikidata ingest times

## Context

A conversation comparing QLever’s published Wikidata ingest numbers to Mercury’s 85h → 15-25h trajectory surfaced three claims about Mercury / Sky Omega that aren’t currently captured in `docs/limits/` or in recent ADRs. None of these are bugs. All of them are claims that are currently true *with caveats that aren’t written down*. A careful reader would reasonably infer the wrong thing.

This memo documents the three items and notes a meta-pattern about the limits discipline that’s worth addressing.

## The meta-pattern

The current trigger for writing a `docs/limits/` entry is implicit: “Martin notices something weird while implementing.” That works for issues that surface from inside the codebase. It does not work for issues that surface only from outside — i.e. gaps between what the system *does* and what its descriptions *imply* it does. Those gaps are visible to a reader, a benchmarker, or an outside comparison conversation, but invisible from inside the build.

All three items below are this kind of gap. They came up in conversation, not in code.

**Recommendation:** Add a routine sweep — quarterly, or triggered by major ADRs — where someone (Martin, Claude Code, a fresh chat session) walks the public-facing claims (README, MERCURY.md, AI.md, articles, validation runs) and asks: *what would a careful reader infer that isn’t yet true?* Items found go to limits docs; items that change architectural framing go to ADRs. This is a small standing process, not a project.

## Item 1: Wikidata benchmarks are Reference profile, not Cognitive profile

**Latent claim:** “Sky Omega ingested 21.3B Wikidata triples in 85h on a single M5 Max laptop.” A reader would assume this represents Sky Omega’s primary use case.

**Reality:** Wikidata ingestion exercises the **Reference profile** of Mercury — large, mostly-static, ad-hoc-SPARQL-shaped. The **Cognitive profile** is a different workload entirely: smaller working set, write-heavy, bitemporal-dense, query patterns driven by James’s tail-recursive loop rather than ad-hoc SPARQL. The Wikidata numbers say nothing direct about Cognitive profile performance.

**Why it matters:** The QLever comparison is profile-to-profile (Reference vs QLever-overall), not Sky-Omega-overall vs QLever. Without the distinction written down, validation numbers in `docs/validations/` will be misread as benchmarks of Sky Omega’s primary purpose. That’s a credibility risk the moment someone serious actually reads them.

**Suggested action:** New ADR (next available number after ADR-036) — *“Mercury workload profiles: Reference vs Cognitive.”* Define each profile, characterize its workload shape, state which existing validations measure which profile. Cross-reference from MERCURY.md and from any validation run that exercises one profile but not the other. The Phase 6/7 Wikidata numbers should be tagged Reference profile explicitly.

## Item 2: bz2 decompression is single-threaded

**Latent claim:** Discussions of Mercury’s Wikidata ingest pipeline imply parallel decompression. (In the source conversation, parallel bz2 was assumed without checking, and was load-bearing in an architectural trade-off discussion.)

**Reality:** Mercury’s bz2 pipeline currently runs single-threaded decompression. Single-threaded `bzcat` sustains roughly 50–70 MB/s; parallel decompression (e.g. `lbzcat -n N`) scales to 300+ MB/s on enough cores. bzip2 is block-structured (~900KB blocks) and each block decompresses independently, so the parallelism is straightforward when we choose to add it. On the full Wikidata bz2 (~110 GB compressed), the gap is roughly an hour of pipeline-feeding wall time.

**Why it matters:** Two reasons.

1. The recent architectural discussion of Shape A (two-pass over compressed input) vs Shape B (one-pass with staged intermediates) assumed parallel decompression is available. The trade-off math shifts depending on bz2 throughput. Today the assumption doesn’t hold.
1. The 15–25h trajectory implicitly budgets for SSD-saturated sequential reads. Single-threaded bz2 doesn’t deliver that; it caps the producer side of the pipeline well below NVMe throughput.

**Suggested action:** New `docs/limits/` entry — *“bz2 decompression is single-threaded.”* Document current measured throughput, characterize the gap to parallel, note the known fix path (block-level parallelism), and explicitly mark *“not a decision yet”* — the cost/benefit measurement against the rest of the pipeline hasn’t been run. This is the limits-doc shape: known optimization, characterized cost, deferred for measured reasons.

## Item 3: AtomStore does not eagerly prefix-compress

**Latent claim:** Vocabulary handling in Mercury is “comparable to QLever” or “BCL-only equivalent of QLever’s approach.” A reader benchmarking memory usage would assume prefix compression is in place.

**Reality:** AtomStore stores atoms without eager prefix compression. QLever achieves roughly **45% vocabulary memory reduction** on Wikidata via greedy common-prefix detection (URIs share long prefixes like `http://www.wikidata.org/entity/`, etc.). On ~3.4B Wikidata terms at ~50 bytes average, that’s roughly **75 GB of memory currently spent** that the optimization would recover.

**Why it matters:** On a 128 GB machine this is the difference between vocabulary fitting comfortably in RAM and needing partial-merge tricks to stay under the limit. It also means vocabulary-phase comparisons against QLever are not apples-to-apples — QLever does more work per term but produces smaller intermediate state, which likely nets in their favor on the merge phase. Without this written down, validation numbers comparing Mercury’s vocabulary phase to QLever’s will be misinterpreted by anyone reading them.

**Suggested action:** New `docs/limits/` entry — *“AtomStore does not eagerly prefix-compress.”* Quantify the current memory cost on Wikidata, cite QLever’s published 45% reduction as a reference point, characterize the likely implementation approach (greedy common-prefix detection at vocabulary build time). Mark *“not a decision yet”* — the trade-off involves AtomStore complexity and potential lookup-time costs that need measurement before commitment.

## Closing

None of these are urgent in the implementation sense. All three are urgent in the documentation sense — every day they remain unwritten, the gap between what Sky Omega does and what its descriptions imply it does widens. The fix is cheap: one ADR + two limits-doc entries, probably half a day of writing.

The meta-pattern (routine sweeps of public claims for latent assumptions) is the more durable thing. If it becomes a standing practice, items like these get caught when they become latent rather than waiting for an outside conversation to surface them.

## Suggested order of work

1. ADR for Reference vs Cognitive profile — highest leverage, reframes how all current and future validations should be read.
1. `docs/limits/` entry for single-threaded bz2 — small, factual, unblocks honest discussion of pipeline shape.
1. `docs/limits/` entry for AtomStore prefix compression — small, factual, reframes vocabulary-phase comparisons.
1. Add a section to AI.md or CLAUDE.md describing the routine-sweep practice for latent assumptions, so it becomes part of the standing operating discipline rather than a one-off.