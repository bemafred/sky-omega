# ADR-044: SPARQL UPDATE literal escape canonicalization

## Status

**Status:** Completed — 2026-05-17 (Proposed 2026-05-17 [rev 1] → briefly Accepted → reverted to Proposed [rev 2] → Proposed [rev 3] → Accepted → Completed all same-day; implementation shipped Mercury 1.7.73 in Parts 1+2+3+4+5A; full Mercury test suite 4,515 / 0 failed / 6 skipped; dogfood validation passed end-to-end via mercury CLI; see "Scope revision" for the iteration trail and Validation plan for the test matrix).

## Scope revision

### Rev 1 → rev 2 (2026-05-17)

The rev 1 framing treated this as a UPDATE-path issue ("SPARQL UPDATE stores literals verbatim; streaming parsers canonicalize") and proposed canonicalizing in `SparqlParser.ParseLiteral`. Implementation-stage read of the parser found two problems with that framing:

1. **The ref-struct constraint.** `SparqlParser` is declared `internal ref partial struct` and holds `_source` as `ReadOnlySpan<char>`. It cannot hold managed scratch (`StringBuilder`, `string`, `char[]` fields) without changing its storage class. The clean "decode into parser scratch" pattern the streaming parsers use isn't directly portable.

2. **The literal-consumer surface is wider than the UPDATE path.** SPARQL source literals flow to the atom store (and to atom-store-match comparisons) via *every* consumer that materializes a `Term`. Canonicalizing only the UpdateExecutor write path opens a NEW asymmetry: stored atoms become canonical (`"a"b"`, 5 bytes) but FILTER literal arguments stay verbatim (`"a\"b"`, 6 bytes) — `FILTER(?o = "a\"b")` against a canonical-stored atom returns zero rows. The bug moves; it doesn't close.

Rev 2 reframed the Decision around the materialization-site helper so every literal consumer sees the canonical form, not just the UPDATE path. Effort estimate revised from 4-6h to ~10-12h. Validation plan extended to include cross-form FILTER paired-ingestion tests.

A staged delivery (write-path now, parser-side later as ADR-045) was explicitly considered and rejected — see Alternatives. Reasoning: a half-canonicalized substrate creates new bug shapes during the gap window; better to ship the coherent unit even at higher cost.

### Rev 2 → rev 3 (2026-05-17)

A verification pass on rev 2's surface enumeration found errors in both directions:

**False positives** in rev 2's site list (verified by direct read):
- `MultiPatternScan.cs:1195/1210/1226` are stored-atom parsing helpers (`TryParseLongLiteral`, `TryParseDoubleLiteral`, `TryParseBooleanLiteral`), not source-literal materialization. Confirmed by reading the call site at line 1161: input comes from `bindings.GetString(idx)`, not `_source`. Numeric stored atoms can't contain `\"` so the `IndexOf` bug pattern doesn't apply.
- `TriplePatternScan.cs:1600` and `CrossGraphMultiPatternScan.cs:365` are variable-name binding lookups (`if (term.IsVariable) { var varName = _source.Slice(...); ... }`), not literal materialization.

**Missed sites** in rev 2's enumeration:
- `UpdateExecutor.cs:442` `InstantiateTerm` — INSERT WHERE / DELETE WHERE template materialization (per-binding-generated triples flow through here).
- `UpdateExecutor.cs:697` `InstantiateTermFromSpan` — sibling helper, same role.
- `MultiPatternScan.cs:957` `ResolveTerm` — multi-pattern execution.
- `TriplePatternScan.cs:1527` `ResolveTermWithStorage` — storage-side resolution.
- `TriplePatternScan.cs:1610+` `ResolveTermForQuery` — transitive paths.

**Substrate-discipline finding (rev 3 addition):** the pattern-execution materialization sites (`ResolveSlotTerm` in `QueryResults.Patterns.cs:79`, `ResolveTerm` in `MultiPatternScan.cs:957`, `ResolveTermWithStorage` in `TriplePatternScan.cs:1527`, `ResolveTermForQuery` in `TriplePatternScan.cs:1610+`) are **near-identical copy-pastes** of each other — same prefix-expansion logic, same blank-node handling, same position-field pattern. Similarly, `InstantiateTerm` / `InstantiateTermFromSpan` in `UpdateExecutor` are near-duplicates.

Adding canonicalization at each of these duplicates propagates the duplication. The substrate-correct answer is: **consolidate first, canonicalize once.** Without consolidation, the per-site canonicalization is 11 mechanical edits with 4×ResolveTerm-shaped duplication. With consolidation, it's ~6 edits with 1× canonicalization point. Rev 3 expresses this as Decision Part 2 (consolidation) → Part 3 (canonicalization edits).

Rev 3 changes from rev 2:
- Corrected surface enumeration: 11 verified sites across 5 files (rev 2 listed ~10 with false positives + missed sites).
- Decision Part 2 added (consolidation refactor); rev 2's Part 2 → Part 3, etc.
- Effort estimate clarified, not increased: ~10-13h, now decomposed across the consolidation + canonicalization phases.
- Hypothesis H1 reframed to match the chosen mechanism (helper, not parser change).
- Scratch-buffer aliasing question addressed explicitly in Decision Part 1.

### Discovery during Part 4 (2026-05-17)

