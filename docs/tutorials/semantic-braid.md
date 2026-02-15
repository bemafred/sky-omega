# Semantic Braid -- Structured Conversation Memory

The Semantic Braid is the interwoven structure of human input, LLM responses,
tool outputs, and orchestration signals that forms the active context of an
interaction. By storing this structure as temporal triples in Mercury, you
make conversational context queryable, auditable, and persistent.

> **Prerequisites:** Familiarity with Mercury triples and SPARQL. See
> [Your First Knowledge Graph](your-first-knowledge-graph.md) for RDF basics
> and [Temporal RDF](temporal-rdf.md) for time-bound facts.

---

## What the Semantic Braid Is

Every conversation between a human and an LLM produces a stream of exchanges:
questions, answers, tool calls, decisions, corrections. Most systems treat this
as a flat log -- a scrolling buffer that gets truncated when the context window
fills up.

The Semantic Braid treats this stream as structured data. Each exchange is a
triple (or set of triples) with:

- **Who** said it (human, LLM, tool, orchestrator)
- **What** was said (the content, a decision, an observation)
- **When** it happened (valid time for temporal queries)
- **What phase** it belongs to (Emergence, Epistemics, Engineering)

This makes the conversation queryable: "What decisions were made?" "What
assumptions were surfaced?" "When did we move from exploration to
implementation?"

---

## The Braid vs. Long-Term Memory

The Semantic Braid is **not** long-term memory. It is working context --
the active, evolving structure that an LLM sees during interaction.

| Concern | Semantic Braid | Mercury Store (Lucy) |
|---------|---------------|---------------------|
| Lifetime | Ephemeral (one session) | Persistent (survives restarts) |
| Purpose | Working context for the LLM | Durable semantic memory |
| Content | Raw exchanges, in-flight reasoning | Curated, consolidated facts |
| Pruning | Continuous (fits context window) | Periodic (store maintenance) |

The braid is where meaning emerges. Lucy is where meaning is preserved. James
governs the flow between them: what enters the braid, what leaves it, and
what gets consolidated into durable memory.

---

## Modeling Conversations as Triples

### A vocabulary for exchanges

Define a small vocabulary for conversation structure:

```turtle
@prefix braid: <http://sky-omega.org/braid/> .
@prefix xsd: <http://www.w3.org/2001/XMLSchema#> .

# Exchange types
braid:HumanInput    a braid:ExchangeType .
braid:LlmResponse   a braid:ExchangeType .
braid:ToolOutput     a braid:ExchangeType .
braid:Orchestration  a braid:ExchangeType .

# EEE phases
braid:Emergence   a braid:Phase .
braid:Epistemics  a braid:Phase .
braid:Engineering a braid:Phase .
```

### Recording an exchange

Each exchange becomes a set of triples about a unique exchange node:

```sparql
INSERT DATA {
    <urn:exchange:001> a braid:HumanInput ;
        braid:content "How should we handle authentication?" ;
        braid:timestamp "2025-09-15T10:30:00Z"^^xsd:dateTime ;
        braid:phase braid:Emergence ;
        braid:session <urn:session:2025-09-15> .
}
```

```sparql
INSERT DATA {
    <urn:exchange:002> a braid:LlmResponse ;
        braid:content "There are three main approaches: session tokens, JWT, and OAuth2..." ;
        braid:timestamp "2025-09-15T10:30:05Z"^^xsd:dateTime ;
        braid:phase braid:Emergence ;
        braid:inResponseTo <urn:exchange:001> ;
        braid:session <urn:session:2025-09-15> .
}
```

### Recording a decision

When the conversation produces a decision, record it as a distinct fact:

```sparql
INSERT DATA {
    <urn:decision:001> a braid:Decision ;
        braid:title "Use JWT for API authentication" ;
        braid:rationale "Stateless, works with microservices, no session store needed" ;
        braid:decidedDuring <urn:exchange:005> ;
        braid:phase braid:Epistemics ;
        braid:timestamp "2025-09-15T10:45:00Z"^^xsd:dateTime ;
        braid:session <urn:session:2025-09-15> .
}
```

### Recording an assumption

Assumptions surfaced during conversation are valuable to track:

```sparql
INSERT DATA {
    <urn:assumption:001> a braid:Assumption ;
        braid:content "All API clients can handle token refresh" ;
        braid:surfacedDuring <urn:exchange:003> ;
        braid:status "untested" ;
        braid:phase braid:Epistemics ;
        braid:session <urn:session:2025-09-15> .
}
```

