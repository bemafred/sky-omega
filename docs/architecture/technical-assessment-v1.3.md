# Architectural & Technical Assessment: Sky Omega v1.3.0

## Executive Summary

Sky Omega is a **167K-line C# repository** implementing a semantic knowledge substrate (Mercury) with zero-GC design principles, targeting .NET 10 / C# 14. At v1.3, Mercury is a mature, well-tested RDF quad store with a full SPARQL 1.1 engine achieving **100% W3C core conformance** (1,181/1,181 tests). The codebase demonstrates exceptional engineering discipline: 718 commits, 24 accepted ADRs, and a clean public API surface of 21 types behind 3 facades.

**Verdict: Production-grade knowledge substrate. Strong foundations for 2.0 cognitive layer.**

---

## 1. Codebase Profile

| Metric | Value |
|--------|-------|
| Grand total | ~167,199 lines |
| Source code | ~86,764 lines |
| Tests | ~50,265 lines (3,970 test cases) |
| Benchmarks | 3,408 lines (97 classes) |
| Documentation | 25,911 lines |
| Projects in solution | 16 |
| Commits | 718 |
| External dependencies (Mercury core) | **0** (BCL only) |
| .NET target | net10.0 / C# 14 |

### Size Distribution

The SPARQL engine alone is 44,743 lines — **60% of Mercury's source** — reflecting the complexity of a full W3C-conformant query processor. Storage is comparatively lean at 6,014 lines, a sign of effective abstraction.

---

## 2. Architecture Assessment

### 2.1 Layering & Dependency Hygiene

The dependency graph is clean and well-stratified:

```
Mercury.Mcp ─────────────────────────────┐
Mercury.Solid ──────────────────────┐     │
Mercury.Pruning ──────────────┐     │     │
                              ▼     ▼     ▼
Mercury (core)  ◄────── Mercury.Runtime ◄── Mercury.Abstractions
   (BCL only)          (BCL only)           (BCL only)
```

**Strengths:**
- Mercury core has **zero external dependencies** — a rare achievement for an engine of this scope
- Clear separation: abstractions → runtime → core → consumers
- Mercury.Solid, Mercury.Pruning, and Mercury.Mcp are independent leaves

**Observation:** The BCL-only constraint in Mercury core has forced disciplined design (no JSON.NET, no DI container, no logging framework). This makes the library embeddable anywhere .NET runs without version conflicts.

### 2.2 Public API Surface (v1.3 Facade Design)

The v1.3 release made a significant architectural decision: **internalizing ~140 types** behind 3 static facades.

| Facade | Purpose | Key Methods |
|--------|---------|-------------|
| `SparqlEngine` | Query & update | `Query()`, `Update()`, `Explain()`, `GetNamedGraphs()`, `GetStatistics()` |
| `RdfEngine` | Parse & write RDF | `ParseAsync()`, `WriteAsync()`, `DetermineFormat()`, `NegotiateFromAccept()` |
| `PruneEngine` | Store maintenance | `Execute()` with `PruneOptions` → `PruneResult` |

**Assessment:** This is a mature API design choice. The 21 public types provide a small, learnable surface while the 140+ internal types retain full implementation flexibility. The DTO-based return types (`QueryResult`, `UpdateResult`, `PruneResult`, `StoreStatistics`) are clean and serialization-friendly.

**Concern:** Static facades make dependency injection harder. For embedding scenarios requiring testability, an interface layer (e.g., `ISparqlEngine`) would be valuable — noted as deferred in ADR-021.

### 2.3 Storage Architecture

The storage layer is the strongest part of the codebase:

| Component | Design | Assessment |
|-----------|--------|------------|
| **QuadStore** | Multi-index (GSPO/GPOS/GOSP/TGSPO), ReaderWriterLockSlim | Clean orchestration, proper lifecycle |
| **QuadIndex** | B+Tree, 16KB pages, 185-degree, 56-byte temporal keys | Efficient, soft-delete only (no merge) |
| **AtomStore** | Append-only mmap, FNV-1a hash (16M buckets), lock-free reads | Elegant design, atomic pointer swap for resize |
| **WAL** | Fixed 72-byte records, hybrid checkpoint (16MB or 60s) | Correct, conservative fsync strategy |
| **PageCache** | Clock algorithm, 10K page slots | Simple, effective, not tunable |
| **TrigramIndex** | UTF-8 trigrams, mmap posting lists | Good pre-filter, no deletion support |

