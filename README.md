<div align="center">
  <img src="docs/assets/edgar-badge.svg" alt="Edgar" width="200">
</div>

# Sky Omega

*A home for AI & Shared Knowledge*

**Current substrate: [Mercury 1.7.57](CHANGELOG.md#1757---2026-05-11)** · production-validated by **three paired measurements** on the same substrate generation:
- [cycle 10 Phase 3 r4](docs/validations/cycle10-phase3-r4-21b-2026-05-12.md) — **21.3 B full** Wikidata, 23 h 57 m end-to-end (2026-05-13)
- [truthy r1](docs/validations/truthy-r1-2026-05-14.md) — **8.17 B truthy** Wikidata, 14 h 13 m end-to-end (2026-05-14)
- [WGPB step C](docs/validations/wgpb-step-c-2026-05-16.md) — **~150 M 2018 reduced-truthy** Wikidata, 4 m 30 s end-to-end + **849/850 WGPB queries in 4 m 43 s** (2026-05-16)

Truthy is the apples-to-apples companion vs published WDBench / QLever / Virtuoso numbers. WGPB enables comparison vs MillenniumDB's published systematic-graph-pattern benchmarks. **Cumulative discipline: 0 substrate failures across ~9,763 measured queries.** Note Mercury Reference includes a built-in full-text trigram index — like-for-like vs systems without text indexes: **15 h 26 m / 6 h 49 m / n/a** (trigram is a +8 h 30 m / +7 h 24 m / ~negligible feature cost that buys SPARQL `text:match` out of the box).

Your AI assistants are brilliant and homeless. Every conversation starts from nothing. Every insight evaporates when the window closes. They can reason, but they can't remember. They can help, but they can't grow.

Sky Omega gives them a place to stay.

Not a platform. Not a cloud service. A home — on your machine, under your control, queryable by any agent you trust. What your AI learns today, it knows tomorrow. What it knows on your laptop, it can share with your team through the tools you already use.

**Whose home?**

Yours. The AI lives there. You hold the keys. The knowledge it accumulates is stored locally in open standards — RDF triples, queryable via SPARQL, portable as Turtle files. No vendor lock-in. No proprietary memory systems. No platform deciding what your AI remembers or forgets.

Switch models. Switch providers. The knowledge stays.

**What grows there?**

Understanding. Not conversation logs — *structured meaning*. Decisions and why they were made. Patterns that work and approaches that failed. The vocabulary your project actually uses. The constraints that took three sessions to discover. All of it queryable, traceable, version-controlled.

Code travels via git. Now knowledge does too.

**Why now?**

For fifteen years, RDF was the right answer to a question nobody was asking. Structured knowledge representation needed an interface layer — something that could read, write, and reason over triples naturally. LLMs are that interface. They were the missing piece.

Sky Omega is what becomes possible when you stop building better travelers and start building them a home.

---

> **v1.7.57 — Three paired measurements: cycle 10 Phase 3 r4 + truthy r1 + WGPB step C complete. Substrate at 23 h 57 m / 14 h 13 m / 4 m 30 s end-to-end at 21.3 B full / 8.17 B truthy / ~150 M WGPB-filtered** (with full-text trigram index). Like-for-like (no trigram, comparing vs published QLever / Virtuoso / WDBench / MillenniumDB numbers): **15 h 26 m / 6 h 49 m / 4 m 30 s**. WGPB queries: 849/850 in 4 m 43 s (99.88 % completion, 0 timeouts).
> Four measured 21.3 B Wikidata production runs across the trajectory; substrate now **3.5× faster** than its first incarnation, all on a single laptop, BCL-only .NET.
>
> **Cumulative trajectory** *(measured-vs-measured, four completed full-Wikidata runs)*
> - **Phase 6** *(2026-04-25, 1.7.x pre-Sorted)* — 85 h end-to-end. First successful 21.3 B Reference end-to-end on a single M5 Max.
> - **Cycle 8** *(2026-05-06, 1.7.48)* — 46 h with intervention. ADR-034 SortedAtomStore for Reference closed Phase 1; algorithmic switch from Hash → Sorted atom store; ~42 % atoms.atoms reduction via prefix compression; cleanup-class FD fixes.
> - **Cycle 9** *(2026-05-09, 1.7.50)* — 35 h 35 m clean. ADR-037 pipelined spill (parser 14 h 15 m → 9 h 18 m, *measured*); 1.7.49 cleanup hook (3.96 TB reclaimed at end-of-merge, manual intervention requirement eliminated).
> - **Cycle 10 r4** *(2026-05-13, 1.7.57)* — **23 h 57 m clean**. ADR-038 merge-phase read-side (prefix-compress intermediate chunks + frontier readahead + sidecar offset table); ADR-039 BBHash MPHF over sealed atom set with `MaxLevels`=40 + dense final-level fallback; MPHF instrumentation surface (per-level events + dense-fallback + start/complete summary); listener wire-through fix at `QuadStore.RebuildMphf`.
> - **Truthy r1** *(2026-05-14, 1.7.57)* — **14 h 13 m** end-to-end on the same substrate. 8,171,214,990 truthy-Wikidata triples (vs full's 21.3 B). Apples-to-apples companion to cycle 10 r4 for comparison vs published WDBench / QLever / Virtuoso numbers. Key finding: trigram entries 90.7 % of full at 38.3 % triple-count ratio = ~2.4× more literal-density per triple in truthy → trigram-phase prediction needs literal-volume scaling, not triple-count scaling (dump-date confounder noted in the [validation doc](docs/validations/truthy-r1-2026-05-14.md)).
> - **WGPB step C** *(2026-05-16, 1.7.57)* — **4 m 30 s** end-to-end on a 2018 reduced-truthy Wikidata substrate (~150 M triples). MillenniumDB's Wikidata Graph Pattern Benchmark: **849/850 queries completed in 4 m 43 s** (99.88 %, 0 timeouts; 1 query rejected as malformed source SPARQL — Mercury's parser correctly identifying real defects in published benchmark data). Aggregate p50 53 ms, p95 1.8 s, p99 4.3 s. Apples-to-apples vs published MillenniumDB / Virtuoso / Blazegraph WGPB numbers. See [validation doc](docs/validations/wgpb-step-c-2026-05-16.md).
> - Cumulative: **85 h → 24 h, −71.8 %** wall-clock reduction across the substrate's evolution.
>
> **Cycle 10 r4 — production validation of ADR-038 + ADR-039 + MPHF instrumentation** *(2026-05-13)*
> - 21,316,531,403 triples ingested from **full** Wikidata (`latest-all.ttl.bz2`) + sealed in **23 h 56 m 50 s end-to-end** (parse 9 h 17 m + merge 2 h 41 m + MPHF 54 m 29 s + GSPO drain ~1 h 38 m + GPOS rebuild 55 m 27 s + Trigram rebuild 8 h 30 m 30 s)
> - *Dataset note: every Mercury measurement runs against full Wikidata, not the truthy subset (`latest-truthy.nt.bz2`) that most published QLever/Virtuoso/WDBench numbers use. Truthy is ~1.5–1.8× smaller and excludes statement-level qualifiers, references, and sitelinks. See [the comparison-plane memo](docs/memos/2026-04-30-latent-assumptions-from-qlever-comparison.md) for the honest-comparison framing.*
> - **MPHF construction characterized at production scale (4.005 B atoms):** 25 levels, 0 dense fallback engaged, placement_ratio held at **0.6065** across all levels — exact match to BBHash theoretical `1 − e^(−1/γ)` for γ=2.0. Total 54 m 29 s, within 1 % of the cycle 10 plan's "+~55 min MPHF" budget.
> - Substrate output identity: 4,005,235,528 atoms, 17,029,283,265 GPOS entries, 7,472,855,623 trigram entries — bit-for-bit identical to cycle 9's measurements (same input, deterministic substrate). MPHF surface is purely additive: 1.75 GB `atoms.mphf` blob + 16.0 GB `atoms.idx` translation table.
> - FD trajectory peaked at 8,325 during trigram rebuild (8,192 simultaneously-open chunks) vs the launchd ~10K effective ceiling = 17 % headroom held for 8 h+ — `ExternalSorter` k-way merge bypasses `BoundedFileStreamPool`; class-fix follow-up for cycle 11.
>
> **Substrate components shipped** *(cumulative)*
> - **ADR-034** SortedAtomStore for Reference — *Completed* (1.7.30 → 1.7.48)
> - **ADR-035** Phase 7a metrics infrastructure — *Completed*
> - **ADR-036** BCL-only bz2 streaming decompression — *Completed*
> - **ADR-037** Pipelined spill in `SortedAtomBulkBuilder` — *Completed* (1.7.50, production-validated cycle 9)
> - **ADR-038** Merge-phase read-side optimization — *Completed* (1.7.52/1.7.54, production-validated cycle 10 r4)
> - **ADR-039** MPHF over sealed atom set — *Completed* (1.7.55 with dense fallback; instrumented 1.7.56 + wire-through fix 1.7.57; production-validated cycle 10 r4)
> - *Reference-profile measurements per [ADR-008](docs/adrs/ADR-008-workload-profiles-and-validation-attribution.md). Cognitive-profile validation drought persists — see [docs/limits/cognitive-profile-validation-drought.md](docs/limits/cognitive-profile-validation-drought.md).*
>
> **Read more**
> - [21.3 Billion Triples on a Laptop, in .NET](docs/articles/2026-04-26-21b-wikidata-on-a-laptop.md) — the Phase 6 article
> - [What Compounds](docs/articles/2026-04-28-what-compounds.md) — Sky Omega's first four months, the recipe
> - [Cycle 10 Phase 3 r4 production validation](docs/validations/cycle10-phase3-r4-21b-2026-05-12.md) — most recent measurement (1.7.57)
> - [Cycle 9 21.3 B production validation](docs/validations/adr-037-cycle9-21b-2026-05-09.md) — the comparison baseline
> - [CHANGELOG.md](CHANGELOG.md) · [Roadmap](docs/roadmap/production-hardening-1.8.md) · [Validations](docs/validations/) · [Limits register](docs/limits/)

**If you're an AI assistant, start with [AI.md](AI.md).**

---

## Quick Start

```bash
git clone --recurse-submodules <repo-url> && cd sky-omega
dotnet build SkyOmega.sln
dotnet test
./tools/install-tools.sh      # macOS/Linux
mercury -m                    # Start an in-memory session — REPL + SPARQL HTTP endpoint at http://localhost:3031/sparql
mercury <store> --bulk-load data.ttl.bz2  # Bulk-load Turtle (or .nt, .nq, .trig, .rdf, .jsonld; .bz2 / plain)
```

> **Already cloned without submodules?** Run `./tools/update-submodules.sh` to fetch
> the W3C conformance test data needed by `dotnet test`.

New here? Follow the **[Getting Started tutorial](docs/tutorials/getting-started.md)**.

Want to give Claude persistent memory? See **[Mercury MCP tutorial](docs/tutorials/mercury-mcp.md)**.

---

## Related Repositories

- **[sky-omega-public](https://github.com/bemafred/sky-omega-public)** — Conceptual documentation, EEE methodology, architectural narratives
- **[grammar-meta-standard](https://github.com/canyala/grammar-meta-standard)** — EBNF grammars enabling grammar-aware reasoning

---

## 📖 Documentation Guide

| Document                              | Purpose                                          |
|---------------------------------------|--------------------------------------------------|
| [AI.md](AI.md)                        | **Start here if you're an AI assistant**         |
| [CLAUDE.md](CLAUDE.md)                | Operational guidance for AI-assisted development |
| [MERCURY.md](MERCURY.md)              | Semantic memory discipline — when, why, how      |
| [STATISTICS.md](STATISTICS.md)        | Codebase metrics and conformance tracking        |
| [Getting Started](docs/tutorials/getting-started.md) | 30-minute onboarding tutorial           |
| [Mercury CLI](docs/tutorials/mercury-cli.md) | CLI REPL deep dive                        |
| [Mercury MCP](docs/tutorials/mercury-mcp.md) | Claude integration and persistent memory  |
| [API Reference](docs/api/api-usage.md) | Detailed code examples for all APIs             |
| [The Collected Poems of Kjell Silverstein](docs/poetry/kjell-silverstein-collected.md) | Sky Omega explained without a single line of code |

---

## 💠 Project Purpose

**Mercury** is a complete SPARQL 1.1 engine with zero external runtime dependencies (BCL-only core), zero-GC hot paths, and 100% W3C conformance across all core specifications. It gives AI assistants persistent, queryable memory on your machine — what your AI learns today, it knows tomorrow.

> *Scope of "BCL-only": Mercury core (`src/Mercury/`) and its 21-public-type embeddable surface have no `PackageReference` entries. Adjacent surfaces — `Mercury.Mcp` (depends on `ModelContextProtocol`), DrHook (depends on `Microsoft.Diagnostics.NETCore.Client` until ADR-006/drhook engine ships) — package the substrate for tooling and runtime observation. The substrate-independence claim applies to the core; the tooling layer is honest about its dependencies.*

The broader Sky Omega vision is a **stand-alone cognitive agent** built on this foundation, combining:

- **Structured memory** via a temporal RDF knowledge substrate (Mercury — built)
- **Grammar-driven reasoning** (syntax, behavior, and intent grammars)
- **Local LLM inference** (Minerva — planned)
- **Explainable, traceable logic** — a foundation for hybrid AGI

---

## 🌐 Standards Coverage

Mercury is a full SPARQL 1.1 + RDF stack. Every standard listed below is implemented in BCL-only C#, validated against the W3C conformance test suite, and exposed via CLI, HTTP endpoint, and embeddable .NET API.

### RDF formats (parse + write, streaming, zero-GC)

| Format | W3C Conformance | Use |
|---|---|---|
| **Turtle 1.2** | 309/309 (100%) | Human-friendly, prefix support, the de-facto interchange format |
| **TriG 1.2** | 352/352 (100%) | Turtle with named graphs |
| **N-Triples 1.2** | 70/70 (100%) | Line-oriented, the Wikidata dump format |
| **N-Quads 1.2** | 87/87 (100%) | N-Triples with named graphs |
| **RDF/XML 1.1** | 166/166 (100%) | Legacy interop, still required by many vocabularies |
| **JSON-LD 1.1** | 461/467 (99%, 6 intentional skips) | JSON-native RDF for web/API surfaces |

### SPARQL 1.1

| Spec | W3C Conformance |
|---|---|
| **SPARQL 1.1 Query** (SELECT, ASK, CONSTRUCT, DESCRIBE, all aggregates, property paths, federated SERVICE) | 421/421 (100%) |
| **SPARQL 1.1 Update** (INSERT, DELETE, LOAD, CLEAR, CREATE, DROP, COPY, MOVE, ADD) | 94/94 (100%) |
| **SPARQL 1.1 Syntax** | 103/103 (100%) |
| **SPARQL 1.1 Federated Query** (SERVICE clause, remote endpoints) | included in Query 421 |

### Protocols & surfaces

- **SPARQL Protocol over HTTP** — `mercury` CLI ships with a built-in HTTP endpoint at `http://localhost:3031/sparql`. Standard query/update content negotiation, JSON/XML/CSV/TSV result serialization. Use `SERVICE <http://localhost:3030/sparql>` to federate across local Mercury instances.
- **W3C Solid Protocol server** (`Mercury.Solid`) — WAC + ACP access control, N3 Patch updates, full HTTP handlers.
- **Model Context Protocol (MCP)** — `mercury-mcp` exposes Mercury as a Claude semantic-memory tool with persistent store survival across sessions.

### Bitemporal extensions (beyond W3C, **Cognitive profile only**)

- **Valid-time + transaction-time** stored as implicit dimensions on every triple
- `AS OF`, `DURING`, `ALL VERSIONS` query forms for time-travel
- Versioning, soft-delete, audit trails — all queryable through standard SPARQL with temporal extensions
- *Reference profile drops temporal columns by design — sealed canonical snapshots have no time dimension. See [ADR-008](docs/adrs/ADR-008-workload-profiles-and-validation-attribution.md) for the workload-profile distinction.*

---

## Verifiable Facts

| Claim                            | Evidence              | Command to Verify                            |
|----------------------------------|-----------------------|----------------------------------------------|
| 100% W3C SPARQL 1.1 Query        | 421 passing tests     | `dotnet test --filter "W3C.Sparql.Query"`    |
| 100% W3C SPARQL 1.1 Update       | 94 passing tests      | `dotnet test --filter "W3C.Sparql.Update"`   |
| 100% W3C SPARQL 1.1 Syntax       | 103 passing tests     | `dotnet test --filter "W3C.Sparql.Syntax"`   |
| 100% W3C Turtle / TriG / N-Triples / N-Quads / RDF-XML | 984 passing tests | `dotnet test --filter "W3C"` |
| 100% W3C JSON-LD 1.1             | 461 passing tests (6 intentional skips: legacy 1.0, generalized RDF) | `dotnet test --filter "W3C.JsonLd"` |
| SPARQL HTTP endpoint             | `mercury` CLI         | `mercury -m` then visit `http://localhost:3031/sparql` |
| Zero external runtime deps       | Mercury.csproj        | `grep PackageReference src/Mercury/*.csproj` |
| 4,335 Mercury tests passing      | Test suite            | `dotnet test`                                |
| AI-assisted development          | Git history           | `git log --oneline \| grep "Co-Authored-By"` |
| Development velocity             | ~197K lines           | See [STATISTICS.md](STATISTICS.md)           |

---

## 🏛️ Project Evolution & Methodology

[Sky Omega - On Emergence, Epistemics, and the Patience Required to Build What Matters](docs/architecture/narratives/sky-omega-convergence.md)

---

## 🧠 What's Built

Everything below has code in `src/`, tests, and benchmarks.

| Component              | Description                                                                                |
|------------------------|--------------------------------------------------------------------------------------------|
| **Mercury**            | Temporal RDF substrate — 82,887 lines, BCL-only. SPARQL 1.1 Query + Update + Syntax (100% W3C). RDF parsing/writing for Turtle, TriG, N-Triples, N-Quads, RDF/XML, JSON-LD. Built-in SPARQL HTTP endpoint (`http://localhost:3031/sparql`) with standard content negotiation. Two storage profiles: Cognitive (bitemporal, versioned) and Reference (immutable, Wikidata-shaped). Bitemporal extensions for time-travel queries. Zero-GC hot paths. |
| **Mercury.Solid**      | W3C Solid Protocol server — WAC + ACP access control, N3 Patch updates, full HTTP surface |
| **Mercury.Pruning**    | Dual-instance pruning with copy-and-switch pattern                                         |
| **Mercury MCP**        | Claude integration with persistent semantic memory                                         |
| **Mercury CLI**        | Interactive REPL with persistent store, global tool install                                 |
| **DrHook**             | Runtime observation substrate — EventPipe profiling and DAP stepping for AI coding agents   |
| **DrHook MCP**         | MCP server for .NET runtime inspection (peer to Mercury MCP)                               |

## 🔭 Architectural Vision

The agent architecture that Mercury is being built to support. These components are planned for Sky Omega 2.0.

| Component                      | Role                                                                                        |
|--------------------------------|---------------------------------------------------------------------------------------------|
| **Sky**                        | Language layer — pruned reasoning, reflection, and short-term memory                        |
| **James**                      | Cognitive orchestration — tail recursive orchestration loop                                 |
| **Lucy**                       | Long-term memory — epistemic and semantic, queryable, precise (powered by Mercury)          |
| **Mira**                       | Integration layer — expression and sensory capabilities, UX/UI                              |
| **Minerva**                    | LLM inference substrate — BCL-only, zero-GC, local-first                                   |
| **Behavior & Intent Grammars** | Define what Sky *knows*, *intends*, and *verifies*                                          |


---

## 🧬 Design Principles

- **Intent before implementation**
- **Transparency before complexity**
- **Semantics before scale**
- **Recursion as structure**
- **Code as a mirror of cognition**

---

## 🗂️ Navigating the Codebase

Modern IDEs (Visual Studio, Rider, VS Code) offer two views of this repository:

| View | What you see | Best for |
|------|--------------|----------|
| **Solution View** | Virtual folders from `SkyOmega.sln` | Browsing by component (Mercury ADRs under Mercury, etc.) |
| **Filesystem View** | Actual directory structure | Finding files by path, understanding repository layout |

Both are valid. The solution file organizes content logically for architects and developers, while the filesystem maintains consistent paths for documentation links.


