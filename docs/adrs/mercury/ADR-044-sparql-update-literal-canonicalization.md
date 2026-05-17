# ADR-044: SPARQL UPDATE literal escape canonicalization

## Status

**Status:** Proposed — 2026-05-17

## Context

Mercury has two RDF ingestion paths and they disagree on the stored form of any literal containing escape sequences. The asymmetry was characterized in [`docs/limits/sparql-update-literal-escape-canonicalization.md`](../../limits/sparql-update-literal-escape-canonicalization.md) on 2026-05-17 by code reading; it surfaced from the 1.7.72 `GetLexicalForm` fix (commit `0a2f8f9`), which patched the downstream symptom but not the underlying root cause.

### The two paths

**Streaming parsers** (Turtle, N-Triples, N-Quads, TriG) decode escape sequences at parse time:

- `src/Mercury/Turtle/TurtleStreamParser.Terminals.cs:289-312` — `ParseStringLiteral` decodes via `ParseAndAppendEscapeToSb` into a `StringBuilder`, then wraps the decoded form with `string.Concat("\"", lexicalForm, "\"")`. A source `\"` becomes a real `"` character inside the stored value.
- `src/Mercury/NTriples/NTriplesStreamParser.cs:460-495`, `src/Mercury/NQuads/NQuadsStreamParser.cs:541+`, `src/Mercury/TriG/TriGStreamParser.cs:1257+` — same pattern.

**SPARQL parser** (used by `INSERT DATA`, `DELETE DATA`, `INSERT/DELETE WHERE`) stores literals verbatim:

- `src/Mercury/Sparql/Parsing/SparqlParser.cs:2259-2326` — `ParseLiteral` returns `Term.Literal(start, _position - start)`, a span into the original SPARQL source text. Escape sequences are scanned past for boundary detection only; no unescaping.
- `src/Mercury/Sparql/Execution/UpdateExecutor.cs:1265-1311` — `ExpandPrefixedName` short-circuits when `term[0] == '"'` and returns the term span unchanged. The literal flows to `Store.AddCurrentBatched` with `\"` preserved as two characters.

### Concrete divergence

For the logical RDF triple `<s> <p> "a\"b"`, empirically verified by probe 2026-05-17 (build a fresh `QuadStore`, ingest the same logical triple via both paths, read raw atom bytes):

| Path | Stored bytes (hex) | Stored form | Length |
|------|--------------------|-------------|--------|
| Turtle / N-Triples / N-Quads / TriG | `22 61 22 62 22` | `"a"b"` | 5 |
| SPARQL `INSERT DATA` | `22 61 5C 22 62 22` | `"a\"b"` | 6 |

The atom store interns these as **different atoms**. Consequences observable today:

- `SELECT ?o WHERE { <s> <p> "a\"b" }` written through the SPARQL parser pattern-matches the 6-char verbatim atom. Against Turtle-loaded data (5-char decoded atom), it returns zero rows. The user's SPARQL form is correct; the substrate's storage is split.
- `STR(?o)` returns `a\"b` (escape sequence as literal text) for SPARQL-loaded data, `a"b` (decoded) for Turtle-loaded data. Any downstream filter that operates on the lexical form sees different content for the same logical RDF.
- The 1.7.72 `GetLexicalForm` `LastIndexOf` patch keeps boundary detection correct on the verbatim form but does nothing about the lexical-form divergence.

### Storage representation — three options considered

Before deciding what the canonical form should be, three substrate representations were on the table:

| Option | Stored bytes for `"a\"b"@sv` | Length | `STR()` cost |
|---|---|---|---|
| **1. Verbatim source** | `"a\"b"@sv` | 9 | decode escapes on demand |
| **2. Wrapped-decoded** | `"a"b"@sv` | 8 | `Slice(1, lastQuote-1)` |
| **3. Tagged abstract** | payload=`a"b` + lang=`sv` + kind=Literal | 3 + metadata fields | return payload directly |

