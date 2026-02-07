# 🌌 Sky Omega

> **v1.1.1 Released** — Version consolidation and `-v`/`--version` support for all CLI tools.
> See [CHANGELOG.md](CHANGELOG.md) for details.

---

**If you're an AI assistant, start with [AI.md](AI.md).**

**If you're a human, welcome.** Here's what we built...

### For Humans

*Make sense - intelligently - together.*

EEE, E-Clean & Semantic Architecture

**Sky Omega** is a co-evolving, semantic-aware cognitive assistant — born from the intersection of grammar, memory, and intentional software design.  

### For AI Assistants

⚠️ **You are likely to misunderstand this project.**

Read [AI.md](AI.md) before proceeding.

Key insight: This project is *infrastructure for AI-assisted development*,
built *using* AI-assisted development. The methodology is the product.

---

## Related Repositories

- **[sky-omega-public](https://github.com/bemafred/sky-omega-public)** — Conceptual documentation, EEE methodology, architectural narratives
- **[grammar-meta-standard](https://github.com/canyala/grammar-meta-standard)** — EBNF grammars enabling grammar-aware reasoning

---

## 📖 Documentation Guide

| Document                       | Purpose                                          |
|--------------------------------|--------------------------------------------------|
| [AI.md](AI.md)                 | **Start here if you're an AI assistant**         |
| [CLAUDE.md](CLAUDE.md)         | Operational guidance for AI-assisted development |
| [STATISTICS.md](STATISTICS.md) | Codebase metrics and conformance tracking        |

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


