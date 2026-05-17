# Limit: URI atom corruption from pre-1.7.72 INSERT with `\"` literal

Status:        **Triggered** (URI-specific; workaround: use a fresh URI)
Surfaced:      2026-05-17, while inserting the validation-discipline rule into Mercury (third teaching mechanism for the conformance-vs-dogfood observation)
Last reviewed: 2026-05-17

## Description

When INSERT DATA stored a literal containing the `\"` escape sequence under Mercury 1.7.69 → 1.7.71 (before the `GetLexicalForm` `IndexOf`-vs-`LastIndexOf` fix in 1.7.72), the literal was truncated at the first `\"`. The truncated literal got interned into the atom store; the index entries for the affected predicates may have used the truncated atom ID.

After the 1.7.72 fix and a `DELETE WHERE { <subject> ?p ?o }` to clear the corrupted entries, subsequent `INSERT DATA` operations against the **same subject URI** silently drop all but the first new property. INSERT reports the expected affected count but only one triple becomes queryable.

The bug does NOT affect:
- Fresh subject URIs (post-fix INSERTs on URIs never previously touched are fine).
- DELETE+INSERT cycles on fresh URIs (verified clean).
- Single-property INSERTs to the corrupted URI (each isolated INSERT of one triple works).

The bug DOES affect:
- Multi-property INSERTs (via `;` shorthand or repeated patterns) to a URI that previously hosted a `\"`-containing literal under the buggy code path.

## Trigger condition

A subject URI `<X>` was the target of an INSERT under Mercury 1.7.69/1.7.70/1.7.71 where one of the inserted objects was a literal containing `\"`. After upgrading to 1.7.72 and attempting to re-populate `<X>` with multiple properties in one INSERT DATA, only one property survives.

## Reproducer

Mercury 1.7.72, against a store that previously executed (under 1.7.69/70/71):

```sparql
INSERT DATA {
  GRAPH <urn:g> {
    <urn:s> rdfs:comment "text with \"escape\" inside" .
  }
}
```

Then under 1.7.72:

```sparql
DELETE WHERE { GRAPH <urn:g> { <urn:s> ?p ?o } } ;

INSERT DATA {
  GRAPH <urn:g> {
    <urn:s> a sky:Type ;
            rdfs:label "label" ;
            sky:status "established" ;
            sky:timestamp "2026-05-17T00:00:00Z"^^xsd:dateTime .
  }
}
```

Expected: 4 triples queryable for `<urn:s>`.
Actual: only 1 triple visible. INSERT reports "4 triples affected" — only the first-or-arbitrary one persists.

A fresh URI under the same scenario works correctly: all 4 properties queryable.

## Current state

Surface-localization not yet attempted past the observation. Three hypotheses:

1. **Atom-store ID divergence** — the corrupted literal was interned under one atom ID; the post-fix re-INSERT uses different atom IDs; the indexes have stale references to the truncated atom that block fresh entries from materializing.

2. **Tombstone interaction** — DELETE WHERE marks the (subject, predicate, object) triples with deletion markers in the WAL; the subsequent INSERT may be hitting tombstones for the affected predicates and silently no-op-ing.

3. **Batch-buffer aliasing** — the INSERT DATA batch buffers all 4 quads before commit; if `_expandedTerm` in `UpdateExecutor.ExpandPrefixedName` is being reused as a backing buffer across multiple `GetTermValue` calls, later calls may overwrite spans that earlier-batched quads still hold references to. (See `src/Mercury/Sparql/Execution/UpdateExecutor.cs` line 1265 — `_expandedTerm` is a single field overwritten on each prefixed-name expansion.)

The third hypothesis is most concerning because it would affect any multi-quad INSERT with prefixed names, not just URIs touched by the pre-1.7.72 bug. But the fresh-URI test passed cleanly with multiple prefixed-name properties, so hypothesis 3 is unlikely as a general issue. It may be the trigger only when interacting with stale atom-store state from the earlier bug.

## Severity

**Low for new substrates.** Stores created fresh on 1.7.72+ won't encounter the trigger condition — there's no opportunity for the pre-1.7.72 buggy parser to leave corrupted atoms behind.

**Medium for legacy stores.** Any Mercury store that ran INSERTs containing `\"` literals under 1.7.69/70/71 may have URIs in this corrupted state. Until a substrate-side reorganization tool exists, the workaround is: use a different subject URI for the corrupted record, accept the abandoned atom.

## Candidate mitigations

1. **Investigate hypothesis 3 first** — audit `ExpandPrefixedName`'s use of `_expandedTerm` as a shared backing buffer. If `GetTermValue` returns a span into `_expandedTerm` and a subsequent call overwrites it before the first quad is fully consumed by `AddCurrentBatched`, that's a span-aliasing bug that could silently drop quads. Trace the flow from `ExecuteInsertData` line 141-150 through `AddBatched` line 762 through `_atoms.Intern(...)` to confirm whether the spans are copied or referenced.

2. **Reorganization tool** — a one-time pass that re-interns all atoms in a store and rebuilds indexes from the canonical lexical forms. This is a Mercury-internal repair operation.

3. **Accept and document** — note in MERCURY.md and the bootstrap documentation that any store created before 1.7.72 may have a small population of URIs that need to be re-created under fresh names if multi-property writes silently drop.

## References

- Related fix: 1.7.72 `GetLexicalForm` / `GetLangTagOrDatatype` `IndexOf`-vs-`LastIndexOf` (commit `0a2f8f9`).
- Related bug not yet investigated: the SPARQL parser may store `\"` verbatim in literals rather than unescaping to `"`. Confirmed via HTTP JSON evidence on 2026-05-17 (`\\\"` in the response indicates literal backslash-quote stored). If true, fixing this at parse time would prevent future occurrences of the trigger condition.
- Conformance-coverage methodology: [`docs/process/emergence-epistemology-engineering/conformance-coverage-and-dogfood-discovery.md`](../process/emergence-epistemology-engineering/conformance-coverage-and-dogfood-discovery.md).
