# ADR-044: SPARQL UPDATE literal escape canonicalization

## Status

**Status:** Proposed (rev 2) — 2026-05-17 (Proposed 2026-05-17; Accepted 2026-05-17; reverted to Proposed 2026-05-17 after implementation-stage read surfaced wider scope — see "Scope revision" below).

## Scope revision (2026-05-17)

The rev 1 framing treated this as a UPDATE-path issue ("SPARQL UPDATE stores literals verbatim; streaming parsers canonicalize") and proposed canonicalizing in `SparqlParser.ParseLiteral`. Implementation-stage read of the parser found two problems with that framing:

1. **The ref-struct constraint.** `SparqlParser` is declared `internal ref partial struct` and holds `_source` as `ReadOnlySpan<char>`. It cannot hold managed scratch (`StringBuilder`, `string`, `char[]` fields) without changing its storage class. The clean "decode into parser scratch" pattern the streaming parsers use isn't directly portable.

2. **The literal-consumer surface is wider than the UPDATE path.** SPARQL source literals flow to the atom store (and to atom-store-match comparisons) via *every* consumer that materializes a `Term`. Canonicalizing only the UpdateExecutor write path opens a NEW asymmetry: stored atoms become canonical (`"a"b"`, 5 bytes) but FILTER literal arguments stay verbatim (`"a\"b"`, 6 bytes) — `FILTER(?o = "a\"b")` against a canonical-stored atom returns zero rows. The bug moves; it doesn't close.

The full surface (enumerated in revised Context below):

- `UpdateExecutor.GetTermValue` — write path. (rev 1 target.)
- `DELETE WHERE` / `INSERT WHERE` pattern literal positions — atom-store-match path.
- `FILTER(?o = "...")`, `FILTER(CONTAINS(?o, "..."))`, `FILTER(STRSTARTS/STRENDS/REGEX(?o, "..."))` — filter-literal arguments compared against bound values.
- `BIND("..." AS ?x)` — expression-literal arguments.
- `SELECT/ASK/CONSTRUCT/DESCRIBE` pattern object positions — atom-store-match path.

Revision direction: this ADR rev 2 reframes the Decision around the centralized hub (parser-extended-buffer OR shared materialization helper) so every literal consumer sees the canonical form, not just the UPDATE path. Engineering effort estimate revised from 4-6h to ~10-12h. Validation plan extended to include cross-form FILTER paired-ingestion tests.

A staged delivery (write-path now, parser-side later as ADR-045) was explicitly considered and rejected — see Alternatives. The architectural reasoning: a half-canonicalized substrate creates new bug shapes during the gap window; better to ship the coherent unit even at higher cost.

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

### Full literal-consumer surface

Every place a SPARQL source literal materializes into a `ReadOnlySpan<char>` that flows to atom storage OR to atom-store-matching comparison. The rev 1 framing covered only the first row.

| Consumer | Path | What happens to a literal with `\"` |
|---|---|---|
| `UpdateExecutor.GetTermValue` (line 1214) | `INSERT DATA / DELETE DATA` | Verbatim span → `Store.AddCurrentBatched` → interned as verbatim atom. |
| `UpdateExecutor` DELETE WHERE single-pattern fast path (line 206-211) | `DELETE WHERE { <s> <p> "lit" }` (no variables) | Verbatim span → `Store.DeleteCurrent` — must match the stored atom byte-for-byte to delete. |
| `MultiPatternScan` literal positions (`src/Mercury/Sparql/Execution/Operators/MultiPatternScan.cs:957`, `1195`, `1210`, `1226`) | Pattern object in `SELECT/ASK/CONSTRUCT/DESCRIBE` | Verbatim span → numeric / boolean / string literal extraction → compared against bound values from store. |
| `TriplePatternScan` literal positions (`src/Mercury/Sparql/Execution/Operators/TriplePatternScan.cs:1527`, `1600`) | Single triple-pattern scan | Verbatim span → atom-store key lookup. |
| `CrossGraphMultiPatternScan` (`src/Mercury/Sparql/Execution/Operators/CrossGraphMultiPatternScan.cs:365`) | Pattern under `GRAPH` clause | Verbatim span → cross-graph atom lookup. |
| `FilterEvaluator` literal arguments | `FILTER(?o = "lit")`, `FILTER(CONTAINS(?o, "lit"))`, `FILTER(STRSTARTS/STRENDS/REGEX(?o, "lit"))` | Verbatim source → `Value.StringValue` → `GetLexicalForm` returns slice including `\"` for verbatim, raw `"` for canonical. **Cross-form mismatch.** |
| `FilterEvaluator.STRDT` / `STRLANG` / `STRUUID` etc. | Computed literal results | Output form follows internal representation chosen by this ADR. |
| `BindExecutor` literal arguments | `BIND("lit" AS ?x)` | Verbatim source → bound value → flows to downstream consumers. |

