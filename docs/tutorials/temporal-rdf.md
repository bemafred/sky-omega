# Memory Time

A Mercury store with the Cognitive profile, the default, remembers. When you insert a triple it is memorized; when you
delete it, it is forgotten, and the store keeps the period in which it was remembered. Each period has two times on
the store's own clock: when the triple was memorized and when it was forgotten. You ask about them in SPARQL, with
three words.

> **Prerequisites:** Mercury installed and a working REPL. See
> [Getting Started](getting-started.md) if you haven't done that yet.

---

## Why Memory Time Matters

Most databases keep the current state. When a fact is deleted it is gone. A Cognitive store keeps what it once
remembered, so you can ask:

- What did we learn last week?
- What have we forgotten since the first of the month, and when?
- When did we first record this fact, and did we ever drop it?
- What did we remember at the time we made that decision?

No audit tables, no change-data-capture pipeline. Deleting is forgetting, and forgetting keeps the record of the
memory.

---

## Two Kinds of Time

There are two different questions about time, and Mercury answers them in two different ways:

| Question | Example | Where it lives |
|----------|---------|----------------|
| When did the store learn this, and when did it let go? | "We recorded Alice's employer on 2026-03-04" | **Memory time**: set by the store, read with the words |
| When was this true in the world? | "Alice worked at Acme from 2020 to 2023" | **Content**: triples you write, like any other fact |

Memory time is the store's. Nobody states it: the store reads its clock when it writes, to the millisecond. When
something happened in the world is information with a source, so you write it as triples.

---

## Memorizing and Forgetting

`INSERT` memorizes:

```sparql
INSERT DATA {
    <http://ex.org/alice> <http://ex.org/worksFor> <http://ex.org/Acme> .
}
```

`DELETE` forgets. The triple is no longer remembered, but the store keeps the period in which it was:

```sparql
DELETE DATA {
    <http://ex.org/alice> <http://ex.org/worksFor> <http://ex.org/Acme> .
}
```

Inserting a triple that is already remembered changes nothing. Inserting one that was forgotten begins a new period.

---

## Querying Memory Time

### What is remembered (default)

A query without the words sees what is remembered now:

```sparql
SELECT ?employer WHERE {
    <http://ex.org/alice> <http://ex.org/worksFor> ?employer .
}
```

After the delete above, this returns nothing.

### MEMORIZED: when a memory was memorized

`MEMORIZED` follows a triple pattern. With a variable it binds the time the matched memory was memorized:

```sparql
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

SELECT ?s ?label ?when WHERE {
    ?s rdfs:label ?label MEMORIZED ?when .
} ORDER BY DESC(?when)
```

`SINCE` and `BEFORE` select by that time. The range is half-open: `SINCE` is at or after, `BEFORE` is strictly before:

```sparql
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>
PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>

# What did we learn last week?
SELECT ?s ?label ?when WHERE {
    ?s rdfs:label ?label MEMORIZED ?when SINCE NOW() - "P7D"^^xsd:dayTimeDuration .
}

# What did we learn in January?
SELECT ?s ?label WHERE {
    ?s rdfs:label ?label MEMORIZED SINCE "2026-01-01"^^xsd:date BEFORE "2026-02-01"^^xsd:date .
}
```

A bound is a literal or an expression without variables, like `NOW() - "P7D"^^xsd:dayTimeDuration`. A time without a
timezone is UTC.

The words belong to the one pattern they follow. In a join, the other patterns keep their own times:

```sparql
PREFIX ex: <http://ex.org/>
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>
PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>

# Findings from last week, with the component each is about, however old that component is
SELECT ?finding ?component WHERE {
    ?finding ex:about ?c MEMORIZED SINCE NOW() - "P7D"^^xsd:dayTimeDuration .
    ?c rdfs:label ?component .
}
```

### FORGOTTEN: when a memory was forgotten

A pattern with `FORGOTTEN` matches forgotten memories too. With a variable it binds when the memory was forgotten, and
leaves the variable unbound for a memory that is still remembered:

```sparql
SELECT ?employer ?memorized ?forgotten WHERE {
    <http://ex.org/alice> <http://ex.org/worksFor> ?employer MEMORIZED ?memorized FORGOTTEN ?forgotten .
} ORDER BY ?memorized
```

This lists every period of Alice's employer facts, each with its two times. A bound selects by the forgotten time:

