# ADR-002 — Documentation and Tutorial Strategy

## Status

**Phase 1 Accepted** (2026-02-15) — Phase 1 implemented and merged. Phases 2-5 remain Proposed.

## Context

Sky Omega v1.2 ships four globally-installable CLI/MCP tools ([ADR-019](mercury/ADR-019-global-tool-packaging.md)), a comprehensive API reference (`docs/api/api-usage.md`), deep architectural documentation (18 Mercury ADRs, canonical concept definitions, EEE methodology), and a working examples project. The repository is preparing for public release under MIT license.

**The problem:** there is no path from “I just cloned this” to “I’m productively using it.”

A developer or AI agent arriving at the repository today encounters:

1. **No getting-started tutorial.** README points to AI.md, CLAUDE.md, MERCURY.md — all internal operational documents. The `tools/` directory with install scripts is not referenced from README.
1. **No tool documentation.** Four CLI tools (`mercury`, `mercury-sparql`, `mercury-turtle`, `mercury-mcp`) have `--help` text in source but no tutorial explaining workflows, use cases, or examples beyond the inline help.
1. **No MCP onboarding.** The main integration story — “give your AI persistent memory” — has no walkthrough. Claude Code configuration is documented only in [ADR-019](mercury/ADR-019-global-tool-packaging.md).
1. **Empty knowledge directories.** `docs/knowledge/patterns/`, `docs/knowledge/decisions/`, `docs/knowledge/vocabulary/` have `.gitkeep` files and an excellent README describing their purpose, but no content.
1. **Undocumented examples.** The `examples/Mercury.Examples` project has storage and temporal examples but no README explaining how to run them or what they demonstrate.
1. **No RDF onboarding.** The project assumes RDF/SPARQL familiarity. For the audience that needs Sky Omega most — developers who haven’t used RDF because the tooling was hostile — there’s no bridge.

### What exists and works well

The repository has strong documentation in specific areas that tutorials should complement, not duplicate:

|Asset                                            |Strength                                              |Gap                                        |
|-------------------------------------------------|------------------------------------------------------|-------------------------------------------|
|`docs/api/api-usage.md`                          |Comprehensive API reference with code examples        |Reference, not tutorial — no narrative flow|
|`docs/tutorials/learning-to-navigate-eee.md`     |Excellent EEE pedagogy via driving/navigation metaphor|Conceptual, not tool-oriented              |
|`docs/architecture/concepts/canonical/`          |20+ concept definitions                               |Internal reference, not onboarding         |
|`docs/process/e-clean-and-semantic-architecture/`|5-part methodology guide                              |Process, not getting-started               |
|`docs/knowledge/README.md`                       |Clear articulation of knowledge flow                  |The directories it describes are empty     |
|`examples/Mercury.Examples/`                     |Working storage + temporal code examples              |No README, no explanation                  |
|`CONTRIBUTING.md`                                |Strong epistemic contract for contributors            |Assumes you already understand the project |
|`AI.md`                                          |Effective “stop and recalibrate” for AI agents        |Assessment guide, not usage guide          |
|CLI `--help` text                                |Accurate, with examples                               |Only visible after installation            |

### Audiences

Three distinct audiences need different entry points:

1. **Users** — Want to use Mercury tools. Need: install, configure, run, query.
1. **Developers** — Want to embed Mercury in their .NET projects. Need: API tutorials, library usage patterns.
1. **Curious minds** — Want to understand the architecture, EEE, semantic braid. Need: conceptual tutorials with concrete grounding.

The existing documentation serves audience 3 well and audience 2 partially (via API reference). Audience 1 has no entry point.

### Alternatives considered

**Do nothing.** Let the code and `--help` text speak for itself. Rejected: this is the strategy that kept RDF adoption low for 15 years. Sky Omega’s thesis is that LLMs are the missing interface layer — but the *human* interface layer is missing too.

**Wiki or external docs site.** Rejected: documentation should travel with the code via git. Turtle files in `docs/knowledge/` are version-controlled knowledge; tutorials should be too.

**Inline code comments only.** Rejected: explains *how* but not *why* or *when*. Tutorials provide narrative and judgment that comments cannot.

**Generate docs from code.** Partial fit for API reference (which already exists manually). Not a substitute for tutorials that teach workflows and mental models.

