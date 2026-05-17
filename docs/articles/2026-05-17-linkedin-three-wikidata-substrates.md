---
title: Five months in. One architect, one AI collaborator. A cognitive substrate that carries Wikidata on a laptop.
date: 2026-05-17
status: draft (LinkedIn-shape, target publish 2026-05-21)
source: docs/articles/2026-05-16-three-substrates-on-a-laptop.md (full repo version)
target-length: ~700 words
audience: LinkedIn — AI substrate watchers, .NET infrastructure people, RDF practitioners
---

# Five months in. One architect, one AI collaborator. A cognitive substrate that carries Wikidata on a laptop.

Five months ago we started building Sky Omega — a cognitive architecture for AI systems with semantic sovereignty. The bet was simple: AI agents forget between conversations because their substrate is wrong. Build the substrate right and persistent cognition becomes possible.

This week the substrate reached production readiness.

---

## What the substrate carries

Three Wikidata builds on the same MacBook Pro (M5 Max, 128 GB unified memory, 8 TB SSD), same .NET 10 substrate, BCL-only — no third-party runtime dependencies:

- **Full Wikidata** — **21,316,531,403 triples** ingested + sealed in **23 h 57 m** end-to-end.
- **Truthy** — **8.17 billion triples** in **14 h 13 m**.
- **WGPB filtered** — **150 million triples** built to queryable in **4 m 30 s**.

Across the three builds plus prior validation cycles: **8,564 SPARQL queries measured, 0 substrate failures.** Median query latency on the full 21.3 B graph is **69 ms cold-cache** (p50, single-shot, 60-second timeout). Every metric record is committed as JSONL in the public repo. Anyone can re-derive any number we publish.

---

## What this is the substrate *of*

Mercury is the storage tier — bitemporal RDF with provenance built in, BCL-only, mmap-backed. The MCP integration makes it the persistence layer for AI coding agents: semantic memory that survives between sessions.

Sky Omega is the architecture: three substrates (**Mercury** for semantic storage, **Minerva** for LLM inference, **DrHook** for runtime observation) and four cognitive layers above them (**Lucy** deep memory, **James** orchestration, **Mira** surfaces, **Sky** agent integration). All BCL-only. Hardware accessed directly via P/Invoke.

The thesis: AI systems need to accumulate epistemic state with provenance, not pattern-match on conversation transcripts. RDF makes that auditable. LLMs make the interface tractable. Semantic sovereignty per person becomes architecturally possible, not aspirational.

---

## What "we" means

One human architect (Martin Fredriksson) and one persistent AI collaborator. Spare hours, sustained discipline. Architectural decisions, debugging, validation arc design, the limits register, every cycle's per-fix attribution — all developed in dialogue under shared epistemic discipline. The substrate is itself the demonstration: a sustained human-AI engineering collaboration produces substrate-grade infrastructure that holds up against full-time research efforts. The discipline is teachable. The compounding is real.

---

## The discipline number

> *I looked and looked / And found nothing there. / No answer, no signal, no sign.*
> *But nothing's not nothing / When nothing's been checked— / That nothing / Was something / Of mine.*
>
> — Kjell Silverstein

8,564 measured SPARQL queries. 0 substrate failures. Cancellation contract honored at scale — every 60-second timeout closed between 60.000 s and the few-second executor budget. Untrusted nothing is just absence. Checked nothing is evidence.

---

## What's next

The heavy optimization is banked. What comes next isn't more performance — the substrate is at its end. **1.8.0 is the cognitive-layer entry point:** Lucy, James, Mira, Sky on top of the three substrates. What this week marks is the substrate being ready to carry that work.

The repo is open source (MIT) at **github.com/bemafred/sky-omega**. Mercury + MCP is a global tool today — install it and your AI agent has persistent semantic memory that outlives sessions. The comparison-plane memo (`docs/memos/2026-04-30-latent-assumptions-from-qlever-comparison.md`) handles like-for-like methodology against QLever-class systems for anyone working in that conversation.

If you build AI systems that need to remember, or you care about semantic sovereignty as an architectural property, or you work on substrates that need to last — the artifacts are open. Curious is enough.

---

*Numbers anchored in cycle 10 r4 validation, truthy r1 validation, WGPB step C validation, and the aggregate distribution doc — all under `docs/validations/`. Current substrate: Mercury 1.7.57, BCL-only .NET 10.*
