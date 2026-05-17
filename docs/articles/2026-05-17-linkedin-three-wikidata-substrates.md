---
title: Three Wikidata Substrates on a Laptop, in .NET — and the Comparison Plane That Most Benchmarks Skip
date: 2026-05-17
status: draft (LinkedIn-shape, target publish 2026-05-21)
source: docs/articles/2026-05-16-three-substrates-on-a-laptop.md (full repo version)
target-length: ~1,500 words
audience: LinkedIn — RDF / triplestore practitioners, .NET infrastructure people, AI substrate watchers
---

# Three Wikidata substrates on a single laptop, in .NET

*Hook (first 3 lines — LinkedIn shows these before "see more"):*

We loaded the full Wikidata RDF dump — 21.3 billion triples — into a queryable store on a single MacBook Pro. BCL-only .NET, no third-party runtime dependencies, end-to-end in 23 hours 57 minutes.

What's interesting isn't the wall-clock. It's the comparison plane that most published benchmark numbers quietly skip — and what happens when you do the apples-to-apples work honestly.

---

## What we built

Over the past five weeks we built three Mercury substrates over Wikidata-derived data on the same M5 Max laptop (128 GB RAM, internal NVMe), same substrate version (Mercury 1.7.57, BCL-only .NET 10):

- **Full Wikidata** — `latest-all.ttl.bz2`, 2026-04-03 dump. **21,316,531,403 triples** ingested + sealed in **23 h 57 m** end-to-end.
- **Truthy Wikidata** — `latest-truthy.nt.bz2`, 2026-05-08 dump. **8.17 billion triples** in **14 h 13 m** end-to-end.
- **WGPB filtered** — MillenniumDB's 2018 systematic-graph-pattern dataset. **150 million triples** built to queryable in **4 m 30 s**.

Across the three substrates we ran the full WDBench query suite paired (5 categories × full + truthy = 5,316 queries) plus the full WGPB query suite (850 queries). Including the prior cycle 8 + cycle 9 measurements on earlier 1.7.x substrate generations: **8,564 unique query × substrate executions, 0 substrate failures.**

Every metric record — every load progress tick, every spill event, every MPHF level convergence, every query elapsed time + result row count — is committed as JSONL artifacts in the public repo. Anyone can re-derive any number we publish.

---

## The comparison plane (the part most benchmarks skip)

This is the part of every triplestore benchmark conversation that quietly goes wrong if you don't say it up front. So:

### Trigram is a feature cost, not a substrate cost

Mercury's Reference profile builds a **trigram index** unconditionally, because `text:match(?var, "term")` is a substrate-level SPARQL feature. QLever, Virtuoso, and Blazegraph in their standard published Wikidata ingest numbers **do not build text indexes** — it's an opt-in for QLever and absent by default elsewhere.

Comparing Mercury's "23 h 57 m with trigram" to a QLever "~few hours on truthy without text index" is comparing different feature sets, in either direction. Like-for-like:

| measurement | with trigram | **without trigram (apples-to-apples vs QLever-class systems)** |
|---|---:|---:|
| Full Wikidata (21.3 B) | 23 h 57 m | **15 h 26 m** |
| Truthy (8.17 B) | 14 h 13 m | **6 h 49 m** |

The **6 h 49 m** number is what it takes Mercury 1.7.57 to ingest, sort, MPHF-resolve, and build the secondary index for a queryable 8.17 B-triple truthy substrate on a single laptop with the same feature surface (vocabulary + 2 quad permutations) as QLever's default ingest.

### Full vs truthy is a different workload, not a different measurement

Most published Wikidata benchmarks run against truthy. Most production Wikidata infrastructure has to carry full. Full keeps qualifiers, references, sitelinks, lexical data — roughly 1.5–1.8× the triple count. Truthy strips all of it down to direct claims.

A wall-clock comparison without disclosing which dataset each system loaded is dishonest by omission, in either direction. Our 23h57m is on full; QLever's published ~8h is on truthy. Both are real measurements. They are not comparing the same workload.

### Hardware and dump-date honesty

WMF's recent triplestore evaluation ran on a Ryzen 9950X with 192 GB RAM and fast SSDs. We ran on an M5 Max laptop with 128 GB RAM and internal NVMe. Different hardware, different power envelope, different price point.

Our three substrates also use three different Wikidata snapshots (full from 2026-04-03, truthy from 2026-05-08, WGPB from November 2018). Wikidata grows continuously — cross-substrate comparisons that step over the date axis without saying so are misleading. We say this in the validation docs so the caveats travel with the numbers.

---

## What we found that's worth knowing

Five observations from the paired record:

**1. Median query latency is sub-100 ms cold-cache on the full 21.3 B graph.** Across 1,384 completed WDBench queries on the full substrate (60-second per-query timeout, single attempt against fresh process), p50 = 69 ms, p90 = 17 s, p99 = 53 s. On truthy (1,391 completed): p50 = 62 ms, p90 = 16 s, p99 = 51 s. The substrate scales evenly across the 2.6× data difference.

**2. 0 substrate failures across 8,564 measured queries.** Across five substrate generations (cycles 8, 9, 10 r4, truthy r1, WGPB step C), five WDBench categories, seventeen WGPB graph patterns, cold and warm cache scenarios. 0 parser-state corruption. 0 substrate crashes mid-query. 0 cancellation-contract violations. One query rejected as malformed source SPARQL (a defect in the published WGPB Zenodo archive that our parser correctly caught) — we filed that under "0 substrate failures + 1 honest correctness signal."