## Decision

### Documentation structure

All tutorials live in `docs/tutorials/` as Markdown files. Each tutorial is self-contained, with clear prerequisites, a single learning objective, and runnable examples.

The tutorials are organized in phases, where each phase is a coherent deliverable that adds value independently.

### Naming convention

Tutorial files use kebab-case descriptive names: `getting-started.md`, `mercury-cli.md`, etc. No numbering — ordering is defined by the getting-started tutorial’s “where to go next” signposting and by this ADR.

### Cross-references

- README.md gets a “Quick Start” section pointing to `docs/tutorials/getting-started.md`
- `examples/Mercury.Examples/` gets a README.md
- Each tutorial links to relevant API reference, ADRs, and canonical concepts where appropriate
- Tutorials reference each other via relative links

### bootstrap.ttl and the bootstrap chain

`docs/knowledge/bootstrap.ttl` provides grounded context for new Mercury stores — component definitions, EEE methodology as queryable structure, core architectural principles. It is designed primarily for the MCP store.

**The intended bootstrap chain:**

1. Claude Code reads `CLAUDE.md` (by convention on session start)
1. `CLAUDE.md` line 96 references `MERCURY.md`: *“See MERCURY.md for when, why, and how to use semantic memory”*
1. `MERCURY.md` “Bootstrap” section instructs: *“If `mercury_stats` shows zero quads, load the bootstrap”* with a `LOAD` command

**Epistemic assessment — this chain is fragile:**

- **CLAUDE.md → MERCURY.md is a soft reference.** The word “see” is a suggestion, not a directive. Claude Code reads CLAUDE.md because that’s the convention, but has no obligation to follow “see also” links. Whether MERCURY.md gets read depends on LLM behavior, not a reliable mechanism.
- **The LOAD path is a placeholder.** MERCURY.md contains `<file:///absolute/path/to/docs/knowledge/bootstrap.ttl>` — requiring the agent to resolve the repo root. This works when the agent is resourceful, but it’s not a concrete instruction.
- **Mixed signals between sections.** MERCURY.md’s “Session Discipline” section says *“If the store is empty, that’s fine — you’re bootstrapping”* while the “Bootstrap” section says *“load the bootstrap.”* The intended reading is probably both — empty is fine, but load bootstrap for grounded context — but that requires inference.
- **MCP disabled in Claude Code:** Nothing fires. No LLM reads either file. The MCP store stays empty and the user has no indication that bootstrap.ttl exists or should be loaded.
- **Standalone mercury-mcp:** A user running `mercury-mcp` without Claude Code gets no bootstrap guidance at all.

This chain currently depends on *probable* LLM behavior, not a *reliable* mechanism. It works in practice because Claude Code tends to be thorough — but “tends to” is Emergence territory, not Engineering.

**Phase 1 addresses this through three actions:**

1. **CLAUDE.md modification:** Promote the MERCURY.md reference from "see also" to an explicit directive in session startup context — *"On first MCP interaction with an empty store, follow the bootstrap procedure in MERCURY.md"*
1. **MERCURY.md modification:** Replace the placeholder path `<file:///absolute/path/to/docs/knowledge/bootstrap.ttl>` with a concrete instruction. The agent should use `pwd` or equivalent to determine the repository root and construct the absolute `file:///` URI, e.g.: *"Determine the repository root (the directory containing CLAUDE.md), then run: `LOAD <file:///{repo_root}/docs/knowledge/bootstrap.ttl>`"*. Alternatively, if Mercury's `LOAD` supports relative paths from the working directory, document that and use `LOAD <docs/knowledge/bootstrap.ttl>`.
1. **Tutorial coverage (mercury-mcp.md):** Document all three bootstrap scenarios — Claude auto-loading, manual loading via CLI attachment, and standalone `mercury-mcp` without Claude Code — so the human always has a path regardless of tooling configuration

-----

## Phase 1 — The Front Door

**Goal:** A new user can go from `git clone` to running their first SPARQL query in under 30 minutes (§1.1). The remaining Phase 1 deliverables extend that path to persistent stores, CLI workflows, and Claude integration — valuable independently but not part of the "30 minutes" promise.

**Deliverables:**

### 1.1 `docs/tutorials/getting-started.md`

The single most critical missing document. Covers:

- Prerequisites: .NET 10 SDK, git
- Clone and build verification (`dotnet build`, `dotnet test` — confirm green)
- Running install scripts (`./tools/install-tools.sh` or `.\tools\install-tools.ps1`)
- Verifying tool installation (`mercury --version`, `mercury-sparql --version`, `mercury-turtle --version`, `mercury-mcp --version`)
- First interactive session: `mercury -m` (in-memory, zero commitment)
  - INSERT DATA with a few triples
  - SELECT query
  - `:stats` to see what happened
  - `:quit` — store deleted, nothing permanent
- First persistent session: `mercury` (default store at platform-specific path)
  - Same workflow, but data survives across sessions
  - Show the store path, explain platform conventions (macOS/Linux/Windows via `MercuryPaths`)
- Signposts: “Where to go next” section linking to Phase 1 and 2 tutorials
- Note the existence of `docs/knowledge/bootstrap.ttl` — what it provides, that it’s primarily for MCP stores, and that it can be loaded into any store for inspection

**Does NOT cover:** RDF theory, SPARQL syntax in depth, architecture, EEE. Those have their own tutorials.

### 1.2 `docs/tutorials/mercury-cli.md`

The persistent REPL — Mercury’s primary interactive surface.

- Starting modes: default persistent (`mercury`), in-memory (`mercury -m`), custom path (`mercury ./mydata`)
- REPL commands: `:help`, `:quit`, `:stats`, `:graphs`, `:prune`
- Loading data: `LOAD <file:///path/to/data.ttl>` — with a concrete example file
- Query workflows: SELECT, ASK, CONSTRUCT with practical examples
- The HTTP endpoint: what it is (`http://localhost:3031/sparql`), how to use it from curl or a browser
- Attaching to a running MCP instance: `mercury -a mcp` — inspect what Claude has stored
- Store management: where stores live, how to back up, how to start fresh
- Pruning from the REPL: `:prune --dry-run`, `:prune`, history modes, graph/predicate exclusion

### 1.3 `docs/tutorials/mercury-mcp.md`

The integration story — giving Claude persistent semantic memory.

- What Mercury MCP is: an MCP server exposing five tools (`mercury_query`, `mercury_update`, `mercury_stats`, `mercury_graphs`, `mercury_prune`)
- Starting standalone: `mercury-mcp` with default persistent store
- Configuring Claude Code:
  - Development (in-repo): `.mcp.json` already present
  - Global (all repos): `claude mcp add --transport stdio --scope user mercury -- mercury-mcp`
- **Worked example:** A complete first conversation with Claude Code where:
  - Claude stores a project decision as triples
  - Claude queries it back in a later message
  - The knowledge persists across sessions
- **Bootstrap: three paths to grounded context.** This section must cover all scenarios explicitly:
1. **Claude Code with MCP enabled:** CLAUDE.md directs Claude to read MERCURY.md, which instructs bootstrap loading on empty store. This is the automatic path — explain what happens and what to expect.
1. **MCP running without Claude Code** (standalone `mercury-mcp`, or Claude Code with MCP disabled): The user must load bootstrap.ttl manually. Show how: attach via CLI (`mercury -a mcp`) and run `LOAD <file:///...>`, or use the HTTP endpoint with curl.
1. **CLI user inspecting bootstrap.ttl:** Not the primary target, but users may want to explore the system’s self-model. Show `mercury -m` followed by `LOAD` for a disposable inspection session.
- The HTTP endpoint (`http://localhost:3030/sparql`) for external tools
- The named pipe for CLI attachment (`mercury -a mcp`)
- Pruning via Claude: when and why to ask Claude to prune

### 1.4 `CLAUDE.md` modification

Strengthen the MERCURY.md reference from advisory to directive. The current text:

> *See **MERCURY.md** for when, why, and how to use semantic memory — including EEE discipline, provenance conventions, and consolidation patterns.*

Must be supplemented with an explicit instruction in the session startup context: on first MCP interaction where `mercury_stats` reports zero quads, read and follow the bootstrap procedure in MERCURY.md. This converts a soft “see also” into a reliable instruction chain.

### 1.5 `MERCURY.md` modification

Two targeted improvements to the Bootstrap section:

