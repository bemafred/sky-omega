# Limit: SPARQL UPDATE stores literals verbatim; streaming parsers canonicalize

Status:        **Resolved** by [ADR-044](../adrs/mercury/ADR-044-sparql-update-literal-canonicalization.md) (Completed in Mercury 1.7.73, 2026-05-17)
Surfaced:      2026-05-17, while investigating whether parser storage should canonicalize escaped literals at insert time (follow-up to 1.7.72 `GetLexicalForm` fix in commit `0a2f8f9`)
Last reviewed: 2026-05-17
Resolved by:   [ADR-044](../adrs/mercury/ADR-044-sparql-update-literal-canonicalization.md) shipped 2026-05-17 in Mercury 1.7.73. Iteration trail: rev 1 (briefly Accepted) → rev 2 (wider consumer surface) → rev 3 (verified surface enumeration + duplication finding) → cleanup pass → Accepted → Parts 1+2+3+4+5A implemented → Completed, all same day. Substrate-level decision (Option 2 wrapped-decoded canonicalization) was confirmed at rev 1; reworks were about Decision mechanism (helper + per-site materialization) and implementation decomposition (Phase 0 consolidation pre-work). Validation: 4,515 / 0 failed / 6 skipped + end-to-end dogfood via mercury CLI.

## Description

Mercury has two ingestion paths for RDF literals and they disagree on storage form for any literal containing escape sequences.

**Streaming parsers (Turtle, N-Triples, N-Quads, TriG)** decode escape sequences at parse time:

- `src/Mercury/Turtle/TurtleStreamParser.Terminals.cs:289-312` — `ParseStringLiteral` decodes via `ParseAndAppendEscapeToSb`, then wraps with `string.Concat("\"", lexicalForm, "\"")`. A source `\"` becomes a real `"` character inside the stored value.
- `src/Mercury/NTriples/NTriplesStreamParser.cs:460-495` — `ParseLiteralSpan` calls `ParseEscapeSequence` and appends the decoded rune.
- `src/Mercury/NQuads/NQuadsStreamParser.cs:541+` and `src/Mercury/TriG/TriGStreamParser.cs:1257+` — same pattern.

**SPARQL parser (used by INSERT DATA / DELETE DATA / INSERT/DELETE WHERE)** stores literals verbatim:

- `src/Mercury/Sparql/Parsing/SparqlParser.cs:2259-2326` — `ParseLiteral` returns `Term.Literal(start, _position - start)`, a span into the original SPARQL source text. Escape sequences are scanned past for boundary detection only; no unescaping.
- `src/Mercury/Sparql/Execution/UpdateExecutor.cs:1265-1311` — `ExpandPrefixedName` short-circuits when `term[0] == '"'` and returns the term span unchanged. The literal flows to `Store.AddCurrentBatched` with `\"` preserved as two characters.

## Concrete divergence

For the logical RDF triple `<s> <p> "a\"b"`:

| Path | Stored bytes (object position) | Length |
|------|-------------------------------|--------|
| Turtle/N-Triples/N-Quads/TriG | `"a"b"` | 5 |
| SPARQL INSERT DATA | `"a\"b"` | 7 |

The atom store interns these as **different atoms**. A SPARQL query that pattern-matches `?s ?p "a\"b"` against the Turtle-loaded form will not match (and vice versa). `FILTER(?o = "a\"b")` will not match against the streaming-parser-loaded form because the SPARQL filter literal is itself parsed by the SPARQL parser into the 7-char verbatim form.

## Why the 1.7.72 fix doesn't close this

Commit `0a2f8f9` patched `Value.GetLexicalForm` and `Value.GetLangTagOrDatatype` to use `LastIndexOf('"')` instead of `IndexOf('"')` for closing-quote detection — so CONTAINS, STRSTARTS, STRENDS, UCASE, LCASE, REGEX now work on SPARQL-stored literals with escapes. But:

