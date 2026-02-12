# Sky Omega

<p align=”center”>
  <img src=”docs/assets/korpen.svg” alt=”Korpen” width=”200”>
</p>

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

> **v1.2.1 Released** — Pruning support in CLI and MCP with QuadStorePool migration.
> See [CHANGELOG.md](CHANGELOG.md) for details.

**If you're an AI assistant, start with [AI.md](AI.md).**

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

---

## 💠 Project Purpose

Sky Omega is designed as a **stand-alone cognitive agent**, powered by:

- **Structured memory** via a temporal RDF triple store (`Lucy`)
- **Grammar-driven reasoning** (syntax, behavior, and intent grammars)
- **Zero-GC performance and SSD-optimized data structures** (e.g., B+ trees, append-only stores)
- **Explainable, traceable logic**—a foundation for hybrid AGI

---

## Verifiable Facts

| Claim                            | Evidence              | Command to Verify                            |
|----------------------------------|-----------------------|----------------------------------------------|
| 100% W3C SPARQL 1.1 conformance  | 512 passing tests     | `dotnet test --filter "FullyQualifiedName~W3C.Sparql"` |
| 100% W3C RDF format conformance  | 1,896 passing tests   | `dotnet test --filter "W3C"`                 |
| Zero external dependencies       | Mercury.csproj        | `grep PackageReference src/Mercury/*.csproj` |
| 3,830 tests passing              | Test suite            | `dotnet test tests/Mercury.Tests`            |
| AI-assisted development          | Git history           | `git log --oneline \| grep "Co-Authored-By"` |
| Development velocity             | 146K lines in weeks   | `git log --since="2025-12-01"`               |

---

## 🏛️ Project Evolution & Methodology

[Sky Omega - On Emergence, Epistemics, and the Patience Required to Build What Matters](docs/architecture/narratives/sky-omega-convergence.md)

---

## 🧠 Core Components

| Component                      | Description                                                                                       |
|--------------------------------|---------------------------------------------------------------------------------------------------|
| **Sky**                        | The language layer. Provides pruned reasoning, reflection, and short-term memory.                 |
| **James**                      | The cognitive orchestration layer. A tail recursive orchestration loop.                           |
| **Lucy**                       | The long-term memory layer. Epistemic and semantic in nature, fast, huge, queryable, and precise. |
| **Mira**                       | The integration layer. The expression and sensory capablities, UX/UI.                             |
| **Mercury**                    | The temporal RDF-substrate. Fast, huge, low-level, persistent and deterministic.                  |
| **Minerva**                    | The LLM inference substrate. BCL-only, zero-GC, local-first. *[planned]*                          |
| **Behavior & Intent Grammars** | Define what Sky *knows*, *intends*, and *verifies*.                                               |


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


