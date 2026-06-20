# Mercury Divergence Register

A living catalog of **divergent implementations** in Mercury — places where ONE concern has two or more parallel implementations. Divergence in a base substrate is a latent catastrophe: the copies drift, and conformance/dogfood tests can stay green while a carve-out quietly certifies the wrong copy. This register is the converse of `docs/limits/`: limits are characterized-but-deferred *capabilities*; this is characterized *debt to converge*.

**Origin:** 6-agent audit, 2026-06-20, after the ADR-045/047 cleanup removed the parallel SPARQL slot-operator execution engine. Every HIGH finding was independently re-verified by `git grep`/`Read`.

**Legend**
- **Severity tier** — **S**: live, can produce *wrong or inconsistent results*. **A**: live structural drift (same algorithm copied; not yet wrong, but drifting). **B**: dead parallel machinery (delete; no behavior risk, but it is the seed). **C**: verified *not* divergence (recorded so it is not re-flagged).
- **Verified** — ✓ confirmed by grep/read here; ◦ agent-reported with `file:line`, reproduce at fix time.
- **Status** — `live` / `dead` / `legitimate`. Items are struck through and dated when converged.

---

## Meta-conclusion

ADR-045/047 genuinely cleaned the SPARQL **execution** path — the unified `TreeJoinExecutor` is the sole engine, no live second path. But the divergence pattern is **pervasive in five layers cleanup never touched**, three of them live and correctness-affecting. One (the two expression evaluators) **predates** that cleanup. The fix patterns already exist in-tree: `LiteralForm` (shared literal canonicalizer) and `BTreeFile` (shared B+Tree substrate) are the *positive* templates to extend.

---

## Tier S — live, correctness-affecting

### S1 · RDF format parsers reimplement atom-minting canonicalization 3–6× — and already disagree  ✓ `live` (S1a converged)
The same source triple ingested as different formats can store a **different atom**. Sub-concerns:
- ~~**S1a escape-decode**~~ — **CONVERGED 2026-06-20.** All four parsers + `LiteralForm` now route ECHAR/UCHAR decode + validation + UTF-16 encode through the single `Rdf/RdfEscape.cs`. Fixed in passing: TriG/N-Quads previously lacked the `>U+10FFFF` guard (an out-of-range `\U` reached a raw downstream throw) — now rejected uniformly at decode; Turtle's dead char-truncating `ParseEscapeSequence`/`ParseUnicodeEscape()` deleted. Locked by `RdfEscapeConvergenceTests` (cross-format identity incl. supplementary-plane `\U0001F600`). *Original evidence:* copies were at `NTriples:551,580`; `Turtle/...Buffer.cs:395,423,456,519,555`; `TriG:1513,1548`; `NQuads:658,694`; `LiteralForm.cs:181`.
- **S1b IRI resolution** (the biggest atom-identity risk) — Turtle (`Buffer.cs:636`, `Terminals.cs:811`, **two copies**) and RDF/XML (`RdfXml/RdfXmlStreamParser.cs:914`) defer to BCL `new Uri`; TriG (`TriGStreamParser.cs:2112`) and JSON-LD (`JsonLd/JsonLdStreamParser.Iris.cs:317`) hand-roll RFC-3986 `RemoveDotSegments`. Same grammar family (Turtle/TriG), two different algorithms → relative IRIs resolve differently.
- **S1c numeric/boolean → xsd typing** — inlined ~4× in Turtle (`Terminals.cs`), once in TriG, and independently in `LiteralForm.CanonicalizeNumericOrBoolean`.
- **S1d** code-point→UTF-16 appender copied 5× (2 impls); **S1e** "is IRI absolute?" predicate in 5 inconsistent forms.

**Convergence project:** extend `LiteralForm` (or a neutral `Rdf/RdfTermCanonicalizer`) into the single canonicalizer all format parsers AND the SPARQL side call. **Verdict: TRUE divergence.**

### S2 · Two complete SPARQL expression evaluators  ✓ `live` **(predates ADR-047)**
`Sparql/Execution/Expressions/FilterEvaluator.cs` (+`.Functions.cs`) vs `BindExpressionEvaluator.cs`: independent recursive-descent parsers, each with its own ~40-function builtin library (MD5/SHA/`ENCODE_FOR_URI`/`SUBSTR`/… duplicated — e.g. `FilterEvaluator.cs:1120` ⟷ `BindExpressionEvaluator.cs:971`). **Capability gap:** `BindExpressionEvaluator` has **0** of `CONTAINS`/`REGEX`/`STRSTARTS`/`STRENDS`/`LANGMATCHES` (FilterEvaluator has 11) → `BIND(CONTAINS(?a,?b) AS ?x)` silently unbinds. The only bridge (`ParseIfFunction`, `BindExpressionEvaluator.cs:1572`) uses the prefix-less `FilterEvaluator` overload, losing prefix expansion in `IF` conditions. **Convergence project:** one evaluator core (typed `Value` in, EBV derived) with one shared function library. **Verdict: TRUE divergence.**

### S3 · SPARQL result serializers implemented 3×; the correct one is dead  ✓ `live`
Canonical `Sparql/Results/Sparql{Json,Xml,Csv}ResultWriter` (W3C-spec) have **zero production callers** (only `SparqlResultFormat.cs` + tests). Live clones: `Sparql/Protocol/SparqlHttpServer.cs:377,420,464` and `Mercury.Sparql.Tool/SparqlTool.cs:264,310,332` — near-identical, and ◦ emit non-conformant TSV the dead canonical writer gets right. `git log -S` (agent) shows the HTTP server originally used the canonical factory and was switched away in the facade-migration. **Convergence project:** route `SparqlHttpServer`/`SparqlTool` through `SparqlResultFormatNegotiator.CreateWriter`; delete both clone sets. **Verdict: TRUE divergence.**

