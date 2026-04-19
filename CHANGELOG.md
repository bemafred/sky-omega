# Changelog

All notable changes to Sky Omega will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## What's Next

**Sky Omega 2.0.0** will introduce cognitive components: Lucy (semantic memory), James (orchestration), Sky (LLM interaction), and Minerva (local inference).

---

## [1.7.17] - 2026-04-19

### Changed
- **Removed `Console.IsInputRedirected` auto-detect from `--no-repl`.** The 1.7.14 auto-detect was too clever — it broke legitimate REPL scripting like `echo ":stats" | mercury --store foo` or `cat queries.sparql | mercury`, silently exiting with no output instead of processing the piped commands. The REPL already handles piped stdin correctly: `StreamReader.ReadLine()` returns null on EOF and the loop exits. The actual motivating case (Rider's profiler keeping stdin open with no data) is now handled by passing `--no-repl` explicitly. Piped stdin with EOF works out of the box again. Explicit opt-out, no magic.

## [1.7.16] - 2026-04-19

### Performance
- **Bulk load 10 M: +12 % throughput (243 K → 272 K triples/sec).** `AtomStore.ComputeHashUtf8` was a byte-at-a-time FNV-1a loop; release profiling after the 1.7.15 SaveMetadata fix showed it at ~7 % of total time, dominated by the existing-atom probe path (each lookup pays one hash computation). Word-wise variant uses the same FNV-1a constants but processes 8 bytes per iteration via `BinaryPrimitives.ReadUInt64LittleEndian`, with a byte-wise tail for the last 0-7 bytes. `ComputeHash(ReadOnlySpan<char>)` reinterprets the chars as bytes and reuses the UTF-8 path. Hashes are recomputed on every lookup (never persisted across versions), so swapping the hash function is safe. BCL-only — no `System.IO.Hashing` dependency.

## [1.7.15] - 2026-04-18

### Performance
- **Bulk load 10 M: +275 % throughput (64.7 K → 243 K triples/sec).** `QuadIndex.SaveMetadata` unconditionally called `_accessor.Flush()` on every invocation — and on macOS that's an msync of the *entire* 256 GB sparse-mmap region, not a single metadata page. Under bulk load, `AllocatePage` calls `SaveMetadata` per new B+Tree page, so the load was issuing thousands of whole-region msyncs. dotTrace sampling reported it at 1.56 % of profile time — a severe under-count because sampling measures wall-clock hits, not the kernel-time amplification of a blocking msync stalling the whole pipeline. Fix: same shape as the 1.7.9 `FlushPage` fix (Bug 1). In bulk mode `SaveMetadata` does the mmap writes (no syscall) and returns; the single `Flush()` at `QuadStore.FlushToDisk()` covers durability for every metadata update made during the load. Cognitive mode unchanged — per-update durability preserved.

## [1.7.14] - 2026-04-18

### Added
- **CLI: `--no-repl` flag and auto-detection of non-TTY stdin.** `mercury --bulk-load file.nt` used to always drop into the REPL after the load — which blocks forever in `read(stdin)` under profilers, CI, child-process launches, or anything else that doesn't have a terminal. Now: if stdin is redirected (pipe, file, `/dev/null`), or `--no-repl` is passed, the CLI exits after the load completes. TTY stdin still drops into the REPL as documented. Discovered when the first dotTrace run wedged because the profiler's stdin isn't a TTY.

### Fixed
- **SPARQL parser: prefixed-name datatype before `;` in INSERT DATA.** `ParseTermForUpdate` only accepted `^^<full-iri>` and ignored `^^prefix:local`. A triple like `ex:s ex:date "2026-04-17"^^xsd:date ; ex:topic "first"` left the parser mid-literal, which misread the trailing `xsd:date ;` and either hung (default graph variant) or threw `Expected '}' but found ';'` (in-graph variant). Full-IRI datatypes were not affected. Legal per SPARQL 1.1 Update grammar but not exercised by the W3C sparql11-update conformance suite. Two regression tests landed yesterday pin this behavior; both pass now.

### Performance
- **Bulk load 10 M: +12 % throughput (57.7 K → 64.7 K triples/sec), GC heap −46 % (154 MB → 83 MB).** Four changes, all identified by dotTrace sampling on a release build:
  - *Atom IDs instead of strings through the batch buffer.* `QuadStore.AddBatched` was calling `AtomStore.GetAtomString` four times per triple to materialize IDs back into strings, buffering those strings, then having `QuadIndex.Add` re-intern them at commit. 40 M string allocations and 40 M redundant hash lookups per 10 M load. The buffer now holds `List<LogRecord>` (atom IDs already live in the record), and a new `ApplyToIndexesById` / `ApplyDeleteToIndexesById` pair routes IDs straight to `QuadIndex.AddRaw` / the new `DeleteRaw`. Removes 1.03 % of profile time in `GetAtomString`, 0.2 % in redundant intern, 0.86 % in `BulkMoveWithWriteBarrier` (string refs no longer tracked by GC). `Recover` and immediate-mode `Add`/`Delete` also switched to the ID path — fewer lookups, same semantics.
  - *Cache `DateTimeOffset.UtcNow` once per batch.* `AddBatched` was calling `UtcNow` per triple for the transaction-time column and `AddCurrentBatched` was calling it again for valid-from. Both now read `_batchTransactionTimeTicks` / `_batchCurrentFrom` captured in `BeginBatch`. Bitemporally equivalent (a batch is one moment) and removes 1.37 % of profile time.
  - *Stop fstat'ing the data file on every atom insert.* `AtomStore.EnsureDataCapacity` read `_dataFile.Length`, which on macOS is an `fstat()` syscall. Added a tracked `_dataCapacity` field updated in lock-step with `SetLength`. Saves 0.57 % of profile time.
  - *Stop fstat'ing the index file on every page allocation.* Same pattern in `QuadIndex.AllocatePage`. Added `_fileCapacity`. Saves 0.54 %.

## [1.7.13] - 2026-04-18

### Fixed
- **`AtomStore` hash table is no longer fixed at 16 M buckets.** The previous `HashTableSize` const (1 << 24) overflowed at ~15.5 M unique atoms (96.72 % load factor, 4096-probe limit). Crashed the 100 M bulk-load gradient at 58.3 M triples — which by then had exhausted 16 M buckets worth of unique entity IRIs, predicates, and literals. The const is now a per-instance `_hashTableSize` initialized from `StorageOptions.AtomHashTableInitialCapacity` (default 16 M, preserves cognitive behavior). Bulk mode bumps the table to 256 M buckets (8 GB sparse mmap), mirroring the `QuadIndex` 256 GB sparse-mmap pattern — physical disk usage tracks touched buckets, not virtual size. Existing stores reopen with their original layout because the bucket count is derived from the index file length. `Clear()` now zeroes in 1 GB chunks so bulk-mode tables don't overflow `Span`'s 2 GB limit. Option B (dynamic rehash-on-grow) stays on the roadmap; only relevant if a cognitive store ever approaches its configured ceiling.

## [1.7.12] - 2026-04-18

### Fixed (workaround)
- **Bulk-mode `QuadIndex` pre-sizes the mmap to 256 GB per index.** Previously the mmap was created at the initial file size (default 1 GB). When `AllocatePage` extended the file via `SetLength`, the existing mmap still covered only 1 GB — writes to pages past that boundary hit `AccessViolationException` in `SplitLeafPage`. Crashed during 100 M bulk-load gradient at 27.9 M triples (~150 K pages × 16 KB = 2.4 GB into a 1 GB mmap). This is a temporary workaround: macOS allocates 256 GB of virtual address space immediately but physical pages only on touch (sparse file), and the per-process VM ceiling (~64 TB) leaves room for full Wikidata at ~1.8 TB per index. Proper fix (mmap-grow via unmap + recreate, OR chunked mmap with stable per-chunk pointers) is a follow-up workstream — not required while this baseline is sufficient. Cognitive mode still uses the original 1 GB initial size; small stores stay small.

## [1.7.11] - 2026-04-18

### Fixed
- **N-Triples parser sliding-buffer lookahead.** Same class of bug as the Turtle parser fix in 1.7.4: `Peek` and `PeekAhead` did not refill the buffer when bytes lay past the current end. Worse, the original `Peek` had `return _endOfStream ? -1 : -1;` — a typo where the refill case was missing entirely (both branches return -1). Any literal larger than the 8 KB buffer hit "Unterminated string literal" prematurely. Discovered when the 100 M bulk-load gradient run crashed at line 27,515,974 of the Wikidata N-Triples slice — a 4,202-character MathML literal exceeded the buffer. Fix: looped self-refill via new `FillBufferSync` (mirror of `FillBufferAsync` using sync `_stream.Read`), same pattern as `TurtleStreamParser.Buffer.cs`. The N-Triples parser now handles arbitrarily long literals correctly, and slow-stream cases (Read returning small chunks) work via the loop.

## [1.7.10] - 2026-04-18

### Fixed
- **Bulk load no longer crashes during checkpoint with `AccessViolationException`.** `CheckpointIfNeeded` was running unconditionally during bulk load, calling `CollectPredicateStatistics` which scans the GPOS index. In bulk mode, GPOS receives no writes (only GSPO is populated; secondaries are deferred to `RebuildSecondaryIndexes`), so scanning an uninitialized B+Tree page walked into invalid memory. Crashed at ~20.8 M triples on the 100 M gradient run when WAL size triggered checkpoint. Fix: skip `CheckpointIfNeeded` entirely when `_bulkLoadMode` — bulk-load contract defers all durability to a single `FlushToDisk()` at load completion. (Defensive guards against scanning uninitialized indexes are a follow-up; this unblocks the gradient.)

## [1.7.9] - 2026-04-18

### Fixed
- **Bulk load no longer issues msync per page write.** `QuadIndex.FlushPage` was calling `MemoryMappedViewAccessor.Flush()` on every B+Tree page modification — that's `msync()` on macOS, and it flushes the **entire** mapped region (multi-GB), not a single page. With ~5 page writes per triple insert × 100 K triples per chunk, the bulk-load path was issuing 500 K full-region msyncs per chunk and pinning the SSD random-write IOPS at ~5,500/sec. This was the actual bottleneck (the 1.7.8 `FileOptions.WriteThrough` change was a no-op for the mmap write path). Now `FlushPage` is a no-op in bulk mode; `QuadIndex.Flush()` exposes the deferred msync; `QuadStore.FlushToDisk()` calls it on all four indexes at load completion alongside the WAL flush. Cognitive mode keeps per-page durability semantics. Expected throughput improvement: 10–100× — depends on how IOPS-bound the previous gradient was vs other costs (atom interning likely the next ceiling).

## [1.7.8] - 2026-04-18

### Fixed
- **`QuadIndex` honors `bulkMode` in its `FileStream` open options.** Previously opened with `FileOptions.WriteThrough` unconditionally; now branches the same way `WriteAheadLog` does. (Effect on the bulk-load hot path turned out to be minimal because writes go through the mmap accessor, not the FileStream — but the option mismatch was inconsistent with WAL design and worth correcting. The actual write-amplification bottleneck is fixed in 1.7.9.)

## [1.7.7] - 2026-04-17

### Fixed
- **`RdfEngine.ConvertAsync` now routes N-Triples output through `NTriplesStreamWriter`** — the convert fast-path previously wrote spans directly to a `StreamWriter`, bypassing the writer's `WriteLiteral` escape logic entirely. This made the 1.7.6 `WriteLiteral` fix dormant for the convert code path. Now the convert emits valid N-Triples end-to-end. Without this, `mercury --convert` kept producing invalid output even with 1.7.6 installed.

## [1.7.6] - 2026-04-17

### Fixed
- **N-Triples writer re-escapes unescaped quotes in literals** — `NTriplesStreamWriter.WriteLiteral` now determines the close-quote position by scanning backward from the suffix shape (`^^<...>` datatype, `@lang-tag`, or plain), rather than forward with backslash tracking. The Turtle parser unescapes `\"` to `"` in memory (the in-memory form is the logical value), so forward escape-tracking in the writer was unreliable once the escape information was lost. Symptom: any literal containing an unescaped quote in the in-memory representation — whose source Turtle used `\"` — was truncated at the first internal quote, producing invalid N-Triples. Discovered when the full Wikidata dump `latest-all.nt` (3.0 TB produced by 1.7.4 convert) failed the Mercury N-Triples parser at triple 2,718.
- **Round-trip regression tests added** — Turtle → N-Triples → parse round-trip for literals with escaped quotes, lang tags, datatypes, and internal backslashes. Closes the coverage gap where writers were never tested against their own readers in the "convert" combination. (`NTriplesStreamWriterTests.WriteTriple_*InternalQuotes*` and `RoundTrip_TurtleLiteralWithEscapedQuotes_ParsesBack`.)

## [1.7.5] - 2026-04-17

### Added
- **`--metrics-out <file>` flag** (mercury CLI) — appends JSONL records for `--convert`, `--load`/`--bulk-load`, and `--rebuild-indexes` operations. Each progress callback emits one record (denser than the throttled terminal display); each phase ends with a `*.summary` record. Captures triple counts, throughput (avg + recent), elapsed time, GC heap, working set, and free disk for benchmark artifacts and post-run analysis.

## [1.7.4] - 2026-04-17

### Fixed
- **Turtle parser sliding-buffer lookahead** — `PeekAhead` and `PeekUtf8CodePoint` now self-refill when the requested bytes lie past the current buffer end, looping until either enough bytes are present or the stream reaches EOF. Previously, multi-byte UTF-8 sequences and multi-character lookaheads (`@prefix`, `<<`, `"""`, `^^`) silently truncated when they straddled the buffer boundary, producing the cumulative "Expected '.' after triple" failure observed during Wikidata ingestion at line 12,741,234. Fixes the parser blocker tracked since 2026-04-06.
- **`PeekAhead` negative-offset guard** — added `pos < 0` check to prevent IndexOutOfRangeException in the triple-term parser's backward-lookahead path.

### Added
- **Boundary-differential test suite** (`ParserBoundaryDifferentialTests`) — 30 cases covering boundary positions for `@prefix`, `<<`, `"""`, multi-byte UTF-8, blank nodes, dot runs, and combined constructs under 1-byte-per-Read slow streams. Reproduces the Wikidata failure mode on synthetic ~5 KB inputs in milliseconds, eliminating the need for the 912 GB dataset to validate parser correctness.

## [1.7.3] - 2026-04-06

### Removed
- **Conditional breakpoint parameters** from `drhook_step_breakpoint` and `drhook_step_break_function` MCP tools — netcoredbg conditional breakpoints use the same func-eval path that deadlocks on macOS/ARM64. Underlying DAP plumbing preserved for future re-enablement.

## [1.7.2] - 2026-04-06

DrHook validation — diagnosed netcoredbg func-eval deadlock, removed broken tools, added integration tests and process metrics.

### Removed
- **`drhook_step_eval`** — netcoredbg's DAP evaluate request hangs indefinitely on macOS/ARM64. The func-eval machinery deadlocks; its internal 15s command timeout never fires. Diagnosed via file-based tracing in `DapClient.SendRequestAsync`. The DAP `context` parameter is irrelevant — netcoredbg ignores it.
- **Watch mode** (`drhook_step_watch_add/remove/list`) — depends on evaluate.

### Added
- **Process metrics in every step response** — OS-level (WorkingSet, PrivateBytes, ThreadCount) via `Process.GetProcessById` syscalls; managed-level (GC heap size, collection counts) via EventPipe `System.Runtime` counters. Deltas from previous capture included. No DAP eval needed.
- **11 integration tests** — exercise session lifecycle, stepping, variable inspection, breakpoint management, and conditional stopping against a live DAP session with pre-built VerifyTarget.
- **Conditional stopping patterns** — netcoredbg conditional breakpoints hang (same func-eval path). Two workarounds validated: (1) unconditional breakpoint inside code-level `if`; (2) `Debugger.Break()`.
- **VerifyTarget project** — pre-built .NET console app for integration tests (`tests/DrHook.Tests/Stepping/VerifyTarget/`).

### Fixed
- **`_sourceBreakpoints.Clear()` missing from `CleanupAsync`** — breakpoint registry was not fully reset between sessions.

### Changed
- **DEBUGGING.md** — documents known limitations, conditional stopping workarounds, launch requirements.
- **ADR-005** — status changed to Superseded. ADR-002 amended with eval hang findings.

## [1.7.1] - 2026-04-05

### Fixed
- **Turtle parser BCP-47 language tags** — tags containing digits (e.g., `@be-tarask`) were rejected. Fixed character class in `LANGTAG` production to include digits per RFC 5646.

## [1.7.0] - 2026-04-05

Wikidata-scale ingestion pipeline — Mercury can now load the full Wikidata dump (16.6B triples, 912 GB Turtle) on a single machine.

### Added

#### Bulk Load Foundation (ADR-027 Phase 1)
- **WAL bulk mode** — `FileOptions.None` with 64 KB buffer bypasses OS write-through cache. 4.3x faster than `WriteThrough` per micro-benchmark (40.8M records/sec at 3.1 GB/sec).
- **`CommitBatchNoSync`** — WAL commit marker without fsync. Single `FlushToDisk()` at load completion.
- **`StorageOptions.BulkMode`** — GSPO-only indexing during bulk load, skip GPOS/GOSP/TGSP/trigram.

#### Streaming I/O (ADR-027 Phase 2)
- **`LoadFileAsync` rewritten** — streams directly from disk with chunked batch commits. No MemoryStream buffering. Decoupled parse-then-write: parser fills buffer (no lock), buffer flushed to store (lock only during materialization).
- **Compression-aware format detection** — `FromPathStrippingCompression` handles `.ttl.gz`, `.nt.bz2`, etc.
- **Transparent GZip decompression** — BCL `GZipStream`, no external dependencies.
- **`ConvertAsync`** — streaming parser-to-writer pipeline, no store. Pure throughput test for parser validation.
- **Progress reporting** — `LoadProgress` with triples/sec, GC heap, working set, interval rate.

#### Deferred Secondary Indexing (ADR-027 Phase 4)
- **`RebuildSecondaryIndexes`** — scans GSPO, populates GPOS/GOSP/TGSP with dimension remapping via `AddRaw` (raw atom-ID insertion, no re-interning). Trigram index rebuilt from object literals.
- **`StoreIndexState`** — persisted state metadata (`Ready`/`PrimaryOnly`/`Building:<index>`). Query planner falls back to GSPO when secondaries unavailable.

#### CLI Convergence (ADR-027 Phase 5)
- **`--store <name>`** — named stores via `MercuryPaths` (e.g., `--store wikidata`)
- **`--bulk-load <file>`** — bulk load with deferred indexing
- **`--load <file>`** — standard load at startup
- **`--convert <in> <out>`** — streaming format conversion (no store, exits after)
- **`--rebuild-indexes`** — build secondary indexes from GSPO
- **`--min-free-space <GB>`** — disk space safeguard (default: 100 GB for bulk loads)
- **REPL commands** — `:load [--bulk] <file>`, `:convert <in> <out>`, `:rebuild-indexes`

#### Runtime Diagnostics
- **Startup diagnostics** — store path, index state, mode, free disk space, min threshold
- **Progress display** — every 10 seconds: elapsed (h:m:s), triples, avg rate, recent rate, GC heap, RSS
- **Completion summary** — triples, elapsed, avg rate, GC heap, working set, free disk remaining

### Fixed

- **Turtle parser buffer boundary bug** — `Peek()` returned `-1` when the input buffer was exhausted mid-statement, even when more data existed in the stream. Fix: `FillBufferSync()` shifts remaining data left and reads more, synchronously. The buffer slides through the stream at any fixed size — 32 bytes parses the same as 8 KB. No dynamic buffer growth needed.
- **FHIR ontology** (88,428 triples, statements up to 3,965 lines) now loads successfully.
- **100 KB IRI and 500 KB literal** — previously documented as parser buffer limitations. Eliminated by the sliding buffer fix.

### Added (Documentation)
- **DEBUGGING.md** — DrHook debugging methodology: when to observe, how to set breakpoints, workflow examples.

## [1.6.1] - 2026-03-30

Closes the test debugging gap — DrHook can now debug .NET test code through `dotnet test`.

### Added

- **`drhook_step_test` MCP tool** — debug .NET test methods end-to-end. Launches `dotnet test` with `VSTEST_HOST_DEBUG=1`, parses the testhost PID from stdout, attaches netcoredbg to the child process, sets breakpoints, and continues to the first hit. Same technique VS Code uses. Test code was the last unreachable target for DrHook stepping.

### Fixed

- **Test debugging gap** — previously documented as a known limitation ("dotnet test spawns a child process that the debugger cannot follow"). The limitation was in the approach (launching under debugger), not in the tooling. Hybrid launch-then-attach solves it.

## [1.6.0] - 2026-03-30

DrHook breakpoint registry, expression evaluation, and environment variable support.

### Added

#### DrHook — Breakpoint Registry (ADR-001)
- **Breakpoint registry** in `SteppingSessionManager` — tracks source, function, and exception breakpoints. Every mutation syncs the full set to DAP, eliminating silent set-and-replace behavior.
- **`drhook_step_breakpoint_remove`** — remove a specific source, function, or exception breakpoint
- **`drhook_step_breakpoint_list`** — list all active breakpoints with file, line, condition, and type
- **`drhook_step_breakpoint_clear`** — clear all breakpoints or by category (source/function/exception)
- **Multi-breakpoint DapClient overloads** — `SetBreakpointsAsync` and `SetFunctionBreakpointsAsync` accept lists
- **Registry seeding** — initial breakpoints from `LaunchAsync`/`RunAsync` seed the registry

#### DrHook — Expression Evaluation (ADR-002)
- **`drhook_step_eval` MCP tool** — evaluate C# expressions in the current stack frame via DAP `evaluate`. Supports property access, indexing, method calls, arithmetic, boolean logic. More targeted than `drhook_step_vars`.
- **`DapClient.EvaluateAsync`** — sends DAP `evaluate` request with frame context
- **Structured error returns** — failed evaluations return JSON with error message, not exceptions. The agent learns from what doesn't work.

#### DrHook — Environment Variables
- **`drhook_step_run` env support** — pass environment variables as `KEY=VALUE` strings to the launched process via DAP `launch` env field

### Changed

- **Tool descriptions updated** — breakpoint tools now say "Add" instead of "Set", removed "WARNING: set-and-replace" notes
- **`drhook_step_launch` description** — recommends `drhook_step_run` or `drhook_step_test` when possible

### Validated

- **ADR-004 final criterion** — netcoredbg `launch` does not follow `dotnet test` child processes. Confirmed empirically: testhost spawned via vstest socket protocol, breakpoint in test code never hit. Workaround validated: prebuilt file-based apps via `dotnet exec`.
- **All four DrHook ADRs accepted** — ADR-001, ADR-002, ADR-003, ADR-004

## [1.5.1] - 2026-03-29

DrHook process-owning stepping and DAP robustness — validated via ad-hoc Sky Omega MVP.

### Added

#### DrHook — Process-Owning Stepping (ADR-004)
- **`drhook_step_run` MCP tool** — launches a .NET executable under debugger control via DAP `launch` with `stopAtEntry`. Eliminates race conditions and MCP timeout issues that made `step_launch` (attach mode) impractical for AI agents. DrHook owns the target process lifecycle.
- **`DapClient.LaunchTargetAsync`** — sends DAP `launch` request with `program`, `args`, `cwd`, `stopAtEntry` parameters
- **Process lifecycle ownership** — `SteppingSessionManager` tracks `_ownsProcess` flag; launch mode terminates debuggee on disconnect, attach mode preserves it
- **ADR-004** — documents design, unknowns, and 5/6 verified success criteria

### Fixed

- **DAP byte framing for non-ASCII** — `Content-Length` is byte count but `DapClient` read chars via `StreamReader`. Non-ASCII characters (Swedish å, ö in type names, paths) caused byte/char misalignment, corrupting the DAP message stream. Fix: read raw bytes from `BaseStream`, decode UTF-8. Header parsing moved to byte-level to avoid `StreamReader` internal buffering. Bug was masked in DrHook.Poc because SteppingHost used ASCII-only code.

### Changed

- **CLAUDE.md** reduced from 879 to 271 lines (69%) — architecture details, SPARQL reference, and production hardening extracted to `docs/architecture/technical/`
- **README.md** documentation guide updated with link to Kjell Silverstein poetry collection

### Documentation

- **`docs/architecture/technical/mercury-internals.md`** — storage, durability, concurrency, zero-GC patterns
- **`docs/architecture/technical/sparql-reference.md`** — features, operators, formats, temporal extensions
- **`docs/architecture/technical/production-hardening.md`** — benchmarks, NCrunch, cross-process coordination
- **`docs/poetry/kjell-silverstein-collected.md`** — Sky Omega explained without a single line of code

---

## [1.5.0] - 2026-03-23

DrHook runtime observation substrate — Sky Omega's second MCP server.

### Added

#### DrHook — Runtime Observation Substrate (ADR-004)
- **DrHook core library** — .NET runtime inspection with two observation layers:
  - **EventPipe observation** — passive profiling (thread sampling, GC events, exception tracing, contention detection) with structured anomaly detection
  - **DAP stepping** — controlled execution via Debug Adapter Protocol (breakpoints, step-through, variable inspection) using netcoredbg
- **DrHook MCP server** (`drhook-mcp`) — 13 MCP tools exposing observation and stepping to AI coding agents, packaged as .NET global tool
- **Hypothesis-driven inspection** — every observation requires a stated hypothesis, forcing epistemic discipline (what do you expect vs what do you see)
- **Code version anchoring** — assembly version captured with every observation to prevent bitemporal desync
- **Signal summarization** — EventPipe output collapsed to structured summaries with anomaly flags (HOTSPOT, GC_PRESSURE, CONTENTION, EXCEPTIONS, IDLE)
- **File-based inspection target** (`examples/drhook-target.cs`) — five scenarios for testing DrHook capabilities
- **16 unit tests** across ProcessAttacher, DapClient, NetCoreDbgLocator, and SteppingSessionManager

### Changed
- **Mercury MCP server version** now reads from assembly attribute instead of hardcoded string
- **Directory.Build.props** Product name updated from "Sky Omega Mercury" to "Sky Omega"
- **install-tools.sh/.ps1** updated to include `drhook-mcp` in global tool installation
- **.mcp.json** updated with DrHook dev-time server configuration

---

## [1.4.0] - 2026-03-22

Transactional integrity and trigram read path — two major architectural advances.

### Added

#### WAL v2 — Transactional Integrity (ADR-023)
- **Transaction boundaries** — `BeginTx`/`CommitTx` markers in WAL enable crash-safe batch semantics; recovery replays only committed transactions
- **Deferred materialization** — batched writes buffer in memory, apply to indexes only at `CommitBatch()`; `RollbackBatch()` discards buffer without touching indexes
- **Per-write transaction time** — each write generates `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` stored in WAL and indexes; preserved through crash recovery
- **80-byte WAL v2 record** — includes `GraphId`, `TransactionTimeTicks`, and transaction markers
- **Replay idempotence** — WAL recovery is safe to re-run; already-applied records are skipped

#### Trigram Read Path (ADR-024)
- **Scan-level pre-filtering for `text:match`** — `MultiPatternScan` restricts enumerator to candidate object atoms from the trigram index, reducing full-text search from O(N) to O(k × log N)
- **Selectivity-based fallback** — candidate sets exceeding 10,000 atoms revert to brute-force scan to avoid index overhead on low-selectivity queries

### Fixed

- **`text:match` culture dependency** — switched to `OrdinalIgnoreCase` per ADR-014, fixing locale-sensitive matching on Swedish characters (å, ä, ö)

---

## [1.3.12] - 2026-03-21

Full-text search is now unconditional — the trigram index is always created.

### Removed

- **`EnableFullTextSearch` option** — `StorageOptions.EnableFullTextSearch` property removed; every `QuadStore` now unconditionally creates a `TrigramIndex`

---

## [1.3.11] - 2026-03-20

Full-text search enabled by default — LLMs can now discover and use `text:match` out of the box.

### Changed

- **`EnableFullTextSearch` defaults to `true`** — trigram index is now built automatically for all new stores; previously required explicit opt-in via `StorageOptions`
- **`mercury_query` MCP tool description** — now advertises `text:match(?var, "term")` for case-insensitive full-text search, making it discoverable by LLMs

---

## [1.3.10] - 2026-03-17

Exclusive store lock and `mercury_version` MCP tool.

### Added

- **Exclusive store lock** — persistent pools acquire a file lock (`store.lock`) preventing concurrent access from multiple processes; throws `StoreInUseException` with owner PID if store is already in use; OS releases lock automatically on crash
- **`mercury_version` MCP tool** — exposes server version at runtime via assembly `InformationalVersion`

### Fixed

- **Multi-process store corruption** — two `mercury-mcp` or `mercury` processes opening the same store would corrupt data; now the second process gets a clear error with actionable guidance

---

## [1.3.9] - 2026-03-17

QuadStorePool explicit store lifecycle — remove implicit creation side effects.

### Changed

- **`QuadStorePool` indexer (`pool["name"]`)** — now pure lookup; throws `KeyNotFoundException` if store doesn't exist (was: silently created store as side effect)
- **`Clear(name)` and `Switch(a, b)`** — now throw if stores don't exist (no implicit creation)
- **`PruneEngine`** — uses explicit `GetOrCreate("secondary")` for prune target

### Added

- **`QuadStorePool.EnsureActive(name)`** — creates store if needed, sets it as active; the proper API for initialization
- **`QuadStorePool.GetOrCreate(name)`** — creates store if needed, returns it; explicit about creation intent

### Fixed

- **Mercury MCP server fresh install** — `mercury-mcp` now calls `EnsureActive("primary")` on startup, fixing "No active store is set" error when `~/Library/SkyOmega/stores/mcp/` has no existing `pool.json`

---

## [1.3.8] - 2026-03-10

QuadIndex generic key fields and time-leading sort order (ADR-022).

### Changed

#### QuadIndex Generic Keys (ADR-022 Phase 1)
- **TemporalKey fields renamed** — `SubjectAtom`/`PredicateAtom`/`ObjectAtom` → `Primary`/`Secondary`/`Tertiary`; `GraphAtom` → `Graph`
- **QuadIndex method parameters** — `subject`/`predicate`/`obj` → `primary`/`secondary`/`tertiary`
- **QuadStore public API** — `obj` → `@object` (idiomatic C# keyword escape)
- **TemporalIndexType enum** — `SPOT`/`POST`/`OSPT`/`TSPO` → `GSPO`/`GPOS`/`GOSP`/`TGSP`

### Fixed

#### TGSP Index (ADR-022 Phases 2–3)
- **TGSP was a byte-for-byte duplicate of GSPO** — introduced `KeySortOrder` enum and `KeyComparer` delegate so TGSP uses `TimeFirst` sort order (ValidFrom leads), while GSPO/GPOS/GOSP use `EntityFirst`
- **Temporal range queries O(N) → O(log N + k)** — `CreateSearchKey` now produces time-leading bounds for `TimeFirst` indexes, enabling B+Tree seek instead of full scan

### Added

- **Page access instrumentation** (`#if DEBUG`) — `PageAccessCount`/`ResetPageAccessCount()` on `QuadIndex` for verifying index efficiency in tests
- **3 verification tests** — sort order correctness (TimeFirst, EntityFirst) and page access efficiency comparison

### Documentation

- **ADR-022** completed — all 4 phases implemented
- **Initial ADRs** for Lucy, James, Sky, and Mira cognitive components (Sky Omega 2.0)

---

## [1.3.7] - 2026-03-07

### Fixed

- **CLI argument validation** — prevent accidental store creation from unrecognized arguments

---

## [1.3.6] - 2026-03-05

CLI and MCP connectivity improvements.

### Added

- **`:attach` / `:a` REPL command** — attach to running MCP (or other Mercury instance) from within the CLI REPL, not just from the command line
- **`mercury_store` MCP tool** — exposes store path via MCP for Claude Code
- **`StorePathHolder`** — DI-injectable store path for MCP tools

### Fixed

- **Pipe prompt sync** — `ReadUntilPromptAsync` no longer false-matches `<...> ` in help text as a prompt, fixing delayed/out-of-sync responses in attached mode
- **`:detach` cleanup** — no more spurious "Cannot access a closed pipe" errors after detaching; graceful pipe disposal on all code paths
- **macOS store paths** — `MercuryPaths.Store()` now resolves to `~/Library/SkyOmega/stores/` on macOS instead of the non-standard `~/.local/share/`

### Changed

- **CLI prompt renamed** — `mercury>` → `cli>` for visual balance with `mcp>` and consistency with store names
- **Goodbye message** — now ends with double linefeed for cleaner terminal output

### Documentation

- All tutorials updated for `cli>` prompt
- ADR-006 updated for `cli>` prompt

---

## [1.3.0] - 2026-02-18

Breaking API surface changes: public facade layer and type internalization.

### Added

#### Public Facades (ADR-003)
- **`SparqlEngine`** — static facade for SPARQL query/update with `QueryResult`/`UpdateResult` DTOs, `Explain()`, `GetNamedGraphs()`, `GetStatistics()`
- **`RdfEngine`** — static facade for RDF parsing, writing, loading, and content negotiation across all six formats
- **`PruneEngine`** — static facade for dual-instance pruning with `PruneOptions`/`PruneResult` DTOs
- **`RdfTripleHandler`/`RdfQuadHandler`** — public delegates for zero-GC callback parsing

#### Public DTOs
- **`QueryResult`** — Success, Kind, Variables, Rows, AskResult, Triples, ErrorMessage, ParseTime, ExecutionTime
- **`UpdateResult`** — Success, AffectedCount, ErrorMessage, ParseTime, ExecutionTime
- **`StoreStatistics`** — QuadCount, AtomCount, TotalBytes, WalTxId, WalCheckpoint, WalSize
- **`PruneResult`** — Success, ErrorMessage, QuadsScanned, QuadsWritten, BytesSaved, Duration, DryRun
- **`PruneOptions`** — DryRun, HistoryMode, ExcludeGraphs, ExcludePredicates
- **`ExecutionResultKind`** enum — Empty, Select, Ask, Construct, Describe, Update, Error, ...

### Changed

#### Breaking: ~140 Types Internalized (ADR-003 Phases 3-4)
- All RDF parsers now internal: `TurtleStreamParser`, `NTriplesStreamParser`, `NQuadsStreamParser`, `TriGStreamParser`, `JsonLdStreamParser`, `RdfXmlStreamParser` — use `RdfEngine` instead
- All RDF writers now internal: `TurtleStreamWriter`, `NTriplesStreamWriter`, `NQuadsStreamWriter`, `TriGStreamWriter`, `RdfXmlStreamWriter`, `JsonLdStreamWriter` — use `RdfEngine` instead
- SPARQL internals now internal: `SparqlParser`, `QueryExecutor`, `UpdateExecutor`, `SparqlExplainer`, `FilterEvaluator`, `QueryPlanner`, `QueryPlanCache`, `LoadExecutor` — use `SparqlEngine` instead
- Content negotiation now internal: `RdfFormatNegotiator`, `SparqlResultFormatNegotiator` — use `RdfEngine.DetermineFormat()`/`NegotiateFromAccept()` instead
- Result writers/parsers now internal: `SparqlJsonResultWriter`, `SparqlXmlResultWriter`, `SparqlCsvResultWriter` and corresponding parsers
- OWL/RDFS reasoning now internal: `OwlReasoner`, `InferenceRules`
- **Mercury public surface reduced to 21 types** (3 facades, 2 protocol, 11 storage, 3 diagnostics, 2 delegates)

### Documentation

- **`docs/api/api-usage.md`** restructured around public facades (1,529 → 900 lines); all internal type examples removed
- **`docs/tutorials/embedding-mercury.md`** updated to use `SparqlEngine`, `RdfEngine` facades
- **CLAUDE.md** updated with Mercury public type count (21 types)
- **ADR-003** completed — Buffer Pattern for Stack Safety, extended to cover facade design and type internalization

---

## [1.2.2] - 2026-02-15

Complete tutorial suite and infrastructure fixes.

### Added

#### ADR-002 Tutorial Suite (Phases 1-5)
- **Phase 1 — The Front Door:** `getting-started.md` (clone to first query in 30 minutes), `mercury-cli.md`, `mercury-mcp.md`, examples README, CLAUDE.md and MERCURY.md bootstrap improvements
- **Phase 2 — Tool Mastery:** `mercury-sparql-cli.md`, `mercury-turtle-cli.md`, `your-first-knowledge-graph.md` (RDF onboarding), `installation-and-tools.md`
- **Phase 3 — Depth and Patterns:** `temporal-rdf.md`, `semantic-braid.md`, `pruning-and-maintenance.md`, `federation-and-service.md`
- **Phase 4 — Developer Integration:** `embedding-mercury.md`, `running-benchmarks.md`, knowledge directory seeding (`core-predicates.ttl`, `convergence.ttl`, `curiosity-driven-exploration.ttl`, `adr-summary.ttl`)
- **Phase 5 — Future:** `solid-protocol.md` (server setup, resource CRUD, containers, N3 Patch, WAC/ACP access control), `eee-for-teams.md` (team-scale EEE methodology with honest boundaries); Minerva tutorial deferred

### Fixed

#### AtomStore Safety (ADR-020)
- **Publication order fix** — store atom bytes before publishing pointer, preventing readers from seeing uninitialized memory
- **CAS removal** — removed unnecessary compare-and-swap on append-only offset
- **Growth ordering** — correct file growth sequencing

#### ResourceHandler Read Lock
- **Missing read lock** in `ResourceHandler` — added `AcquireReadLock`/`ReleaseReadLock` around query enumeration (ADR-021)

#### LOAD File Support
- **`LOAD <file://...>` wired into all update paths** — CLI, MCP tools, MCP pipe sessions, HTTP server
- **Thread affinity fix** — `LoadFromFileAsync` runs on dedicated thread via `Task.Run` to maintain `ReaderWriterLockSlim` thread affinity across `BeginBatch`/`CommitBatch`
- **CLI pool.Active initialization** — eagerly creates primary store to prevent `InvalidOperationException` on first access

### Documentation

- **ADR-002** status updated to "Phase 5 Partially Accepted"
- **STATISTICS.md** documentation lines updated to 26,292 (grand total 165,677)

---

## [1.2.1] - 2026-02-09

Pruning support in Mercury CLI and MCP, with QuadStorePool migration.

### Added

#### Pruning in Mercury CLI
- **`:prune` REPL command** with options: `--dry-run`, `--history preserve|all`, `--exclude-graph <iri>`, `--exclude-predicate <iri>`
- **QuadStorePool migration** — CLI now uses `QuadStorePool` instead of raw `QuadStore`, enabling dual-instance pruning via copy-and-switch
- **Flat-store auto-migration** — existing CLI stores at `~/Library/SkyOmega/stores/cli/` are transparently restructured into pool format on first run

#### Pruning in Mercury MCP
- **`mercury_prune` MCP tool** with parameters: `dryRun`, `historyMode`, `excludeGraphs`, `excludePredicates`
- **QuadStorePool migration** — MCP server now uses `QuadStorePool`, pruning switches stores seamlessly without restart

#### Infrastructure
- **`PruneResult`** class in Mercury.Abstractions for standardized pruning results
- **`Func<QuadStore>` factory constructor** for `SparqlHttpServer` — each request resolves store via factory, enabling seamless store switching after prune without HTTP server restart
- **Flat-store auto-migration** in `QuadStorePool` constructor — detects `gspo.tdb` in base path and restructures into `stores/{guid}/` + `pool.json`

### Changed

- **Mercury.Cli** — migrated from `QuadStore` to `QuadStorePool` (in-memory mode uses `QuadStorePool.CreateTemp`)
- **Mercury.Mcp** — migrated from `QuadStore` to `QuadStorePool` (`MercuryTools`, `HttpServerHostedService`, `PipeServerHostedService`)
- **SparqlHttpServer** — field changed from `QuadStore` to `Func<QuadStore>` factory; existing constructor preserved for backward compatibility

### Tests

- **17 new tests** (3,913 total): `ReplPruneTests` (7), `QuadStorePoolPruneTests` (6), `QuadStorePoolMigrationTests` (4)

---

## [1.2.0] - 2026-02-09

Namespace restructuring for improved code navigation and IDE experience.

### Changed

#### SPARQL Types Namespace (`SkyOmega.Mercury.Sparql.Types`)
- **Split `SparqlTypes.cs`** (2,572 lines, 37 types) into individual files under `Sparql/Types/`
- **New namespace** `SkyOmega.Mercury.Sparql.Types` — one file per type (Query, GraphPattern, SubSelect, etc.)
- Follows folder-correlates-to-namespace convention for better code navigation

#### Operator Namespace (`SkyOmega.Mercury.Sparql.Execution.Operators`)
- **Moved 14 operator files** from `Execution/` to `Execution/Operators/`
- **New namespace** `SkyOmega.Mercury.Sparql.Execution.Operators` — scan operators, IScan interface, ScanType enum
- Files: TriplePatternScan, MultiPatternScan, DefaultGraphUnionScan, CrossGraphMultiPatternScan, VariableGraphScan, SubQueryScan, SubQueryJoinScan, SubQueryGroupedRow, BoxedSubQueryExecutor, QueryCancellation, SyntheticTermHelper, SlotBasedOperators, IScan, ScanType

### Documentation

- **CLAUDE.md** updated with Operators/ and Types/ folder structure
- **STATISTICS.md** line counts updated

---

## [1.1.1] - 2026-02-07

Version consolidation and CLI improvements.

### Added

- **`-v`/`--version` flag** for all CLI tools (`mercury`, `mercury-mcp`, `mercury-sparql`, `mercury-turtle`)

### Changed

- **Centralized versioning** - `Directory.Build.props` is now the single source of truth for all project versions
- **Mercury.Mcp reset** from `2.0.0-preview.1` to `1.1.1` to align with unified versioning

---

## [1.1.0] - 2026-02-07

Global tool packaging, persistent stores, and Microsoft MCP SDK integration.

### Added

#### Global Tool Packaging (ADR-019)
- **`mercury`** - SPARQL CLI installable as .NET global tool
- **`mercury-mcp`** - MCP server installable as .NET global tool
- **`mercury-sparql`** - SPARQL query engine demo as global tool
- **`mercury-turtle`** - Turtle parser demo as global tool
- **Install scripts** - `tools/install-tools.sh` (bash) and `tools/install-tools.ps1` (PowerShell)

#### Persistent Store Defaults
- **`MercuryPaths`** - Well-known persistent store paths per platform
  - macOS: `~/Library/SkyOmega/stores/{name}/`
  - Linux/WSL: `~/.local/share/SkyOmega/stores/{name}/`
  - Windows: `%LOCALAPPDATA%\SkyOmega\stores\{name}\`
- **`mercury`** defaults to persistent store at `MercuryPaths.Store("cli")`
- **`mercury-mcp`** defaults to persistent store at `MercuryPaths.Store("mcp")`

#### Claude Code Integration
- **`.mcp.json`** - Dev-time MCP config for Claude Code at repo root
- **User-scope install** - `claude mcp add --scope user mercury -- mercury-mcp`

### Changed

#### Microsoft MCP SDK Migration
- **Replaced hand-rolled `McpProtocol.cs`** (~494 lines) with official `ModelContextProtocol` NuGet package (0.8.0-preview.1)
- **`[McpServerToolType]`** attribute-based tool registration via `MercuryTools.cs`
- **Hosted service model** - PipeServer and SparqlHttpServer as `IHostedService` implementations
- **`Microsoft.Extensions.Hosting`** - Proper application lifecycle management

#### CLI Library Extraction (ADR-018)
- Extracted CLI logic into testable libraries (`Mercury.Sparql.Tool`, `Mercury.Turtle.Tool`)

### Documentation

- **ADR-019** - Global Tool Packaging and Persistent Stores
- **ADR-018** - CLI Library Extraction
- **Mercury ADR index** updated with all 20 ADRs and correct statuses

---

## [1.0.0] - 2026-01-31

Mercury reaches production-ready status with complete W3C SPARQL 1.1 conformance.

### Added

#### SPARQL Update Sequences
- **Semicolon-separated operations** - Multiple updates in single request (W3C spec [29])
- **`ParseUpdateSequence()`** - Returns `UpdateOperation[]` for batched execution
- **`UpdateExecutor.ExecuteSequence()`** - Static method for atomic sequence execution
- **Prologue inheritance** - PREFIX declarations carry across sequence operations

#### W3C Update Test Graph State Validation
- **Expected graph comparison** - Tests now validate resulting store state, not just execution success
- **Named graph support** - `ut:data` and `ut:graphData` parsing from manifests
- **`ExtractGraphFromStore()`** - Enumerate store contents for comparison
- **Blank node isomorphism** - Correct matching via `SparqlResultComparer.CompareGraphs()`

#### Service Description Enrichment
- **`sd:feature` declarations** - PropertyPaths, SubQueries, Aggregates, Negation
- **`sd:extensionFunction`** - text:match full-text search
- **RDF output formats** - Turtle, N-Triples, RDF/XML for CONSTRUCT/DESCRIBE

### Changed

#### W3C Conformance (100% Core Coverage)
- **SPARQL 1.1 Query**: 421/421 passing (100%)
- **SPARQL 1.1 Update**: 94/94 passing (100%)
- **All tests** now validate actual graph contents, not just success status

### Fixed

#### SPARQL 1.1 CONSTRUCT/Aggregate Gaps (3 tests)
- **`constructlist`** - RDF collection `(...)` syntax in CONSTRUCT templates now generates proper `rdf:first/rdf:rest` chains
- **`agg-empty-group-count-graph`** - COUNT without GROUP BY inside GRAPH ?g now correctly returns count per graph (including 0 for empty graphs)
- **`bindings/manifest#graph`** - VALUES inside GRAPH binding same variable as graph name now correctly filters/expands based on UNDEF vs specific values

#### SPARQL 1.1 Update Edge Cases (10 tests)
- **USING clause dataset restriction** (4 tests) - USING without USING NAMED now correctly restricts named graph access
- **Blank node identity** (4 tests) - Same bnode label across statements now creates unique nodes per W3C scoping rules
- **DELETE/INSERT with mixed UNION branches** (2 tests) - UNION containing both GRAPH and default patterns now executes correctly via `_graphPatternFlags` tracking

### Documentation

- **ADR-002** status changed to "Accepted" - 1.0.0 operational scope achieved
- Release checklist complete per ADR-002 success criteria

---

## [0.6.2] - 2026-01-27

Critical stack overflow fix for parallel test execution.

### Fixed

#### Stack Overflow Resolution (ADR-011)
- **QueryResults reduced from 90KB to 6KB** (93% reduction)
  - Changed `TemporalResultEnumerator` from `ref struct` to `struct`
  - Pooled enumerator arrays in `MultiPatternScan` and `CrossGraphMultiPatternScan`
  - Boxed `GraphPattern` (~4KB) to move from stack to heap
- **All scan types dramatically reduced**:
  - `MultiPatternScan`: 18,080 → 384 bytes (98% reduction)
  - `DefaultGraphUnionScan`: 33,456 → 1,040 bytes (97% reduction)
  - `CrossGraphMultiPatternScan`: 15,800 → 96 bytes (99% reduction)
- **Parallel test execution restored** - Previously limited to single thread as workaround

### Changed

- Re-enabled parallel test execution in xunit.runner.json
- All 3,824 tests pass with parallel execution

### Documentation

- **ADR-011** completed - QueryResults Stack Reduction via Pooled Enumerators
- **StackSizeTests** added - Enforces size constraints to prevent regression

---

## [0.6.1] - 2026-01-26

Full W3C SPARQL 1.1 Query conformance achieved.

### Fixed

#### CONSTRUCT Query Fixes (5 tests now passing)
- **sq12** - Subquery computed expressions (CONCAT, STR) now propagate to CONSTRUCT output
  - Added `HasRealAggregates` to distinguish aggregates from computed expressions
  - Implemented per-row expression evaluation in subquery execution
- **sq14** - `a` shorthand (rdf:type) now correctly expanded in CONSTRUCT templates
- **constructwhere02** - Duplicate triple deduplication in CONSTRUCT WHERE
- **constructwhere03** - Blank node shorthand handling in CONSTRUCT WHERE
- **constructwhere04** - FROM clause graph context in CONSTRUCT WHERE

### Changed

#### W3C Conformance (100% core coverage)
- **SPARQL 1.1 Query**: 418/418 passing (previously 410/418)
- **SPARQL 1.1 Update**: 94/94 passing (unchanged)
- **Total W3C tests**: 1,872 passing

### Remaining Known Limitations
- `constructlist` - RDF collection syntax in CONSTRUCT templates (high complexity)
- `agg-empty-group-count-graph` - COUNT without GROUP BY inside GRAPH (high complexity)
- `bindings/manifest#graph` - VALUES binding GRAPH variable (high complexity)

---

## [0.6.0-beta.1] - 2026-01-26

Major W3C conformance milestone and CONSTRUCT/DESCRIBE content negotiation.

### Added

#### Content Negotiation for CONSTRUCT/DESCRIBE
- **RDF format negotiation** - Accept header parsing with quality values
- **Turtle output** (default) - Human-readable with prefix support
- **N-Triples output** - Canonical format for interoperability
- **RDF/XML output** - XML-based serialization

#### W3C Test Infrastructure
- **Graph isomorphism** - Backtracking search for blank node mapping
- **RDF result parsing** - Support for .ttl, .nt, .rdf expected results
- **CONSTRUCT test validation** - Previously skipped tests now enabled

### Changed

#### W3C Conformance (99% coverage)
- **Total tests**: 1,872 → 3,464 (W3C + internal)
- **SPARQL 1.1 Query**: 96% (215/224) - 9 skipped for SERVICE/entailment
- **SPARQL 1.1 Update**: 100% (94/94)
- **All RDF formats**: 100% conformance maintained

### Fixed

#### SPARQL Conformance Fixes
- **Unicode handling** - Supplementary characters (non-BMP) via System.Text.Rune
- **Aggregate expressions** - COUNT, AVG error propagation, HAVING multiple conditions
- **BIND scoping** - Correct variable visibility in nested groups
- **EXISTS/NOT EXISTS** - Evaluation in ExecuteToMaterialized path
- **CONCAT/STRBEFORE/STRAFTER** - Language tag and datatype handling
- **GRAPH parsing** - Nested group pattern handling
- **IN/NOT IN** - Empty patterns and expressions
- **GROUP BY** - Expression type inference

#### Parser Fixes
- **Turtle Unicode escapes** - \U escape sequences beyond BMP
- **Named blank node matching** - Consistent across parsers
- **Empty string literals** - Correct handling in result comparison

### Documentation

- **ADR-002** - Sky Omega 1.0.0 Operational Scope defined
- **ADR-010** - W3C conformance status updated
- **ADR-012** - Conformance fixes documented

---

## [0.5.0-beta.1] - 2026-01-01

First versioned release of Sky Omega Mercury - a semantic-aware storage and query engine with zero-GC performance design.

### Added

#### Storage Layer
- **QuadStore** - Multi-index quad store with GSPO ordering and named graph support
- **B+Tree indexes** - Page-cached indexes with LRU eviction (clock algorithm)
- **Write-Ahead Logging (WAL)** - Crash-safe durability with hybrid checkpoint triggering
- **AtomStore** - String interning with memory-mapped storage
- **Batch write API** - High-throughput bulk loading (~100,000 triples/sec)
- **Bitemporal support** - ValidFrom/ValidTo/TransactionTime on all quads
- **Disk space enforcement** - Configurable minimum free disk space checks

#### RDF Parsers (6 formats)
- **Turtle** - RDF 1.2 with RDF-star support, zero-GC handler API
- **N-Triples** - Zero-GC handler API + async enumerable
- **N-Quads** - Zero-GC handler API + async enumerable
- **TriG** - Full named graph support
- **RDF/XML** - Streaming parser
- **JSON-LD** - Near zero-GC with context handling

#### RDF Writers (6 formats)
- **Turtle** - With prefix support and subject grouping
- **N-Triples** - Streaming output
- **N-Quads** - Named graph serialization
- **TriG** - Named graph serialization with prefixes
- **RDF/XML** - Full namespace support
- **JSON-LD** - Compact output with context

#### SPARQL Engine
- **Query types** - SELECT, ASK, CONSTRUCT, DESCRIBE
- **Graph patterns** - Basic, OPTIONAL, UNION, MINUS, GRAPH (IRI and variable)
- **Subqueries** - Single and multiple nested SELECT
- **Federated queries** - SERVICE clause with ISparqlServiceExecutor
- **Property paths** - `^iri`, `iri*`, `iri+`, `iri?`, `path/path`, `path|path`
- **Filtering** - FILTER, VALUES, EXISTS, NOT EXISTS, IN, NOT IN
- **40+ built-in functions**:
  - String: STR, STRLEN, SUBSTR, CONTAINS, STRSTARTS, STRENDS, CONCAT, UCASE, LCASE, etc.
  - Numeric: ABS, ROUND, CEIL, FLOOR
  - DateTime: NOW, YEAR, MONTH, DAY, HOURS, MINUTES, SECONDS, TZ, TIMEZONE
  - Hash: MD5, SHA1, SHA256, SHA384, SHA512
  - UUID: UUID, STRUUID (time-ordered UUID v7)
  - Type checking: isIRI, isBlank, isLiteral, isNumeric, BOUND
  - RDF terms: LANG, DATATYPE, LANGMATCHES, IRI, STRDT, STRLANG, BNODE
- **Aggregation** - GROUP BY, HAVING, COUNT, SUM, AVG, MIN, MAX, GROUP_CONCAT, SAMPLE
- **Modifiers** - DISTINCT, REDUCED, ORDER BY (ASC/DESC), LIMIT, OFFSET
- **Dataset clauses** - FROM, FROM NAMED with cross-graph join support
- **SPARQL-star** - Quoted triples with automatic reification expansion
- **SPARQL EXPLAIN** - Query execution plan analysis

#### SPARQL Update
- INSERT DATA, DELETE DATA
- DELETE WHERE, DELETE/INSERT WHERE (WITH clause)
- CLEAR, DROP, CREATE
- COPY, MOVE, ADD
- LOAD (with size and triple limits)

#### Temporal SPARQL Extensions
- **AS OF** - Point-in-time queries
- **DURING** - Range queries for overlapping data
- **ALL VERSIONS** - Complete history retrieval

#### Query Optimization
- **Statistics-based join reordering** - 10-100x improvement on multi-pattern queries
- **Predicate pushdown** - 5-50x improvement via FilterAnalyzer
- **Plan caching** - LRU cache with statistics-based invalidation
- **Cardinality estimation** - Per-predicate statistics collection

#### Full-Text Search
- **TrigramIndex** - UTF-8 trigram inverted index (opt-in)
- **text:match()** - SPARQL FILTER function
- **Unicode case-folding** - Supports Swedish å, ä, ö and other languages

#### OWL/RDFS Reasoning
- **Forward-chaining inference** - Materialization with fixed-point iteration
- **10 inference rules**:
  - RDFS: subClassOf, subPropertyOf, domain, range
  - OWL: TransitiveProperty, SymmetricProperty, inverseOf, sameAs, equivalentClass, equivalentProperty

#### SPARQL Protocol
- **HTTP Server** - W3C SPARQL 1.1 Protocol (BCL HttpListener)
- **Content negotiation** - JSON, XML, CSV, TSV result formats
- **Service description** - Turtle endpoint metadata

#### Pruning System
- **PruningTransfer** - Dual-instance copy-and-switch compaction
- **Filtering** - GraphFilter, PredicateFilter, CompositeFilter
- **History modes** - FlattenToCurrent, PreserveVersions, PreserveAll
- **Verification** - DryRun, checksums, audit logging

#### Infrastructure
- **ILogger abstraction** - Zero-allocation hot path, NullLogger for production
- **IBufferManager** - Unified buffer allocation with PooledBufferManager
- **Content negotiation** - RdfContentNegotiator for format detection

### Architecture

- **Zero external dependencies** - Core Mercury library uses BCL only
- **Zero-GC design** - ref struct parsers, ArrayPool buffers, streaming APIs
- **Thread-safe** - ReaderWriterLockSlim with documented locking patterns
- **.NET 10 / C# 14** - Modern language features and runtime

### Testing

- **1,785 passing tests** across 62 test files
- **Component coverage**: Storage, SPARQL, parsers, writers, temporal, reasoning, concurrency
- **Zero-GC compliance tests** - Allocation validation

### Benchmarks

- **8 benchmark classes** - BatchWrite, Query, SPARQL, Temporal, Parsers, Filters, Concurrent
- **Performance baselines established** - Documented in CLAUDE.md

### Known Limitations

- SERVICE clause does not yet support joining with local patterns
- Multiple SERVICE clauses in single query not yet supported
- TrigramIndex uses full rebuild on delete (lazy deletion not implemented)

[1.3.8]: https://github.com/bemafred/sky-omega/releases/tag/v1.3.8
[1.3.7]: https://github.com/bemafred/sky-omega/releases/tag/v1.3.7
[1.3.6]: https://github.com/bemafred/sky-omega/releases/tag/v1.3.6
[1.3.0]: https://github.com/bemafred/sky-omega/releases/tag/v1.3.0
[1.2.2]: https://github.com/bemafred/sky-omega/releases/tag/v1.2.2
[1.2.1]: https://github.com/bemafred/sky-omega/releases/tag/v1.2.1
[1.2.0]: https://github.com/bemafred/sky-omega/releases/tag/v1.2.0
[1.1.1]: https://github.com/bemafred/sky-omega/releases/tag/v1.1.1
[1.1.0]: https://github.com/bemafred/sky-omega/releases/tag/v1.1.0
[1.0.0]: https://github.com/bemafred/sky-omega/releases/tag/v1.0.0
[0.6.2]: https://github.com/bemafred/sky-omega/releases/tag/v0.6.2
[0.6.1]: https://github.com/bemafred/sky-omega/releases/tag/v0.6.1
[0.6.0-beta.1]: https://github.com/bemafred/sky-omega/releases/tag/v0.6.0-beta.1
[0.5.0-beta.1]: https://github.com/bemafred/sky-omega/releases/tag/v0.5.0-beta.1
