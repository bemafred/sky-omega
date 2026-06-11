# Mercury Architecture Decision Records

| ADR | Title | Status |
|-----|-------|--------|
| [ADR-001](ADR-001-compaction.md) | Compaction Strategy | Completed |
| [ADR-002](ADR-002-ibuffermanager-adoption.md) | IBufferManager Adoption | Completed |
| [ADR-003](ADR-003-buffer-pattern.md) | Buffer Pattern for Stack Safety | Completed |
| [ADR-004](ADR-004-service-scan-interface.md) | SERVICE Execution via IScan Interface | Completed |
| [ADR-005](ADR-005-quadstore-pooling-and-clear.md) | QuadStore Pooling and Clear | Completed |
| [ADR-006](ADR-006-dual-mode-store-access.md) | Dual-Mode Store Access | Completed |
| [ADR-007](ADR-007-union-service-execution.md) | UNION Branch SERVICE Execution | Completed |
| [ADR-008](ADR-008-quadstore-pool-unified.md) | Unified QuadStorePool | Completed |
| [ADR-009](ADR-009-stack-overflow-mitigation.md) | Stack Overflow Mitigation Strategy | Completed |
| [ADR-010](ADR-010-w3c-test-suite-integration.md) | W3C Test Suite Integration | Completed |
| [ADR-011](ADR-011-queryresults-stack-reduction.md) | QueryResults Stack Reduction | Completed |
| [ADR-011b](ADR-011b-implementation-plan.md) | QueryResults Stack Reduction (Implementation Plan) | Completed |
| [ADR-012](ADR-012-conformance-fix-plan.md) | W3C SPARQL Conformance Fix Plan | Completed |
| [ADR-013](ADR-013-remaining-w3c-sparql-gaps.md) | Remaining W3C SPARQL Gaps | Completed |
| [ADR-014](ADR-014-culture-invariance.md) | Culture Invariance for RDF/SPARQL | Completed |
| [ADR-015](ADR-015-solid-architecture.md) | Mercury.Solid Architecture | Completed |
| [ADR-016](ADR-016-cli-tool-upgrade.md) | Mercury CLI Tool Upgrade | Completed |
| [ADR-017](ADR-017-test-environment-independence.md) | Test Environment Independence | Superseded |
| [ADR-018](ADR-018-cli-library-extraction.md) | CLI Library Extraction | Completed |
| [ADR-019](ADR-019-global-tool-packaging.md) | Global Tool Packaging and Persistent Stores | Completed |
| [ADR-020](ADR-020-atomstore-single-writer-contract.md) | AtomStore Single-Writer Contract and Safe Publication | Completed |
| [ADR-021](ADR-021-hardening-store-contract-query-ergonomics-and-surface-isolation.md) | Hardening Store Contract, Query Ergonomics, and Surface Isolation | Deferred |
| [ADR-022](ADR-022-quadindex-generic-keys-and-temporal-sort-order.md) | QuadIndex Generic Key Fields and Time-Leading Sort Order | Completed |
| [ADR-023](ADR-023-transactional-integrity.md) | Transactional Integrity — WAL, Batch Rollback, Transaction Time | Completed |
| [ADR-024](ADR-024-trigram-index-read-path-disconnection.md) | Trigram Index Read Path Disconnection | Completed |
| [ADR-025](ADR-025-repl-line-editor.md) | REPL Line Editor | Completed |
| [ADR-026](ADR-026-bulk-load-path.md) | Bulk Load Path | Superseded |
| [ADR-027](ADR-027-wikidata-scale-streaming-pipeline.md) | Wikidata-Scale Ingestion Pipeline | Completed |
| [ADR-028](ADR-028-atomstore-rehash-on-grow.md) | AtomStore Rehash-on-Grow | Completed |
| [ADR-029](ADR-029-store-profiles.md) | Store Profiles — Cognitive, Graph, Reference, Minimal | Completed |
| [ADR-030](ADR-030-bulk-load-and-rebuild-performance.md) | Bulk Load and Rebuild Performance Architecture | Completed (Phases 2-3 Superseded by ADR-032/033) |
| [ADR-031](ADR-031-read-only-session-fast-path.md) | Read-Only Session Fast Path | Completed (Pieces 1+2; Piece 3 deferred to 031b) |
| [ADR-032](ADR-032-radix-external-sort.md) | Radix External Sort for Index Rebuild | Completed |
| [ADR-033](ADR-033-bulk-load-radix-external-sort.md) | Bulk-Load Radix External Sort | Completed |
| [ADR-034](ADR-034-sorted-atom-store-for-reference.md) | SortedAtomStore for Reference Profile | Completed (Phase 1; Phase 2 BBHash → ADR-039) |
| [ADR-035](ADR-035-phase7a-metrics-infrastructure.md) | Phase 7a Metrics Infrastructure | Completed |
| [ADR-036](ADR-036-bzip2-streaming-decompression.md) | BZip2 Streaming Decompression | Completed (Phase 1 + Phase 2 shipped) |
| [ADR-037](ADR-037-pipelined-spill-bulk-builder.md) | Pipelined Spill in SortedAtomBulkBuilder | Completed (production-validated cycle 9, 2026-05-09) |
| [ADR-038](ADR-038-merge-read-side-optimization.md) | Merge-phase Read-Side Optimization (intermediate prefix compression + frontier readahead) | Completed (production-validated cycle 10 r4, 2026-05-13) |
| [ADR-039](ADR-039-mphf-on-sealed-atom-set.md) | Minimal Perfect Hash Function (BBHash) over Sealed Atom Set | Completed (production-validated cycle 10 r4, 2026-05-13) |
| [ADR-040](ADR-040-readahead-memory-adaptive-sizing.md) | Readahead Memory Adaptive Sizing — Substrate Adapts to Host RAM | Completed (1.7.63 Parts 1+4, 1.7.64 Parts 2+3, 2026-05-16) |
| [ADR-041](ADR-041-cleanup-on-finalize-exception.md) | Cleanup-on-Exception for Bulk-Tmp Intermediates | Completed (1.7.58, 2026-05-16) |
| [ADR-042](ADR-042-mphf-construction-memory-adaptive-sizing.md) | MPHF Construction Memory Adaptive Sizing — Substrate-Correct Data Shapes | Completed (1.7.60 Parts 1+4, 1.7.62 Parts 2+3, 1.7.64 Part 5, 2026-05-16) |
| [ADR-043](ADR-043-metric-emission-decoupling.md) | Metric Emission Decoupling — Bounded Staleness for Live Observability under Shared-Disk Pressure | Completed (1.7.74, 2026-05-17) |
| [ADR-044](ADR-044-sparql-update-literal-canonicalization.md) | SPARQL UPDATE Literal Escape Canonicalization — Atom-Store Identity Across Ingestion Paths | Completed (1.7.73, 2026-05-17) |
| [ADR-045](ADR-045-graph-clause-feature-parity.md) | One Pattern Path — Eliminate the Divergent GRAPH Parse/Execution Path (a default graph is also a graph) | Completed (2026-06-11) |
| [ADR-046](ADR-046-multi-operation-update-facade.md) | Multi-operation SPARQL UPDATE at the Engine Facade (one update path; single = N=1) | Accepted (2026-06-06) |
| [ADR-047](ADR-047-default-path-cutover.md) | One Execution Path — Route the Default Query Path Through the Unified Tree Executor (complete the ADR-045 cutover) | Proposed (2026-06-11) |