---

## Tier A — live structural drift

- **A1 · B+Tree algorithm copy-pasted 4× per profile** ✓ — insert/split/find-leaf in `Temporal/Versioned/Reference/Minimal QuadIndex.cs`. Drift confirmed: the ADR-032 sort-insert fast path (`AppendSorted`) is on **Reference only**; ◦ Temporal's `SplitInternalPage` flushes per-child while others flush once. Fix: lift the skeleton into one generic over an `IKey`/layout descriptor, keep per-profile mutation hooks — finish what `BTreeFile` started.
- **A2 · QuadStore per-profile orchestration** ◦ — add/query/rebuild/flush fan-out written N× because `IQuadIndex` exposes only `Count`/`Flush`/`Clear`. Same root cause as A1 (no rich shared index abstraction).
- **A3 · `ExpandPrefixedName` reimplemented 5×** ✓ — `QueryExecutor.cs:1208`, `ConstructResults.cs:203`, `QueryResults.Patterns.cs:2826`, `Operators/TriplePatternScan.cs:2145`, `UpdateExecutor.cs:1241`.
- **A4 · Four triple-pattern parsers at four capability tiers** ◦ — only `EmitTripleBlockTree`/`TryParseTriplePattern` use the real path grammar (`ParsePredicateOrPath`); three others use `ParseTerm` (no paths). Plus two divergent group-body parsers (`ParseGroupBodyTree` vs `ParseSubSelectPatterns`).
- **A5 · Two chunk-record parsers** ◦ — `MoveNextDirect` vs `MoveNextReadAhead` (`SortedAtomStoreExternalBuilder.cs`), live behind the readahead env/budget flag; the "not used in production" comment is false on a memory-starved host.

---

## Tier B — dead parallel machinery (delete)

- **B1 · `VariableGraphExecutor.cs`** ✓ — entire ~321-line OLD `GRAPH ?g` engine, zero call sites.
- **B2 · OPTIONAL/MINUS/VALUES slot-matcher web** ✓ in `QueryResults.Patterns.cs`, behind `_hasOptional`/`_hasMinus`/`_hasValues`/`_hasPostQueryValues` (24 `= false` assignments, never `true`). **NOT wholesale-deletable** — the EXISTS/FILTER/empty-pattern methods in the same file are live via `_hasExists` (`WHERE {}` queries). Delete the dead matcher subtree only.
- **B3 · Flat-`GraphPattern` WHERE parser (family A) + `ConvertGraphPattern`** ◦ — parse-then-discard; the tree re-parses the WHERE from source. Live remnants feed only metadata (`_cachedPattern` offsets, trigram hints) + the empty-pattern path.
- **B4 · Misc dead** ✓ — `QueryPlanner.ShouldUseLocalFirstStrategy`, `QueryExecutor.CanJoinRows`/`MergeRows`/`PassesFilters`, `QueryExecutor._planner` field. (Note: a memory entry claimed the tree uses `PassesFilters`; it has zero callers — stale.)
- **B5 · Ingestion dead** ◦ — `InMemoryAssignedIds`/`InMemoryReader`; `SortedAtomStoreExternalBuilder.BuildExternal`/`SpillChunks` (test-only); `RdfEngine.LoadAsync` (test-only dup of `LoadStreamingAsync`).
- **B6 · RDF writer dups** ◦ — byte-identical N-Triples/N-Quads `WriteEscapedString`; code-point appender ×5; absolute-IRI predicate ×5 (overlaps S1d/S1e).

---

## Tier C — verified NOT divergence (do not re-flag)

- `HashAtomStore` vs `SortedAtomStore` — distinct concerns (ADR-029/034): mutable open-addressing hash vs sealed sorted/MPHF vocabulary; share `IAtomStore`, duplicate no logic. ✓
- `BTreeFile` — the *correct* shared-substrate factoring (file/mmap/page/metadata); the positive example A1 extends. ✓
- The two k-way merges (`ExternalSorter` binary-heap over fixed blittable radix records vs `MergeAndWrite` `PriorityQueue` over variable UTF-8 compressed/deduped records) — different data shapes, not one concern. ◦
- In-memory `SortedAtomStoreBuilder.Build` vs external builder — textbook RAM-vs-external split with byte-identical output format. ◦
- Format-specific grammars/writers (Turtle pretty, TriG, RDF/XML C14N, JSON-LD); `LiteralForm` (shared literal canon — the S1/S2 template); the `…At(position)` reposition-then-delegate parse entries; `CodePointOps`. ✓

---

## Convergence projects (recommended order)

1. **S1 — shared RDF term canonicalizer** *(in progress)* — atom identity; can silently corrupt the store. Extend `LiteralForm` → all format parsers route through it. Sub-order: S1b (IRI, biggest risk) / S1a (escape) / S1c (numeric) / S1d / S1e.
2. **S2 — single expression evaluator** — close the BIND capability gap.
3. **S3 — serializers → canonical writers** — delete the clones.
4. **A1/A2 — generic B+Tree skeleton + richer `IQuadIndex`.**
5. **A3/A4/B-sweep** — shared `ExpandPrefixedName`; finish the parse-side cutover; delete Tier-B dead code (quick wins, interleave anytime).

## Maintenance

This is a living register. When an item is converged, strike it through with the date and commit; when a new divergence is found, add it under the right tier with `file:line` evidence and a verdict. Tier C exists so legitimate variants are not repeatedly re-flagged.