**Option 1 (verbatim)** preserves source bytes losslessly. `"a"b"@sv`, `"a\"b"@sv`, and `"""a"b"""@sv` stay distinguishable as different stored atoms even though RDF semantics says they're the same triple. Bytes are directly valid RDF source — directly roundtrippable. *Rejected:* violates RDF abstract-value semantics where escape-equivalent literals are the same triple. Forces every cross-format consumer to escape-normalize at query time.

**Option 2 (wrapped-decoded)** precomputes the decode and stores the result. The three escape-equivalent source forms above collapse to a single stored atom — RDF semantics honored at the storage layer. Bytes are NOT directly valid RDF source (the inner `"` characters are unescaped) but the atom store is an internal format, not a wire format; serialization writers re-escape on emit. The 2-byte `"..."` wrapper is doing real work — it's both the type discriminator (Literal vs URI `<...>` vs blank node `_:`) AND the content delimiter. Turtle / N-Triples / N-Quads / TriG already use this form.

**Option 3 (tagged abstract)** breaks the byte-string atom representation into a `(kind, payload, lang?, datatype?)` tuple. Cleanest match to the W3C abstract syntax model. *Rejected pragmatically:* requires a substrate-wide refactor across atom serialization, B+Tree key layouts, the trigram index, every `Value` consumer in `FilterEvaluator`, all parsers, all writers — estimated 2-3 weeks vs Option 2's 4-6 hours. The 2-byte overhead from Option 2's wrapping is roughly what Option 3 would need for the kind discriminator anyway, so the storage delta is small.

**This ADR adopts Option 2.** The SPARQL parser converges on the streaming parsers' wrapped-decoded form. Option 3 stays open as a future ADR if the substrate-wide refactor becomes justified by other pressures (e.g., a JSON-LD path that needs richer datatype handling, or a typed-literal performance push).

### Evidence of real-workload cost

- Commit `0a2f8f9` (1.7.72, 2026-05-17) was triggered by the recall-discipline rule's `rdfs:comment` containing `\"term\"` and being unfindable via `FILTER(CONTAINS(?c, "trigram"))`. The lesson about how to recall efficiently was itself unfindable via recall.
- Companion limit [`atom-store-corruption-from-failed-literal-insert.md`](../../limits/atom-store-corruption-from-failed-literal-insert.md) (also surfaced 2026-05-17) documents pre-1.7.72 URI atom corruption from the same root cause shape — INSERT DATA + escaped literal — that persists at the storage layer even after the GetLexicalForm fix.
- W3C SPARQL 1.1 Query conformance (421/421) does not exercise CONTAINS / STRSTARTS / STRENDS / REGEX against literals with escape sequences, so the conformance harness gives no signal on the divergence. Two dogfood-driven 1.7.72 bugs in the same week confirm the conformance-coverage-and-dogfood-discovery pattern at [`docs/process/emergence-epistemology-engineering/conformance-coverage-and-dogfood-discovery.md`](../../process/emergence-epistemology-engineering/conformance-coverage-and-dogfood-discovery.md).

## Hypothesis (falsifiable)

**H1 — Canonicalizing in the SPARQL parser converges atom-store identity across ingestion paths.** Modifying `SparqlParser.ParseLiteral` to decode escape sequences (matching `TurtleStreamParser.ParseShortStringLiteral`) and emit the canonical form will produce byte-identical stored atoms when the same logical triple is ingested via SPARQL `INSERT DATA` and via Turtle `LOAD`.