The atom-store-match consumers (rows 2-5) are the critical insight. They take a SPARQL source literal and compare it to a stored atom for equality. If stored atoms are canonical but pattern literals are verbatim, the comparison fails for any literal containing `\`. The rev 1 framing missed this entirely — it focused on the storage side (write path) without tracing where stored atoms get compared *back* against source literals.

The filter consumers (rows 6-8) compound the asymmetry. `FILTER(?o = "a\"b")` against a canonical atom `"a"b"`:
- Bound `?o` → `Value.GetLexicalForm()` → `a"b` (3 chars).
- Filter literal `"a\"b"` → `Value.GetLexicalForm()` → `a\"b` (4 chars).
- Equality returns false. The substrate has the right triple stored; the user's query can't find it.

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

**H4 — Centralized canonicalization closes cross-form FILTER mismatch.** *Added in rev 2.* Routing every SPARQL source-literal materialization through `LiteralForm.Canonicalize` means FILTER literal arguments and pattern literal positions match canonical stored atoms exactly, regardless of whether the original SPARQL source used `\"`, `"`, or `"""..."""` syntax.

**Falsified if:** a paired test of the form *(insert via Turtle) → FILTER(?o = "a\"b")* returns zero rows when canonicalization is enabled, OR if any literal-consuming operator was missed and its bound spans remain non-canonical, OR if `Value.GetLexicalForm`'s slice-from-quotes logic produces different output on the canonicalized SPARQL filter literal than on the canonical stored atom for the same logical value.

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

### Part 1 — Introduce a `LiteralCanonicalizer` helper, call it at every materialization site

The ref-struct parser cannot directly hold managed scratch (rev 2 Scope revision, point 1). Two architectures were considered for getting around this:

**A. Parser-extended-buffer.** Caller pre-allocates a span larger than the SPARQL source; parser copies source to leading portion, writes decoded literals to trailing portion, emits Terms with sentinel-encoded offsets distinguishing source from scratch. Pros: zero call-site changes downstream. Cons: changes parser API (new `Span<char>` argument), requires size estimation by callers, Term layout encoding rule for consumers to know.

**B. Materialization-site helper.** Introduce a static `LiteralCanonicalizer` (or `LiteralForm.Canonicalize`) that takes a verbatim source-literal span and returns the canonical form. Fast path: no `\` in literal → return verbatim span unchanged (zero allocation). Slow path: decode into caller-owned `char[]` scratch or a new `string`. Each materialization site changes from `_source.Slice(term.Start, term.Length)` to `LiteralForm.Canonicalize(_source.Slice(term.Start, term.Length), ref _scratch)`. Pros: parser stays untouched (preserves ref-struct discipline); changes are mechanical and localized; one canonical implementation. Cons: ~10+ call sites change (enumerated in Context).

**Chosen: B.** The mechanical multi-site change is preferable to the API shape change. Each call site is a one-line edit; the canonicalizer itself is a single function with a fast-path / slow-path split.

The canonicalizer signature:

```csharp
internal static class LiteralForm
{
    /// Returns canonical wrapped-decoded form of a SPARQL source literal.
    /// Fast path (no '\\' in literal): returns the verbatim span unchanged.
    /// Slow path: decodes \", \\, \n, \r, \t, \b, \f, \', \uXXXX, \UXXXXXXXX
    /// into the caller's scratch and returns a span over the decoded result.
    public static ReadOnlySpan<char> Canonicalize(ReadOnlySpan<char> sourceLiteral, ref char[] scratch);

    /// Helper variant for callers that don't want to manage scratch.
    /// Allocates a new string for the slow path; fast path returns verbatim.
    public static ReadOnlySpan<char> Canonicalize(ReadOnlySpan<char> sourceLiteral, out string? scratchOwner);
}
```

The decode loop is a copy of `TurtleStreamParser.ParseAndAppendEscapeToSb` (43 lines, well-tested via the W3C Turtle conformance suite). Only literal positions (`sourceLiteral[0] == '"'`) need canonicalization; URIs, blank nodes, numerics, booleans pass through unchanged.

### Part 2 — Update every literal-materialization site

The Context section enumerates the full surface. Each site changes mechanically:

```csharp
// Before:
var obj = GetTermValue(quad.ObjectStart, quad.ObjectLength);
// or:
var literal = _source.Slice(term.Start, term.Length);