1. **Replace the placeholder path** (`<file:///absolute/path/to/...>`) with a concrete resolution strategy: instruct the agent to determine the repo root via `pwd` (or by locating `CLAUDE.md`) and construct the absolute `file:///` URI. See the bootstrap chain fix in the Decision section above for the exact formulation.
1. **Resolve the mixed signal** between “Session Discipline” (*“If the store is empty, that’s fine”*) and “Bootstrap” (*“load the bootstrap”*). Make the relationship explicit: an empty store is a valid starting point, *and* loading bootstrap.ttl provides grounded context that makes the first session more productive. These are not contradictory — the first is permission, the second is recommendation.

### 1.6 `examples/Mercury.Examples/README.md`

Brief README for the examples project:

- What it contains (StorageExamples, TemporalExamples)
- How to run: `dotnet run --project examples/Mercury.Examples [storage|temporal|demo|all]`
- What each example demonstrates
- Link to API reference and temporal tutorial (Phase 2)

### 1.7 README.md updates

- Add “Quick Start” section after the introductory prose, before the documentation guide table
- Reference `docs/tutorials/getting-started.md`
- Reference `tools/install-tools.sh` / `tools/install-tools.ps1`
- Add examples project to documentation guide table

### Phase 1 acceptance criteria

- [x] A user with .NET 10 installed can follow getting-started.md from clone to running queries without consulting any other document
- [x] A user can configure Claude Code with Mercury MCP by following mercury-mcp.md
- [x] mercury-mcp.md documents all three bootstrap paths (Claude auto-load, manual via CLI attachment, standalone without Claude)
- [x] CLAUDE.md contains an explicit directive to follow MERCURY.md bootstrap procedure on empty MCP store
- [x] MERCURY.md bootstrap section uses a resolvable path (not a placeholder) and resolves the permission/recommendation ambiguity
- [x] All code examples in tutorials are verified runnable (manually, by the author executing each command/query during authoring)
- [x] All cross-references resolve to existing files
- [x] README.md quick-start section exists and links work

**Implementation note:** Phase 1 also included a code fix not originally in the ADR: `LoadExecutor` gained `file://` URI support and was wired into all update paths (CLI, MCP tools, MCP pipe sessions, HTTP server). Without this fix, `LOAD <file:///...>` — the bootstrap mechanism documented in the tutorials — would have failed silently everywhere.

-----

## Phase 2 — Tool Mastery and RDF Onboarding

**Goal:** Users can leverage all four tools effectively. Users new to RDF can build their first knowledge graph.

**Deliverables:**

### 2.1 `docs/tutorials/mercury-sparql-cli.md`

The batch/scripting tool for non-interactive workflows.

- Use cases: CI pipelines, data extraction, format conversion, scripting
- Load + query in one shot: `mercury-sparql --load data.ttl --query "SELECT ..."`
- Persistent stores for repeated work: `mercury-sparql --store ./mydb --load data.ttl`
- Output formats: `--format json|csv|tsv|xml` for SELECT; `--rdf-format nt|ttl|rdf|nq|trig` for CONSTRUCT
- Query plans: `--explain "SELECT ..."`
- Reading queries from files: `--query-file complex.rq`
- REPL mode: `--repl` with persistent store
- Scripting patterns: piping output, combining with shell tools, batch processing multiple files

### 2.2 `docs/tutorials/mercury-turtle-cli.md`

Validation, conversion, and benchmarking.

- Use cases: CI syntax validation, format migration, performance testing
- Validation: `mercury-turtle --validate input.ttl` — CI-friendly exit codes
- Format conversion: `mercury-turtle data.ttl --output data.nt` (extension detection) or explicit `--output-format`
- Statistics: `mercury-turtle --stats data.ttl` — triple count, predicate distribution
- Loading into persistent stores: `mercury-turtle --input data.ttl --store ./mydb`
- Benchmarking: `mercury-turtle --benchmark --count 100000`
- Conversion matrix: which formats convert to which, with examples

### 2.3 `docs/tutorials/your-first-knowledge-graph.md`

RDF for people who haven’t used it — the bridge tutorial.

