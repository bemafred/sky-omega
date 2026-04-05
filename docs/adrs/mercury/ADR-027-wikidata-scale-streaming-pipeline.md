# ADR-027: Wikidata-Scale Ingestion Pipeline

## Status

**Status:** Proposed — 2026-04-05

Supersedes: [ADR-026 Bulk Load Path](ADR-026-bulk-load-path.md)

## Context

Mercury has 100% W3C SPARQL conformance (2,069/2,069 tests) and 3,978 passing tests. This is a specification claim. Loading and querying the largest freely available RDF dataset on the planet — Wikidata — is an existence proof. These are categorically different.

### Wikidata and the Blazegraph Crisis

The Wikidata Query Service (WDQS) runs on Blazegraph — unmaintained, end-of-life software failing at scale. The graph contains **16.6 billion triples**, growing ~1 billion per year. Database rebuilds take 1–2 months and crash unpredictably. Recovery from corruption requires restarting from scratch — 60+ day cycles. Queries that previously worked now time out as the graph grows.

The Wikimedia Foundation formed the Wikidata Platform (WDP) team in September 2025 to find a replacement. Two finalists remain: **QLever** (University of Freiburg, C++, Apache-licensed) and **Virtuoso** (OpenLink). The evaluation runs July 2025 – June 2026, with migration planned after July 2026.

Published WMF benchmarks (390M triple subset, Ryzen 9950X, 192GB RAM):

| Engine | Load Time | Index Size | Avg Query | SPARQL 1.1 Compliance |
|--------|-----------|------------|-----------|----------------------|
| QLever | 231s | 8 GB | 0.7s | Full (June 2025) |
| Virtuoso | 561s | 13 GB | 2.2s | Non-standard gaps |
| Blazegraph | 6,326s | 67 GB | 4.3s | Best at time of test |

Mercury is not competing to replace Blazegraph — different architecture, single-machine, no clustering. But demonstrating that a zero-dependency C# triple store on consumer hardware can hold the full Wikidata graph with 100% SPARQL conformance — that is a statement nobody else is making. QLever reached full SPARQL 1.1 compliance in June 2025. Mercury has had it from the start.

### The Dataset

The Wikidata full RDF dump is distributed as Turtle (`latest-all.ttl.bz2`). On Omega (`~/Library/SkyOmega/datasets/wikidata/full`):

- **114 GB** bzip2-compressed (`latest-all.ttl.bz2`)
- **912 GB** uncompressed (`latest-all.ttl`)
- **~16.6 billion triples** (current Wikidata graph size)

The dump is prefix-heavy Turtle with 30+ prefixes declared at the top of the file (`wd:`, `wdt:`, `wds:`, `p:`, `ps:`, `pq:`, `schema:`, `skos:`, `prov:`, etc.). Each entity (e.g., `wd:Q42`) spans many lines using `;` and `,` continuation syntax.

### Two Access Patterns

Mercury's write path is designed for cognitive use — ACID-compliant, fsync per write, durable thoughts that survive crashes. This is correct and non-negotiable for semantic memory.

But ingesting Wikidata is a bulk load from an immutable source. If the load fails, you delete the store and start over. Per-write fsync is pure waste — on the M5 Max SSD, each fsync costs ~4.2ms, making single-write ingestion of 16.6B triples take ~806 days.

| Pattern | Writes | Durability | Failure mode | fsync strategy |
|---------|--------|------------|--------------|----------------|
| **Cognitive** (MCP, Lucy) | Few per minute | Every write must survive crash | Observation is lost forever | fsync per write (current) |
| **Bulk load** (Wikidata, DBpedia) | Millions per minute | Source is re-loadable | Delete store, reload | fsync at memory pressure or periodic checkpoint |

### Three Memory Walls

Attempting to load the dump through Mercury's current path hits three fatal bottlenecks:

**Wall 1: `RdfEngine.LoadFileAsync` buffers the entire file into a `MemoryStream`.** Lines 91–93 copy the complete file into memory before parsing begins. This exists to avoid `ReaderWriterLockSlim` thread-affinity issues when `FileStream.ReadAsync` resumes on a different thread after `await`. For a 912 GB file on a 128 GB machine, this is physically impossible.

