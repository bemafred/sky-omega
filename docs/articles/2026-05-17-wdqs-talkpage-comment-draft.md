---
title: WDQS backend update — talk-page comment draft
date: 2026-05-17
status: draft (post by 2026-05-25 feedback deadline)
target-venue: https://www.wikidata.org/wiki/Wikidata_talk:SPARQL_query_service/WDQS_backend_update (or Final Report talk page)
target-length: 300-400 words
posture: contribution to the public record, not a candidacy request
---

# Draft talk-page comment — WDQS backend evaluation

## Posting instructions

- Venue: Wikidata talk page for the WDQS backend update, OR Final Report talk page (whichever is more active in May 2026 — check before posting)
- Sign with `~~~~` (Wikimedia signature) using Martin Fredriksson's Wikidata account
- Do NOT cross-post to wikidata-tech mailing list unless someone replies on the talk page and the conversation merits broader visibility
- Do NOT file a Phabricator subtask
- Post once, mid-window (target 2026-05-19 to 2026-05-21), then engage in replies only

## The comment

---

Thank you to the Search Team and the Wikidata Platform Engineering team for the depth of this evaluation. The acceptance criteria, the benchmark methodology, and the published recommendations document considerable work, and the comparison between QLever, Virtuoso, and the Blazegraph baseline is the kind of rigorous evaluation the RDF community benefits from broadly.

This is not a candidacy comment — we are not proposing our own work as a Blazegraph replacement. The two finalists meet the acceptance criteria; the migration path is sound. We are commenting because the evaluation framing (production-realistic deployment on server-class hardware, full SPARQL 1.1, continuous-update workload) defines one important point in the RDF substrate design space, and our recent work documents a different point that may be of interest to community members thinking about adjacent questions.

For context: we have published measurements of the full Wikidata RDF dump (`latest-all.ttl.bz2`, 21.3 billion triples) loaded onto a single consumer laptop (Apple M5 Max, 128 GB RAM, internal NVMe) using a BCL-only .NET substrate called Mercury, with 100% W3C SPARQL 1.1 conformance and queryable end-to-end in 23 hours 57 minutes. We also published a paired truthy measurement (8.17 billion triples, 14 hours 13 minutes) so that comparisons against published truthy-based benchmarks (QLever, WDBench) are explicit about which dataset each system loaded.

The validation arc, the per-cycle JSONL metric artifacts, and an explicit comparison-plane memo documenting which numbers in the broader RDF literature compare on the same workload versus different workloads are all in the public repository at github.com/bemafred/sky-omega under `docs/validations/`, `docs/articles/`, and `docs/memos/2026-04-30-latent-assumptions-from-qlever-comparison.md`.

Two specific contributions we hope are useful to the WMF process and the broader community regardless of the migration outcome:

1. **The comparison-plane analysis.** Most published Wikidata benchmark numbers run against truthy; production infrastructure must serve full. Trigram / text indexes are sometimes included, sometimes not, often without disclosure. Hardware classes vary across an order of magnitude. The memo above documents how these axes interact and which apples-to-apples comparisons hold. Adopting any portion of this framing in future evaluation reports would strengthen the public RDF benchmarking record.

2. **The consumer-hardware datapoint.** A single laptop carrying the full Wikidata graph queryably is a category the WMF evaluation framing did not address (correctly, given the production-deployment focus). For community members interested in semantic-sovereignty deployments, personal or organizational Wikidata mirrors, or experimental query workloads on commodity hardware, the measurements above establish that this is empirically tractable rather than aspirational.

We would welcome feedback on the validation methodology, additional benchmarks we should run for fuller coverage, or pointers to public datasets where a comparable measurement would be useful to the community.

Thank you again for the rigor of this evaluation. ~~~~

---

## Pre-publish checklist

- [ ] Confirm the 8,564-query figure (or whichever specific numbers we cite) match the latest validation docs
- [ ] Confirm the Mercury 1.7.57 cycle 10 r4 numbers are current (cycle 11 may have shipped by post date)
- [ ] Confirm github.com/bemafred/sky-omega is the correct public repo URL
- [ ] Confirm whether to link the LinkedIn article (if published before the talk-page post) or hold (if the article goes after)
- [ ] Read the page's existing comments before posting — if the conversation has moved on, adjust the tone to fit the room
- [ ] Sign in to Wikidata with the right account (Martin's, not a new one — established account = more credibility)

## What to do if it gets engagement

- **Technical questions about Mercury:** answer briefly with link to specific JSONL artifact. Don't pitch.
- **Methodology challenges:** thank them, address the specific point, link the validation doc. If they're right, say so.
- **"Why didn't you submit during the evaluation window?":** honest answer — Mercury wasn't at 21.3 B-validated state during the evaluation window (Sept 2025 – early 2026); cycle 8 → cycle 10 trajectory happened April-May 2026, after the evaluation closed.
- **"Will you keep developing this for Wikidata-class workloads?":** yes; trajectory documented in the limits register (`docs/limits/`).
- **Hostile or dismissive:** disengage politely. The public-good contribution stands regardless of any one reader's reception.

## What NOT to do

- Don't reply to every comment
- Don't get drawn into Blazegraph-vs-QLever-vs-Virtuoso side debates
- Don't push the LinkedIn piece in talk-page replies — separate venues, separate purposes
- Don't promise specific future work or roadmap commitments in public
- Don't acknowledge any specific WMF staff member by name unless they've already commented and you're replying directly