// After:
var obj = LiteralForm.Canonicalize(GetTermValue(quad.ObjectStart, quad.ObjectLength), ref _literalScratch);
// or:
var literal = LiteralForm.Canonicalize(_source.Slice(term.Start, term.Length), ref _literalScratch);
```

Sites (ordered by complexity, simpler first):

1. `UpdateExecutor.GetTermValue` — write path. Add a per-call scratch via existing `_expandedTerm` field pattern or new `_literalScratch`.
2. `UpdateExecutor` DELETE WHERE single-pattern fast path (line 206-211).
3. `MultiPatternScan` literal positions (lines 957, 1195, 1210, 1226).
4. `TriplePatternScan` literal positions (lines 1527, 1600).
5. `CrossGraphMultiPatternScan` (line 365).
6. `FilterEvaluator` — the trickier site. `Value.StringValue` is currently a `ReadOnlySpan<char>` slice; callers like CONTAINS, STRSTARTS, etc. operate on it. Two sub-choices: (a) canonicalize at the point the literal source becomes a `Value`, or (b) canonicalize on-demand inside each FilterEvaluator built-in. Sub-option (a) preferred; the cost is paid once per literal, not once per filter call.
7. `BindExecutor` literal arguments — same shape as filter literals.

### Part 3 — Tests must close the cross-form conformance gap

Three categories of paired-ingestion tests in `Mercury.Tests`:

1. **Atom identity after paired ingestion.** Load `<s> <p> "a\"b"` via Turtle and via SPARQL `INSERT DATA` into the same store. Assert: `Store.AtomCount` reports the same number after both inserts as after just one (semantic dedupe). Asserts H1 directly.
2. **Cross-form FILTER equality.** Insert canonical atoms via Turtle. Run `SELECT ?o WHERE { ?s ?p ?o FILTER(?o = "a\"b") }`. Assert: returns the row. Asserts H4 (new — see below).
3. **Cross-form FILTER substring.** Same setup. Run `FILTER(CONTAINS(?o, "a\"b"))`. Assert: returns the row. Asserts H4.

A new falsifiable hypothesis H4 should be added to the Hypothesis section: "Canonicalizing at every materialization site means SPARQL source literals match canonical stored atoms regardless of which path (write or filter) they entered through."

The existing 1.7.72 regression set at `tests/Mercury.Tests/Sparql/SparqlEngineTests.cs` covers the SPARQL → SPARQL roundtrip. Under the canonicalized path, both sides (INSERT literal and FILTER literal) canonicalize identically, so the existing tests continue to pass.

### Part 4 — Migration for legacy stores

(was Part 3 in rev 1; renumbered after Part 2 expansion above.) Three options, preferred order:

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

- **Performance regression on escape-containing literals.** The zero-allocation fast path (verbatim source span) becomes an allocate-into-scratch path for any literal containing `\`. Affects both write-path (INSERT DATA) and read-path (FILTER literal arguments, pattern literal positions). Mitigation: most bulk-load goes through the streaming parsers (which already canonicalize); SPARQL INSERT is the cognitive-write path, not the bulk path; volume is low. Hot SPARQL queries with many escape-containing filter literals would see a slight regression; profiling would tell if it matters.
- **~10 materialization sites change.** The Context-enumerated surface is the change blast radius. Each site needs a scratch field plus the `LiteralForm.Canonicalize` call. Risk: missing a site means cross-form FILTER returns empty rows for that path. The Validation plan tests (Parts 3-6) are designed to surface this; categorize each test failure as "missed site to fix" or "legitimate canonicalization side-effect."
- **Legacy-store atom duplication under tolerant read.** Until a Cognitive store is migrated via export → reload, the atom store may contain both `\"` and `"` forms of the same logical literal. Indexes route to one or the other depending on which path interned it. Query results stay consistent (LastIndexOf handles both) but storage is non-optimal. Mitigation: documented in the migration runbook (Part 4A).
- **`STR()` semantic change for legacy SPARQL-INSERT data on upgrade.** A query that previously got `a\"b` from `STR(?o)` on legacy-stored data will get `a"b` after the store is migrated via export → reload. SPARQL specification says the latter is correct; users relying on the literal-text-of-the-source-form behavior have a workaround (`REPLACE(STR(?o), "\"", "\\\\\"")` or similar). Acknowledge in the CHANGELOG.

### Neutral

- **Substrate code path becomes uniform across SPARQL consumers.** Every literal materialization site adopts the same canonicalization gate. One canonical implementation; no per-consumer drift risk going forward.
- **No effect on serialization writers.** N-Triples / Turtle / SPARQL-result writers already emit canonical lexical forms; they consume `GetLexicalForm` output and re-escape on emit. The change to canonicalize at input boundaries does not require changes on output.
- **Streaming parsers (Turtle/N-Triples/N-Quads/TriG) unchanged.** They already canonicalize. Their interaction with FilterEvaluator is what becomes consistent: a literal loaded via Turtle now matches an identical filter literal authored in SPARQL.

## Validation plan

*Expanded in rev 2 to cover the full literal-consumer surface.*

1. **Existing regression suite remains green.** All 4,463 Mercury tests, including the 1.7.72 escaped-quote regression set at `tests/Mercury.Tests/Sparql/SparqlEngineTests.cs`, pass under the canonicalized path. This is the H2 falsification surface — if the `LastIndexOf` logic breaks on canonical atoms, the existing tests trip.
2. **Paired-ingestion atom-identity test** (H1). Turtle + SPARQL insert of the same logical triple → `Store.AtomCount` after both inserts equals count after one (semantic dedupe).
3. **Cross-form FILTER tests** (H4). Insert via Turtle, query via SPARQL with FILTER literal that uses `\"` escapes — all of `?o = ...`, `CONTAINS`, `STRSTARTS`, `STRENDS`, `REGEX` must return the expected rows. Run the matrix in reverse too (SPARQL insert, FILTER via Turtle? — not realistic; FILTER literals are always SPARQL source).
4. **Cross-form pattern test.** Insert via Turtle, query via SPARQL pattern with literal in object position: `SELECT ?s WHERE { ?s ?p "a\"b" }` — should return the row.
5. **Cross-form DELETE WHERE test.** Insert via Turtle, then `DELETE WHERE { <s> <p> "a\"b" }` — should delete the row.
6. **BIND canonical-form test.** `BIND("a\"b" AS ?x) FILTER(?x = ?o)` where `?o` is bound to a canonical stored atom.
7. **Round-trip migration smoke test for Part 4A.** Build a Cognitive store via SPARQL INSERT DATA with escaped literals (under pre-canonicalization release) → export to N-Triples → bulk-load into a fresh store on the new release → query both stores with the same SPARQL → assert identical row sets.
8. **W3C SPARQL 1.1 Query conformance** stays at 421/421 (no regression). W3C SPARQL 1.1 Update stays at 94/94.
9. **W3C Turtle / N-Triples / N-Quads / TriG / RDF-XML** all stay at their respective passing counts — these parsers don't change but their interaction with FilterEvaluator does (canonicalized comparisons).
10. **WDBench / WGPB benchmark suites unchanged.** Wikidata data flows in via N-Triples; canonicalization at filter-side may change result identity for queries that exercise escape-containing filter literals. Run a small WDBench subset against `wiki-21b-ref` post-change and assert result-set identity to the pre-change baseline; investigate any divergence as either (a) a pre-change bug now fixed, or (b) a regression to address.