- The lexical form returned by `GetLexicalForm` on the SPARQL path still contains `\"` (the literal backslash and quote characters), not `"` (the decoded character). So `STR(?c)` returns the escape sequence to the user, not the logical string.
- For Turtle-ingested data, `GetLexicalForm` returns `a"b` (decoded). For SPARQL-ingested data of the same logical triple, it returns `a\"b` (escaped). Any string-manipulation predicate sees different content.
- The asymmetry persists at the bytes level, which the limit on `atom-store-corruption-from-failed-literal-insert` already noted at line 86 of that file.

## Trigger condition

Same logical RDF dataset ingested via two paths produces different stored bytes and different SPARQL query results. Concretely:

1. Load a Turtle file containing one triple with a `\"` literal.
2. INSERT DATA the same triple as SPARQL.
3. Query `SELECT (COUNT(*) AS ?n) WHERE { ?s ?p ?o }` — expect 1 (semantic), observe 2 (atom-store distinct).

Also: any FILTER that operates on the lexical form (CONTAINS, REGEX, STR, UCASE, ...) returns different results depending on which ingestion path created the data, which is invisible to the user.

## Current state

Asymmetry confirmed by code reading. Not yet exercised by a paired ingestion test. The `0a2f8f9` regression set added in `tests/Mercury.Tests/Sparql/SparqlEngineTests.cs` (region "Regression: escaped quote in stored literal") only covers the SPARQL → SPARQL roundtrip, where both sides see the verbatim form and CONTAINS finds the after-escape substring as long as the search string also matches against the verbatim form (it does in those tests).

The pre-1.7.72 GetLexicalForm bug was a downstream consequence of this same root cause — code that walked the stored literal couldn't tell `\"` from `"`. Fixing only GetLexicalForm patched one consumer; the underlying ambiguity remains for any future code path that walks literal contents.

## Candidate mitigations

1. **Canonicalize in SPARQL parser** (preferred long-term). Make `SparqlParser.ParseLiteral` decode escape sequences into a string buffer the way the streaming parsers do, then store the decoded form. This brings SPARQL into parity with Turtle/N-Triples/N-Quads/TriG.
   - Cost: SPARQL parser no longer zero-allocation for literals containing escapes (the literal span is currently a slice into the source buffer). For literals without escapes, the fast path can remain a span.
   - Risk: existing stores have SPARQL-path verbatim literals. A migration would be needed, or queries would silently break on legacy data. Same compatibility surface as the related URI-atom corruption limit.

2. **Canonicalize in UpdateExecutor** (lighter touch). Leave the parser alone; have `UpdateExecutor` decode the escape sequences before passing to `Store.AddCurrentBatched`. Same correctness outcome, narrower blast radius.

3. **Accept and document** (worst option). Tell users "SPARQL INSERT and Turtle LOAD produce semantically distinct triples for literals with escapes." This violates RDF specification — escape sequences are syntactic, the abstract value is the decoded string.

## Engineering effort estimate

- Option 1 or 2: ~4-6 hours including the cross-format paired test that should accompany it (the missing test that would have surfaced this years earlier).
- Migration tooling for legacy stores: ~1-2 hours for a one-pass re-intern script.

## References

- Companion limit: [`atom-store-corruption-from-failed-literal-insert.md`](atom-store-corruption-from-failed-literal-insert.md), which flagged this asymmetry in its References section without filing it as its own entry.
- 1.7.72 GetLexicalForm fix: commit `0a2f8f9`.
- 1.7.72 regression tests: `tests/Mercury.Tests/Sparql/SparqlEngineTests.cs` region "Regression: escaped quote in stored literal (1.7.72)".
- Streaming parser unescape implementations: `TurtleStreamParser.Buffer.cs:519`, `NTriplesStreamParser.cs:551`, `NQuadsStreamParser.cs:658`, `TriGStreamParser.cs:1513`.
- SPARQL parser verbatim path: `SparqlParser.cs:2259-2326`, `UpdateExecutor.cs:1265-1311`.
- Conformance-coverage methodology: [`docs/process/emergence-epistemology-engineering/conformance-coverage-and-dogfood-discovery.md`](../process/emergence-epistemology-engineering/conformance-coverage-and-dogfood-discovery.md) — both 1.7.72 bug discoveries reinforce that real workloads keep finding bugs in shapes W3C SPARQL 1.1 Query conformance (421/421) doesn't exercise.