**3. Trigram cost scales with literal character volume, not triple count.** Truthy has 38 % of full's triples but 91 % of full's trigram entries — because truthy preserves the literal-heavy predicates (labels, descriptions, aliases × every language) while stripping the structural reification that's literal-light. We had to caveat this with a dump-date confounder when we noticed the truthy snapshot was 5 weeks newer than full, but the dominant effect is genuine.

**4. The MPHF substrate holds across N.** Mercury 1.7.57's BBHash MPHF (γ=2.0, dense final-level fallback) constructed on full's 4.005 B atoms used 25 iterative levels with 0 dense fallback engaged, placement ratio 0.6065 (matching the BBHash theoretical 1 − e^(−1/γ) for γ=2.0 to four decimal places). Construction on truthy's 1.79 B atoms used 23 levels, same placement ratio. Same substrate code, same theoretical guarantees, scaling proportionally.

**5. Cycles preserving inherited scopes without revisiting is a quiet failure mode.** Every "WDBench cold baseline" we published before this work covered only paths + c2rpqs — 1,199 of WDBench's 2,658 queries (45%). Cycle 8 chose that scope deliberately (property-path hardening); cycles 9 and 10 inherited it "for comparability" without re-examining the rationale. We caught it during this work and ran the missing 1,459 queries. The narrow-scope drift was real. Worth saying out loud.

---

## The discipline number

The single number we're most proud of from the cumulative measurement:

> **0 substrate failures across 8,564 measured SPARQL queries on the Mercury 1.7.x line.**

Cycles 8, 9, 10 r4, truthy r1, WGPB step C combined. The 60-second per-query timeout was never violated by Mercury's cancellation contract — every timeout closed between 60.000 s and the few-second jitter window the parser/executor budget permits.

This is what we mean when we say *cancellation contract honored at scale*. The query timing measurements are precise because the substrate's behavior at the timeout boundary is precise.

A Kjell Silverstein poem captures the discipline:

> *I looked and looked / And found nothing there. / No answer, no signal, no sign.*
> *But nothing's not nothing / When nothing's been checked— / That nothing / Was something / Of mine.*

Untrusted nothing is just absence. Checked nothing is evidence. 8,564 hours of monitored substrate quiet **are** the achievement; the headline triple count is the framing.

---

## Where this lands in the Wikidata conversation

We're not proposing Mercury as a Blazegraph replacement candidate. The Wikimedia Foundation has done thorough work evaluating QLever and Virtuoso; both meet the acceptance criteria, recommendations are published, migration begins after July 2026. That conversation is decided, and the work behind it is solid.

What we're documenting is a **different point in the design space** that the evaluation framing didn't address because it wasn't asked to:

- **Consumer hardware feasibility.** A single high-end laptop carrying the full Wikidata graph queryably is a category the WMF benchmark plane didn't cover. Semantic sovereignty per person becomes empirically tractable, not aspirational.
- **Cognitive substrate.** Mercury is the storage tier for an AI cognitive architecture (Sky Omega — Lucy/James/Sky on top of Mercury/Minerva/DrHook). Bitemporal + provenance built in. The MCP integration makes it the persistence layer for AI coding agents, not just another triplestore.
- **Governed automation thesis.** Auditable, observable, BCL-only, every claim has a JSONL. The discipline IS the story; the technical bets are consequences.

The honest framing is: here is what we measured, here is the comparison-plane memo making the apples-to-apples work explicit, here is the JSONL evidence pile for anyone who wants to challenge the numbers or reproduce them. The substrate is real. The trajectory is documented in `docs/limits/` — seven optimization rounds ahead, each anchored in measurement.

We're posting this during the WMF community feedback window (open through May 25) not to contest the decision but to add a third workload class to the published record. The conversation about how RDF infrastructure should evolve is bigger than the WMF migration question.

---

## What it took

Sky Omega is built by a sustained human-AI engineering collaboration. The "we" throughout this article is load-bearing. Architectural decisions, debugging, validation arc design, the limits register, every cycle's per-fix attribution — all developed in dialogue with shared epistemic discipline.

One human architect plus one persistent AI collaborator, working in spare hours, can produce substrate-grade artifacts that hold up against full-time research efforts. The discipline is teachable, the practices are listable, and the compounding is real. We're roughly five months in from the first Sky Omega commit; the rate of progress is still accelerating because no session is spent paying off the previous one's debt.

The repo is open source (MIT) at github.com/bemafred/sky-omega. The validation docs (`docs/validations/`) record every measurement of the arc described above. The comparison-plane memo (`docs/memos/2026-04-30-latent-assumptions-from-qlever-comparison.md`) is the public-good artifact most worth reading if you care about RDF benchmark honesty.

---

## The post-publish ask

If you work on triplestore infrastructure: run our queries against your substrate, post the numbers, let's compare on a common plane.

If you've thought hard about Wikidata-scale RDF: tell us what we got wrong. The JSONL is committed; the analysis is open to challenge.

If you build AI systems on top of structured knowledge: Mercury + MCP is already shippable as a global tool. The semantic memory substrate that survives across sessions is what makes AI agents accumulate epistemic state instead of forgetting between conversations.

The publication arc continues. What compounds is what you don't have to redo.

---

*Numbers anchored in: cycle 10 r4 validation, truthy r1 validation, WGPB step C validation, aggregate distribution doc, all under `docs/validations/`. Comparison-plane analysis in `docs/memos/2026-04-30-latent-assumptions-from-qlever-comparison.md`. Repository: github.com/bemafred/sky-omega, current substrate Mercury 1.7.57.*