Validation document: `docs/validations/adr-044-canonical-literals-{date}.md`.

## Alternatives considered

The three storage-representation options (verbatim source, wrapped-decoded, tagged abstract) are analyzed in **Context → Storage representation**. The remaining alternatives sit at the implementation-mechanism layer:

- **Parser-extended-buffer (Option A from Decision Part 1).** Caller pre-allocates an oversized buffer; parser writes decoded literals to the trailing scratch region and emits Terms with sentinel-encoded offsets. Pros: consumers see canonical Terms without per-call decoding. Cons: parser API change (extra `Span<char>` argument), caller size-estimation burden, sentinel-encoding rule for downstream consumers. **Rejected in favor of the materialization-site helper (Option B).** Reconsider if Option B's performance turns out to matter at scale (the per-materialization-site call is a cheap check + occasional decode; profiling would tell).
- **Stage as write-path-only now, parser-side later as ADR-045.** *Explicitly considered and rejected in rev 2.* Sticks to rev 1's 4-6h effort by canonicalizing only `UpdateExecutor.GetTermValue`; defers FILTER/pattern canonicalization to a future ADR. Rejected because: (a) opens a NEW cross-form FILTER asymmetry that doesn't exist today (literal stored verbatim matches verbatim filter), (b) creates a transition window where the substrate has BOTH inconsistencies present (legacy verbatim atoms + new canonical atoms + verbatim filters), (c) the substrate-discipline rule of "ship the minimum coherent unit" interprets coherence at the user-visible behavior level, not the lines-of-code level — a half-canonicalized substrate isn't coherent.
- **Canonicalize in `UpdateExecutor` only (rev 1 alternative).** Subsumed by the staging rejection above. Removed from this list because it's the same proposal under a different name.
- **Tolerant writes via dual-storage.** At INSERT time, store both the canonical and verbatim atoms; at query time, match either form. Strictly more compatible with legacy data — no migration required. Rejected: doubles the atom count for any literal with escapes, wastes storage, adds a query-time UNION. Papers over the divergence rather than fixing it.
- **Schema-version bump that refuses to open pre-canonical stores.** Cleanest separation; legacy stores require explicit migration. Rejected as too aggressive — Cognitive stores hold session memory the operator may not want to migrate on a substrate upgrade. The tolerant-read property of the 1.7.72 LastIndexOf logic (H2) lets old and new coexist; that's better.
- **Do nothing and document the divergence.** Tell users "SPARQL INSERT and Turtle LOAD produce semantically distinct triples for literals with escapes." Rejected: violates RDF specification (escape sequences are syntactic, the abstract value is the decoded string); inconsistent with the substrate-discipline rule that surface semantics match the W3C model.

