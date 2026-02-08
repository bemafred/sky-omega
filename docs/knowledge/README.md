# Shared Knowledge

This directory contains structured knowledge extracted from Mercury stores and version-controlled as Turtle (`.ttl`) files. It bridges the gap between local semantic memory (which lives in each Mercury store) and shared project understanding (which travels via git).

## Purpose

Code travels via git. Knowledge doesn't — unless it has a place to go. This is that place.

Mercury stores accumulate queryable, structured, provenance-tracked knowledge locally. Some of that knowledge is valuable beyond a single machine: architectural patterns, validated decisions, emerged vocabulary, lessons learned. Without a directory to receive it, extraction never happens — not because anyone decides against it, but because there's nowhere for it to go.

## What Belongs Here

**bootstrap.ttl** — Foundational knowledge that any new Mercury store should have. Auto-loaded on first session with an empty store. This is the readonly foundation: architectural invariants, methodology as queryable structure, the emerged predicate vocabulary. A fresh clone loads this and immediately has grounded context.

**patterns/** — Validated patterns extracted from real use. Parser invariants, storage patterns, EEE transitions that actually occurred. These are transferable — useful regardless of which machine or agent encounters them.

**decisions/** — Architectural decisions in triple form. Both what was chosen and what was rejected, with rationale. Git history shows *what* changed in the code. This directory records *what was understood* when the change was made.

**vocabulary/** — The predicate vocabulary that emerged from use. Not a designed ontology — a record of what predicates stabilized through convergence. Reference material for new sessions so they don't reinvent terms that already exist.

## What Does NOT Belong Here

- Raw session graph dumps (those are local, ephemeral)
- Project-specific transient facts (line counts, current test status)
- Unvalidated speculation (mark it in your local store, don't share it)
- Anything that duplicates what the code already expresses

## Flow

```
Local Mercury store
  └── Session graphs (raw, per-session)
        └── Topic graphs (consolidated, thematic)
              └── Selective extraction
                    └── Turtle files here (curated, shared, version-controlled)
                          └── Git → other clones → LOAD into their Mercury
```

Each arrow is a curation step. Each step loses detail and gains generality.

## Loading Shared Knowledge

On first session with an empty store, load the bootstrap:

```sparql
LOAD <file:///path/to/docs/knowledge/bootstrap.ttl>
```

Other files are loaded deliberately when relevant — not automatically.

## Contributing Knowledge Back

If a session discovers something worth sharing, extract it as Turtle and commit it. The commit review becomes an epistemic review: Is this knowledge valid? Is it general? Does it conflict with existing shared triples?

## Format

All files use [Turtle](https://www.w3.org/TR/turtle/) syntax. Turtle is text, so git diffs knowledge evolution meaningfully.