---

## Querying the Braid

### What decisions were made?

```sparql
SELECT ?title ?rationale WHERE {
    ?d a braid:Decision ;
       braid:title ?title ;
       braid:rationale ?rationale .
}
```

### What assumptions remain untested?

```sparql
SELECT ?content WHERE {
    ?a a braid:Assumption ;
       braid:content ?content ;
       braid:status "untested" .
}
```

### What happened during the Emergence phase?

```sparql
SELECT ?type ?content WHERE {
    ?exchange a ?type ;
              braid:content ?content ;
              braid:phase braid:Emergence .
    FILTER(?type != braid:ExchangeType)
}
ORDER BY ?exchange
```

### When did we transition from Emergence to Engineering?

```sparql
SELECT ?phase (MIN(?ts) AS ?started) WHERE {
    ?exchange braid:phase ?phase ;
              braid:timestamp ?ts .
}
GROUP BY ?phase
ORDER BY ?started
```

This shows when each phase began, revealing the flow of the conversation
from open exploration to structured reasoning to implementation.

### What did a specific session produce?

```sparql
SELECT ?type ?title WHERE {
    {
        ?item a braid:Decision ;
              braid:title ?title ;
              braid:session <urn:session:2025-09-15> .
        BIND("Decision" AS ?type)
    } UNION {
        ?item a braid:Assumption ;
              braid:content ?title ;
              braid:session <urn:session:2025-09-15> .
        BIND("Assumption" AS ?type)
    }
}
```

---

## Integration with MCP

When Mercury runs as an MCP server, Claude can maintain a semantic braid
automatically. The pattern is:

1. **During conversation:** Claude stores significant exchanges, decisions,
   and assumptions as triples via `mercury_update`
2. **Between sessions:** The triples persist in the MCP store
3. **In future sessions:** Claude queries past decisions and assumptions via
   `mercury_query` to inform new conversations

### Example MCP workflow

Claude might store a decision like this:

```sparql
INSERT DATA {
    GRAPH <urn:braid:session-2025-09-15> {
        <urn:decision:jwt-auth> a <http://sky-omega.org/braid/Decision> ;
            <http://sky-omega.org/braid/title> "Use JWT for API authentication" ;
            <http://sky-omega.org/braid/rationale> "Stateless, no session store" ;
            <http://sky-omega.org/braid/timestamp> "2025-09-15T10:45:00Z"^^<http://www.w3.org/2001/XMLSchema#dateTime> .
    }
}
```

In a later session, Claude can query what decisions exist:

```sparql
SELECT ?title ?rationale WHERE {
    GRAPH ?session {
        ?d a <http://sky-omega.org/braid/Decision> ;
           <http://sky-omega.org/braid/title> ?title ;
           <http://sky-omega.org/braid/rationale> ?rationale .
    }
}
```

Using named graphs per session keeps the braid organized. You can query
within a session, across sessions, or across all sessions.

---

## Inspecting the Braid from the CLI

Use CLI attachment to inspect what Claude has stored:

```bash
mercury -a mcp
```

```
mcp> SELECT ?type (COUNT(*) AS ?n) WHERE {
  ->   GRAPH ?g { ?item a ?type }
  -> } GROUP BY ?type ORDER BY DESC(?n)
```

This shows the distribution of exchange types, decisions, and assumptions
across all sessions.

---

## The Alpha Architecture Note

The Semantic Braid is a design direction, not a finished system. The
vocabulary shown here is illustrative -- your project may need different
exchange types, phases, or metadata. The core idea is stable:

- Model conversation structure as triples
- Use temporal dimensions to track when things happened
- Use named graphs to separate sessions
- Use SPARQL to query the accumulated context

The orchestration layer (James) that automatically manages braid construction
and pruning is part of Sky Omega's future architecture. Today, you can build
and query braids manually using Mercury's existing tools.

---

## See Also

- [Temporal RDF](temporal-rdf.md) -- time-bound facts and version tracking
- [Mercury MCP Server](mercury-mcp.md) -- persistent memory for Claude
- [Federation and SERVICE](federation-and-service.md) -- querying across
  instances
- [Pruning and Maintenance](pruning-and-maintenance.md) -- managing store
  growth