**Wall 2: `_batchBuffer` accumulates all records in memory.** `AddBatched` writes to WAL and buffers every record as `(LogRecord, string, string, string, string)` in a `List<>`. `CommitBatch` then iterates the entire buffer to materialize to indexes. For 16.6B triples at ~100 bytes per tuple, this would require ~1.7 TB of buffer memory.

**Wall 3: `ApplyToIndexes` writes to all four B+Tree indexes simultaneously.** Every `CommitBatch` materializes each record to GSPO, GPOS, GOSP, and TGSP indexes plus the trigram index on literals. At Wikidata scale, this means 4x the page splits, 4x the cache pressure, and 4x the I/O per triple. This is not a memory wall per se, but it is a throughput wall that multiplies the total load time by roughly 4x.

### Store Size Estimation

Four B+Tree indexes, each indexing ~16.6B quads with interned atom IDs:

- Quad index storage: ~32 bytes/entry x 4 indexes x 16.6B = **~2.1 TB**
- Atom table: millions of unique IRIs, hundreds of millions of unique literals
- Total estimated store size: **3–5 TB** on the 8 TB SSD

This is viable on the target hardware (M5 Max, 128 GB unified memory, 8 TB SSD) but leaves no room for waste.

### Load Time Estimation

At 1M triples/sec sustained throughput (optimistic), 16.6B triples takes ~4.6 hours. B+Tree index maintenance degrades as trees grow — page splits, cache eviction, I/O amplification. Realistic estimate for four-index simultaneous writes: **12–30 hours**. With deferred secondary indexing (primary only during load): **5–10 hours** for the primary pass, plus sequential secondary index builds.

## Decision

### 1. Bulk Load Mode

QuadStore gains a bulk load mode that separates the cognitive write path from high-throughput ingestion.

**WriteAheadLog gains `AppendNoSync`** — identical to `Append` but without `Flush(flushToDisk: true)`:

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public void AppendNoSync(LogRecord record)
{
    record.TxId = ++_currentTxId;
    record.Checksum = record.ComputeChecksum();
    record.WriteTo(_writeBuffer);
    _logFile.Write(_writeBuffer);
    // No fsync — caller manages durability
}
```

The existing `Append` (cognitive path) is unchanged.

**`BulkLoadSession`** wraps QuadStore with the bulk-optimized path:

```csharp
var bulkLoad = store.BeginBulkLoad(new BulkLoadOptions
{
    MemoryThresholdBytes = 96L * 1024 * 1024 * 1024, // 96GB on 128GB machine
    OnProgress = (loaded, elapsed) =>
        Console.WriteLine($"{loaded:N0} triples in {elapsed.TotalSeconds:F1}s")
});

foreach (var quad in parser.Parse(stream))
    bulkLoad.Add(quad);

