# ADR-051: Trigram Index over IRIs — Fast Fuzzy Recall over IRI Names

**Status:** Proposed — 2026-06-23 (Emergence)

## Context

[ADR-024](ADR-024-trigram-index-read-path-disconnection.md) wired the trigram index into the `text:match` read path. Both the write path and the pre-filter it connected are **object-literal-only**:

- **Write:** `QuadStore` feeds an atom to the trigram index only when the **object** atom's lexical form starts with `"` (a literal) — `_trigramIndex.IndexAtom(objectId, utf8Span)` gated on `utf8Span[0] == (byte)'"'`, at all three build sites (inline add `QuadStore.cs:1467`, rebuild `:1620`, sorted-rebuild `:1890`).
- **Read:** the candidate pre-filter (`_trigramObjectCandidates` / `_trigramCandidatesByVar`) restricts only the **object** position of a pattern whose object is a `text:match` variable.

This literals-only / object-only scope was **never a decision** — it was carried forward from the original write path through ADR-024, whose subject was the read-path *disconnection*, not the index *scope*. (Surfaced 2026-06-22: "it was never specifically decided that trigram should only apply to literals only — it was just assumed.")

The consequence surfaced during recall work. `text:match` itself is **not** literals-only — the function matches the lexical form of a `String` **or** a `Uri` (`FilterEvaluator` accepts both). So `FILTER(text:match(?s, "drhook"))` over IRI subjects returns correct results — but as a **full scan**, with no trigram acceleration. Fast **fuzzy / substring recall over IRI names** (subject and predicate local-names — the connect-the-dots primitive for a cognitive memory) is therefore unavailable at scale.

This is **Lucy's associative-recall primitive** (`ck:obs-recall-decisions-are-lucy-recall-acts`; `obs:017`: "minimum viable `lucy_recall(topic)` using text:match"). Lucy recalls by topic across a memory whose subjects are IRIs (`ck:obs-…`, per-session graph IRIs); trigram-accelerated IRI search is what makes that recall fast and fuzzy rather than O(N) scan. The recall skill (`.claude/skills/lucy/SKILL.md`) already issues `text:match` over IRIs — this ADR gives that path its acceleration.

## Decision (proposed)

Extend trigram indexing and the `text:match` pre-filter beyond object literals to **IRIs**, so substring/fuzzy `text:match` over subject/predicate/IRI-object names is index-accelerated. The trigram index is already **atom-keyed** (`IndexAtom(atomId, utf8)`), so an indexed IRI atom is matchable wherever that atom appears — the work is (1) feed IRI atoms to the index, and (2) extend the pre-filter to non-object positions.

### D1 — Index IRI atoms (write path)

Index an atom when its lexical form is an IRI (`utf8[0] == '<'`) in addition to a literal (`'"'`). **Open question (resolve at Accepted):** hook per **atom-intern** — index each atom once when first interned, position-agnostic, the natural fit for an atom-keyed index (no per-position re-indexing, no double-add; the rebuild iterates the atom store rather than quad objects) — vs per **quad-position** (extend the current per-object loop to subjects/predicates). Atom-intern is the recommended shape.

### D2 — Index the FULL IRI lexical form (no-false-negatives invariant)

Index the whole `<…>` lexical form, not just the local-name. `text:match` matches the full lexical form, and the trigram pre-filter must never produce a **false negative** (ADR-024's contract: false positives OK — verified by the `Contains` step; false negatives are not). Indexing only local-names would make `text:match` on a namespace substring return zero candidates → wrong-empty. Cost: shared-namespace trigrams yield large posting lists, but the query term's *discriminating* trigrams still narrow the intersection. (A separate local-name index for relevance ranking is a possible follow-on, not this ADR.)

### D3 — Extend the pre-filter to subject (and predicate) positions (read path)

Today the candidate atom IDs gate only the object position. Extend `FilterAnalyzer` + `TreeJoinExecutor` (`_trigramCandidatesByVar`) + `TriplePatternScan` (`_trigramObjectCandidates`) so a `text:match` variable bound to a **subject** (and optionally predicate) position drives a candidate pre-filter there too. The `text:match` function stays the verification step — no correctness dependence on the pre-filter, only speed.

### D4 — Profile scoping

Scope IRI indexing to the **Cognitive** profile (the recall workload). For **Reference** (Wikidata-scale: billions of IRI atoms — every Q-/P-id), trigram-over-IRIs would bloat the index with no recall consumer; keep Reference object-literal-only. Mechanism to resolve at Accepted: Cognitive-only by profile, or a `StorageOptions` index-scope flag (default-on for Cognitive, off for Reference). This also dovetails with the under-exercised Cognitive workload ([`cognitive-profile-validation-drought`](../../limits/cognitive-profile-validation-drought.md)).

### Out of scope

True **edit-distance** fuzzy (typo tolerance) and **relevance ranking** (order by shared-trigram count) are natural follow-ons the trigram substrate enables, but this ADR is the *index-scope* extension (substring/fuzzy over IRIs), not a ranking model.

## Validation

**Proposed → Accepted** once D1 (intern vs per-position) and D4 (profile mechanism) are chosen and a probe shows, on a Cognitive store: (a) `text:match` on IRI subjects returns trigram candidates **agreeing exactly with the full-scan `Contains`** result (no false negatives); (b) a measured speedup vs the full scan (the ADR-024-style `text:match` evaluation-count probe, extended to subject position — candidates ≪ N).

**Accepted → Completed** when:
- IRI atoms are trigram-indexed across the write + rebuild + sorted-rebuild paths, under the chosen profile scope.
- The pre-filter accelerates `text:match` in subject position (D3), verified by an evaluation-count test.
- A recall benchmark (Lucy associative recall over a Cognitive store of `ck:`-style IRIs) shows the index path beating the scan with identical results.
- Full W3C conformance + suite intact; a no-false-negatives test for subject IRIs mirrors the existing literal-object trigram tests.

## Consequences

- Lucy's **associative recall** (`lucy_recall(topic)`) becomes index-accelerated over IRI subject/predicate names — fast fuzzy connect-the-dots, not O(N) scan. The Mercury *primitive* the Lucy skill already exercises gets its acceleration; Lucy's recall *policy* is unchanged.
- The trigram index grows on a Cognitive store (IRI atoms now indexed); bounded by the same posting-list mechanism. Reference is unaffected (profile-scoped).
- Establishes the trigram index as a general full-text primitive over **all atom lexical forms**, not an object-literal special case — closing the inherited-assumption scope gap ADR-024 left.
- Ties off the recall-improvement thread: the graph-targeting half is already fixed (recall queries the `GRAPH ?g` union); IRI-trigram is the remaining "fuzzy recall" half.

## References

- [ADR-024](ADR-024-trigram-index-read-path-disconnection.md) — wired the object-literal trigram read path; this extends its scope to IRIs.
- `.claude/skills/lucy/SKILL.md` — the recall consumer (associative recall via `text:match`).
- `ck:obs-recall-is-graph-targeting-not-textmatch` (its `ck:enhancement`) and `ck:obs-recall-decisions-are-lucy-recall-acts` — the recall findings that surfaced this.
- [`docs/limits/cognitive-profile-validation-drought.md`](../../limits/cognitive-profile-validation-drought.md) — the Cognitive workload this serves.
- Cognitive-layers roadmap Phase 2 (Lucy).
