# ADR-024: Trigram Index Read Path Disconnection

## Status

**Status:** Completed — 2026-03-22

## Context

A static code analysis (March 2026, prompted by Codex review) revealed that the trigram full-text search index has an active write path but a completely disconnected read path. The SPARQL `text:match` filter function performs brute-force string matching without consulting the trigram index.

This is the same failure category as the TGSP bug documented in ADR-022: correct results, passing tests, wrong complexity class. Both involve infrastructure that appears complete — write path active, read path implemented and tested in isolation — but the integration that would deliver the promised performance benefit does not exist.

### What the code does

**Write path (active — executes on every literal assertion):**

`QuadStore.ApplyToIndexes()` (line ~496–505) checks whether the object is a literal (starts with `"`), resolves the atom ID, retrieves the UTF-8 span, and calls `_trigramIndex.IndexAtom(atomId, utf8Span)`.

`IndexAtom` performs:
1. RDF literal extraction (strips quotes, language tags, datatype suffixes)
2. Unicode case-folding via `string.ToLowerInvariant()` (handles Swedish å/ä/ö)
3. UTF-8 re-encoding
4. Overlapping 3-byte trigram extraction
5. For each trigram: hash table lookup (FNV-1a, 1M buckets, quadratic probing) → posting list append with deduplication

The posting file is memory-mapped, append-only, with geometric growth (64 → 128 → ... → 4096 atom IDs per trigram).

Every literal assertion pays this cost. The index grows on disk (`.hash` + `.posts` files in the `trigram/` directory).

**Read path (implemented, tested, never called from query pipeline):**

`QueryCandidates(searchQuery)` extracts trigrams from the query, retrieves each trigram's posting list, and intersects them. The intersection is the core mechanism: a string can contain query Q only if it contains *every* trigram of Q. The result is a candidate set with possible false positives but no false negatives.

`IsCandidateMatch(atomId, searchQuery)` is the point-query variant — checks whether a specific atom appears in all trigram posting lists for the query.

**Neither method is called from anywhere outside `TrigramIndex.cs`.** The only callers are `TrigramIndexTests.cs`.

**What happens at query time:**

When a SPARQL query contains `FILTER(text:match(?label, "stockholm"))`:

1. `FilterAnalyzer` performs predicate pushdown — pushes the filter to the earliest pattern level where `?label` is bound
2. `MultiPatternScan` iterates bindings from the B+Tree index scan
3. `FilterEvaluator.ParseTextMatchFunction()` evaluates the filter
4. The implementation (line ~315): `textArg.GetLexicalForm().Contains(queryArg.GetLexicalForm(), StringComparison.OrdinalIgnoreCase)`

Pure O(n) substring scan on every binding that reaches the filter. The trigram index is never consulted.

### What the comments claim

`ParseTextMatchFunction` (line 282–284):

> *"This function is designed to work with the trigram index for pre-filtering, but the actual matching is done here with case-insensitive comparison. The trigram index provides candidate filtering at query planning time."*

The MCP tool description:

> *"Supports text:match(?var, \"term\") in FILTER clauses for case-insensitive full-text search."*

Both describe the intended architecture. Neither describes the actual behavior. The comments use present tense ("provides") for integration that was never implemented.

### The epistemic pattern

This is the second instance of a specific failure mode in Mercury:

| ADR | Component | Write cost paid | Read benefit collected | Detection method |
|-----|-----------|----------------|----------------------|-----------------|
| ADR-022 | TGSP index | Yes (duplicate B+Tree) | No (O(N) full scan) | Static analysis + runtime observation |
| **ADR-024** | Trigram index | Yes (posting lists on every assertion) | No (brute-force Contains) | Static analysis |

Both share the signature: tests pass, results are correct, the performance contract is violated, and the violation is invisible to any test that checks correctness rather than complexity class.

The comment in `ParseTextMatchFunction` is architecture-as-intended phrased as architecture-as-implemented. That suppresses the obvious follow-up question: "but does it actually do this?"

### Cost being paid for no benefit

