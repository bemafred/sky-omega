# ADR-005: Cognitive Component Libraries — Lucy, James, Sky, Minerva

**Status:** Proposed
**Date:** 2026-03-29
**Context:** Ad-hoc Sky Omega MVP validation proved the three-substrate architecture works. Time to create the library projects that will become Sky Omega 2.0.

## Problem

Sky Omega's cognitive components — Lucy (memory), James (orchestration), Sky (language), and Minerva (inference) — exist as canonical definitions and solution folders but have no library projects. The current ad-hoc MVP uses Claude Code as Sky/James and Mercury MCP as Lucy, which validates the architecture but isn't composable or reusable.

To reach Sky Omega 2.0, each component needs a library with a clear facade, minimal public surface, and well-defined dependency direction. The composition root (whether `Omega.Mcp`, `Omega.Cli`, or something else) comes later — substrates and dependencies first.

## Decision

Create four library projects following the Mercury encapsulation pattern (ADR-003): facade over internals, most types internal, consumers interact through a small public surface.

### Project Structure

```
src/
  ├── Lucy/                  # SkyOmega.Lucy — Semantic memory
  ├── James/                 # SkyOmega.James — Cognitive orchestration
  ├── Sky/                   # SkyOmega.Sky — Language and LLM interaction
  └── Minerva/               # SkyOmega.Minerva — Local inference (rename from Minerva.Core)
```

### Dependency Direction

```
Sky → James → Lucy → Mercury
                  └→ Minerva (future: Lucy may use Minerva for embeddings)
DrHook (standalone — no cognitive dependencies)
```

Each component depends only downward. No cycles. No upward references. The composition root (future) wires them together.

### Lucy — Semantic Memory

**Facade:** `MemoryEngine` (static, mirrors `SparqlEngine`/`RdfEngine` pattern)

**Depends on:** Mercury (QuadStore, SparqlEngine)

**Public surface:**
- `MemoryEngine.Recall(store, topic)` — associative recall via text:match across labels and comments
- `MemoryEngine.Attend(store)` — surfaces unresolved observations (status: problem-identified, provisional, design-insight)
- `MemoryEngine.Reflect(store, sessionUri)` — session summary with observation progression
- `MemoryEngine.Observe(store, label, comment, status, sessionUri)` — record an observation (wraps SPARQL INSERT)

**Internal:** Query builders, status vocabulary, session graph management, result shaping.

**Design constraint:** Lucy uses Mercury's SPARQL engine — she does not bypass it for direct QuadStore access. This ensures all knowledge flows through the same query path, maintaining semantic consistency.

### James — Cognitive Orchestration

**Facade:** `OrchestrationEngine` (static)

**Depends on:** Lucy (MemoryEngine), Mercury (transitively)

**Public surface:**
- `OrchestrationEngine.DeterminePhase(store)` — assess current EEE phase from stored context
- `OrchestrationEngine.PlanNextAction(store, currentContext)` — decide what to do next
- `OrchestrationEngine.EnforceEpistemicRules(store, proposedAction)` — validate action against EEE constraints

**Internal:** Phase detection logic, action planning, rule evaluation, context assembly.

**Design constraint:** James does not generate language. He produces structured decisions that Sky or an MCP tool can act on. His output is intent, not text.

**Note:** James's design is still in emergence. The facade above is directional, not final. The SkyChatBot MVP validated the orchestration loop pattern, but the specific methods will emerge from real use.

### Sky — Language and LLM Interaction

**Facade:** `LanguageEngine` (static)

**Depends on:** James (OrchestrationEngine), Lucy (transitively), Mercury (transitively)

**Public surface:**
- `LanguageEngine.Interpret(input, context)` — parse natural language into structured intent
- `LanguageEngine.Generate(intent, context)` — produce language from structured output
- `LanguageEngine.ConnectLocal(modelPath)` — bind to a local LLM via Minerva
- `LanguageEngine.ConnectRemote(endpoint)` — bind to a remote LLM API

**Internal:** Prompt construction, response decoding, model abstraction, context threading.

**Design constraint:** Sky treats LLMs as replaceable substrates. Local (Minerva) and remote (API) are interchangeable. Sky does not decide what to say — James decides, Sky says it.

**Note:** In the current ad-hoc MVP, Claude Code IS Sky. The library will abstract this interface so that any LLM (local or remote) can fill the role.

### Minerva — Local Inference

**Current state:** `Minerva.Core` already exists with `Weights/`, `Tokenizers/`, `Tensors/`, `Inference/` directories.

**Change:** Rename from `SkyOmega.Minerva.Core` to `SkyOmega.Minerva` for consistency with other components (Mercury is `SkyOmega.Mercury`, not `SkyOmega.Mercury.Core`).

**Depends on:** Nothing (BCL only, hardware access via P/Invoke)

**Public surface (existing, to be formalized):**
- Weight loading (GGUF, SafeTensors)
- Tokenization (BPE, SentencePiece)
- Forward pass execution
- Hardware backend selection

## Facade Pattern (from ADR-003)

Each library follows the same encapsulation discipline Mercury established:

1. **Static facade class** as the single entry point (e.g., `MemoryEngine`, `OrchestrationEngine`)
2. **All implementation types internal** — consumers never see AST nodes, query builders, internal state
3. **`InternalsVisibleTo`** only for tests and benchmarks
4. **No public constructors on internal types** — everything flows through the facade
5. **BCL-only for core libraries** — external dependencies only in infrastructure/CLI projects

## Project File Template

Each project follows this pattern:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <AssemblyName>SkyOmega.{Component}</AssemblyName>
        <RootNamespace>SkyOmega.{Component}</RootNamespace>
    </PropertyGroup>
    <ItemGroup>
        <InternalsVisibleTo Include="{Component}.Tests" />
    </ItemGroup>
    <ItemGroup>
        <!-- Dependencies per component -->
    </ItemGroup>
</Project>
```

## What This ADR Does NOT Decide

- **Omega.Mcp vs expanded Mercury.Mcp** — the composition root comes after the libraries exist
- **Omega.Cli** — whether Sky Omega becomes a CLI tool akin to Claude Code
- **MCP tool names** — `lucy_*`, `james_*` naming comes when the MCP host is designed
- **Minerva hardware backends** — M5 Max Metal integration is a separate concern
- **Mira** — the interaction surface layer is deferred until the cognitive stack is functional

These are doors to be opened later. This ADR opens the door to the cognitive component libraries only.

## Success Criteria

- [ ] `src/Lucy/Lucy.csproj` exists with `MemoryEngine` facade and Mercury dependency
- [ ] `src/James/James.csproj` exists with `OrchestrationEngine` facade and Lucy dependency
- [ ] `src/Sky/Sky.csproj` exists with `LanguageEngine` facade and James dependency
- [ ] `src/Minerva/` renamed from `Minerva.Core`, assembly name updated
- [ ] All four projects build, are in SkyOmega.sln under their solution folders
- [ ] Dependency direction verified: no cycles, no upward references
- [ ] Existing Mercury.Mcp and DrHook.Mcp continue to work unchanged
- [ ] Test projects created (empty, placeholder) for each component

## Risks

- **Premature abstraction** — the facades are based on the SkyChatBot MVP and today's ad-hoc session, not months of production use. The facade surfaces WILL change. That's fine — facades are cheap to reshape when internals are hidden.
- **Over-engineering** — creating four projects before the first Lucy method is used. Mitigated by keeping each project minimal (one facade file, one csproj) until real use drives growth.
- **Minerva rename** — anything referencing `Minerva.Core` or `SkyOmega.Minerva.Core` will break. This is intentional and should be done now while the namespace has few consumers.