**Falsified if:** a paired-ingestion test (same logical triple via both paths into the same store) shows distinct atom IDs in the atom store after the change, OR if any of the streaming parsers turn out to produce a non-canonical form themselves (i.e., the "canonical" form isn't shared).

**H2 — The 1.7.72 `LastIndexOf` boundary-detection logic gracefully handles both legacy verbatim atoms and new canonical atoms.** `Value.GetLexicalForm`'s current `LastIndexOf('"')` (commit `0a2f8f9`) correctly locates the closing quote whether the literal interior contains `\"` (escape sequence as two chars) or `"` (decoded character).

**Falsified if:** a binding from a canonical atom (interior contains a real `"`) is fed to `GetLexicalForm` and returns the wrong slice, OR if any other consumer of `Value.StringValue` (FilterEvaluator function paths, result writers, SparqlExplain) assumes the literal interior contains no unescaped `"`.

**H3 — Legacy stores can be migrated via existing export → reload tooling.** A Cognitive-profile store created before this ADR ships can be made canonical-compatible by `mercury` export to N-Triples (uses canonical lexical forms) + bulk-load from that N-Triples file into a fresh store. No bespoke migration tool required.

**Falsified if:** the N-Triples writer emits any literal in a form that differs from what `NTriplesStreamParser` would store on re-read, OR if existing Cognitive stores contain bitemporal data that the export → reload round-trip drops.

## Decision

### Why Option 2's "malformed-looking" internal form is safe

A reader inspecting raw atom bytes will see things like `"a"b"` (5 bytes) and reasonably ask: "isn't this ambiguous?" If those bytes were RDF source, yes — a left-to-right parse would terminate at `"a"` and choke on the loose `b"`. The atom store is not RDF source. It relies on `LastIndexOf('"')` for boundary detection (commit `0a2f8f9`, 1.7.72), which is unambiguous *by W3C grammar*, not by Mercury convention:

Atom layout under Option 2:

```
"<content>"               plain literal
"<content>"@<langtag>     language-tagged literal
"<content>"^^<iri>        datatype-typed literal
```

After the closing wrapper `"`, the only valid trailing bytes are:

- **Lang tag** per BCP 47: `[a-zA-Z]+ ('-' [a-zA-Z0-9]+)*` — alphanumerics and `-` only. **No `"` permitted.**
- **Datatype IRI** per RFC 3987: explicitly excludes `<`, `>`, `"`, space, `{`, `}`, `|`, `\`, `^`, backtick. **No `"` permitted.**

So `LastIndexOf('"')` always finds the closing wrapper quote, regardless of how many `"` characters appear inside `<content>`. The safety property is a spec guarantee.

**Type discrimination** is similarly safe — each atom kind has a unique leading byte:

- Literal: `"…`
- URI: `<…` (URIs cannot contain `"` per RFC 3987)
- Blank node: `_:…`
- Numeric / boolean: digit / `+` / `-` / `.` / `t` / `f`

No collisions. The leading byte is a free discriminator.

**Edge cases verified** (probe + code reading, 2026-05-17):

| Source | Stored form | `GetLexicalForm` | `GetLangTagOrDatatype` |
|---|---|---|---|
| `"a\"b"@sv` | `"a"b"@sv` (8) | `a"b` | `@sv` |
| `"a\"\"\"b"` | `"a"""b"` (7) | `a"""b` | empty |
| `"abc\""` | `"abc""` (6) | `abc"` | empty |
| `"\""` | `"""` (3) | `"` | empty |
| `""` | `""` (2) | (empty) | empty |
| `""@en` | `""@en` (5) | (empty) | `@en` |
| `"email@x.com"` | `"email@x.com"` (13) | `email@x.com` | empty |
| `"a"b"` ≡ `"a\"b"` | `"a"b"` (5) | `a"b` | empty |

The last row is a quiet positive: two source forms collapse to one stored atom. RDF abstract-value semantics honored for free at the storage layer; no cross-encoding query divergence.

**What Option 2 gives up:** lossless source-encoding preservation. The substrate cannot tell whether the operator typed `\"` or `"` or used `"""..."""` long-literal syntax. Per RDF abstract syntax these are the same triple; per "what did the user actually type" they aren't. For a semantic-memory substrate this is the right trade — if lossless source preservation matters for a future use case (forensic, audit, replay), it lives in a separate provenance layer, not the atom store.

### Part 1 — Canonicalize in `SparqlParser.ParseLiteral`

Replace the verbatim span return with an unescape-into-buffer path that mirrors `TurtleStreamParser.ParseShortStringLiteral`:

```csharp
// Today (SparqlParser.cs:2259-2326):
return Term.Literal(start, _position - start);   // verbatim source span

// Proposed: when the literal contains a '\', accumulate decoded chars
// into a scratch buffer and return a Term that points at the decoded form.
// When the literal contains no '\', keep the zero-allocation source span.
```

Implementation outline:

- Scan the literal during boundary detection. If no `\` is seen, return the verbatim span as today (zero-allocation fast path).
- If `\` is seen, restart parsing into a per-parser scratch `StringBuilder` (or pooled `char[]` if the streaming parsers' pattern is the right model), decode `\"`, `\\`, `\n`, `\r`, `\t`, `\b`, `\f`, `\'`, `\uXXXX`, `\UXXXXXXXX` as the streaming parsers already do, then emit a `Term.Literal` that references the scratch.
- The `Term` representation must distinguish "span into source" from "span into scratch" — either via a flag on `Term` or via a per-parser convention that scratch lifetime exceeds the executor's batch boundary.

The cost is a per-literal-containing-escapes allocation. The fast path (no escapes) stays zero-allocation. Substrate-discipline tradeoff: parsing performance regression for INSERT DATA of literals with escapes, in exchange for correctness across ingestion paths.

### Part 2 — Tests must close the conformance-coverage gap

Two paired-ingestion tests in `Mercury.Tests`:

1. **Atom identity after paired ingestion.** Load `<s> <p> "a\"b"` via Turtle and via SPARQL `INSERT DATA` into the same store. Assert: `mercury_stats` reports exactly one atom for the object position, not two. Asserts H1 directly.
2. **Lexical-form convergence.** Same paired ingestion, then `SELECT ?o WHERE { <s> <p> ?o } FILTER(CONTAINS(STR(?o), "a\"b"))` matches both rows from both ingestion paths. Asserts H1 + H2 jointly.

The existing 1.7.72 regression set at `tests/Mercury.Tests/Sparql/SparqlEngineTests.cs` ("Regression: escaped quote in stored literal (1.7.72)") covers the SPARQL → SPARQL roundtrip; that suite stays as-is and continues to pass under the canonicalized path because the SPARQL filter literal would canonicalize the same way as the SPARQL INSERT literal.

### Part 3 — Migration for legacy stores

Three options, preferred order:

**A. Document the export → reload migration.** `mercury export <store> --format=nt > out.nt` then `mercury bulk-load <new-store> out.nt`. The N-Triples writer emits canonical lexical forms (verified pre-change by inspecting `NTriplesStreamWriter`); the bulk-load path uses the streaming parser, which already produces canonical atoms. Result: the new store has canonical atoms for every triple, including literals previously inserted via SPARQL with escapes. Operator effort: one CLI invocation per direction. No bespoke tooling.

**B. Tolerant read path.** `Value.GetLexicalForm`'s current `LastIndexOf('"')` (1.7.72) correctly handles both forms — see H2. New canonical writes coexist with legacy verbatim atoms in the same atom store; queries against either form go through the same boundary-detection logic. The cost: the atom store may contain duplicate atoms for the same logical literal until export → reload. Acceptable for Cognitive profile (bitemporal, low atom counts relative to Reference); not relevant for Reference profile (sealed, immutable, never ingested via SPARQL UPDATE).

**C. Bespoke re-intern tool.** A `mercury repair --canonicalize-literals` substrate command that walks the atom store, detects atoms with `\` escape sequences, re-interns under the canonical form, rewrites index entries to redirect the old ID to the new. Higher operator cost and substrate-side complexity; defers to A unless someone produces a Cognitive store where (A)'s round-trip is intolerable.

Recommend (A) for the documented migration path. (B) is in place by construction (the 1.7.72 LastIndexOf fix makes it true). (C) is built only if (A) proves insufficient on a real legacy substrate.

## Consequences

### Positive

- **Atom-store identity is preserved across ingestion paths.** Same logical triple → same atom regardless of which parser saw it. Cross-format queries return consistent results.
- **`STR(?o)` returns the same value regardless of ingestion path.** Filter predicates that operate on lexical form (`CONTAINS`, `STRSTARTS`, `STRENDS`, `REGEX`, `UCASE`, `LCASE`, `STRLEN`, `SUBSTR`) become path-independent.
- **The 1.7.72 `LastIndexOf` patch becomes redundant at insert time** (boundary detection no longer needs to skip escape sequences for newly inserted data) but remains load-bearing for legacy data under tolerant-read (Decision Part 3, option B). It is kept; the comment is updated to reflect both roles.
- **One fewer bug-shape class.** "Where does the literal end?" is no longer a question new code can get wrong, because the literal interior no longer contains characters that look like delimiters.
- **W3C compliance.** RDF abstract syntax defines literals as Unicode strings; escape sequences are concrete-syntax artefacts. Canonicalizing in the parser brings Mercury's SPARQL path into alignment with the abstract model.

### Negative / risks

- **Performance regression on INSERT DATA with escaped literals.** The zero-allocation fast path (verbatim source span) becomes an allocate-into-scratch path for any literal containing `\`. For substrates that bulk-INSERT via SPARQL with escaped literals at scale, this matters. Mitigation: most bulk-load goes through the streaming parsers (which already canonicalize); SPARQL INSERT is the cognitive-write path, not the bulk path; volume is low.
- **Term representation change.** If `Term` today implies "span into source," changing it to optionally point at a scratch buffer ripples through `UpdateExecutor`, the parser, and any caller that materializes term spans. Scope to characterize before committing.
- **Legacy-store atom duplication under tolerant read.** Until a Cognitive store is migrated via export → reload, the atom store may contain both `\"` and `"` forms of the same logical literal. Indexes route to one or the other depending on which path interned it. Query results stay consistent (LastIndexOf handles both) but storage is non-optimal. Mitigation: documented in the migration runbook.
- **`STR()` semantic change for SPARQL-INSERT-loaded data on upgrade.** A query that previously got `a\"b` from `STR(?o)` will get `a"b` after migration. SPARQL specification says the latter is correct; users relying on the literal-text-of-the-source-form behavior have a workaround (`REPLACE(STR(?o), "\"", "\\\\\"")` or similar). Acknowledge in the CHANGELOG.

### Neutral

- **Parser code path becomes uniform across formats.** SPARQL `ParseLiteral` adopts the same StringBuilder-based decoded-form pattern that Turtle / N-Triples / N-Quads / TriG already use. One fewer asymmetry between substrate ingestion paths.
- **No effect on serialization writers.** N-Triples / Turtle / SPARQL-result writers already emit canonical lexical forms; they consume `GetLexicalForm` output and re-escape on emit. The change to canonicalize on input does not require changes on output.

## Validation plan

1. **Existing regression suite remains green.** All 4,463 Mercury tests, including the 1.7.72 escaped-quote regression set at `tests/Mercury.Tests/Sparql/SparqlEngineTests.cs`, pass under the canonicalized path. This is the H2 falsification surface — if the LastIndexOf logic breaks on canonical atoms, the existing tests trip.
2. **New paired-ingestion tests** (Part 2 above) cover H1 + H2 jointly. Two tests; both must pass.
3. **`mercury_stats` paired-ingestion smoke test.** Manual scripted verification: empty Cognitive store → load `escaped-literals.ttl` via Turtle → INSERT DATA the same triples via SPARQL → assert `Store.AtomCount` equals the Turtle-only baseline (no doubling).
4. **Round-trip migration smoke test for Part 3 option A.** Build a Cognitive store via SPARQL INSERT DATA with escaped literals → export to N-Triples → bulk-load into a fresh store → query both stores with the same SPARQL → assert identical row sets (modulo the canonical-vs-verbatim divergence on STR output for the unmigrated store).
5. **W3C SPARQL 1.1 Query conformance** stays at 421/421 (no regression). W3C SPARQL 1.1 Update stays at 94/94.
6. **WDBench / WGPB benchmark suites unchanged.** Wikidata data flows in via N-Triples; the SPARQL parser change does not touch its ingestion path. Smoke-verify by running a small WDBench subset against `wiki-21b-ref` post-change and asserting result-set identity to the pre-change baseline.

Validation document: `docs/validations/adr-044-canonical-literals-{date}.md`.

## Alternatives considered

The three storage-representation options (verbatim source, wrapped-decoded, tagged abstract) are analyzed in **Context → Storage representation**. The remaining alternatives sit at the implementation-mechanism layer:

- **Canonicalize in `UpdateExecutor` instead of `SparqlParser`.** Same correctness outcome with a narrower blast radius (the Term shape stays "span into source"; UpdateExecutor decodes before handing off to the store). Pros: smaller diff, parser stays unchanged. Cons: leaves the `Term` API non-canonical — any future consumer of parsed terms (e.g., a SPARQL planner that materializes literals for optimization) sees the verbatim form and has to know to decode. The parser-side fix is the more substrate-correct location. Reconsider if the parser-side change ripples beyond expectations.
- **Tolerant writes via dual-storage.** At INSERT time, store both the canonical and verbatim atoms; at query time, match either form. Strictly more compatible with legacy data — no migration required. Rejected: doubles the atom count for any literal with escapes, wastes storage, adds a query-time UNION. Papers over the divergence rather than fixing it.
- **Schema-version bump that refuses to open pre-canonical stores.** Cleanest separation; legacy stores require explicit migration. Rejected as too aggressive — Cognitive stores hold session memory the operator may not want to migrate on a substrate upgrade. The tolerant-read property of the 1.7.72 LastIndexOf logic (H2) lets old and new coexist; that's better.
- **Do nothing and document the divergence.** Tell users "SPARQL INSERT and Turtle LOAD produce semantically distinct triples for literals with escapes." Rejected: violates RDF specification (escape sequences are syntactic, the abstract value is the decoded string); inconsistent with the substrate-discipline rule that surface semantics match the W3C model.

## Engineering effort estimate

- Part 1 (parser canonicalization): ~3-4 hours. Most of the work is identifying the right `Term` representation change and the per-parser scratch lifetime. The decode logic itself is a straightforward copy of `TurtleStreamParser.ParseAndAppendEscapeToSb` (43 lines).
- Part 2 (paired-ingestion tests): ~1 hour. Two tests, both straightforward extensions of the existing `SparqlEngineTests` shape.
- Part 3A (migration runbook): ~30 min. CHANGELOG note + one-paragraph addition to MERCURY.md upgrade notes pointing at `mercury export` + `bulk-load`.
- Total: ~4-6 hours implementation + validation.

If the `Term` change ripples wider than expected, Part 1 grows; the UpdateExecutor-only alternative (alternatives above) bounds the blast radius at the cost of substrate purity.

## References

- Surfacing limit: [`docs/limits/sparql-update-literal-escape-canonicalization.md`](../../limits/sparql-update-literal-escape-canonicalization.md) — characterization that this ADR promotes.
- Companion limit: [`docs/limits/atom-store-corruption-from-failed-literal-insert.md`](../../limits/atom-store-corruption-from-failed-literal-insert.md) — same root cause; the URI-specific symptom this ADR's canonicalization closes structurally.
- 1.7.72 GetLexicalForm fix: commit `0a2f8f9` — the downstream patch that motivated the deeper investigation; H2 above codifies the back-compat property the patch added.
- 1.7.72 regression test set: commit `5aa4c79`, `tests/Mercury.Tests/Sparql/SparqlEngineTests.cs` region "Regression: escaped quote in stored literal (1.7.72)" — the existing 4-test pin that this ADR's paired-ingestion additions sit alongside.
- Conformance-coverage methodology: [`docs/process/emergence-epistemology-engineering/conformance-coverage-and-dogfood-discovery.md`](../../process/emergence-epistemology-engineering/conformance-coverage-and-dogfood-discovery.md) — the meta-pattern that two 1.7.72 bugs in one week reinforced; this ADR is the architectural close on the literal-storage branch of that pattern.
- Streaming parser canonical implementations: `src/Mercury/Turtle/TurtleStreamParser.Buffer.cs:519` (`ParseAndAppendEscapeToSb`), `src/Mercury/NTriples/NTriplesStreamParser.cs:551`, `src/Mercury/NQuads/NQuadsStreamParser.cs:658`, `src/Mercury/TriG/TriGStreamParser.cs:1513`.
- SPARQL parser current verbatim path: `src/Mercury/Sparql/Parsing/SparqlParser.cs:2259-2326`, `src/Mercury/Sparql/Execution/UpdateExecutor.cs:1265-1311`.
