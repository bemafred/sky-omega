# ADR-001: LLM Pool Architecture

**Component:** Sky (language interaction — pooled LLM dispatch)
**Status:** Proposed — 2026-03-08

## Summary

Sky provides language capabilities to Sky Omega through a pooled architecture of local and remote LLM backends. She abstracts over provider differences, routes requests based on task characteristics and epistemic state, and treats all LLMs — cloud APIs, local engines, Minerva — as replaceable substrates behind a uniform dispatch interface. Sky is the mouth and ears of the system; she does not decide what to say or when.

## Context

From the canonical definition: Sky is responsible for language generation and interpretation, conversational continuity, and prompt construction and response decoding. She executes under the direction of James, using context supplied through the Semantic Braid.

The LLM landscape (February 2026, documented in `docs/architecture/technical/llm-lanscape-map.md`) reveals a critical architectural insight: the OpenAI chat/completions format has become the de facto wire protocol. Nearly every provider and local engine supports it — Ollama, vLLM, SGLang, LM Studio, llama.cpp, Together.ai, Groq, Fireworks.ai. Anthropic uses a different Messages API but is reachable through a thin translation layer.

This means Sky's pool architecture can target a single wire protocol with adapters for the exceptions, rather than abstracting across fundamentally different APIs.

### What Sky Is Not

- Not a cognitive decision-maker — James decides what to ask and when
- Not a memory system — Lucy handles persistence
- Not an epistemic authority — Sky generates language; James validates reasoning
- Not a model trainer or fine-tuner — Sky consumes models, not creates them

### The Pool Concept

Sky Omega needs multiple LLM backends simultaneously:

- **Remote APIs** for frontier reasoning (Claude, GPT, DeepSeek)
- **Local engines** for sovereignty, latency, and cost (Ollama, vLLM, future Minerva)
- **Specialized models** for specific tasks (Nordic language models, code-focused models)
- **Graceful degradation** — if a remote API is unavailable, route to local; if GPU is absent, fall back to CPU

James's epistemic state machine (ADR-001) adds another dimension: different EEE phases benefit from different model characteristics. Emergence benefits from creative, exploratory models. Engineering benefits from precise, instruction-following models.

## Decision

### 1) Pool as the primary abstraction

Sky SHALL organize LLM backends into a pool with typed members:

```
Pool
├── Local
│   ├── General (Ollama: Mistral, Phi, Llama)
│   ├── Code (DeepSeek-Coder, StarCoder)
│   ├── Nordic (GPT-SW3, Viking)
│   └── Embedding (future: semantic similarity)
├── Remote
│   ├── OpenAI (GPT family)
│   ├── Anthropic (Claude family)
│   ├── DeepSeek (V3.2 API)
│   └── Open-model hosts (Groq, Together, Fireworks)
└── Minerva (future: sovereign local inference)
```

Pool membership is configured, not hardcoded. Members can be added, removed, or reconfigured without code changes.

### 2) OpenAI chat/completions as primary wire protocol

Sky SHALL use the OpenAI chat/completions format as the canonical wire protocol for pool communication. This covers ~95% of backends natively. Exceptions (Anthropic Messages API) are handled by thin adapter layers that translate at the boundary.

For .NET implementation: a minimal HTTP client using `System.Net.Http` and `System.Text.Json` speaks OpenAI format natively. No SDK dependency required for the core path. Optional SDK usage (OpenAI .NET SDK, Microsoft.Extensions.AI) is a convenience, not a requirement.

### 3) Dispatch strategies — James-directed routing

Sky SHALL accept routing directives from James that select pool members based on task characteristics:

| Dimension | Routing Signal | Example |
|-----------|---------------|---------|
| EEE phase | Emergence → creative model; Engineering → precise model | Emergence: Claude/GPT; Engineering: DeepSeek-Coder |
| Language | Swedish content → Nordic-specialized model | Text generation in Swedish → Viking/GPT-SW3 |
| Task type | Code generation → code model; Reasoning → frontier model | Code: DeepSeek-Coder; Reasoning: Claude |
| Sovereignty | Sensitive content → local only | Medical/personal data → Ollama/Minerva, never remote |
| Latency | Interactive → fastest available; Batch → cheapest | Quick response: local small model; Deep analysis: remote frontier |
| Fallback | Primary unavailable → next best alternative | Remote down → local; GPU absent → CPU |

The routing strategy is a first-class concept — configurable, inspectable, and itself an epistemic artifact storable in Lucy.

### 4) Uniform response model

Sky SHALL normalize all backend responses into a uniform model before returning to James:

- Text content (the generated language)
- Token usage (prompt + completion, for cost tracking)
- Model identification (which pool member responded)
- Latency metrics (for routing optimization)
- Streaming support (normalized SSE across providers)

James and Lucy never see provider-specific response formats.

### 5) Context management per pool member

Each pool member has different capabilities (context window size, token limits, supported features). Sky SHALL track these per member and respect them when constructing requests:

- Context window limits → Sky truncates/summarizes the braid to fit
- Feature support (tool use, vision, structured output) → Sky routes only to capable members
- Rate limits → Sky manages backpressure and queuing

### 6) BCL-only for core; SDK usage optional

Sky's core dispatch logic SHALL be BCL-only (`System.Net.Http`, `System.Text.Json`). This maintains Mercury's zero-dependency principle at the substrate level. SDK packages (OpenAI .NET SDK, MEAI `IChatClient`) MAY be used in higher-level convenience layers but are not required for pool operation.

### 7) Minerva as a future pool member

When Minerva matures from POC to implementation, she joins the pool as a local backend — sovereign inference with no network dependency. Sky's pool architecture SHALL accommodate this from day one: Minerva is just another pool member with a local transport instead of HTTP.

## Consequences

### What This Enables

- **Model-agnostic orchestration** — James directs reasoning without knowing which LLM executes it
- **Graceful degradation** — sovereignty and availability through fallback chains
- **Task-appropriate routing** — Nordic models for Swedish, code models for code, frontier for reasoning
- **Cost and latency awareness** — metrics per pool member inform routing optimization
- **Minerva integration path** — local sovereign inference slots into the same abstraction

### What Requires Experimentation

- Routing heuristic tuning — which model characteristics matter most per EEE phase
- Braid truncation strategies — how to fit long context into small-window models
- Multi-model synthesis — when to query multiple pool members and merge results
- Streaming normalization — aligning different provider streaming formats
- Dispatch strategy configuration format — how James expresses routing preferences
- Cost tracking granularity — per-request vs. per-session vs. per-task

### Follow-up ADRs

**Sky ADR-002: Wire Protocol and Adapters** — Detailed specification of the OpenAI chat/completions canonical format, Anthropic adapter, streaming normalization, error handling.

**Sky ADR-003: Dispatch Strategy Model** — How routing strategies are expressed, configured, and stored. Interaction with James's epistemic state for phase-aware routing.

## References

- `docs/architecture/concepts/canonical/sky.md` — Canonical definition
- `docs/architecture/technical/llm-lanscape-map.md` — LLM ecosystem reconnaissance
- `docs/architecture/concepts/canonical/semantic-braid.md` — Working context for LLM interaction
- `docs/adrs/James/ADR-001-epistemic-state-machine.md` — Epistemic state-aware dispatch