bulkLoad.Commit();
```

**Memory monitoring** tracks WAL size, atom store growth, and estimated B+Tree dirty pages. When dirty footprint approaches the configured threshold, the session commits the current WAL batch (single fsync), checkpoints, flushes atom store indexes, and continues. This creates natural waves: accumulate, flush, accumulate, flush. Each wave processes as many triples as memory allows.

The threshold defaults to 75% of available physical memory. On a 128GB machine you can accumulate far more before flushing than on a 16GB machine. The hardware determines the batch size, not a constant.

**Failure semantics during bulk load:**
- Crash before Commit: store is in an undefined state. Delete and retry.
- Crash during a pressure-flush: partial checkpoint. Guaranteed path is delete and retry.
- Crash after Commit: fully durable.

### 2. Streaming File I/O with Transparent Decompression

`RdfEngine.LoadFileAsync` drops the `MemoryStream` buffer entirely. The thread-affinity problem is solved by using synchronous `Stream.Read` on the `FileStream` — the streaming parsers already use buffered reads internally, and synchronous I/O does not hop threads.

`RdfEngine` gains transparent decompression based on file extension:

```csharp
private static Stream OpenWithDecompression(string filePath)
{
    var fileStream = File.OpenRead(filePath);

    return Path.GetExtension(filePath).ToLowerInvariant() switch
    {
        ".gz"  => new GZipStream(fileStream, CompressionMode.Decompress),
        ".bz2" => new BZip2InputStream(fileStream),  // SharpZipLib — isolated, replaceable
        _      => fileStream
    };
}
```

The BZip2 dependency (SharpZipLib or equivalent) is acceptable — and required, since the Wikidata dump ships as `.bz2`. It is isolated to the stream wrapping layer — parsers and writers never see it. When a BCL-only BZip2 decoder is written, one line changes. The core architecture's semantic sovereignty is unaffected.

Format detection strips compression extensions before determining RDF format:

- `full.ttl.bz2` -> strip `.bz2` -> detect `.ttl` -> Turtle format, BZip2 decompression
- `full.nt.gz` -> strip `.gz` -> detect `.nt` -> N-Triples format, GZip decompression
- `data.ttl` -> no compression -> Turtle format, raw FileStream

### 3. Chunked Batch Commits

The current `RdfEngine.LoadAsync` wraps the entire load in one `BeginBatch`/`CommitBatch`. This is Wall 2 — `_batchBuffer` grows unbounded.

The streaming load path uses **periodic micro-batches**:

```
BeginBatch()
  add N triples (N configurable, default 1M)
CommitBatch()
BeginBatch()
  add N triples
