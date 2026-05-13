# Latent assumptions in the QLever / Virtuoso / Blazegraph WDBench comparison plane

**Date:** 2026-04-30 (drafted), updated 2026-05-13 with cycle 10 r4 + truthy-not-downloaded clarification.

## The disclosure that travels with every Mercury Wikidata measurement

Sky Omega's Wikidata measurements are against `latest-all.ttl.bz2` — the **full** Wikidata canonical distribution. Most published numbers from QLever (Hannah Bast et al., Univ. Freiburg) and the WDBench paper's headline figures run against `latest-truthy.nt.bz2` — the **truthy** subset. They are NOT the same workload.

A direct wall-clock comparison between Mercury "23 h 57 m on cycle 10 r4" and a published QLever "8 h on Wikidata" without disclosing which dataset each loaded is dishonest by omission, in either direction.

## What full has that truthy strips

The Wikidata data model layers two stories on top of every claim:

1. **The direct claim** — `entity wdt:P31 value` ("entity is an instance of value"). Truthy keeps these and only these.
2. **The statement-node indirection** — `entity p:P31 _:statement; _:statement ps:P31 value; _:statement pq:P580 "2020-01-01"; _:statement prov:wasDerivedFrom _:reference; _:reference pr:P854 <url>`. The statement node carries qualifiers (when / where / context) and references (citations / provenance). Full keeps the whole graph. Truthy collapses the entire statement-and-its-baggage to one `wdt:` triple, throwing away every qualifier and reference.

Plus:
- **Sitelinks** — `<entity> schema:about <wiki-url>` × every language Wikipedia / Wikiquote / Wiktionary / Wikisource where the entity has a page. Full keeps these; truthy drops them entirely.
- **Lexical data** (lemmas, forms, senses for the linguistic side of Wikidata). Full keeps; truthy partially drops.

Approximate ratio at our 2026-04-03 dump:
- Full (`latest-all.ttl.bz2`): **21,316,531,403 triples** (Mercury cycle 10 r4)
- Truthy (`latest-truthy.nt.bz2`): ~12–15 B (we have NOT downloaded the current dump; historical ratio of full:truthy is roughly 1.5–1.8 : 1)

## What this means for cross-system comparison

| If you compare … | … you are comparing |
|---|---|
| Mercury 23 h 57 m (cycle 10 r4 on full) vs QLever ~8 h on truthy | Two different workloads. The 1.5× dataset-size delta is the floor of the gap; the rest is genuine substrate difference. |
| Mercury 23 h 57 m vs Blazegraph WDQS ~unbounded | Both on full, but Blazegraph's documented capacity-wall around 12–13 B is the more informative comparison than wall-clock. |
| Mercury cycle 10 r4 vs hypothetical Mercury-on-truthy | Not run. Open follow-up. Would isolate substrate effect from dataset-size effect. |

The Phase 6 article (2026-04-26) framed it correctly: "QLever (Hannah Bast et al., Univ. Freiburg, mature C++ codebase) reports substantially faster full-Wikidata loads on comparable consumer hardware. … the gap between 85 h and their published numbers is a gap in *what work each system is doing per triple*, not in physics." That's still the honest framing. With cycle 10 r4's 23 h 57 m we've closed a substantial part of that gap, but the remaining delta is partly substrate (genuine optimization headroom) and partly workload (full has more triples per claim).

## Why Sky Omega chose full

Three reasons, in order:

1. **Full is what Wikidata's production infrastructure must serve.** Blazegraph WDQS, now its QLever successor, has to carry the qualifier/reference/sitelink baggage because applications depend on it (revision history, citation chains, multilingual UX). A substrate-capability claim aimed at *replacing or augmenting that infrastructure* must demonstrate full-data capacity.
2. **Full is the harder workload.** If the substrate handles 21.3 B triples in 24 h, it trivially handles 12–14 B in less. The reverse implication doesn't hold.
3. **Honest disclosure is cheaper than honest comparison.** Loading both and running both is days of extra compute; documenting the dataset distinction is a paragraph.

## Open follow-ups when truthy-comparison becomes warranted

If/when external interest (Wikidata community, WMF evaluation, an external benchmark publication) makes apples-to-apples vs published WDBench numbers worth the compute:

1. Download `latest-truthy.nt.bz2` (current dump).
2. Run `mercury --bulk-load latest-truthy.nt.bz2 --profile Reference …` with full 1.7.57 substrate.
3. Re-run the WDBench cold-baseline against the truthy substrate (WDBench's query set was authored against truthy).
4. Publish the truthy measurement alongside the full measurement; cite both side-by-side in any external comparison.

Estimated compute: ~14–16 h bulk-load + ~5–6 h rebuild-indexes (scaled from cycle 10 r4 by the dataset ratio) + the WDBench run (10–11 h per cycle 9's cold-baseline pattern). Total ~30 h on a single laptop for the apples-to-apples package.

## Cross-references

- [Cycle 10 Phase 3 r4 validation](../validations/cycle10-phase3-r4-21b-2026-05-12.md) — the most recent full-Wikidata measurement, ⚠ disclosure section
- [Phase 6 article](../articles/2026-04-26-21b-wikidata-on-a-laptop.md) — first public framing of "comparable but not identical to QLever's workload"
- [STATISTICS.md](../../STATISTICS.md) — every scale-validation row that uses "21.3 B" should be read as "full Wikidata, 21.3 B"