- Starts with a relatable domain (e.g., a small team with projects, skills, and decisions)
- From spreadsheet thinking to triple thinking: why rows and columns aren’t enough when relationships matter
- Writing Turtle by hand: prefixes, URIs, literals, types
- Loading and querying: using `mercury-sparql` or the `mercury` REPL
- Named graphs: separating concerns (this project’s data vs. that project’s data)
- The “aha” moment: a SPARQL query that would be painful in SQL but natural in triples (e.g., transitive relationships, optional data, union across different schemas)
- Why this matters for Sky Omega: structured meaning, not just data storage

**Pedagogical note:** This tutorial follows the established approach — progressive refinement from simple analogies to structured understanding. Show, don’t tell. Let readers discover why triples work by experiencing the limitations of the alternative.

### 2.4 `docs/tutorials/installation-and-tools.md`

Comprehensive tool lifecycle management. Where getting-started.md (§1.1) covers the *happy path* — run the install script, verify versions — this tutorial covers the *full lifecycle*: manual installation, updates, uninstalls, platform differences, and port configuration.

- What `install-tools.sh` / `install-tools.ps1` does under the hood (build, pack, global install)
- Manual installation via `dotnet tool install`
- Updating tools after pulling new code
- Uninstalling: `dotnet tool uninstall -g SkyOmega.Mercury.Cli` etc.
- Store locations by platform (macOS, Linux/WSL, Windows) with actual paths
- Port conventions: 3030 (MCP), 3031 (CLI), and how to override with `--port`
- Verifying tool health: version checks, store accessibility

### Phase 2 acceptance criteria

- [ ] All four CLI tools have dedicated tutorials with runnable examples
- [ ] A user unfamiliar with RDF can build and query a knowledge graph by following the tutorial
- [ ] Scripting/CI patterns are documented with copy-pasteable examples
- [ ] All cross-references resolve

-----

## Phase 3 — Depth and Patterns

**Goal:** Users understand Mercury’s distinctive capabilities — temporal RDF, semantic braid, store maintenance — and can apply them.

**Deliverables:**

### 3.1 `docs/tutorials/temporal-rdf.md`

Mercury’s bitemporal capabilities, grounded in the existing examples project.

- Valid time vs. transaction time: what they are and why both matter
- Basic temporal triples: recording when facts were true
- Time-travel queries: “What did we know as of date X?”
- Version tracking: how entities change over time
- Snapshot reconstruction: rebuilding the state of knowledge at any point
- Bitemporal corrections: retroactively fixing what was recorded
- Practical use cases: employment history, configuration evolution, decision audit trails
- Links to `examples/Mercury.Examples/TemporalExamples.cs` for runnable code

### 3.2 `docs/tutorials/semantic-braid.md`

Using Mercury for structured conversation memory.

- The concept: temporal triples tracking (human input, LLM response, EEE phase)
- Why this matters: prevents illegitimate reasoning transitions, makes epistemic state queryable
- Concrete example: storing a multi-turn conversation as triples
- SPARQL queries over conversation history: “What decisions were made?”, “What assumptions were surfaced?”, “When did we transition from Emergence to Engineering?”
- Integration with MCP: how Claude can maintain its own semantic braid
- The alpha architecture note: this is the direction, implementation is evolving

### 3.3 `docs/tutorials/pruning-and-maintenance.md`

Store hygiene for long-lived Mercury instances.

- Why pruning matters: temporal stores accumulate history, stores grow
- The pruning model: primary/secondary transfer with atomic switch
- Always dry-run first: `--dry-run` in both CLI and MCP
- History modes:
  - `FlattenToCurrent` — collapse history, keep latest state
  - `PreserveVersions` — keep version boundaries but compact
  - `PreserveAll` — full history, just rewrite for compaction
- Filters: `--exclude-graph`, `--exclude-predicate` — protect important data
- CLI pruning: `:prune` in the REPL
- MCP pruning: `mercury_prune` tool via Claude
- Monitoring: `:stats` / `mercury_stats` to track growth
- Backup strategy: the store is just a directory — copy it

### 3.4 `docs/tutorials/federation-and-service.md`

Querying across Mercury instances.

- The architecture: CLI and MCP run as separate instances with separate stores
- SERVICE queries: `SERVICE <http://localhost:3030/sparql> { ... }` from CLI to query MCP store
- Auto-detection: CLI reports when MCP instance is detected
- Pipe attachment: `mercury -a mcp` for direct REPL access to MCP store
- The vision: personal → team → organizational instances with federated queries
- Practical workflow: Claude accumulates knowledge via MCP, developer inspects and curates via CLI