Cross-form FILTER equality tests written for Validation plan item 3 failed not because of the canonicalization wiring but because of an unrelated pre-existing bug in `FilterEvaluator.CompareEqual` at line 1919: String equality used raw `StringValue.SequenceEqual` without normalizing the wrap-vs-unwrap asymmetry between bound values (wrapped, from store: `"abc"`) and filter-side parsed literals (unwrapped content: `abc`). Probe verified: `FILTER(?o = "abc")` returned zero rows against any stored literal "abc", even with no escapes involved. The bug pre-dated ADR-044.

The bug blocked H4 closure for the direct `?o = "literal"` equality shape (CONTAINS / STRSTARTS / STRENDS / REGEX / pattern-match / DELETE-WHERE were unaffected because they call `GetLexicalForm` internally). Rather than file as a separate limit, the fix was brought into ADR-044's scope:

```csharp
// Before (pre-existing bug):
ValueType.String => left.StringValue.SequenceEqual(right.StringValue),

// After (rev 3 Part 4):
ValueType.String =>
    left.GetLexicalForm().SequenceEqual(right.GetLexicalForm()) &&
    left.GetLangTagOrDatatype().SequenceEqual(right.GetLangTagOrDatatype()),
```

The fix:
- Compares lexical forms via `GetLexicalForm` (normalizes wrap-vs-unwrap).
- Compares lang tag / datatype suffix via `GetLangTagOrDatatype` (keeps `@en` ≠ `@de` and `xsd:integer` ≠ `xsd:double` distinguishable per SPARQL spec).
- Validated against the full Mercury suite: 4,515 passed, 0 failed, 6 skipped. No existing test relied on the buggy behavior.

H4 fully closed. The 3 cross-form equality tests that motivated the discovery now pass alongside the 7 cross-form tests that pass on canonicalization wiring alone.

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

**Option 3 (tagged abstract)** breaks the byte-string atom representation into a `(kind, payload, lang?, datatype?)` tuple. Cleanest match to the W3C abstract syntax model. *Rejected pragmatically:* requires a substrate-wide refactor across atom serialization, B+Tree key layouts, the trigram index, every `Value` consumer in `FilterEvaluator`, all parsers, all writers — estimated 2-3 weeks vs Option 2's ~10-13 hours (per rev 3 Engineering effort). The 2-byte overhead from Option 2's wrapping is roughly what Option 3 would need for the kind discriminator anyway, so the storage delta is small.

**This ADR adopts Option 2.** The SPARQL parser converges on the streaming parsers' wrapped-decoded form. Option 3 stays open as a future ADR if the substrate-wide refactor becomes justified by other pressures (e.g., a JSON-LD path that needs richer datatype handling, or a typed-literal performance push).

### Full literal-consumer surface

*Verified rev 3 by direct read (not grep heuristic).* Every place a SPARQL source literal materializes into a `ReadOnlySpan<char>` that flows to atom storage OR to atom-store-matching comparison. 11 sites in 5 files, grouped by category.

#### Write side (UPDATE/DELETE) — 5 sites

| # | Site | Helper | Flows to |
|---|---|---|---|
| 1 | `UpdateExecutor.cs:143-150` (INSERT DATA) | `GetTermValue` (line 1214) | `Store.AddCurrentBatched` |
| 2 | `UpdateExecutor.cs:174-176` (DELETE DATA) | `GetTermValue` | `Store.DeleteCurrentBatched` |
| 3 | `UpdateExecutor.cs:206-211` (DELETE WHERE single-pattern fast path) | `ExpandPrefixedName` (line 1265) | `Store.DeleteCurrent` |
| 4 | `UpdateExecutor.cs:442` `InstantiateTerm` | `ExpandPrefixedName.ToString()` | `INSERT/DELETE WHERE` template materialization (per binding generates a triple through here) → `Store.AddCurrent` / `DeleteCurrent` |
| 5 | `UpdateExecutor.cs:697` `InstantiateTermFromSpan` | Sibling of #4 | Same role, different call shape |

#### Read-match side (pattern matching) — 4 sites, near-identical copy-pastes

| # | Site | Method | Role |
|---|---|---|---|
| 6 | `QueryResults.Patterns.cs:79` | `ResolveSlotTerm` | `OPTIONAL` pattern handling (`TryMatchSingleOptionalPatternFromSlot`) |
| 7 | `MultiPatternScan.cs:957` | `ResolveTerm` | Multi-pattern execution |
| 8 | `TriplePatternScan.cs:1527` | `ResolveTermWithStorage` | Storage-side resolution |
| 9 | `TriplePatternScan.cs:1610+` | `ResolveTermForQuery` | Transitive paths |

All four flow to `_store.QueryCurrent(subject, predicate, obj, _graph)` or similar. Each method does roughly the same: handle synthetic terms, handle blank nodes, handle variables, then `var termSpan = _source.Slice(term.Start, term.Length)` followed by prefix-expansion logic and a verbatim return for literals. They share the same `_expandedSubject`/`_expandedPredicate`/`_expandedObject` position-field idiom.

#### Filter / BIND (literal argument value comparison) — 2 sites

| # | Site | Role |
|---|---|---|
| 10 | `FilterEvaluator.cs:488` | Literal arguments in `CONTAINS`, `STRSTARTS`, `STRENDS`, `REGEX`, `=`, etc. |
| 11 | `BindExpressionEvaluator.cs:407` | `BIND("..." AS ?x)` — escape sequences scanned past but raw source span returned |