The write-side cost is non-trivial:
- Every literal assertion triggers: UTF-8 decode → case-fold → UTF-8 re-encode → trigram extraction → N hash lookups → N posting list appends with linear dedup scans
- Disk footprint: `.hash` file is fixed at 1M × `sizeof(HashBucket)` = 16MB; `.posts` file starts at 64MB and grows
- The `_postingPosition` advances monotonically (append-only); old posting lists from reallocation are never reclaimed

## Decision

### Phase 1: Planning and scan integration — set-based pre-filtering

Mercury is a cognitive substrate. Full-text search over RDF literals is not a convenience feature — it is a primary query pattern for any system that stores natural language (chat memory, document content, labels, descriptions). The correct fix changes the complexity class, not the constant factor.

When planning/pushing a `text:match` filter on a variable bound by a triple pattern's object position, Mercury must carry that information into scan construction. The execution path that opens the underlying `QuadStore` enumerator then calls `QueryCandidates` to obtain candidate object atom IDs and restricts the scan to those candidates.

This transforms the query from:

- Scan all objects matching the triple pattern → evaluate `Contains` on each binding → O(N)

To:

- `QueryCandidates(searchTerm)` → intersect posting lists → get k candidate object atom IDs → probe the most appropriate object-bearing access path for each candidate → verify with `Contains` → O(k × log N) where k << N

The trigram intersection is a necessary condition — no false negatives. The `Contains` verification on the candidate bindings eliminates false positives. Correctness is preserved; complexity class changes.

**This is the same fix pattern as ADR-022.** ADR-022 didn't optimise the full scan — it fixed the TGSP sort order so the B+Tree seek operates on the right leading dimension. Here, the trigram index *is* the leading dimension for text search. Wiring it at the filter-evaluator level (checking `IsCandidateMatch` per binding but still iterating all bindings) would be the equivalent of ADR-022 keeping the wrong sort order but adding a faster comparator. Wrong level of intervention.

**Required changes:**

1. **Filter recognition in planning.** Detect `text:match(?var, "constant")` where the search term is a constant string and the variable is bound to a triple pattern's object position. The planner/filter-analysis layer must produce structured metadata for this filter, not just a generic "evaluate filter at level X" assignment.

2. **Candidate-driven scan on QuadStore.** Add a new method or scan mode that accepts a set of candidate object atom IDs and combines them with any already-bound graph/subject/predicate terms. This is **not** hardwired to GOSP. The correct access path depends on the rest of the pattern:
   - If predicate is bound, probing `GPOS` per candidate object may be cheaper
   - If only object is bound, `GOSP` is the natural path
   - The implementation should preserve existing index-selection heuristics wherever possible and only add the candidate constraint as an extra bound dimension

3. **Selectivity-based fallback.** When the candidate set is large relative to the store (low selectivity — common trigrams, short search terms), the overhead of many point probes may exceed a single scan. The planner/executor should fall back to brute-force scan + filter when the estimated candidate fan-out is too high. A simple starting heuristic is acceptable, but it should be based on candidate atoms plus expected binding fan-out, not candidate count alone.

4. **Verification.** The `Contains` check remains as ground truth on the candidate bindings. The trigram index is a pre-filter, not a replacement. This is non-negotiable — trigram intersection can produce false positives (e.g., "abc" and "bca" share all trigrams of a 3-character query).

### Phase 2: Short queries and edge cases

Queries shorter than 3 characters cannot use trigram pre-filtering (no trigrams to extract). `QueryCandidates` currently returns an empty list for these. The correct behaviour is to fall back to the brute-force scan path — which is what happens today, so this is already correct by accident. Phase 2 makes this explicit and considers whether bigram or unigram fallback indices are worth the write-side cost.

### Non-goal: filter-level point integration

Threading `IsCandidateMatch` into `FilterEvaluator` to short-circuit per-binding would optimise within the wrong complexity class. You still iterate every binding from the B+Tree scan; you just replace O(n) `Contains` with O(t) hash lookups for non-matches. This is the equivalent of making a linear scan slightly faster rather than eliminating it. It is explicitly not selected.