### Phase 3 acceptance criteria

- [ ] Temporal RDF tutorial includes runnable examples referencing the examples project
- [ ] Semantic braid is explained with concrete SPARQL, not just conceptual description
- [ ] Pruning tutorial covers both CLI and MCP surfaces
- [ ] Federation tutorial demonstrates cross-instance queries

-----

## Phase 4 — Developer Integration and Reference

**Goal:** Developers can embed Mercury in their own .NET projects. Knowledge directories are seeded with initial content.

**Deliverables:**

### 4.1 `docs/tutorials/embedding-mercury.md`

Using Mercury as a library in .NET projects.

- Adding Mercury to a .NET 10 project (package reference or project reference)
- QuadStore: creation, opening, closing, the pool pattern
- Writing triples: `AddCurrent`, temporal `Add`
- Reading triples: `QueryCurrent`, temporal queries
- SPARQL from code: `SparqlParser` → `QueryExecutor` pattern
- Content negotiation: reading and writing multiple RDF formats
- The zero-GC patterns: why they exist, how to work with `ReadOnlySpan<char>` and `BindingTable`
- Concurrency: `AcquireReadLock`/`ReleaseReadLock`, concurrent read patterns
- When to use the library vs. the tools

### 4.2 `docs/tutorials/running-benchmarks.md`

Performance validation and measurement.

- The benchmarks project: what it measures (storage, SPARQL, temporal, concurrent, parsers, SERVICE, filter pushdown)
- Running benchmarks: `dotnet run -c Release --project benchmarks/Mercury.Benchmarks`
- Interpreting results: what the numbers mean, what “good” looks like
- Useful for: contributors, evaluators, performance regression detection

### 4.3 `docs/knowledge/` — Initial content seeding

Populate the empty knowledge directories with curated content:

- **`vocabulary/core-predicates.ttl`** — Extract emerged predicates from `bootstrap.ttl` and actual usage. Document each predicate with `rdfs:comment`. This is reference material for new sessions.
- **`patterns/convergence.ttl`** — Curate from `docs/scratches/reasoning/patterns/convergence.ttl` with explanatory comments.
- **`patterns/curiosity-driven-exploration.ttl`** — Curate from `docs/scratches/reasoning/patterns/curiosity-pattern.ttl`.
- **`decisions/adr-summary.ttl`** — Key architectural decisions as queryable triples with rationale. Not a replacement for ADR Markdown — a queryable index.

**Note:** `docs/scratches/reasoning/patterns/` contains four additional pattern files (`evolving-tools.ttl`, `speculative-assertion.ttl`, `what-if.ttl`, `temperature-strategy.ttl`). Only `convergence` and `curiosity-driven-exploration` are selected for initial seeding because they represent the most stable, broadly applicable patterns. The others are candidates for future seeding once they mature through additional session usage.

Each file must follow the knowledge directory’s own README: curated, general, no raw session dumps, no duplication of what code expresses.

### Phase 4 acceptance criteria

- [ ] A developer can add Mercury to a new .NET 10 project by following the embedding tutorial
- [ ] Benchmark tutorial includes expected output and interpretation guidance
- [ ] At least one file exists in each knowledge subdirectory (vocabulary, patterns, decisions)
- [ ] Knowledge files are valid Turtle, loadable into Mercury

-----

## Phase 5 — Future (Deferred)

These tutorials are recognized as valuable but depend on components still maturing:

### 5.1 `docs/tutorials/solid-protocol.md`

Mercury.Solid is implemented ([ADR-015](mercury/ADR-015-solid-architecture.md)) but not yet production-validated. Tutorial deferred until Solid integration is exercised in real workflows. Would cover: Pod concepts, resource CRUD, N3 Patch, access control, and Mercury’s graph-to-pod mapping.

### 5.2 `docs/tutorials/minerva-local-inference.md`

Minerva is at skeleton stage ([ADR-001](minerva/ADR-001-weight-formats.md) approved). Tutorial deferred until weight loading and tokenization are functional. Would cover: GGUF/SafeTensors weight loading, native tokenizers, Metal/CUDA acceleration via P/Invoke.

### 5.3 `docs/tutorials/eee-for-teams.md`