**Key strength:** The temporal design (ValidFrom/ValidTo/TransactionTime in every key) is deeply integrated, not bolted on. AS OF, DURING, and ALL VERSIONS queries are first-class operations.

**Key concern:** Default disk footprint is **~5.5 GB per store** (4x1GB indexes + 1GB atoms + 512MB hash). ForTesting reduces this to ~320MB, but production deployments need to account for this.

### 2.4 SPARQL Engine

At 44,743 lines, the SPARQL engine is comparable in scope to mid-tier industry implementations:

| Aspect | Assessment |
|--------|------------|
| **Parser** | Excellent — true zero-GC ref struct, recursive descent, depth-limited |
| **Executor** | Good — pull-based operator pipeline, filter pushdown, plan caching |
| **Operators** | Good — TriplePatternScan, MultiPatternScan (NLJ), ServiceScan |
| **Functions** | Excellent — 50+ functions, proper type coercion |
| **W3C conformance** | Outstanding — 100% core (1,181/1,181), 100% SPARQL Query (421/421) |

**Critical limitation: Nested Loop Join only.** No hash join means queries with wide intermediate results can exhibit O(M^N) behavior. Filter pushdown mitigates this significantly, but for analytical workloads with many unbound variables, performance will degrade. This is acceptable for the cognitive agent use case (small, focused queries) but would be a blocker for general-purpose SPARQL endpoint use.

**Cardinality estimation** is heuristic-based (predicate-only statistics, rough estimates). Sufficient for current workloads but won't adapt to skewed distributions.

### 2.5 Zero-GC Compliance

The zero-GC design is **genuine and consistent**, not just marketing:

- Parsers: `ref struct` with `ReadOnlySpan<char>` — verified zero-allocation
- Query operators: `ref struct` with pooled buffers — 93-99% stack reduction achieved
- Writers: Streaming output, no intermediate collections
- Critical insight: "Zero-GC means no *uncontrolled* allocations" — pooled heap memory is acceptable

**Remaining allocation hotspots:**
- `Regex` in FILTER evaluation (recompiled per call)
- JSON-LD parser (System.Text.Json context allocations)
- AtomStore.GetAtomString() (intentional — not hot path)

---

## 3. Quality Assessment

### 3.1 Testing

| Metric | Value |
|--------|-------|
| Total test cases | 3,970 |
| W3C conformance tests | 2,063/2,069 (99.7%) |
| Test-to-source ratio | 0.58:1 (lines) |
| Concurrency tests | 15 dedicated tests |
| REPL tests | 147 tests |
| Benchmark classes | 97 |

**Strengths:**
- W3C conformance suite is comprehensive and 100% on core
- Cross-process store coordination tests exist
- Benchmark suite covers all major components

**Gaps:**
- Mercury.Solid: only 25 tests for 4,459 lines of code (0.56% ratio) — needs expansion
- No property-based / fuzz testing for parsers
- No load/stress testing beyond benchmarks
- TemporalResultEnumerator disposal compliance not tested

### 3.2 Crash Safety & Durability

The WAL design is **correct and conservative**:
- fsync after every single write (safe but slow: ~300/sec)
- Batch API amortizes fsync (100,000+/sec)
- Hybrid checkpoint triggers bound recovery time
- Recovery scans log, stops at corruption, replays uncommitted

**One concern:** `RollbackBatch()` leaves in-memory indexes dirty. If crash occurs after rollback but before next checkpoint, indexes are inconsistent with WAL. The write lock prevents concurrent access, but recovery doesn't explicitly handle this case.

### 3.3 Concurrency

Threading model is **correct but manual**:
- `ReaderWriterLockSlim` provides single-writer / multiple-reader semantics
- Callers must explicitly `AcquireReadLock()`/`ReleaseReadLock()` around query enumeration
- ref struct constraint prevents the enumerator from managing lock lifetime

This is the right trade-off for zero-GC (using-based lock wrappers would require IDisposable), but it creates a foot-gun for API consumers. The facade layer (`SparqlEngine`) should handle locking internally.

### 3.4 ADR Discipline

24 Mercury ADRs, all but 2 fully implemented. This is **exceptional** engineering discipline:
- Each ADR has clear context, decision, and consequences
- Implementation phases tracked with checkboxes
- Superseded ADRs explicitly noted
- Code traces directly to ADR decisions