CommitBatch()
...
```

Each `CommitBatch` materializes only that chunk's records from `_batchBuffer`, then clears it. Memory stays bounded. The `BulkLoadSession` manages fsync strategy and memory monitoring at a higher level, while chunked commits manage buffer size. They are complementary, not competing.

Progress reporting hooks into the chunk boundary:

```csharp
OnProgress?.Invoke(new LoadProgress
{
    TriplesLoaded = totalCount,
    Elapsed = elapsed,
    TriplesPerSecond = totalCount / elapsed.TotalSeconds,
    BytesRead = stream.Position,
    Phase = LoadPhase.PrimaryIndex
});
```

### 4. Deferred Secondary Indexing

During bulk load, only the primary GSPO index is built. The other three (GPOS, GOSP, TGSP) and the trigram index are populated in separate sequential passes after the primary load completes.

This requires:

**a) A `BulkLoadMode` on QuadStore** that restricts `ApplyToIndexes` to GSPO only:

```csharp
private void ApplyToIndexesBulk(
    ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate,
    ReadOnlySpan<char> @object, DateTimeOffset validFrom,
    DateTimeOffset validTo, long transactionTime,
    ReadOnlySpan<char> graph)
{
    // Primary index only — secondaries built in Phase B
    _gspoIndex.AddHistorical(subject, predicate, @object,
        validFrom, validTo, transactionTime, graph);
}
```

**b) A `RebuildSecondaryIndexes()` method** that scans GSPO and populates each secondary index:

```csharp
public void RebuildSecondaryIndexes(Action<RebuildProgress>? onProgress = null)
{
    // Phase B1: Scan GSPO -> build GPOS
    ScanAndBuild(_gspoIndex, _gposIndex,
        (s, p, o) => (p, o, s), onProgress, "GPOS");

    // Phase B2: Scan GSPO -> build GOSP
    ScanAndBuild(_gspoIndex, _gospIndex,
        (s, p, o) => (o, s, p), onProgress, "GOSP");

    // Phase B3: Scan GSPO -> build TGSP
    ScanAndBuild(_gspoIndex, _tgspIndex,
        (s, p, o) => (s, p, o), onProgress, "TGSP");

    // Phase C: Build trigram index on literals
    RebuildTrigramIndex(onProgress);
}
```

Each secondary build is a sequential read of the primary index — optimal for B+Tree traversal. The output keys can be sorted before insertion for maximum fill factor and minimal page splits.

**c) A store state marker** — metadata in the pool or store directory indicating construction status:

| State | Meaning | Queries allowed |
|-------|---------|-----------------|
| `Ready` | All indexes built and consistent | Yes — full optimization |
| `PrimaryOnly` | GSPO populated, secondaries empty | Limited — GSPO-only query plans |
| `Building:<index>` | Secondary index construction in progress | Limited — completed indexes available |

The query planner (`SelectOptimalIndex`) respects this state — if GPOS is not yet built, it falls back to GSPO scanning instead of selecting a stale index.

### 5. Streaming Conversion Pipeline

`RdfEngine` gains a conversion capability — parser in, writer out, no store:

```csharp
public static async Task<long> ConvertAsync(
    string inputPath, string outputPath,
    Action<ConvertProgress>? onProgress = null)
{
    var inputFormat = DetectFormat(inputPath);
    var outputFormat = DetectFormat(outputPath);

    await using var input = OpenWithDecompression(inputPath);
    await using var output = OpenWithCompression(outputPath);
    using var writer = CreateWriter(output, outputFormat);

    long count = 0;
    await ParseAsync(input, inputFormat, (s, p, o) =>
    {
        writer.WriteTriple(s, p, o);
        count++;
        if (count % 1_000_000 == 0)
            onProgress?.Invoke(new ConvertProgress { TriplesConverted = count });
    });

    return count;
}
```

This is the purest parser throughput test — zero storage overhead. It validates that Mercury's Turtle parser survives 912 GB of Wikidata before committing to a multi-hour store load.

It also produces artifacts: `mercury --convert latest-all.ttl.bz2 latest-all.nt` generates the N-Triples version, which is independently useful for resumable loading (every line is self-contained, no parser state).

### 6. CLI Convergence

Mercury.Cli becomes the primary tool. All capabilities from Mercury.Cli.Sparql and Mercury.Cli.Turtle are accessible through Mercury.Cli — either via the REPL or command-line arguments.

**ReplSession gains:**

| Command | Description |
|---------|-------------|
| `:load <file>` | Load RDF file into active store (format auto-detected, compression transparent) |
| `:load --bulk <file>` | Bulk load with deferred indexing |
| `:convert <input> <output>` | Streaming format conversion |
| `:benchmark <file>` | Parse benchmark (no store) |

**Mercury.Cli command line gains:**

| Flag | Description |
|------|-------------|
| `--load <file>` | Load file into store at startup |
| `--bulk-load <file>` | Bulk load with deferred indexing |
| `--convert <input> <output>` | Streaming conversion (no store, no REPL) |
| `--rebuild-indexes` | Rebuild secondary indexes on existing store |

Mercury.Cli.Sparql and Mercury.Cli.Turtle continue to work as thin convenience wrappers. They do not gain new capabilities that Mercury.Cli lacks.

### 7. Resumability

**N-Triples:** Trivially resumable. Every line is self-contained. On restart, count committed triples (from store statistics), then skip that many lines from the input file. Byte-offset seeking with scan-to-next-newline also works.

**Turtle:** Resumability requires replaying the prefix declarations (declared once at file start), then seeking to a triple boundary. The approach:

1. Store the byte offset of the last committed chunk in pool metadata
2. On restart, re-parse the prefix header (fast — typically <1 KB)
3. Seek to the stored byte offset
4. Scan forward to the next `.` (statement terminator) at line start
5. Resume parsing

For the initial implementation, the pragmatic path: if load is interrupted, record the triple count. On restart, re-parse from the beginning but skip already-loaded triples (don't write to store until past the checkpoint). This is slower than seeking but correct and simple. Optimize to byte-offset seeking in a later pass.

## Benchmarking Strategy

The WMF published a formal Triple Store Evaluation Methodology. Mercury's benchmarks should be directly comparable — same dataset, overlapping query workloads, same metrics.

### Metrics (aligned with WMF methodology)

| Metric | Description |
|--------|-------------|
| **Ingestion rate** | Triples/sec sustained, with breakdown by phase (primary index, secondary rebuild) |
| **Query latency** | p50, p95, p99 for standard query workloads |
| **Throughput** | Queries/sec under concurrent load |
| **Index size** | On-disk footprint per index and total |
| **Memory usage** | Peak RSS during load and during query |
| **SPARQL compliance** | W3C test suite pass rate (Mercury: 2,069/2,069) |

### Query Workloads

1. **WDBench** — the Wikidata-specific benchmark suite used in the WMF evaluation. Standard academic benchmark for Wikidata SPARQL performance.
2. **WDQS community queries** — representative queries from the Wikidata Query Service logs, published by the WMF as part of their evaluation.
3. **Property path queries** — transitive closure and path traversal, which stress-test index lookup patterns.
4. **Aggregation queries** — GROUP BY, COUNT, HAVING over large result sets.

### What We Publish

A benchmark artifact with:
- Hardware specification (M5 Max, 128 GB unified memory, 8 TB SSD)
- Dataset version (Wikidata dump date, triple count)
- Ingestion timeline (phase breakdown with triples/sec)
- Query results table (latency percentiles per query)
- Comparison context (WMF published results for QLever, Virtuoso, Blazegraph on comparable queries)
- SPARQL conformance (2,069/2,069 W3C, vs. competitors' scores at time of benchmark)

Mercury's differentiators in this context: zero-GC architecture, zero external dependencies (BCL only), bitemporal by default, single-machine consumer hardware. These are not claims of superiority — they are architectural facts that let the benchmark speak for itself.

## Horizontal Scaling: Read-Replica Fleet

Mercury already has a production-grade W3C SPARQL 1.1 Protocol HTTP endpoint (`SparqlHttpServer`) running in both Mercury.Cli (port 3031) and Mercury.Mcp (port 3030). BCL-only, `HttpListener`, full content negotiation (JSON, XML, CSV, TSV for results; Turtle, N-Triples, RDF/XML for graphs), CORS, service description. This is not future work — it exists today.

This means horizontal scaling is an operational concern, not an architectural one:

1. Load Wikidata once on a single machine (this ADR)
2. Copy the store directory to N machines (it's just files — memory-mapped, no daemon state)
3. Start `mercury` or `mercury-mcp` on each node
4. Place a load balancer in front

Each node is fully independent for reads. No coordination protocol, no distributed consensus, no cluster management. The load balancer distributes queries; each node answers from its local store.

### Why This Works

**Zero-GC eliminates the scaling tax.** JVM-based stores (Blazegraph, Jena) suffer GC pause lottery across nodes — one node enters a major collection while others are fine, creating tail latency spikes that the load balancer cannot predict. Mercury has no GC pauses. Every node delivers predictable latency. This is the property that makes load balancing actually work at scale.

**BCL-only means trivial deployment.** Copy binary, copy store files, start. No dependency graph, no configuration drift, no JVM tuning per node. The deployment artifact is the same across all nodes.

**Append-only store means consistent snapshots.** The store can be copied while serving reads — no locking, no quiescing. New nodes join the fleet by receiving a store copy.

**Memory-mapped I/O lets the OS optimize per machine.** Each node's page cache adapts to its available memory independently. No application-level cache coordination needed.

### Write Model

Wikidata dumps are periodic — the current model (even for QLever) is weekly reload from dumps. The write propagation model matches: one ingestion node loads the new dump, snapshots the store, distributes to the fleet. No real-time write replication needed.

For the cognitive write path (Lucy, MCP sessions), writes go to a designated writer node. Read replicas receive periodic snapshots. This is the same model that works for database read replicas everywhere — simple, proven, no distributed transaction complexity.

### Throughput Scaling

If one M5 Max handles X queries/sec on the full Wikidata graph, N machines handle N*X. Linearly. No coordination tax. This is the benchmark number worth publishing — single-node latency AND fleet throughput projection.

## Sky Omega 2.0: Cognitive Nodes

Every other Wikidata backend candidate — QLever, Virtuoso, Blazegraph — is infrastructure. A query engine. A pipe that data flows through.

Sky Omega is not infrastructure. When 2.0 is complete, each node in the fleet runs the full cognitive stack:

- **Mercury** — the knowledge substrate (storage, SPARQL, HTTP endpoint)
- **Minerva** — local LLM inference (zero-GC, Metal/CUDA, no API dependency)
- **Lucy** — semantic memory (structured knowledge, not embeddings)
- **James** — orchestration (judgment, attention, strategy)
- **Sky** — cognitive agent (reasoning, reflection, learning)
- **Mira** — interaction surfaces (CLI, chat, IDE, voice)

Each node doesn't just answer SPARQL queries — it understands the data. A fleet of Sky Omega nodes serving Wikidata is not a query service. It is a distributed knowledge system where every node can reason about its contents, explain its answers, learn from interactions, and maintain epistemic provenance for every assertion.

No other triple store has this trajectory. QLever will always be a fast C++ query engine. Virtuoso will always be database infrastructure. Sky Omega is infrastructure that thinks — and the Wikidata benchmark is the proof that the foundation can hold the weight.

## Validation Sequence

The Wikidata load is validated in stages, each building confidence before committing to the next:

1. **`mercury --convert latest-all.ttl.bz2 latest-all.nt`** — Parser stress test. Proves TurtleStreamParser handles 912 GB (decompressed from 114 GB bz2). Produces N-Triples artifact. Pure throughput, no store. Expected: several hours (decompression-bound).

2. **`:load --bulk latest-all.nt`** — First store load. N-Triples parser (simpler, no prefix state), primary GSPO index only. Resumable. Expected: 5–10 hours.

3. **`:rebuild-indexes`** — Build GPOS, GOSP, TGSP from GSPO scan. Three sequential passes. Expected: 3–6 hours each.

4. **Benchmark queries** — Run WDBench and community queries against the live store. Capture latency percentiles and throughput. Publish results.

5. **`:load --bulk latest-all.ttl.bz2`** — Full Turtle path with BZip2 decompression. Proves the complete pipeline. Second store (or reload after clearing).

Stages 1–4 produce the publishable benchmark artifact. Stage 5 proves generality.

## Implementation Plan

### Phase 1: Bulk Load Foundation
- [ ] `WriteAheadLog.AppendNoSync` — no-fsync variant of Append
- [ ] `BulkLoadSession` — wraps QuadStore with bulk-optimized write path
- [ ] Memory monitoring via `GC.GetGCMemoryInfo()` or WAL size tracking
- [ ] Periodic checkpoint at memory threshold
- [ ] Tests: bulk load vs. cognitive path correctness equivalence

### Phase 2: Streaming I/O
- [ ] `RdfEngine`: Drop `MemoryStream` buffer in `LoadFileAsync`, use synchronous file I/O
- [ ] `RdfEngine`: Add `OpenWithDecompression` — GZip (BCL) + BZip2 (SharpZipLib)
- [ ] `RdfEngine`: Add format detection with compression extension stripping
- [ ] `RdfEngine.ConvertAsync` — streaming parser-to-writer pipeline with progress callback
- [ ] Tests: round-trip conversion tests (Turtle -> NT -> Turtle)

### Phase 3: Chunked Loading
- [ ] `RdfEngine.LoadAsync`/`LoadFileAsync`: Chunked `BeginBatch`/`CommitBatch` (configurable chunk size, default 1M)
- [ ] Progress callback with triples/sec, bytes read, elapsed time
- [ ] Integrate with `BulkLoadSession` for memory-pressure-aware flushing
- [ ] Tests: load + query round-trip at 10M triple scale

### Phase 4: Deferred Secondary Indexing
- [ ] `QuadStore.BulkLoadMode` — restrict `ApplyToIndexes` to GSPO only
- [ ] `QuadStore.RebuildSecondaryIndexes()` — sequential scan-and-build for GPOS, GOSP, TGSP
- [ ] Store state metadata (`Ready`, `PrimaryOnly`, `Building:<index>`)
- [ ] Query planner respects store state — falls back to available indexes
- [ ] Trigram index rebuild as separate phase
- [ ] Tests: query correctness after deferred build matches eager four-index load

### Phase 5: CLI Convergence
- [ ] `ReplSession`: `:load`, `:load --bulk`, `:convert`, `:benchmark`, `:rebuild-indexes`
- [ ] Mercury.Cli: `--load`, `--bulk-load`, `--convert`, `--rebuild-indexes` command-line flags
- [ ] ReplSession `:load` delegates to `RdfEngine.LoadFileAsync` via injected function (maintains transport-agnostic design)
- [ ] Progress display in REPL (triples/sec, ETA, percentage)

### Phase 6: Wikidata Validation and Benchmarking
- [ ] Stage 1: Convert `latest-all.ttl.bz2` -> `latest-all.nt` on Omega
- [ ] Stage 2: Bulk load `latest-all.nt` into Mercury named store
- [ ] Stage 3: Rebuild secondary indexes
- [ ] Stage 4: Run WDBench + community queries, capture benchmark results
- [ ] Stage 5: Load `latest-all.ttl.bz2` directly (full pipeline proof)
- [ ] Publish benchmark artifact with WMF-comparable metrics

## Consequences

### Positive
- Mercury proves it can hold and query the largest freely available RDF dataset on a single machine
- Existence proof exceeds specification compliance — this is what "state of the art" means for a triple store
- Every component of the pipeline is independently testable: decompression, parsing, conversion, loading, indexing
- The benchmark artifact enters the conversation at exactly the moment the Wikidata community is evaluating SPARQL backends
- Horizontal scaling is operational, not architectural — the HTTP endpoint and store-as-files model already exist
- Zero-GC across a fleet eliminates the tail latency problem that plagues JVM-based stores
- Streaming pipeline handles any size dataset — not just Wikidata but any future corpus
- Memory-aware flushing adapts to any hardware (16GB laptop to 128GB workstation)
- Cognitive write path untouched — no risk to semantic memory durability
- Sky Omega 2.0 transforms query nodes into cognitive nodes — a trajectory no other triple store can match

### Trade-offs
- BZip2 introduces a non-BCL dependency (SharpZipLib) — required since Wikidata ships as `.bz2`. Explicitly accepted as isolated and replaceable.
- Deferred indexing adds store state complexity — mitigated by explicit state metadata and query planner awareness
- Two write paths (cognitive + bulk) to maintain — but they share WAL infrastructure and the distinction is a single flag
- Resumability for Turtle is approximate in the initial implementation (re-parse and skip) — acceptable for correctness, optimize later
- Bulk load has no crash recovery — explicit design choice, not a limitation

### Risks
- B+Tree behavior at 16.6B entries is untested territory — page split frequency, cache eviction patterns, and I/O amplification at this scale are unknown unknowns. This is exactly the emergence surface we want to expose.
- Atom table memory pressure — interning billions of unique IRIs may stress `AtomStore` in ways not seen at smaller scale. Wikidata has high atom cardinality (millions of unique predicates via property-specific prefixes like `p:P31`, `ps:P31`, `pq:P31`).
- macOS mmap limits at multi-TB store sizes — may require `vm.max_map_count` tuning or architectural changes to how `QuadIndex` manages memory-mapped regions.
- Store size may approach 5 TB — viable on 8 TB SSD but demands careful space monitoring during load.

## References

- [ADR-022 QuadIndex Generic Keys](ADR-022-quadindex-generic-keys-and-temporal-sort-order.md) — `KeySortOrder`, JIT-devirtualized `KeyComparer`
- [ADR-023 Transactional Integrity](ADR-023-transactional-integrity.md) — WAL transaction boundaries, batch semantics
- [Wikidata SPARQL Query Service Backend Update](https://www.wikidata.org/wiki/Wikidata:SPARQL_query_service/WDQS_backend_update) — Blazegraph replacement project
- [WMF Triple Store Evaluation Methodology](https://www.wikidata.org/wiki/Wikidata:SPARQL_query_service/Triple_store_evaluation_methodology) — evaluation criteria and benchmark methodology
- [Scaling Wikidata Benchmarking Final Report](https://www.wikidata.org/wiki/Wikidata:Scaling_Wikidata/Benchmarking/Final_Report) — QLever, Virtuoso, Blazegraph comparison
- [Wikidata RDF Dump Format](https://www.mediawiki.org/wiki/Wikibase/Indexing/RDF_Dump_Format) — prefix vocabulary, entity structure, statement reification model
- [Wikidata Database Download](https://www.wikidata.org/wiki/Wikidata:Database_download) — dump distribution, formats, truthy vs. full
- fsync micro-benchmark (2026-03-30): M5 Max fsync latency ~4.2ms