The existing EEE tutorial (`learning-to-navigate-eee.md`) is individual-focused. A team-oriented tutorial covering EEE in collaborative workflows, VGR workshop patterns, and organizational instance usage is valuable but depends on workshop experience. Deferred until post-workshop lessons are available.

-----

## Consequences

### Positive

- Sky Omega becomes approachable to its target audience: developers who need AI-assisted development infrastructure but haven’t used RDF
- The “missing interface layer” thesis extends to documentation — LLMs can help *use* Mercury, but humans need a readable path to understand *why*
- Claude Code can implement tutorials phase by phase with clear acceptance criteria and file-level deliverables
- Knowledge directories transition from aspirational to concrete, proving the curation flow described in their README
- Each phase is independently valuable — if only Phase 1 ships, the project is already significantly more accessible

### Negative

- Documentation maintenance burden: tutorials must be updated when tool behavior changes. Mitigated by adding tutorial review as a responsibility in CONTRIBUTING.md — changes to CLI flags, REPL commands, or MCP tool signatures should trigger a check of affected tutorials.
- Risk of tutorials drifting from implementation: mitigated by including verifiable commands and by treating tutorials as first-class artifacts (changes to tool behavior should trigger tutorial review)
- Phase 4 knowledge seeding requires epistemic judgment about what has stabilized enough to share — premature extraction violates the knowledge directory’s own principles

### Invariants

- Tutorials never duplicate API reference — they link to it
- Tutorials use runnable examples, not pseudocode
- All file paths in tutorials use the conventions established by `MercuryPaths` and [ADR-019](mercury/ADR-019-global-tool-packaging.md)
- Prerequisites state .NET 10 (LTS) explicitly
- `bootstrap.ttl` is primarily for MCP stores; CLI stores do not auto-load it
- The bootstrap chain (CLAUDE.md → MERCURY.md → LOAD) must be a directive, not a suggestion — soft references between operational documents produce unreliable behavior
- Every bootstrap scenario must have a human-readable path: automated via Claude, manual via CLI/HTTP, and standalone without any LLM
- The pedagogical approach — progressive refinement, show don’t tell, let readers discover — applies to all tutorials, not just the EEE one

## Files Created

### Phase 1

|File                                 |Action                                                         |
|-------------------------------------|---------------------------------------------------------------|
|`docs/tutorials/getting-started.md`  |Created                                                        |
|`docs/tutorials/mercury-cli.md`      |Created                                                        |
|`docs/tutorials/mercury-mcp.md`      |Created                                                        |
|`CLAUDE.md`                          |Modified (bootstrap directive strengthened)                    |
|`MERCURY.md`                         |Modified (resolvable path, permission/recommendation clarified)|
|`examples/Mercury.Examples/README.md`|Created                                                        |
|`README.md`                          |Modified (Quick Start section)                                 |

### Phase 2

|File                                          |Action |
|----------------------------------------------|-------|
|`docs/tutorials/mercury-sparql-cli.md`        |Created|
|`docs/tutorials/mercury-turtle-cli.md`        |Created|
|`docs/tutorials/your-first-knowledge-graph.md`|Created|
|`docs/tutorials/installation-and-tools.md`    |Created|

### Phase 3

|File                                       |Action |
|-------------------------------------------|-------|
|`docs/tutorials/temporal-rdf.md`           |Created|
|`docs/tutorials/semantic-braid.md`         |Created|
|`docs/tutorials/pruning-and-maintenance.md`|Created|
|`docs/tutorials/federation-and-service.md` |Created|

### Phase 4

|File                                                      |Action |
|----------------------------------------------------------|-------|
|`docs/tutorials/embedding-mercury.md`                     |Created|
|`docs/tutorials/running-benchmarks.md`                    |Created|
|`docs/knowledge/vocabulary/core-predicates.ttl`           |Created|
|`docs/knowledge/patterns/convergence.ttl`                 |Created|
|`docs/knowledge/patterns/curiosity-driven-exploration.ttl`|Created|
|`docs/knowledge/decisions/adr-summary.ttl`                |Created|

## ADR Index Update

This ADR should be added to `docs/adrs/README.md`:

```markdown
| [ADR-002](ADR-002-documentation-and-tutorial-strategy.md) | Documentation and Tutorial Strategy | Proposed |
```