---

## 4. Component Maturity Matrix

| Component | Lines | Maturity | Production Ready | Risk |
|-----------|------:|:--------:|:----------------:|:----:|
| Mercury Storage | 6,014 | 10/10 | Yes | Low |
| Mercury SPARQL | 44,743 | 9/10 | Yes | Low |
| Mercury RDF Parsers | 20,342 | 10/10 | Yes | Low |
| Mercury.Pruning | 1,275 | 9/10 | Yes | Low |
| Mercury.Runtime | 3,540 | 10/10 | Yes | Very Low |
| Mercury.Abstractions | ~200 | 10/10 | Yes | Very Low |
| Mercury.Solid | 4,459 | 7/10 | No (needs auth) | Medium |
| Mercury.Mcp | 353 | 9/10 | Yes | Low |
| Minerva.Core | 161 | 1/10 | No | N/A |

---

## 5. Strategic Assessment for 2.0

### What's Strong (Keep)

1. **BCL-only core** — This is a competitive moat. No dependency hell, embeddable everywhere.
2. **W3C conformance** — 100% core conformance is a credibility anchor.
3. **Temporal semantics** — First-class temporal RDF is rare and valuable for cognitive agents.
4. **Zero-GC discipline** — Enables predictable latency for real-time cognitive operations.
5. **Facade API** — Clean 21-type surface makes adoption straightforward.

### What Needs Attention (Before 2.0)

1. **Hash join operator** — NLJ-only execution limits analytical query patterns. Adding even a simple hash join for equi-joins would expand the workload envelope significantly.

2. **Mercury.Solid authentication** — Currently uses `X-WebID` header (development mode). Solid-OIDC is required before any external exposure.

3. **TemporalResultEnumerator safety** — The struct-based enumerator with manual Dispose() is the biggest API foot-gun. Consider a wrapper that enforces cleanup.

4. **Regex caching** in FilterEvaluator — Per-call compilation is a performance cliff for FILTER-heavy queries.

5. **Interface layer for facades** — `ISparqlEngine` / `IRdfEngine` would enable testability without impacting the current static API.

### What Can Wait (Post 2.0)

- B+Tree merge on delete (soft deletes are fine for cognitive workloads)
- PageCache tuning (fixed 10K slots works at current scale)
- Statistics invalidation (stale stats are hints, not correctness issues)
- TrigramIndex deletion support (append-only acceptable)
- AtomStore hash table sizing (16M buckets at 6.25% load is wasteful but not harmful)

### Minerva Reality Check

Minerva is currently 161 lines of stubs. The specs are well-drafted (GGUF, SafeTensors, tokenizers), but the implementation gap is significant. For 2.0, consider:
- Start with GGUF reader (most constrained format, clearest spec)
- BPE tokenizer next (prerequisite for inference)
- Quantized matrix multiplication last (most complex, most hardware-dependent)

The BCL-only constraint will be the hardest part of Minerva — high-performance tensor math without BLAS/MKL/cuBLAS requires either SIMD intrinsics (`Vector256<T>`) or native interop.

---

## 6. Summary Scorecard

| Dimension | Score | Notes |
|-----------|:-----:|-------|
| **Architecture** | 9/10 | Clean layering, BCL-only core, proper separation |
| **Code quality** | 9/10 | Zero-GC discipline, consistent patterns, culture-aware |
| **Testing** | 8/10 | Excellent conformance, gaps in Solid and property testing |
| **Documentation** | 10/10 | 25K lines, ADRs, tutorials, API docs, examples |
| **Performance** | 8/10 | Great batch writes, NLJ limitation for complex queries |
| **Durability** | 9/10 | Correct WAL, conservative fsync, minor rollback edge case |
| **API design** | 9/10 | Clean facades, small surface, needs interface layer |
| **Operational maturity** | 8/10 | Global tools, MCP integration, cross-process coordination |
| **Vision alignment** | 9/10 | Mercury delivers on promise; Minerva not yet started |
| **Overall** | **8.8/10** | Strong v1.3 foundation for cognitive platform |

Mercury at v1.3 is a **technically impressive, well-engineered knowledge substrate** that delivers on its core promises: zero-GC performance, W3C conformance, temporal RDF, and a clean API surface. The path to 2.0 is clear: add a hash join, harden Solid authentication, begin Minerva, and introduce the cognitive layers (Lucy, James, Sky) that give the project its name.