#### Verified false positives (rev 2 errors)

| Claimed site | Why it's not on the surface (verified) |
|---|---|
| `MultiPatternScan.cs:1195/1210/1226` (`TryParseLongLiteral`, `TryParseDoubleLiteral`, `TryParseBooleanLiteral`) | Operate on stored atoms, not source spans. Call site at line 1161 confirms: `value` comes from `bindings.GetString(idx)`. Numeric stored atoms can't contain `\"`. |
| `TriplePatternScan.cs:1600` | `if (term.IsVariable) { var varName = _source.Slice(...); ... }` — variable-name binding lookup. |
| `CrossGraphMultiPatternScan.cs:365` | `TryBindVariable` — variable-name materialization for binding lookup. |
| `VALUES { "lit1" "lit2" }` | Populates binding table; values become bindings, not stored atoms directly. (Subsequent pattern match against the binding IS on the surface via sites 6-9 — but the VALUES site itself isn't.) |
| Plain `CONSTRUCT` template literals | Template literals become OUTPUT triples returned as RDF, not stored. (`INSERT { template } WHERE { ... }` template literals ARE on the surface via sites 4-5.) |
| `LOAD <source-uri>` | URI not literal. |
| Federated `SERVICE <endpoint>` | Forwarded to remote endpoint as SPARQL text; remote handles its own canonicalization. |

#### Remaining uncertainty (not directly verified, called out for the validation surface)

- **Sub-queries** (`SELECT ... { SELECT ... WHERE { ?s ?p "lit" } }`) — likely routes through sites 6-9 (pattern-execution path) but not directly confirmed.
- **Property paths with constant objects** (`?s :path "lit"`) — likely routes through one of sites 6-9.
- **SPARQL-star quoted triples** with literal objects (`<< ?s ?p "lit" >>`) — separate parsing path, unverified.

The validation tests (Decision Part 4) cover the verified surface; uncertainty items become test failures if missed (cross-form FILTER returns empty rows when bound to a sub-query result, etc.).

### Why the surface matters

The write-side sites (1-5) intern verbatim bytes into the atom store. The read-match sites (6-9) take verbatim SPARQL source spans and compare them against stored atom bytes for pattern equality. The filter sites (10-11) materialize verbatim spans into `Value.StringValue` for filter-side comparisons.

Today, all three categories agree on verbatim form, so cross-site comparisons happen to work. Canonicalizing only some of them (e.g., rev 1's UPDATE-only plan) breaks the agreement: stored atoms become canonical, pattern literals stay verbatim, equality fails. **The canonicalization must happen at every site or at none.**

The filter consumers (sites 10-11) illustrate the cross-form failure mode that drives this ADR. `FILTER(?o = "a\"b")` against a canonical atom `"a"b"`:
- Bound `?o` → `Value.GetLexicalForm()` → `a"b` (3 chars).
- Filter literal `"a\"b"` → `Value.GetLexicalForm()` → `a\"b` (4 chars).
- Equality returns false. The substrate has the right triple stored; the user's query can't find it.

### Evidence of real-workload cost

- Commit `0a2f8f9` (1.7.72, 2026-05-17) was triggered by the recall-discipline rule's `rdfs:comment` containing `\"term\"` and being unfindable via `FILTER(CONTAINS(?c, "trigram"))`. The lesson about how to recall efficiently was itself unfindable via recall.
- Companion limit [`atom-store-corruption-from-failed-literal-insert.md`](../../limits/atom-store-corruption-from-failed-literal-insert.md) (also surfaced 2026-05-17) documents pre-1.7.72 URI atom corruption from the same root cause shape — INSERT DATA + escaped literal — that persists at the storage layer even after the GetLexicalForm fix.
- W3C SPARQL 1.1 Query conformance (421/421) does not exercise CONTAINS / STRSTARTS / STRENDS / REGEX against literals with escape sequences, so the conformance harness gives no signal on the divergence. Two dogfood-driven 1.7.72 bugs in the same week confirm the conformance-coverage-and-dogfood-discovery pattern at [`docs/process/emergence-epistemology-engineering/conformance-coverage-and-dogfood-discovery.md`](../../process/emergence-epistemology-engineering/conformance-coverage-and-dogfood-discovery.md).

## Hypothesis (falsifiable)

**H1 — Routing every SPARQL literal materialization through `LiteralForm.Canonicalize` converges atom-store identity across ingestion paths.** *Reframed in rev 3 to match the chosen mechanism (helper, not parser-side change).* After every site enumerated in Context calls the helper, the same logical triple ingested via SPARQL `INSERT DATA` and via Turtle `LOAD` produces byte-identical stored atoms.

**Falsified if:** a paired-ingestion test (same logical triple via both paths into the same store) shows distinct atom IDs in the atom store after the change. The "streaming parsers might not be canonical themselves" clause from rev 2 is closed — the 2026-05-17 probe confirmed all four streaming parsers (Turtle, N-Triples, N-Quads, TriG) produce the canonical wrapped-decoded form. A future regression that changes a streaming parser's behavior would surface here.

**H2 — The 1.7.72 `LastIndexOf` boundary-detection logic gracefully handles both legacy verbatim atoms and new canonical atoms.** `Value.GetLexicalForm`'s current `LastIndexOf('"')` (commit `0a2f8f9`) correctly locates the closing quote whether the literal interior contains `\"` (escape sequence as two chars) or `"` (decoded character).

**Falsified if:** a binding from a canonical atom (interior contains a real `"`) is fed to `GetLexicalForm` and returns the wrong slice, OR if any other consumer of `Value.StringValue` (FilterEvaluator function paths, result writers, SparqlExplain) assumes the literal interior contains no unescaped `"`.

**H3 — Legacy stores can be migrated via existing export → reload tooling.** A Cognitive-profile store created before this ADR ships can be made canonical-compatible by `mercury` export to N-Triples (uses canonical lexical forms) + bulk-load from that N-Triples file into a fresh store. No bespoke migration tool required.

**Falsified if:** the N-Triples writer emits any literal in a form that differs from what `NTriplesStreamParser` would store on re-read, OR if existing Cognitive stores contain bitemporal data that the export → reload round-trip drops.

**H4 — Centralized canonicalization closes cross-form FILTER mismatch.** *Added in rev 2; further qualified in rev 3 Part 4.* Routing every SPARQL source-literal materialization through `LiteralForm.Canonicalize` means FILTER literal arguments and pattern literal positions match canonical stored atoms exactly, regardless of whether the original SPARQL source used `\"`, `"`, or `"""..."""` syntax. **Rev 3 Part 4 found that closing the equality shape additionally requires the wrap-vs-unwrap fix in `FilterEvaluator.CompareEqual`** (see Scope revision → "Discovery during Part 4"). With both the canonicalization wiring AND the equality fix in place, all 14 cross-form tests pass.

**Falsified if:** a paired test of the form *(insert via Turtle) → FILTER(?o = "a\"b")* returns zero rows when both canonicalization and the equality fix are enabled, OR if any literal-consuming operator was missed and its bound spans remain non-canonical, OR if `Value.GetLexicalForm`'s slice-from-quotes logic produces different output on the canonicalized SPARQL filter literal than on the canonical stored atom for the same logical value.

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

**B. Materialization-site helper.** Introduce a static `LiteralCanonicalizer` (or `LiteralForm.Canonicalize`) that takes a verbatim source-literal span and returns the canonical form. Fast path: no `\` in literal → return verbatim span unchanged (zero allocation). Slow path: allocate a new immutable `string` with escapes decoded, return its span via an `out string? scratchOwner` parameter (the aliasing analysis below explains why `out string?` rather than `ref char[]`). Each materialization site changes from `_source.Slice(term.Start, term.Length)` to `LiteralForm.Canonicalize(_source.Slice(term.Start, term.Length), out _scratchOwner)`. Pros: parser stays untouched (preserves ref-struct discipline); changes are mechanical and localized; one canonical implementation. Cons: 11 call sites enumerated in Context; post-Phase-0 consolidation drops this to ~6 mechanical edits.

**Chosen: B.** The mechanical multi-site change is preferable to the API shape change. Each call site is a one-line edit; the canonicalizer itself is a single function with a fast-path / slow-path split.

The canonicalizer signature:

```csharp
internal static class LiteralForm
{
    /// Returns canonical wrapped-decoded form of a SPARQL source literal.
    /// Fast path (no '\\' in literal): returns the verbatim span unchanged.
    /// Slow path: allocates a new string with escapes decoded and returns
    /// the caller-owned new-string span. The caller must hold the
    /// `out string? scratchOwner` reference for the span's lifetime.
    public static ReadOnlySpan<char> Canonicalize(ReadOnlySpan<char> sourceLiteral, out string? scratchOwner);
}
```

**Scratch-buffer aliasing (rev 3 clarification).** A naive `ref char[] scratch` API would let a single consumer call `Canonicalize` twice in one operation (e.g., subject + object both literals with escapes) and have the second call overwrite the first call's scratch contents — silent span-aliasing bug. The chosen `out string?` signature avoids this entirely: each slow-path call allocates a new `string` (immutable), and the caller binds it to a position-specific field (`_subjectScratch`, `_objectScratch`, etc.) following the existing `_expandedSubject` / `_expandedPredicate` / `_expandedObject` idiom already in use across the operator code. Old spans into old strings remain valid because strings are immutable; field rebinding doesn't overwrite the old string's bytes.

For sites that resolve only a single position per call (e.g., FILTER literal arguments), a single scratch field suffices. For sites that resolve multiple positions per call (UpdateExecutor write path, pattern-match operators), position-specific scratch fields are required. The existing `_expanded{Subject,Predicate,Object}` pattern already handles this for prefix expansion; canonicalization extends the same idiom.

The decode loop is a copy of `TurtleStreamParser.ParseAndAppendEscapeToSb` (43 lines, well-tested via the W3C Turtle conformance suite). Only literal positions (`sourceLiteral[0] == '"'`) need canonicalization; URIs, blank nodes, numerics, booleans pass through unchanged.

### Part 2 — Phase 0: consolidate duplicated term-resolution methods

*Added in rev 3 after the duplication finding (Scope revision → Rev 2 → rev 3). Outcome updated post-execution: Phase 0b shipped; Phase 0a deferred per the fallback clause.*

Before adding `LiteralForm.Canonicalize` at every materialization site, the duplicated `ResolveTerm`-shaped methods in the operator code should be consolidated into a single shared helper. The four near-identical copies (sites 6-9 in the Context surface table) make the canonicalization edit 4×; consolidation makes it 1×. Independently, the duplication is overdue cleanup.

**Phase 0a — DEFERRED.** Attempted 2026-05-17. The four `ResolveTerm`-shaped methods (`ResolveSlotTerm` in `QueryResults.Patterns.cs`, `ResolveTerm` in `MultiPatternScan.cs`, `ResolveTermWithStorage` and `ResolveTermForQuery` in `TriplePatternScan.cs`) were found to share prefix-expansion logic but diverge on five feature axes — synthetic term handling, blank-node handling, numeric literal expansion, typed-value formatting from bindings, and position-specific vs shared scratch buffers. A single consolidated helper would need feature flags on all five axes (Christmas-tree pattern, trades one debt for another). Per this ADR's fallback clause, Phase 0a is deferred and the duplication is held as separate debt in [`docs/limits/sparql-resolve-term-family-duplication.md`](../../limits/sparql-resolve-term-family-duplication.md). Sites 6-9 receive per-site canonicalization edits in Part 3 (4 mechanical edits instead of 1 consolidated edit).

**Phase 0b — SHIPPED.** Consolidated 2026-05-17. `UpdateExecutor.InstantiateTerm` (line 442) and `UpdateExecutor.InstantiateTermFromSpan` (line 697) were verified byte-for-byte identical (same signature `(Term, BindingTable) → string?`, same body). The latter was deleted; its six callers redirected to the former. Full Mercury suite green post-consolidation (4,463 / 0 failed / 6 skipped — same as baseline), confirming the refactor is behavior-preserving. Sites 4-5 collapse to one canonicalization edit in Part 3.

**Phase 0 validation (Validation plan item 0):** Full Mercury test suite green after Phase 0b and BEFORE any canonicalization changes. Passed: 4,463 tests, 0 failures, 6 skipped — identical to pre-Phase-0 baseline.

**Net impact on Part 3 edit count:** Phase 0b's consolidation drops sites 4+5 to 1 edit; Phase 0a's deferral keeps sites 6-9 at 4 edits. Post-Phase-0 total: 3 (write side: GetTermValue + DELETE WHERE fast path + consolidated InstantiateTerm) + 4 (read-match side: per-site) + 2 (filter/BIND) = **9 mechanical edits**, not the rev 3 projected ~6 nor the un-consolidated 11. The duplication debt is paid in Part 3 with 3 extra edits rather than in Phase 0 with a feature-flag refactor.

### Part 3 — Update every literal-materialization site

The Context section enumerates 11 verified sites. After Phase 0b shipped and Phase 0a deferred (see Part 2), the actual edit count is 9.

Each site changes mechanically:

```csharp
// Before:
var obj = GetTermValue(quad.ObjectStart, quad.ObjectLength);
// or:
var literal = _source.Slice(term.Start, term.Length);

// After:
var obj = LiteralForm.Canonicalize(GetTermValue(quad.ObjectStart, quad.ObjectLength), out _objectScratch);
// or:
var literal = LiteralForm.Canonicalize(_source.Slice(term.Start, term.Length), out _literalScratch);
```

Sites by category (post-Phase-0b):

1. **Write side (3 edits):** `UpdateExecutor.GetTermValue` (covers sites 1+2 from Context), `UpdateExecutor` DELETE WHERE fast path (site 3), consolidated `InstantiateTerm` (covers sites 4+5 after Phase 0b).
2. **Read-match side (4 edits, Phase 0a deferred):** `QueryResults.Patterns.cs:79` `ResolveSlotTerm`, `MultiPatternScan.cs:957` `ResolveTerm`, `TriplePatternScan.cs:1527` `ResolveTermWithStorage`, `TriplePatternScan.cs:1583` `ResolveTermForQuery`. Each gets the same one-line edit at its literal-return point. See [`docs/limits/sparql-resolve-term-family-duplication.md`](../../limits/sparql-resolve-term-family-duplication.md) for the duplication debt.
3. **Filter / BIND (2 edits, no consolidation candidate):** `FilterEvaluator` literal-argument materialization (site 10), `BindExpressionEvaluator` literal-argument materialization (site 11).

Total: 9 edits + canonicalizer helper. Each edit needs a position-specific scratch field on its owning class (`_objectScratch`, `_filterLiteralScratch`, etc.) — see the aliasing note in Part 1.

### Part 4 — Tests must close the cross-form conformance gap

Three categories of paired-ingestion tests in `Mercury.Tests`:

1. **Atom identity after paired ingestion.** Load `<s> <p> "a\"b"` via Turtle and via SPARQL `INSERT DATA` into the same store. Assert: `Store.AtomCount` reports the same number after both inserts as after just one (semantic dedupe). Asserts H1 directly.
2. **Cross-form FILTER equality.** Insert canonical atoms via Turtle. Run `SELECT ?o WHERE { ?s ?p ?o FILTER(?o = "a\"b") }`. Assert: returns the row. Asserts H4.
3. **Cross-form FILTER substring.** Same setup. Run `FILTER(CONTAINS(?o, "a\"b"))`. Assert: returns the row. Asserts H4.

The existing 1.7.72 regression set at `tests/Mercury.Tests/Sparql/SparqlEngineTests.cs` covers the SPARQL → SPARQL roundtrip. Under the canonicalized path, both sides (INSERT literal and FILTER literal) canonicalize identically, so the existing tests continue to pass.

### Part 5 — Migration for legacy stores

Three options, preferred order:

**A. Document the export → reload migration.** *(Recommended.)* `mercury export <store> --format=nt > out.nt` then `mercury bulk-load <new-store> out.nt`. The N-Triples writer emits canonical lexical forms (verified pre-change by inspecting `NTriplesStreamWriter`); the bulk-load path uses the streaming parser, which already produces canonical atoms. Result: the new store has canonical atoms for every triple, including literals previously inserted via SPARQL with escapes. Operator effort: one CLI invocation per direction. No bespoke tooling.

**B. Tolerant read path.** `Value.GetLexicalForm`'s current `LastIndexOf('"')` (1.7.72) correctly handles both forms — see H2. New canonical writes coexist with legacy verbatim atoms in the same atom store; queries against either form go through the same boundary-detection logic. The cost: the atom store may contain duplicate atoms for the same logical literal until export → reload. Acceptable for Cognitive profile (bitemporal, low atom counts relative to Reference); not relevant for Reference profile (sealed, immutable, never ingested via SPARQL UPDATE).

**C. Bespoke re-intern tool.** A `mercury repair --canonicalize-literals` substrate command that walks the atom store, detects atoms with `\` escape sequences, re-interns under the canonical form, rewrites index entries to redirect the old ID to the new. Higher operator cost and substrate-side complexity; defers to A unless someone produces a Cognitive store where (A)'s round-trip is intolerable.

Recommend (A) for the documented migration path. (B) is in place by construction (the 1.7.72 LastIndexOf fix makes it true). (C) is built only if (A) proves insufficient on a real legacy substrate.

## Consequences

### Positive

- **Atom-store identity is preserved across ingestion paths.** Same logical triple → same atom regardless of which parser saw it. Cross-format queries return consistent results.
- **`STR(?o)` returns the same value regardless of ingestion path.** Filter predicates that operate on lexical form (`CONTAINS`, `STRSTARTS`, `STRENDS`, `REGEX`, `UCASE`, `LCASE`, `STRLEN`, `SUBSTR`) become path-independent.
- **The 1.7.72 `LastIndexOf` patch becomes redundant at insert time** (boundary detection no longer needs to skip escape sequences for newly inserted data) but remains load-bearing for legacy data under tolerant-read (Decision Part 5, option B). It is kept; the comment is updated to reflect both roles.
- **One fewer bug-shape class.** "Where does the literal end?" is no longer a question new code can get wrong, because the literal interior no longer contains characters that look like delimiters.
- **W3C compliance.** RDF abstract syntax defines literals as Unicode strings; escape sequences are concrete-syntax artefacts. Canonicalizing in the parser brings Mercury's SPARQL path into alignment with the abstract model.

### Negative / risks

- **Performance regression on escape-containing literals.** The zero-allocation fast path (verbatim source span) becomes an allocate-into-scratch path for any literal containing `\`. Affects both write-path (INSERT DATA) and read-path (FILTER literal arguments, pattern literal positions). Mitigation: most bulk-load goes through the streaming parsers (which already canonicalize); SPARQL INSERT is the cognitive-write path, not the bulk path; volume is low. Hot SPARQL queries with many escape-containing filter literals would see a slight regression; profiling would tell if it matters.
- **11 materialization sites, 9 edits post-Phase-0.** The Context-enumerated surface is the change blast radius. Phase 0b shipped (consolidated `InstantiateTerm` collapses sites 4+5 to 1 edit); Phase 0a deferred (4 pattern-match copies stay as separate edits per the [duplication-debt limit](../../limits/sparql-resolve-term-family-duplication.md)). Each edit needs a position-specific scratch field (see Part 1 aliasing note) plus the `LiteralForm.Canonicalize` call. Risk: missing a site means cross-form FILTER returns empty rows for that path. Validation plan tests are designed to surface this; categorize each test failure as "missed site to fix" or "legitimate canonicalization side-effect."
- **Legacy-store atom duplication under tolerant read.** Until a Cognitive store is migrated via export → reload, the atom store may contain both `\"` and `"` forms of the same logical literal. Indexes route to one or the other depending on which path interned it. Query results stay consistent (LastIndexOf handles both) but storage is non-optimal. Mitigation: documented in the migration runbook (Part 5A).
- **`STR()` semantic change for legacy SPARQL-INSERT data on upgrade.** A query that previously got `a\"b` from `STR(?o)` on legacy-stored data will get `a"b` after the store is migrated via export → reload. SPARQL specification says the latter is correct; users relying on the literal-text-of-the-source-form behavior have a workaround (`REPLACE(STR(?o), "\"", "\\\\\"")` or similar). Acknowledge in the CHANGELOG.

### Neutral

- **Substrate code path becomes uniform across SPARQL consumers.** Every literal materialization site adopts the same canonicalization gate. One canonical implementation; no per-consumer drift risk going forward.
- **No effect on serialization writers.** N-Triples / Turtle / SPARQL-result writers already emit canonical lexical forms; they consume `GetLexicalForm` output and re-escape on emit. The change to canonicalize at input boundaries does not require changes on output.
- **Streaming parsers (Turtle/N-Triples/N-Quads/TriG) unchanged.** They already canonicalize. Their interaction with FilterEvaluator is what becomes consistent: a literal loaded via Turtle now matches an identical filter literal authored in SPARQL.

## Validation plan

*Expanded in rev 2 to cover the full literal-consumer surface; rev 3 added Phase 0 checkpoint + uncertainty-surface tests.*

0. **Phase 0 consolidation isolation check (rev 3).** Run the full 4,463-test suite after Phase 0 refactor lands and BEFORE the canonicalizer call sites are touched. Green = the consolidation is behavior-preserving. Red = consolidation bug to fix before canonicalization layers on top. Without this checkpoint, a post-canonicalization failure can't be attributed to refactor-vs-feature.
1. **Existing regression suite remains green.** All 4,463 Mercury tests, including the 1.7.72 escaped-quote regression set at `tests/Mercury.Tests/Sparql/SparqlEngineTests.cs`, pass under the canonicalized path. This is the H2 falsification surface — if the `LastIndexOf` logic breaks on canonical atoms, the existing tests trip.
2. **Paired-ingestion atom-identity test** (H1). Turtle + SPARQL insert of the same logical triple → `Store.AtomCount` after both inserts equals count after one (semantic dedupe).
3. **Cross-form FILTER tests** (H4). Insert via Turtle, query via SPARQL with FILTER literal that uses `\"` escapes — all of `?o = ...`, `CONTAINS`, `STRSTARTS`, `STRENDS`, `REGEX` must return the expected rows.
4. **Cross-form pattern test.** Insert via Turtle, query via SPARQL pattern with literal in object position: `SELECT ?s WHERE { ?s ?p "a\"b" }` — should return the row.
5. **Cross-form DELETE WHERE test.** Insert via Turtle, then `DELETE WHERE { <s> <p> "a\"b" }` — should delete the row.
6. **BIND canonical-form test.** `BIND("a\"b" AS ?x) FILTER(?x = ?o)` where `?o` is bound to a canonical stored atom.
7. **Round-trip migration smoke test for Part 5A.** Build a Cognitive store via SPARQL INSERT DATA with escaped literals (under pre-canonicalization release) → export to N-Triples → bulk-load into a fresh store on the new release → query both stores with the same SPARQL → assert identical row sets.
8. **W3C SPARQL 1.1 Query conformance** stays at 421/421 (no regression). W3C SPARQL 1.1 Update stays at 94/94.
9. **W3C Turtle / N-Triples / N-Quads / TriG / RDF-XML** all stay at their respective passing counts — these parsers don't change but their interaction with FilterEvaluator does (canonicalized comparisons).
10. **WDBench / WGPB benchmark suites unchanged.** Wikidata data flows in via N-Triples; canonicalization at filter-side may change result identity for queries that exercise escape-containing filter literals. Run a small WDBench subset against `wiki-21b-ref` post-change and assert result-set identity to the pre-change baseline; investigate any divergence as either (a) a pre-change bug now fixed, or (b) a regression to address.
11. **Uncertainty-surface tests (rev 3).** Cover the not-directly-verified paths called out in Context → "Remaining uncertainty":
    - **Sub-query literal** — `SELECT * WHERE { ?s ?p ?o { SELECT ?o WHERE { ?s2 ?p2 ?o FILTER(?o = "a\"b") } } }` against canonical Turtle-loaded data. Returns the row if sub-query pattern execution routes through canonicalized sites.
    - **Property path with constant object** — `?s :path "a\"b"` against canonical Turtle-loaded data. Returns the row if property-path execution routes through canonicalized sites.
    - **SPARQL-star quoted triple with literal object** — `<< ?s ?p "a\"b" >>` against canonical Turtle-loaded data. Returns the row if quoted-triple expansion routes through canonicalized sites.

Each failure surfaces a missed materialization site to add to Part 3.

Validation document: `docs/validations/adr-044-canonical-literals-{date}.md`.

## Alternatives considered

The three storage-representation options (verbatim source, wrapped-decoded, tagged abstract) are analyzed in **Context → Storage representation**. The remaining alternatives sit at the implementation-mechanism layer:

- **Parser-extended-buffer (Option A from Decision Part 1).** Caller pre-allocates an oversized buffer; parser writes decoded literals to the trailing scratch region and emits Terms with sentinel-encoded offsets. Pros: consumers see canonical Terms without per-call decoding. Cons: parser API change (extra `Span<char>` argument), caller size-estimation burden, sentinel-encoding rule for downstream consumers. **Rejected in favor of the materialization-site helper (Option B).** Reconsider if Option B's performance turns out to matter at scale (the per-materialization-site call is a cheap check + occasional decode; profiling would tell).
- **Stage as write-path-only now, parser-side later as ADR-045.** *Explicitly considered and rejected in rev 2.* Sticks to rev 1's 4-6h effort by canonicalizing only `UpdateExecutor.GetTermValue`; defers FILTER/pattern canonicalization to a future ADR. Rejected because: (a) opens a NEW cross-form FILTER asymmetry that doesn't exist today (literal stored verbatim matches verbatim filter), (b) creates a transition window where the substrate has BOTH inconsistencies present (legacy verbatim atoms + new canonical atoms + verbatim filters), (c) the substrate-discipline rule of "ship the minimum coherent unit" interprets coherence at the user-visible behavior level, not the lines-of-code level — a half-canonicalized substrate isn't coherent.
- **Tolerant writes via dual-storage.** At INSERT time, store both the canonical and verbatim atoms; at query time, match either form. Strictly more compatible with legacy data — no migration required. Rejected: doubles the atom count for any literal with escapes, wastes storage, adds a query-time UNION. Papers over the divergence rather than fixing it.
- **Schema-version bump that refuses to open pre-canonical stores.** Cleanest separation; legacy stores require explicit migration. Rejected as too aggressive — Cognitive stores hold session memory the operator may not want to migrate on a substrate upgrade. The tolerant-read property of the 1.7.72 LastIndexOf logic (H2) lets old and new coexist; that's better.
- **Do nothing and document the divergence.** Tell users "SPARQL INSERT and Turtle LOAD produce semantically distinct triples for literals with escapes." Rejected: violates RDF specification (escape sequences are syntactic, the abstract value is the decoded string); inconsistent with the substrate-discipline rule that surface semantics match the W3C model.

## Engineering effort estimate

*Rev 3: now decomposed with Phase 0 consolidation. Total similar to rev 2 (~10-13h), but distributed across a more honest set of phases.*

- Part 1 (`LiteralForm.Canonicalize` + `CanonicalizeContent` helpers): ~2 hours actual. Single static class, fast path + slow path, decode loop ported from `TurtleStreamParser.ParseAndAppendEscapeToSb`, shared via `DecodeEscapeAppend`. 38 direct unit tests in `LiteralFormTests`.
- Part 2 (Phase 0 consolidation refactor): ~1 hour actual. Phase 0b only: `InstantiateTermFromSpan` (byte-identical with `InstantiateTerm`) deleted, callers redirected. Phase 0a deferred — `ResolveTerm`-family duplication held in `docs/limits/sparql-resolve-term-family-duplication.md`. Full Mercury test suite green post-Phase-0b (4,463 / 0 failed / 6 skipped, baseline-identical).
- Part 3 (canonicalization call-site edits): ~3 hours actual. 8 edits post-Phase-0b (1 write-side via `ExpandPrefixedName` covering Context sites 1-5 + 4 read-match + 2 filter/BIND). Write side collapsed from 3 to 1 because `ExpandPrefixedName` is the shared hub — discovery from implementation read.
- Part 4 (tests + equality-bug fix): ~3 hours actual. 14 cross-form tests in `CrossFormCanonicalizationTests` covering paired-ingestion atom identity (H1), 6 FILTER shapes (`=`, CONTAINS, STRSTARTS, STRENDS, REGEX, unicode-escape-equivalent), pattern match, DELETE WHERE, BIND, and 4 uncertainty-surface tests (subquery × 2, ASK, CONSTRUCT). Discovered + fixed pre-existing wrap-vs-unwrap equality bug in `FilterEvaluator.CompareEqual` (see Scope revision → Discovery during Part 4).
- Part 5A (migration runbook): ~30 min. CHANGELOG note + one-paragraph addition to MERCURY.md.
- Total actual: ~10 hours implementation + validation. Full suite end state: **4,515 / 0 failed / 6 skipped** (was 4,463 baseline; +52 net additions: 38 LiteralForm tests + 14 cross-form tests).

Post-implementation outcome (2026-05-17):

- Phase 0a was non-trivial as anticipated; fallback engaged (per-site edits + new limits-register entry for the ResolveTerm-family duplication).
- One bonus discovery during Part 4 — a pre-existing wrap-vs-unwrap equality bug in `FilterEvaluator.CompareEqual` was blocking H4 closure for the `?o = "literal"` shape. Fix brought into scope; documented in Scope revision → Discovery during Part 4.
- Uncertainty-surface tests (Validation plan item 11) all passed without follow-up work — subqueries, ASK, and CONSTRUCT all route through the canonicalized pattern operators transparently. Property-path and SPARQL-star coverage deferred (out of test scope; can be added when concrete query shapes need them).

## References

- Surfacing limit: [`docs/limits/sparql-update-literal-escape-canonicalization.md`](../../limits/sparql-update-literal-escape-canonicalization.md) — characterization that this ADR promotes.
- Companion limit: [`docs/limits/atom-store-corruption-from-failed-literal-insert.md`](../../limits/atom-store-corruption-from-failed-literal-insert.md) — same root cause; the URI-specific symptom this ADR's canonicalization closes structurally.
- 1.7.72 GetLexicalForm fix: commit `0a2f8f9` — the downstream patch that motivated the deeper investigation; H2 above codifies the back-compat property the patch added.
- 1.7.72 regression test set: commit `5aa4c79`, `tests/Mercury.Tests/Sparql/SparqlEngineTests.cs` region "Regression: escaped quote in stored literal (1.7.72)" — the existing 4-test pin that this ADR's paired-ingestion additions sit alongside.
- Conformance-coverage methodology: [`docs/process/emergence-epistemology-engineering/conformance-coverage-and-dogfood-discovery.md`](../../process/emergence-epistemology-engineering/conformance-coverage-and-dogfood-discovery.md) — the meta-pattern that two 1.7.72 bugs in one week reinforced; this ADR is the architectural close on the literal-storage branch of that pattern.
- Streaming parser canonical implementations: `src/Mercury/Turtle/TurtleStreamParser.Buffer.cs:519` (`ParseAndAppendEscapeToSb`), `src/Mercury/NTriples/NTriplesStreamParser.cs:551`, `src/Mercury/NQuads/NQuadsStreamParser.cs:658`, `src/Mercury/TriG/TriGStreamParser.cs:1513`.
- SPARQL parser current verbatim path: `src/Mercury/Sparql/Parsing/SparqlParser.cs:2259-2326`, `src/Mercury/Sparql/Execution/UpdateExecutor.cs:1265-1311`.