### Non-goal: deletion support

`RemoveAtom` is currently a no-op. This is acceptable for an append-only store. Deletion support (tombstones + compaction) is a separate concern and not addressed here.

## Verification

The existing `SparqlTextSearchTests` verify correctness — they assert that `text:match` returns the right results. They would continue to pass regardless of whether the trigram index is consulted. This is the same testing gap as ADR-022: correctness tests cannot detect complexity-class violations.

### Strategy: `#if DEBUG` evaluation counter

The same pattern used in ADR-022 for TGSP page access counting. `QuadIndex` has:

```csharp
#if DEBUG
    private long _pageAccessCount;
    internal long PageAccessCount => _pageAccessCount;
    internal void ResetPageAccessCount() => _pageAccessCount = 0;
#endif
```

with `Interlocked.Increment(ref _pageAccessCount)` in `GetPage()`. The test (`QuadIndexTests.cs:627–655`) populates 5,000 entries, queries a narrow time window, and asserts `timePages < entityPages` — proving the TimeFirst index visits fewer B+Tree pages than EntityFirst for the same query.

The analogous instrumentation for full-text search:

1. **Add a static `#if DEBUG` counter to `FilterEvaluator`** — `TextMatchEvaluationCount` / `ResetTextMatchEvaluationCount()`. Increment it in `ParseTextMatchFunction`. This counts how many times the brute-force `Contains` is actually executed.

2. **The test** populates a store with N literals (N should be large enough that the difference is unambiguous — 1,000 is sufficient). Only a small number k match the search term. Reset the counter, execute a `text:match` query, read the counter.

3. **Before integration (current state):** The counter equals the total number of bindings reaching the filter — approximately N for a single triple pattern with N objects. Every binding is evaluated.

4. **After integration:** The counter tracks only candidate bindings that survive the trigram pre-filter and still need `Contains` verification.

5. **The assertion:** `TextMatchEvaluationCount` must be significantly less than N. For a controlled test dataset with **one triple per literal atom**, the assertion can be tighter: `TextMatchEvaluationCount <= candidateSetSize`, where `candidateSetSize` is obtained by calling `QueryCandidates` directly. In the general case, multiple triples may share the same literal atom, so the reliable invariant is relative: the counter must drop sharply versus the pre-integration baseline.

### What this proves

The counter is not measuring wall-clock time or throughput — it is measuring **which code path executed**. If the planner/scan integration correctly routes `text:match` through the trigram index, the filter evaluator only sees candidate bindings. If the integration is absent or broken, the filter evaluator sees every binding. The counter distinguishes these two cases with zero ambiguity when the test data shape is controlled.

This is the same epistemics as ADR-022: the test doesn't measure "is it faster" (a performance question susceptible to noise), it measures "did it visit fewer things" (a structural question with a deterministic answer).

## Consequences

1. Full-text search becomes O(k × log N) for selective queries — the complexity class appropriate for a substrate that will store millions of triples with natural-language objects
2. The write-side cost (trigram extraction and posting list maintenance on every literal assertion) becomes justified — it is the cost of maintaining an index that is actually used
3. The existing object-bearing indexes gain a new consumer — candidate-driven point lookups. This validates the four-index architecture: GSPO for subject-leading, GPOS for predicate-leading, GOSP for object-leading, TGSP for time-leading. Full-text search is object-constrained, but the best probe path still depends on which other terms are bound
4. **DrHook relevance:** This is the second bug in the "correct results, wrong complexity class" category. Both ADR-022 and ADR-024 are invisible to correctness tests. DrHook's runtime inspection capability — observing actual execution paths, measuring scan ranges, counting filter evaluations — is the detection method that would catch this class of bug systematically. These two ADRs are the empirical case for DrHook's third discipline: compile, test, **inspect**

## Related

- **ADR-022** — TGSP index: same failure category (write cost paid, read benefit absent, tests pass)
- **ADR-023** — Transactional integrity: same review origin (Codex, March 2026)
- **DrHook ADR** — Runtime inspection as the systematic answer to complexity-class violations
