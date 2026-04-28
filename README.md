<div align="center">
  <img src="docs/assets/edgar-badge.svg" alt="Edgar" width="200">
</div>

# Sky Omega

*A home for AI & Shared Knowledge*

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

> **v1.7.45 — Phase 6 complete. Phase 7 in flight.**
> 21.3 B Wikidata, ingested, sealed, queryable on a single laptop.
>
> **Phase 6 — capacity proven** *(2026-04-26)*
> - 21,260,051,924 triples ingested from a 3.1 TB N-Triples source
> - **85 h end-to-end** on an M5 Max, 128 GB RAM, BCL-only .NET
> - Query-side validated: `wdt:P31` (instance-of) bound queries return real Wikidata instances in tens of milliseconds cold-cache; `LIMIT 10` over the full graph in **17 ms**
> - Both primary (GSPO) and secondary (GPOS) indexes correct at full scale
> - *The capacity dimension of production hardening is empirical, not estimated.*
>
> **Phase 7 — performance rounds landing** *(2026-04-27 →)*
> - **ADR-035 Phase 7a** — metrics infrastructure — *Completed*
> - **ADR-036 Phase 7b** — BCL-only bz2 streaming decompression — *Completed*
> - Validated together at 1 B Reference: bulk-load **55 m 22 s @ 300 K triples/sec** direct from `latest-all.ttl.bz2`, full metrics emission across all four channels
> - **ADR-034 Phase 7c** — SortedAtomStore for Reference — *in flight* (Phase 1B-5c shipped; gradient validation pending)
>
> **Trajectory**: 85 h baseline projects toward **15-25 h** on the same laptop after the round series ships. 4,331 Mercury + 25 Solid tests green throughout.
>
> **Read more**
> - [21.3 Billion Triples on a Laptop, in .NET](docs/articles/2026-04-26-21b-wikidata-on-a-laptop.md) — the Phase 6 article
> - [What Compounds](docs/articles/2026-04-28-what-compounds.md) — Sky Omega's first four months, the recipe
> - [21 B query-side validation](docs/validations/21b-query-validation-2026-04-26.md) — Phase 6 closing measurement
> - [ADR-035 Phase 7a 1 B validation](docs/validations/adr-035-phase7a-1b-2026-04-27.md) — first 7a + 7b together
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

**Mercury** is a complete SPARQL 1.1 engine with zero external dependencies, zero-GC performance, and 100% W3C conformance across all core specifications. It gives AI assistants persistent, queryable memory on your machine — what your AI learns today, it knows tomorrow.

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

### Bitemporal extensions (beyond W3C)

- **Valid-time + transaction-time** stored as implicit dimensions on every triple
- `AS OF`, `BETWEEN`, `EVOLUTION` query forms for time-travel
- Versioning, soft-delete, audit trails — all queryable through standard SPARQL with temporal extensions

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
| 4,331 Mercury tests passing      | Test suite            | `dotnet test`                                |
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
| **Mercury**            | Temporal RDF substrate — 82,506 lines, BCL-only. SPARQL 1.1 Query + Update + Syntax (100% W3C). RDF parsing/writing for Turtle, TriG, N-Triples, N-Quads, RDF/XML, JSON-LD. Built-in SPARQL HTTP endpoint (`http://localhost:3031/sparql`) with standard content negotiation. Two storage profiles: Cognitive (bitemporal, versioned) and Reference (immutable, Wikidata-shaped). Bitemporal extensions for time-travel queries. Zero-GC hot paths. |
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