```sparql
PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>

# What have we forgotten since the first of November?
SELECT ?s ?p ?o ?when WHERE {
    ?s ?p ?o FORGOTTEN ?when SINCE "2026-11-01"^^xsd:date .
}
```

### INCLUDING FORGOTTEN: every pattern

`INCLUDING FORGOTTEN` after the query makes every pattern match forgotten memories as well as remembered ones:

```sparql
SELECT ?employer WHERE {
    <http://ex.org/alice> <http://ex.org/worksFor> ?employer .
} INCLUDING FORGOTTEN
```

### What was remembered at a time

There is no word for "what did we remember at time t". It is two patterns: what was memorized before `t` and is still
remembered, and what was memorized before `t` and forgotten after it. With `b` one millisecond after `t`:

```sparql
PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>

SELECT ?employer WHERE {
    { <http://ex.org/alice> <http://ex.org/worksFor> ?employer
          MEMORIZED BEFORE "2026-06-15T12:00:00.001Z"^^xsd:dateTime }
    UNION
    { <http://ex.org/alice> <http://ex.org/worksFor> ?employer
          MEMORIZED BEFORE "2026-06-15T12:00:00.001Z"^^xsd:dateTime
          FORGOTTEN SINCE "2026-06-15T12:00:00.001Z"^^xsd:dateTime }
}
```

---

## World Time Is Content

"Alice worked at Acme from 2020 to 2023" is a fact about the world. Write it as triples, with the dates as values, and
query it with `FILTER`:

```sparql
PREFIX ex: <http://ex.org/>
PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>

INSERT DATA {
    ex:alice ex:employment ex:alice-acme .
    ex:alice-acme ex:employer ex:Acme ;
                  ex:start "2020-01-01"^^xsd:date ;
                  ex:end   "2023-06-30"^^xsd:date .
}
```

```sparql
PREFIX ex: <http://ex.org/>
PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>

# Where did Alice work on 2021-06-15?
SELECT ?employer WHERE {
    ex:alice ex:employment ?e .
    ?e ex:employer ?employer ; ex:start ?start .
    OPTIONAL { ?e ex:end ?end }
    FILTER(?start <= "2021-06-15"^^xsd:date && (!BOUND(?end) || ?end > "2021-06-15"^^xsd:date))
}
```

The two kinds of time combine. "Which employments did we record last month" is `MEMORIZED SINCE` on the
`ex:employment` pattern, and the dates in the result say when each employment was true.

A correction is the same: if Alice actually left in 2022, delete the old end date and insert the new one. The old value
is forgotten, not erased, so `FORGOTTEN` still shows what was recorded before and when it was corrected.

---

## The Code API

The same times are readable from code, on a Cognitive store:

| Method | What it does |
|--------|--------------|
| `AddCurrent(s, p, o, graph)` | Memorizes a quad |
| `DeleteCurrent(s, p, o, graph)` | Forgets a quad |
| `BeginBatch()`, `AddCurrentBatched(...)`, `DeleteCurrentBatched(...)`, `CommitBatch()` | Many writes in one clock reading |
| `QueryCurrent(s, p, o, graph)` | What is remembered: `ResolvedQuad`, on every profile |
| `QueryMemories(s, p, o, graph, memorizedSince, memorizedBefore, forgottenSince, forgottenBefore, includingForgotten)` | The memories with their times: `ResolvedTemporalQuad`, with `MemorizedAt` and `ForgottenAt` (null while remembered) |

No method takes a memory time to write: the store sets it. A test that needs known times gives the store a clock,
`StorageOptions.Clock`. On a Graph, Reference or Minimal store, the words and `QueryMemories` are an error: those
profiles keep no times.

---

## Running the Examples

The examples project includes runnable memory-time demonstrations:

```bash
dotnet run --project examples/Mercury.Examples -- temporal
```

It runs five examples: memorizing and forgetting, what was remembered at a time, what was memorized since a time, a
fact remembered again, and the times surviving a reopen of the store.

---

## See Also

- [Memory Time in Mercury](../architecture/technical/temporal-rdf.md) -- the model and how it is stored
- [SPARQL Reference](../architecture/technical/sparql-reference.md#memory-time-cognitive-sparql-adr-061) -- every
  rule of the words
- [Mercury CLI](mercury-cli.md) -- queries in the REPL
- [Pruning and Maintenance](pruning-and-maintenance.md) -- a prune keeps what is remembered
- [Your First Knowledge Graph](your-first-knowledge-graph.md) -- RDF basics
