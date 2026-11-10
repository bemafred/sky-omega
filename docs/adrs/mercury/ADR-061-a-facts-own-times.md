# ADR-061: A Fact's Own Times — The Cognitive Store Can Tell, in SPARQL, When It Learned, Held and Retracted Each Fact

## Status

**Status:** Proposed — 2026-11-10

Drafted at Martin's request after a design discussion the same day (Mercury
`urn:sky-omega:obs:2026-11-10-mercury-time-extension-shape`). The direction is his; the syntax and the open questions
below are for his assessment.

## Why

The Cognitive profile is bitemporal because Sky Omega's memory is meant to be time-aware. Martin, 2026-11-10: "The
bitemporality is for creating a 'time aware' composite intelligence memory architecture in Sky Omega. Functionally
comparable to human memory but semantically more precise. To make up for lossy memory."

Two kinds of time meet in such a memory, and they belong in different places.

- **Intrinsic time** is the memory's own record of itself:
  - when it learned a fact (transaction time);
  - over what interval it holds the fact true (valid time);
  - when it ended a belief (a DELETE ends valid time, [ADR-054](ADR-054-delete-ends-valid-time.md));
  - when it retracted a fact recorded in error.

  The store sets these at every write, and nobody else can. They need no triple.
- **Extrinsic time** is a fact about the world: a commit at 18:22:33, a file's modification time, a run's start. That
  is content. The memory states it as a triple, named for its source. Such a triple has intrinsic times of its own: the
  memory learned at 18:25:19 that the push happened at 18:25:15.

Human memory loses intrinsic time in known ways:
- it misdates events;
- it forgets the source;
- recalling a memory can rewrite it without a trace of the old version;
- hindsight hides what was believed before.

A bitemporal store answers each of these exactly:
- when it learned a fact;
- when the fact held;
- which earlier belief a revision replaced;
- what it held true at any moment (`AS OF`).

The store keeps all of this today. A query can choose a moment or a range (`AS OF`, `DURING`, `ALL VERSIONS`), but it
cannot read a single one of those times. The memory knows when, and cannot tell.

**What that costs, observed 2026-11-10** (DrHook ADR-017 session):
- **A guessed timestamp, a fourth time.** The agent wrote `dcterms:created` values into its records from its own sense
  of time, without reading a clock. Seven of eight were wrong, by 1 to 25 seconds; two had first been written 16
  and 17 minutes off.
- **75 queries to read eight write times.** A subagent bisected each record's first appearance under `AS OF`, to the
  second, because no query could ask for them.
- **Recency recall misses the hand-written times.** The `lucy` skill's recency recall orders by `dct:date`, which
  these records do not carry, so they never sort.

The hand-written copy of an intrinsic time is exactly where the loss came back in.

## Context

### What the store keeps per version

A Cognitive store keeps four `TemporalQuadIndex`es. Each version of a quad carries:
- `ValidFrom` and `ValidTo`, in milliseconds since the epoch; open-ended is `DateTimeOffset.MaxValue`;
- `TransactionTime`, set by `QuadStore.Add` from `DateTimeOffset.UtcNow` at the write;
- `IsDeleted`: retracted, recorded in error ([ADR-054](ADR-054-delete-ends-valid-time.md) D2);
- the retraction's own transaction time (`ModifiedAt`).

The storage API already hands all of it out: `ResolvedTemporalQuad` has `ValidFrom`, `ValidTo`, `TransactionTime`,
`IsDeleted` and `RetractedAt`. What is missing is only a way for SPARQL to bind it.

### Who sets which time

- **Transaction time:** only the store. It is purely intrinsic.
- **Valid time:**
  - A SPARQL insert sets it to "from now, open-ended" (`AddCurrent`), so for a SPARQL write valid-from equals
    transaction time.
  - The C# API can state it (`Add(…, validFrom, validTo)`), which is valid time's classical meaning: when the fact held
    in the world.
  - SPARQL has no way to write it.

### The temporal extensions today

