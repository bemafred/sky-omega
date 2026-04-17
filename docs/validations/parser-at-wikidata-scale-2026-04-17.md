# Turtle Parser Validation at Wikidata Scale — 2026-04-17

**What this is:** an end-to-end run of Mercury's Turtle parser and N-Triples writer against the full April 2026 Wikidata dump, validating that the parser fix landed in 1.7.4 holds at real-world scale.

**What this is not:** a Wikidata benchmark. The Mercury triple store was not exercised. See [Scope](#scope) below.

## Scope

### Exercised

- Turtle parser: sliding-buffer lookahead, prefix resolution, RDF 1.2 features (multi-byte UTF-8, triple-quoted literals, reified triples, predicate-object lists, blank nodes, language tags, datatypes)
- N-Triples writer: zero-GC output
- Sustained mmap'd disk I/O on a single process
- The 1.7.4 parser fix (`PeekAhead` + `PeekUtf8CodePoint` self-refill)

### NOT exercised

- Mercury triple store ingestion
- Atom store interning behavior
- B+Tree index write amplification
- WAL / transaction semantics
- Query correctness or latency (SPARQL, OWL)
- Bitemporal operations

Mercury-as-triple-store performance at Wikidata scale is **not** validated by this run. That work belongs to Phase 4 of [ADR-027](../adrs/mercury/ADR-027-wikidata-scale-streaming-pipeline.md) and will produce a separate benchmark record when complete.

## Hardware

| | |
|---|---|
| Machine | MacBook Pro — M5 Max |
| RAM | 128 GB |
| Storage | 8 TB NVMe SSD (APFS) |
| OS | macOS |

## Software

| | |
|---|---|
| Mercury | 1.7.4 (parser fix) |
| .NET | 10.0.0 / Arm64 RyuJIT AdvSIMD |
| Buffer size | 8 KB default |

## Input

| | |
|---|---|
| Source | Wikidata `latest-all.ttl.bz2` (April 2026 dump), decompressed to `latest-all.ttl` |
| Input size | 912 GB Turtle |
| Encoding | UTF-8 |

## Measurements

| Metric | Value |
|---|---|
| **Triples processed** | **21,316,531,403** (21.3 B) |
| **Elapsed time** | 7,892.7 s (2h 11m 32s) |
| **Sustained throughput** | **2,700,774 triples/sec** |
| **Output size (`latest-all.nt`)** | ~2.9 TB (measured near completion; ~150 bytes/triple average) |
| **Average disk write rate** | ~365 MB/sec |

### Throughput stability across the run

Readings captured at three points, wall-clock anchored:

| Wall-clock (local) | Elapsed | Triples | Rate |
|---|---|---|---|
| ~17:06 | 2,132 s | 5.7 B | 2.69 M/sec |
| ~18:23 | 6,735 s | 18.1 B | 2.69 M/sec |
| ~18:41 | 7,892 s | 21.3 B | 2.70 M/sec |

Throughput is essentially bit-identical at the beginning, middle, and end. The parser's own state does not grow pathologically — prefix table is bounded by distinct prefixes in the input, output buffer resets per triple, and the convert path does not intern atoms.

**Scope-honest reading:** the parser + N-Triples writer pipeline has no growth-dependent slowdown over 21.3 B triples on this hardware. That is a narrow, direct finding. It does not generalize to the store-ingestion pipeline, which has its own state (atom store, four indexes, WAL) that is not touched here.

## Historical context

| | |
|---|---|
| Prior failure point (pre-1.7.4) | line 12,741,234 (~10 M triples) |
| Blocker duration | 2026-04-06 → 2026-04-17 (~11 days) |
| Scale multiplier vs blocker | 1,678× |
| Root cause | Turtle parser sliding-buffer lookahead primitives did not self-refill when bytes lay past buffer end; multi-byte UTF-8 and multi-char lookaheads (`@prefix`, `<<`, `"""`, `^^`) silently truncated at the boundary |
| Fix scope | ~30 lines in `TurtleStreamParser.Buffer.cs`: `PeekAhead` and `PeekUtf8CodePoint` loop `FillBufferSync` until enough bytes present or EOF |
| Method | Static analysis → differential boundary tests on ~5 KB synthetic inputs → fix → full-scale validation (this run) |

## Reproduction

```bash
# Install Mercury global tool at 1.7.4 or later
./tools/install-tools.sh

# Convert (expects latest-all.ttl in current directory)
mercury --convert latest-all.ttl latest-all.nt

# With structured metrics output (1.7.5+)
mercury --convert latest-all.ttl latest-all.nt --metrics-out convert.jsonl
```

The `.ttl.bz2` source can be decompressed with `bzip2 -dk latest-all.ttl.bz2` (keeps the .bz2; produces the .ttl).

## Provenance

- Convert logs captured in session transcript, 2026-04-17
- Measurements stored in Mercury semantic memory (graph `urn:sky-omega:session:2026-04-17`), including the scope-correction record after an initial overreach was caught
- Parser fix landed as Mercury 1.7.4; structured-metrics CLI flag as 1.7.5
