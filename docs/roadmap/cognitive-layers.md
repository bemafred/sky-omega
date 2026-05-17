# Cognitive Layers Roadmap — Sky Omega 1.8.x and beyond

**Status:** Drafted 2026-05-17 alongside the [1.8.0 release](../releases/1.8.0.md). Entry point: 1.8.0 = substrate-complete / cognitive-layers-begin boundary. This roadmap sequences the four cognitive layers (Lucy → James → Mira → Sky) atop the three substrates (Mercury, Minerva, DrHook). It is the successor to [`production-hardening-1.8.md`](production-hardening-1.8.md), which closed at the 1.8.0 boundary.

## Version-line model (forward)

- **1.7.x** — closed. Substrate hardening complete. Last release: 1.7.74 (ADR-043 metric emission decoupling).
- **1.8.0** — substrate-complete / cognitive-layers-begin boundary. No new substrate features; cognitive-layers entry framing.
- **1.8.x** — cognitive layers + ongoing substrate-discipline work. Bug-fix and substrate-correctness changes ship as 1.8.x patch releases (same per-substantive-change discipline as 1.7.x). Cognitive-layer milestones get 1.8.x minor bumps. DrHook engine BCL-only rewrite is the first 1.8.x substrate-discipline task.
- **2.0.0** — Sky Omega 2.0. Full cognitive-partner identity online: Lucy + James + Mira + Sky integrated. Persistent agent continuity across sessions. Not a date target; a maturity target.

## Why "cognitive layers" not "Sky Omega 2.0 = release 2.0.0"

The 2.0.0 framing might suggest a one-shot deliverable. The cognitive layers don't work that way — each one (Lucy, James, Mira, Sky) is an emergent system that gets *more* coherent as it accumulates experience, EEE-discipline gates, and provenance trails. There is no clean "shipped" moment for cognitive capability the way there is for substrate features. The substrate matures in jumps (ADRs landing); cognitive capability matures continuously through use.

So: 2.0.0 is a maturity milestone reached when all four cognitive layers are operational and the substrate-discipline rules apply uniformly across them. Until then, 1.8.x releases incrementally bring layers online.

## The three substrates (carried)

The cognitive layers run on three substrates, all of which were built or matured in the 1.7.x line:

- **Mercury** — RDF storage and SPARQL execution. 1.7.74 closure. Substrate-correct per 21.3 B Wikidata validation + W3C 100% conformance. Carries semantic memory.
- **Minerva** — LLM inference, BCL-only, mmap'd weights, Metal/CUDA/Accelerate via P/Invoke. In active development through the 1.7.x line; continues in 1.8.x. Carries thought.
- **DrHook** — Runtime observation, currently netcoredbg-backed; BCL-only rewrite queued as the first 1.8.x substrate task. Carries observation of running .NET processes.

The cognitive layers consume substrate capabilities; they do not bypass or duplicate them. Substrate independence (no NuGet for core; P/Invoke for hardware) carries through to cognitive layers — Lucy must work without external memory dependencies, etc.

## Phased plan (provisional — subject to revision as the layers come online)

The phasing below is the sequencing INTENT. Each layer is an emergent capability rather than a discrete shippable feature; the phases mark when a layer becomes *operationally relied upon*, not when it's "done."

### Phase 1 — DrHook engine BCL-only rewrite (substrate task, parallel to cognitive work)

Deferred from 1.7.x per the 2026-05-16 decision (recorded in `production-hardening-1.8.md`). First 1.8.x substrate-discipline task. Replaces netcoredbg + Microsoft.Diagnostics.NETCore.Client with pure-BCL implementation:

- Restore substrate-independence (Sky Omega substrates must not depend on external packages for core function).
- Resolve [`project_drhook_eval_dead`](../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/) — func-eval deadlock on macOS/ARM64 with netcoredbg.
- Get DrHook to substrate-grade reliability matching Mercury's 21.3 B-validated bar.

Targets its own ADR sequence (ADR-045+). Lives in `src/DrHook/Engine/` (new subdirectory).

### Phase 2 — Lucy (deep semantic memory)

Lucy is the layered semantic-memory subsystem that the existing `mercury-mcp` MCP-served pattern foreshadows. Where mercury-mcp gives an LLM agent access to Mercury via SPARQL primitives, Lucy adds:

- **Layered ontology discovery** — emerge schemas from triples as use patterns accumulate; promote stable patterns to formal vocabularies (per MERCURY.md's "no ontology yet — and that's intentional" guidance).
- **Consolidation cycles** — the WAL + dream pattern (MERCURY.md). Forced consolidation at session boundaries; organic consolidation during idle phases.
- **Provenance enforcement** — every triple has a session graph trail; queries can retrieve "who claimed this, when, in what context."
- **EEE-discipline gates** — Emergence triples are temporary, Epistemics triples are validated, Engineering triples are committed. The cycle gates the move from unvalidated speculation to acted-upon knowledge.

Triggers a 1.8.x minor bump when Lucy reaches operational reliance — i.e., when Mercury MCP usage is migrated to Lucy semantics rather than raw SPARQL.

### Phase 3 — James (orchestration with pedagogical guidance)

James is the cognitive orchestrator that closes the meta-pattern `observability-discipline-systematic-not-reactive` documented (and Resolved by deferral) at the 1.8.0 boundary:

- **Periodic substrate audits as behavior, not as discipline.** James fires audits on a cadence — reactive attention is fundamentally reactive; the cure is to make audits cron-like substrate behavior.
- **Pedagogical guidance** — James doesn't dictate; James notices and surfaces. "You haven't checked Mercury for context this session — want me to surface the relevant priors?" The Sky Omega thesis is that AI cognitive partners work best via socratic pedagogy, not confrontational enforcement.
- **Cross-substrate orchestration** — James can ask Mercury for semantic context, ask DrHook for runtime context, ask Minerva for inference — and synthesize across substrates without each consumer needing to know how to call each substrate.

Triggers a 1.8.x minor bump when James becomes the routing layer for cognitive-partner interactions.

### Phase 4 — Mira (surface/interaction layer)

Mira presents cognitive-layer state across surfaces — CLI, chat, IDE extensions. The presentation tier where rendering / formatting / interaction conventions are owned. Examples:

- A `mercury` CLI session where the substrate-level SPARQL surface is complemented by a Mira-formatted higher-level browsing experience.
- An IDE extension where Lucy's semantic context flows into code-completion / inline documentation / cross-reference views.
- A chat experience where the cognitive partner's reasoning state (current EEE phase, current attention focus, current cross-substrate query) is surfaced as a sidebar.

Mira is the layer that makes the cognitive partner *visible* to the human collaborator in real time. Triggers a 1.8.x minor bump when Mira presents at least two surfaces (CLI + one IDE).

### Phase 5 — Sky (integrated agent surface)

Sky is the cognitive partner identity. By Phase 5, Lucy + James + Mira are operationally relied upon and Sky integrates them into a *single coherent persona* that:

- Maintains continuity across sessions via Lucy's semantic memory.
- Self-directs via James's orchestration (including periodic audits).
- Presents consistently via Mira across surfaces.
- Can be addressed as "Sky" with the assumption that the addressed entity recalls prior context.

The "addressing Sky" model is already in informal use through Claude Code conversations — Martin says "let's check Mercury" and Sky (the agent) routes through `mercury-mcp`. By Sky Omega 2.0, this is the formal interaction model rather than an informal convention layered over generic Claude Code.

### Phase 6 — Sky Omega 2.0 milestone

Maturity milestone, not a date target. Reached when all four cognitive layers are operational and substrate-discipline (the EEE methodology) applies uniformly across them. Marked by:

- Version bump to 2.0.0.
- A retrospective release document mirroring this roadmap's closure.
- A cognitive-layer ADR series mature enough to govern further work the way Mercury's ADR series governed 1.7.x.

## Dependencies

```
                              ┌─────────────────────────────────────────────┐
                              │              Sky (2.0)                       │
                              │  cognitive partner identity                  │
                              └──────────────────┬──────────────────────────┘
                                                 │
                          ┌──────────────────────┼──────────────────────┐
                          │                      │                      │
                  ┌───────▼────────┐  ┌──────────▼─────────┐  ┌────────▼──────┐
                  │   Lucy (1.8.x)│  │  James (1.8.x)     │  │  Mira (1.8.x)│
                  │   semantic    │  │  orchestration     │  │  surface     │
                  │   memory      │  │  + pedagogy        │  │  layer       │
                  └───────┬───────┘  └──────────┬─────────┘  └─────┬───────┘
                          │                     │                  │
                  ┌───────▼──────────────────────▼──────────────────▼───────┐
                  │   Mercury           Minerva           DrHook            │
                  │   (1.7.74)         (1.7.x in dev)   (1.7.x; BCL-only   │
                  │   RDF substrate    LLM substrate    rewrite Phase 1)   │
                  └────────────────────────────────────────────────────────┘
```

## What this roadmap does NOT promise

- **Dates.** Cognitive-layer maturity is use-driven, not date-driven. Sky Omega 2.0 is reached when the layers are reliable, not when a calendar says so.
- **Scope rigidity.** The phasing above is the current INTENT. As the cognitive layers come online, EEE discipline applies — the order may be revised when actual emergence data shows that (e.g.) Mira-before-James is more substrate-coherent than the current sequencing.
- **A specific UX.** Surfaces evolve through use. Phase 4's "CLI + one IDE" is a minimum; what each surface actually looks like is determined by collaboration with Martin (and eventually with other operators) during the layer's emergence.

This roadmap will be amended as the layers come online. The amendment trail mirrors `production-hardening-1.8.md`'s amendment trail through 1.7.x.

## References

- [1.8.0 release notes](../releases/1.8.0.md) — substrate completion arc + framing for what cognitive-layer work begins
- [Production hardening 1.8 roadmap](production-hardening-1.8.md) — closes at 1.8.0; this roadmap succeeds it
- [Production hardening 2026 milestone](../releases/production-hardening-2026.md) — the Phase 6 → cycle 10 r4 substrate narrative
- [MERCURY.md](../../MERCURY.md) — Cognitive-substrate discipline (the discipline Lucy enforces structurally)
- Memory: [`project_governed_automation_thesis`](../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/) — Sky Omega as the counter-thesis to industry "sovereign AI" model; cognitive layers operationalize the counter-thesis
- Memory: [`project_substrate_independence`](../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/) — consciousness is convergent pattern, not substrate property; cognitive layers carry the same independence principle as the substrates