`AS OF`, `DURING` and `ALL VERSIONS` follow a query's solution modifiers (`SparqlParser`, the temporal clause after [18]
SolutionModifier). They choose which versions match, and bind nothing. They are Mercury-only by intent: a standard engine
does not run them, and that is not a problem. Standard queries are unaffected, because a standard query has ended
before the clause begins. The W3C suites check that every standard query behaves as the specification says (since
[ADR-059](ADR-059-every-w3c-test-runs.md), every test runs).

### Profiles

Only the Cognitive profile is bitemporal ([ADR-029](ADR-029-store-profiles.md)).
- **Graph** is versioned for soft deletes: `CreatedAt`, `ModifiedAt`, `Version`, `IsDeleted`. It has no valid time.
- **Reference** and **Minimal** have no time at all.

`QuadStore.RequireTemporalProfile` already makes `QueryAsOf`, `QueryEvolution`, `QueryChanges` and `TimeTravelTo` fail
on them: "Use a Cognitive-profile store for temporal queries".

## Decision

### Principles

1. **Intrinsic times are read, never written.** A record does not stamp its own write time; the store's transaction
   time is that record. Extrinsic times stay triples, named for their source. Existing `dcterms:created` values stay
   as they are: memory vocabulary is research data.
2. **Mercury-only by intent, and non-intrusive.** Like `AS OF`, the extension is not meant to run elsewhere.
   Non-intrusive means a query without it is untouched: no standard query can reach the extension's grammar, and the
   W3C results cannot move. No pragma or switch is needed.
3. **Temporal stores only.** On any other profile the extension is an error through `RequireTemporalProfile`, the same
   as `AS OF`. Never silently unbound.
4. **A time belongs to the version a pattern matched.** It is bound when a triple pattern matches a stored version.
   Under `ALL VERSIONS` each version is its own solution with its own times. A function of the terms
   (`f(?s, ?p, ?o)`) cannot do this: under `ALL VERSIONS` one quad has several versions.
5. **No cost when unused**, and nothing allocated on the query hot path.
6. **Clear of SPARQL 1.2.** The extension takes none of 1.2's new syntax: triple terms `<<( )>>`, reifiers `~`,
   annotations `{| |}`, the `VERSION` directive. A standard 1.2 query must stay untouched once Mercury supports it.

### What a query can read

| Name (working) | The time | Notes |
|----------------|----------|-------|
| recorded | the version's transaction time: when the memory learned it | always present |
| valid from, valid to | the interval the memory holds the version true | valid-to unbound while open-ended (Q2) |
| retracted | when a version recorded in error was retracted | only under `ALL VERSIONS`, which is the only mode that returns retracted versions (Q4) |

Each is an `xsd:dateTime` in UTC, at the store's millisecond precision (Q3).

### Candidate syntax

The lead candidate is a clause right after a triple pattern, binding that pattern's version. Working form:

```sparql
SELECT ?label ?learned WHERE {
  GRAPH ?g { ?s rdfs:label ?label RECORDED ?learned . }
}

SELECT ?label ?from ?to ?learned WHERE {
  GRAPH ?g { ?s rdfs:label ?label VALID ?from ?to RECORDED ?learned . }
} ALL VERSIONS
```

In SPARQL 1.1 a bare word cannot follow a triple's object, except the keywords that begin the next pattern (`FILTER`,
`OPTIONAL`, `GRAPH` and so on). A new keyword there is therefore unambiguous. It is also clear of 1.2's `~` and `{|`.

| Candidate | For | Against |
|-----------|-----|---------|
| **A clause after the triple pattern** (lead) | binds exactly the version that pattern matched; reads like the temporal clauses it joins; visible to the reader | new grammar after the object: keywords to choose, every triple form to cover (Q1) |
| An extension function, `mercury:validFrom(?s, ?p, ?o)` | SPARQL 1.1's own extension point, like `text:match` | cannot tell versions apart, nor two patterns over the same terms (principle 4) |
| SPARQL 1.2 annotations or triple terms | the coming standard way to speak about a triple | they mean reification, statements about a triple, not its versions; taking them now collides with 1.2 later (principle 6) |
| A `#pragma` comment on the pattern | no grammar change | meaning hidden in a comment, tied to position; its one advantage, other engines ignoring it, is not a goal (principle 2) |