## Engineering effort estimate

*Revised in rev 2 to reflect the full literal-consumer surface.*

- Part 1 (`LiteralForm.Canonicalize` helper): ~2 hours. Single static method, fast path + slow path, decode loop ported from `TurtleStreamParser.ParseAndAppendEscapeToSb`. Direct unit tests in `SkyOmega.Bcl.Tests` or a new `LiteralFormTests`.
- Part 2 (update each materialization site): ~3-4 hours. ~10 call sites enumerated in Context. Mechanical edits but each needs a scratch field on its owning class + verification that the spans returned outlive their consumers (the same span-aliasing analysis the existing `_expandedTerm` field already navigates).
- Part 3 (tests — paired ingestion + cross-form FILTER + DELETE WHERE + BIND): ~2-3 hours. ~6-8 tests across the categories in Validation plan.
- Part 4A (migration runbook): ~30 min. CHANGELOG note + one-paragraph addition to MERCURY.md.
- Validation pass (full Mercury suite + W3C SPARQL + small WDBench): ~1-2 hours. Identify any benchmark divergence as bug-fix vs regression.
- Total: ~10-12 hours implementation + validation.

If a missed materialization site shows up during validation (a literal arriving at the atom store comparison via a code path not in the Context enumeration), Part 2 grows by the time to find + plumb that site. The validation tests are designed to surface this: cross-form FILTER returning unexpected zero rows = missed site.

## References

- Surfacing limit: [`docs/limits/sparql-update-literal-escape-canonicalization.md`](../../limits/sparql-update-literal-escape-canonicalization.md) — characterization that this ADR promotes.
- Companion limit: [`docs/limits/atom-store-corruption-from-failed-literal-insert.md`](../../limits/atom-store-corruption-from-failed-literal-insert.md) — same root cause; the URI-specific symptom this ADR's canonicalization closes structurally.
- 1.7.72 GetLexicalForm fix: commit `0a2f8f9` — the downstream patch that motivated the deeper investigation; H2 above codifies the back-compat property the patch added.
- 1.7.72 regression test set: commit `5aa4c79`, `tests/Mercury.Tests/Sparql/SparqlEngineTests.cs` region "Regression: escaped quote in stored literal (1.7.72)" — the existing 4-test pin that this ADR's paired-ingestion additions sit alongside.
- Conformance-coverage methodology: [`docs/process/emergence-epistemology-engineering/conformance-coverage-and-dogfood-discovery.md`](../../process/emergence-epistemology-engineering/conformance-coverage-and-dogfood-discovery.md) — the meta-pattern that two 1.7.72 bugs in one week reinforced; this ADR is the architectural close on the literal-storage branch of that pattern.
- Streaming parser canonical implementations: `src/Mercury/Turtle/TurtleStreamParser.Buffer.cs:519` (`ParseAndAppendEscapeToSb`), `src/Mercury/NTriples/NTriplesStreamParser.cs:551`, `src/Mercury/NQuads/NQuadsStreamParser.cs:658`, `src/Mercury/TriG/TriGStreamParser.cs:1513`.
- SPARQL parser current verbatim path: `src/Mercury/Sparql/Parsing/SparqlParser.cs:2259-2326`, `src/Mercury/Sparql/Execution/UpdateExecutor.cs:1265-1311`.
