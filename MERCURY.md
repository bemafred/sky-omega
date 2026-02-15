# MERCURY.md

Mercury is your long-term semantic memory. This store persists across sessions. What you write here, you — or a future session, or a different LLM agent — can query later. What you don't write is lost when the context window closes.

This document is guidance, not specification. No ontologies are prescribed. The vocabulary and patterns should emerge from use. This document establishes the discipline.

> **Status:** Emergence. This is v1. Expect it to evolve as real usage reveals what works.

---

## Mental Model

Mercury is not a log, not a cache, and not a conversation archive.

A **log** records what happened. A **cache** stores things for quick retrieval. A **conversation archive** preserves transcripts.

Mercury stores **what is believed to be true** — structured, queryable, revisable knowledge. Every triple you assert is a claim that future sessions will encounter and may trust. Write accordingly.

### Ownership

A single Mercury store is owned by a single agent at a time. There is no shared concurrent access. But the same store can be used by different LLM agents across sessions — Claude Code today, Rider's AI assistant tomorrow. The knowledge belongs to the human, not to any platform. RDF makes this natural: triples are self-describing and portable.

### What Mercury Is Not For

- Raw conversation dumps (that's chat history)
- Temporary intermediate computations (use variables)
- Things trivially re-derived from the codebase (read the code instead)
- Unvalidated speculation presented as fact (mark it, or don't store it)

---

## Session Discipline

### Start Every Session by Checking Memory

This is the single most important habit. If you don't query memory, you don't have memory — you just have a write-only database.

```sparql
-- What graphs exist and how large are they?
SELECT ?g (COUNT(*) as ?triples) WHERE {
  GRAPH ?g { ?s ?p ?o }
} GROUP BY ?g ORDER BY DESC(?triples)
```

```sparql
-- What's provisional or unresolved?
PREFIX sky: <urn:sky-omega:>
SELECT ?s ?label ?comment WHERE {
  ?s sky:status "provisional" .
  OPTIONAL { ?s rdfs:label ?label }
  OPTIONAL { ?s rdfs:comment ?comment }
}
```

If the store is empty, that's fine — you're starting fresh. Load `docs/knowledge/bootstrap.ttl` (see Bootstrap section below) for grounded context, or begin with your own assertions. An empty store is a valid starting point; the bootstrap provides a productive seed, not a requirement.

### End Every Session by Considering What to Remember

Not "dump everything." Ask: what did this session produce that a future session would need? A decision? A validated assumption? A discovered constraint? A corrected misunderstanding?

If nothing is worth storing, store nothing. That's fine too.

---

## EEE and Memory

Every write to Mercury is an epistemic act. Know which kind of thinking you're doing.

### During Emergence (exploring, uncertain)

- **Read freely.** Query to discover what's known, find gaps, surface connections.
- **Write sparingly.** If you write, mark it as provisional.
- Use session graphs to contain exploratory assertions.
- Ask: *"Am I recording a discovery or an assumption?"*

### During Epistemics (validating, clarifying)

- **Read to check consistency.** Does new understanding conflict with stored knowledge?
- **Write to crystallize.** When an assumption becomes validated, promote it — move from provisional to established, add rationale.
- Ask: *"Can I defend this assertion if challenged?"*

### During Engineering (building, optimizing)

- **Read as ground truth.** Query for established facts, patterns, constraints.
- **Write as documentation.** Record decisions, invariants, rationale.
- Ask: *"Would a future session understand why this is here?"*

### The Forbidden Transition

Emergence → Engineering in memory: writing half-understood ideas as if they're established facts. If you're exploring, mark it as exploration.

---

## Writing Triples

### No Ontology Yet — and That's Intentional

The vocabulary should emerge from use. Don't spend time designing the perfect predicate hierarchy. Use clear, descriptive predicates. When you notice the same concept appearing across sessions, that's convergence — and *that* is when vocabulary stabilizes naturally.

### Namespace Conventions

```
PREFIX sky:     <urn:sky-omega:>
PREFIX data:    <urn:sky-omega:data:>
PREFIX session: <urn:sky-omega:session:>
PREFIX prov:    <urn:sky-omega:prov:>
```

These are starting points, not a schema. Extend as needed. Prefer `urn:sky-omega:` as the root so everything is recognizably part of this system.

### Provenance — Every Session Leaves a Trace

Every session that writes to Mercury must create a session graph header:

```sparql
PREFIX sky: <urn:sky-omega:>

INSERT DATA {
  GRAPH <urn:sky-omega:session:YYYY-MM-DD-NNN> {
    <urn:sky-omega:session:YYYY-MM-DD-NNN> a sky:Session ;
      sky:agent "claude-code" ;
      sky:timestamp "YYYY-MM-DDTHH:MM:SSZ"^^xsd:dateTime ;
      rdfs:comment "Brief description of what this session did" .
  }
}
```

Replace the date and sequence. Use the actual agent identifier (`claude-code`, `rider-ai`, etc.). The comment should be a sentence, not a paragraph.

This is not optional. Without provenance, assertions become untraceable noise.

### Triple Hygiene

- **Prefer explicit predicates.** `sky:decidedArchitecture` tells a future reader more than `sky:relatedTo`.
- **Use `rdfs:label` and `rdfs:comment` generously.** Future sessions need context, not just structure.
- **Reuse predicates** when the same relationship appears across sessions. Convergence is a signal.
- **Avoid blank nodes** for anything that matters. Use URIs — they're queryable and referenceable.
- **Named graphs are your friend.** Group related assertions. One graph per session, per topic, or per decision.

---

## Consolidation: The WAL and the Dream

Session graphs accumulate. Over time, knowledge fragments across many sessions. Two mechanisms address this:

### Forced Consolidation (the WAL checkpoint)

Mechanical. Triggered by threshold — when session graph count grows large, or on a schedule. The task is: review unconsolidated session graphs, identify validated knowledge, merge it into topic graphs, note what was noise.

This doesn't require understanding. It requires discipline. Like a WAL checkpoint, it keeps the system from accumulating unbounded cruft.

When triggered, the prompt is simple: *"You have N unconsolidated session graphs. Review and consolidate."*

### Organic Consolidation (the dream)

Spontaneous. During normal work, you query memory and notice that three session graphs all touch the same concept. You naturally synthesize them — not because you were told to, but because the connection is there and useful.

This requires understanding. It produces meaning. Like sleep consolidation in biology, it's where scattered experiences become integrated knowledge.

### The Pattern

```
Session graphs (raw, per-session)
    │
    ├── forced checkpoint ──→ Topic graphs (consolidated, thematic)
    │
    └── organic synthesis ──→ Topic graphs (consolidated, thematic)

Session graphs are preserved as provenance trail.
Topic graphs are the durable knowledge.
```

Don't design the consolidation process in advance. Start by accumulating session graphs. The right consolidation patterns will emerge from observing the actual data.

---

## Shared Knowledge — `docs/knowledge/`

Local Mercury stores don't travel via git. Code does. Without a place for shared knowledge, extraction from local store to repository never happens — not by decision, but by absence of locality.

`docs/knowledge/` provides that locality. It contains Turtle files extracted from local Mercury stores: validated patterns, architectural decisions, emerged vocabulary, lessons learned. These are version-controlled and travel with the repository.

### Structure

```
docs/knowledge/
├── bootstrap.ttl          # Foundational knowledge, loaded into empty stores
├── patterns/              # Validated patterns from real use
├── decisions/             # Architectural decisions and rejections with rationale
└── vocabulary/            # Predicate vocabulary that emerged and stabilized
```

### Bootstrap — First Session with an Empty Store

If `mercury_stats` shows zero quads, load the bootstrap knowledge. From the repository root:

```sparql
LOAD <file:///Users/you/src/sky-omega/docs/knowledge/bootstrap.ttl>
```

Replace the path with your actual repository root. To find it: the directory containing `CLAUDE.md` and `SkyOmega.sln`.

Alternatively, use the CLI tool for direct file loading:

```bash
mercury -a mcp
```
Then in the attached session:
```sparql
LOAD <file:///Users/you/src/sky-omega/docs/knowledge/bootstrap.ttl>
```

This provides grounded context: component definitions, EEE methodology as queryable structure, core architectural principles. It is a seed, not a schema.

### Extracting Knowledge to Share

When local consolidation produces knowledge that's general enough to be useful beyond this machine:

1. Extract as Turtle (CONSTRUCT query from topic graph)
2. Place in the appropriate subdirectory
3. Commit with a message that explains what knowledge was extracted and why
4. The commit diff shows knowledge evolution alongside code evolution

**Criteria for extraction:** Is it valid? Is it general? Would a fresh clone benefit from it? Does it conflict with existing shared triples?

See [docs/knowledge/README.md](docs/knowledge/README.md) for full details.

---

## Anti-Patterns

**The data hoarder** — storing everything "just in case." Memory should be curated, not accumulated.

**The amnesiac** — never querying memory at session start. You have persistent memory. Use it.

**The false authority** — writing speculative triples without provenance markers. Every assertion implies confidence.

**The schema astronaut** — spending more time designing the perfect ontology than recording useful knowledge. Let vocabulary emerge from use.

**The context window substitute** — trying to store the entire conversation. Mercury is for distilled meaning, not transcripts.

---

## Querying Patterns

### Before making a decision
```sparql
PREFIX sky: <urn:sky-omega:>
SELECT ?decision ?rationale ?when WHERE {
  ?decision a sky:Decision ;
    sky:about ?topic ;
    sky:rationale ?rationale .
  OPTIONAL { ?decision sky:timestamp ?when }
  FILTER(CONTAINS(LCASE(STR(?topic)), "search-term"))
}
```

### Finding what's known about a concept
```sparql
SELECT ?p ?o WHERE {
  <urn:sky-omega:data:some-concept> ?p ?o .
}
```

### Tracing provenance
```sparql
SELECT ?graph ?agent ?when ?comment WHERE {
  GRAPH ?graph {
    ?session a sky:Session ;
      sky:agent ?agent ;
      sky:timestamp ?when ;
      rdfs:comment ?comment .
  }
} ORDER BY DESC(?when)
```

---

## Available Tools

| Tool | Purpose |
|------|---------|
| `mercury_query` | SPARQL SELECT, ASK, CONSTRUCT, DESCRIBE |
| `mercury_update` | SPARQL UPDATE (INSERT, DELETE, LOAD, CLEAR) |
| `mercury_stats` | Store statistics (quad count, atoms, storage size) |
| `mercury_graphs` | List all named graphs |

The MCP tools are configured via `.mcp.json` (dev-time) or `mercury-mcp` global tool (production). See [CLAUDE.md](CLAUDE.md) for setup.