### What each temporal mode binds

| Mode | Versions a pattern matches | Times bound |
|------|----------------------------|-------------|
| default (now) | the version valid now | that version's |
| `AS OF t` | the version valid at `t` | that version's |
| `DURING [a, b]` | each version overlapping the range | each its own, one solution per version |
| `ALL VERSIONS` | every version, retracted ones included | each its own; retracted only for a retracted version |

## Open questions

- **Q1. The exact form.** Which keywords (`RECORDED`, `VALID` are working names), and whether a pattern can name only
  the times it wants. How the clause attaches to the abbreviated forms: `;` and `,` lists, blank-node property lists,
  property paths.
- **Q2. An open-ended valid-to:** unbound, or a literal for "open"? Unbound reads naturally (`!BOUND(?to)` = still
  held), but it means a missing time and "no end" look alike.
- **Q3. Precision and type.** The store keeps milliseconds. `xsd:dateTime` in UTC with millisecond fraction, or
  `xsd:dateTimeStamp`?
- **Q4. Retracted versions.** Bind the retraction time (`RetractedAt`) only under `ALL VERSIONS`, or also expose
  "retracted" as a flag?
- **Q5. Patterns that match no single stored version:**
  - a property path;
  - a triple inferred by `OwlReasoner` (materialized, so it has versions, but the memory did not learn it from a
    writer);
  - a `SERVICE` pattern.

  An error, unbound, or the inferred triple's own times?
- **Q6. Interaction with the planner.** The pattern tree (ADR-047, ADR-052, ADR-055) must carry each pattern's time
  variables to the scan that binds them, and joins must keep each pattern's own times.
- **Q7. Consumers.**
  - The `lucy` skill's recency recall would order by `RECORDED`, not by a hand-written date.
  - The agent stops stamping `dcterms:created`. Until this lands it stamps with `NOW()`.
  - The Mercury MCP tool and `docs/architecture/technical/sparql-reference.md` describe the extension.
- **Q8. Writing valid time from SPARQL.** Out of scope here: extrinsic times stay triples (principle 1). A later need
  could revisit it, since the C# API already can.

## Steps

1. **Martin's assessment** of this draft: the principles, the lead syntax, Q1 to Q8.
2. **Grammar and parse:** the clause in `SparqlParser`, recorded in the pattern buffer with zero allocation. A test that
   every W3C result is unchanged.
3. **Binding:** the scan that matches a temporal pattern binds the version's times. Tests per temporal mode, per
   profile (the error on Graph, Reference, Minimal), for joins, and for `ALL VERSIONS` with retracted versions.
4. **Consumers:** the `lucy` skill's recency recall, the MCP tool description, the SPARQL reference.
5. **Dogfood** on the live memory store: read today's eight records' write times with one query, and compare them with
   the bisected values in `urn:sky-omega:obs:2026-11-10-write-times-from-the-store`.

## Not in this ADR

- The Graph profile's version metadata (`CreatedAt`, `ModifiedAt`, `Version`): it serves soft deletes, and is exposed
  only if a need appears (Martin, 2026-11-10).
- Writing valid time from SPARQL (Q8).
- `NOW()` returning different values within one query, which SPARQL 1.1 §17.4.5.1 forbids. It is a conformance defect
  on its own, and waits for Martin's go.

## References

- [ADR-029](ADR-029-store-profiles.md): store profiles; only Cognitive is bitemporal.
- [ADR-054](ADR-054-delete-ends-valid-time.md): DELETE ends valid time; retraction of a fact recorded in error.
- [ADR-059](ADR-059-every-w3c-test-runs.md): every W3C test runs.
- `docs/architecture/technical/sparql-reference.md`, Temporal SPARQL Extensions.
- Mercury: `urn:sky-omega:obs:2026-11-10-write-times-from-the-store`,
  `urn:sky-omega:obs:2026-11-10-mercury-time-extension-shape`, in graph
  `urn:sky-omega:session:2026-11-07-adr016-step5`.
