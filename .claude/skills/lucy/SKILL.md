---
name: lucy
description: Recall what Sky Omega's semantic memory (Mercury) knows about a topic — Lucy's deep-memory recall layer. Use BEFORE acting on anything that may already be recorded: at session start, when a topic/decision/bug/component recurs, when the user asks "what do we know about X" / "have we seen this before" / "what did we decide about Y", or before re-deriving a finding that might already exist. Performs associative (text:match), connective (graph traversal), and status-driven (recency) recall across ALL memory graphs. Recognition, not search.
---

# Lucy — semantic-memory recall

Lucy is Sky Omega's structured semantic memory. This skill is her **recall** surface over the Mercury triple store (`mcp__mercury__mercury_query`). The goal is **recognition** — "here is what we already know about X, and where it came from" — not a raw query dump. Recall has three acts (per the Lucy design, `ck:` graph `obs:017`): **associative** (topic match), **connective** (graph traversal), **status-driven** (recency/attention).

## The one rule that makes recall work

**Always query the union of all graphs — never the bare default graph.** Memory is spread across named graphs (the durable `ck` graph + per-session graphs); a no-`FROM`/no-`GRAPH` query hits the *empty* unnamed default graph and recalls nothing. Wrap every recall pattern in `GRAPH ?g { … }`. (This is exactly the trap that makes recall silently miss; see `ck:obs-recall-is-graph-targeting-not-textmatch`.)

`text:match(?v, "term")` is case-insensitive and matches **both IRIs and literals** (so it finds `ck:obs-…drhook…` subjects *and* literal text). Prefer it over `CONTAINS`/`REGEX` for recall.

## Memory shape (so recall is informed)

- `https://sky-omega.dev/ck/graph` — **durable design-knowledge** (`ck:Observation` nodes, lessons, decisions). The first place to look for "what do we know".
- `https://sky-omega.dev/sessions/<date>-<topic>/graph` and `urn:sky-omega:session:<date>` — per-session observations (raw, time-stamped).
- `urn:sky-omega:bootstrap` — foundational component/vocabulary facts.
- Common vocabulary: `rdfs:label`, `rdfs:comment`, `ck:finding`, `ck:reachForWhen`, `ck:implication`, `ck:maturity`, `ck:appliesTo`, `ck:learnedIn`, `dct:date`; links: `ck:relatedFinding`, `ck:refines`, `ck:transfersTo`, `rdfs:seeAlso`.

Prefixes for the queries below:
```sparql
PREFIX ck: <https://sky-omega.dev/ck/>
PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>
PREFIX dct: <http://purl.org/dc/terms/>
```

**Declare every prefix you use.** `mercury_query` returns *no results* — silently, no error — for an undeclared prefix. A surprise-empty recall is more often a missing `PREFIX` than absent memory (`ck:obs-temporal-extensions-usable`).

## Recall procedure

Given a `<topic>` (one or more terms — try synonyms/variants if the first pass is thin):

### 1. Associative recall — find anchors
Find the subjects that mention the topic anywhere (IRI or literal), across all graphs:
```sparql
SELECT DISTINCT ?g ?s WHERE {
  GRAPH ?g { ?s ?p ?o .
    FILTER(text:match(?s, "<topic>") || text:match(?o, "<topic>")) }
} LIMIT 60
```
If thin, broaden: split the topic into terms and `||` them, or match a shorter stem.

### 2. Status-driven recall — what's recent / active (native bitemporal)
Mercury is bitemporal — every triple carries valid-time, so recency is a substrate property, not just a
`dct:date` literal. Surface labelled nodes newest-first so attention lands on current work, not stale priors:
```sparql
SELECT ?g ?s ?label ?date WHERE {
  GRAPH ?g { ?s rdfs:label ?label . OPTIONAL { ?s dct:date ?date }
    FILTER(text:match(?label, "<topic>") || text:match(?s, "<topic>")) }
} ORDER BY DESC(?date) LIMIT 25
```
Three **temporal-recall** modes extend this (the clause goes after `WHERE`/`LIMIT`; all verified usable):
- **Point-in-time** — *what did we know as of a past moment* — append `AS OF "2026-06-20"^^xsd:date`.
- **Window** — *what was valid / changed during a period* — append `DURING ["2026-06-20"^^xsd:date, "2026-06-23"^^xsd:date]`.
- **Epistemic history** — *how a belief evolved / what was superseded* — append `ALL VERSIONS`.

Default (no clause) = valid now.

### 3. Connective recall — expand anchors into full nodes and neighbours
For the most relevant anchors from steps 1–2, read the **whole node** and **one hop** of links:
```sparql
SELECT ?g ?p ?o WHERE { GRAPH ?g { <anchor-iri> ?p ?o } }            # the node itself (with provenance: graph, date, maturity)
SELECT ?g ?s2 ?p WHERE { GRAPH ?g { ?s2 ?p <anchor-iri> } }          # what points AT it
```
Follow `ck:relatedFinding` / `ck:refines` / `ck:transfersTo` / `rdfs:seeAlso` targets one hop to connect the dots.

### 4. Present as recognition
Synthesize — do **not** dump triples. Write what Lucy *remembers*:
- Lead with the few most relevant + most recent findings, each in prose.
- Attach **provenance** to each: which graph/session it came from and its `dct:date` (and `ck:maturity` when present — e.g. `validated-live`, `synthesis-for-ideation`).
- Group by theme; note connections you traversed ("this refines …").
- **Name the gaps**: if the topic returns little or nothing, say so plainly — absence is a recall result ("nothing recorded about Z"), not silence.

## When NOT to use

- For a single known triple you can fetch directly, just `mercury_query` — recall is for "what do we know about a *topic/area*".
- Recall reads memory; it does not write. Recording observations stays the reflexive `mercury_update` habit (see MERCURY.md).

## Provenance & honesty

Lucy carries provenance and epistemic status — recalled facts reflect what was true *when written*. If a recalled memory names a file/flag/function, verify it still exists before acting on it. Distinguish what memory *claims* from what you have *re-verified*.
