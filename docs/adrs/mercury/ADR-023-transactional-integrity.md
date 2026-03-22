# ADR-023: Transactional Integrity — WAL, Batch Rollback, and Transaction Time

## Status

**Proposed** — 2026-03-22

## Context

An external code review (Codex, March 2026) identified three structurally related integrity violations in Mercury's storage layer. These are not edge-case bugs — they are contract violations in a system that claims transactional batching and bitemporal semantics.

All 329 storage tests pass. The test suite documents and accepts the current behavior rather than guarding against it.

### Violation 1: RollbackBatch() does not roll back

`QuadStore.RollbackBatch()` (line 456) clears `_activeBatchTxId` and releases the write lock. It does **not** undo the index mutations that `AddBatched`/`DeleteBatched` already applied via `ApplyToIndexes` (line 362) and `ApplyDeleteToIndexes` (line 409).

After rollback:
- **In-memory state is dirty** — indexes contain mutations from the abandoned batch
- **Queries return rolled-back data** until the process restarts
- **If a checkpoint occurs before restart**, the dirty index state is persisted to disk, making the rollback permanent despite the caller's intent

The doc comment on line 453-454 says *"Recovery will not replay these records"* — this is only true if a checkpoint happened after `CommitBatch()` advanced `_currentTxId` but before the crash, which is not what the comment implies.

The existing rollback test (`QuadStoreTests.cs:908`) only verifies lock release, not state visibility.

### Violation 2: WAL has no transaction boundaries

The WAL protocol consists of three operations:

| Operation | What it does | What it writes to WAL |
|-----------|-------------|----------------------|
| `BeginBatch()` | Returns `_currentTxId + 1` | Nothing |
| `AppendBatch()` | Writes record with batch TxId | `Add` or `Delete` record |
| `CommitBatch()` | Advances `_currentTxId`, fsyncs | Nothing |

`LogOperation` has three variants: `Add`, `Delete`, `Checkpoint`. There is no `BeginTx` or `CommitTx` marker.

Recovery (`LogRecordEnumerator`) replays all records with `TxId > _lastCheckpointTxId`. It cannot distinguish committed batches from uncommitted ones because:

1. `BeginBatch()` writes no marker
2. `CommitBatch()` writes no marker — it only fsyncs and updates in-memory `_currentTxId`
3. After crash, `RecoverState()` scans all records and sets `_currentTxId` to the highest TxId found — **including uncommitted batch records**

**Failure scenario:**
1. `BeginBatch()` → TxId = 5
2. `AppendBatch()` × 500 records (TxId = 5, written to WAL, no fsync)
3. Crash before `CommitBatch()`
4. Recovery: finds 500 records with TxId = 5, replays all of them
5. `_currentTxId` is set to 5 — the uncommitted batch is now committed

The existing WAL test (`WriteAheadLogTests.cs:622`) explicitly asserts that uncommitted records are replayable, confirming this is known and accepted behavior rather than a tested invariant.

### Violation 3: Transaction time is frozen at construction

`QuadIndex` sets `_currentTransactionTime` once in its constructor (line 107):

```csharp
_currentTransactionTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
```

This value is reused for every subsequent `Add` and `Delete` (line 146, line 227). In a long-lived process (MCP server, CLI session), all writes collapse to a single transaction timestamp — the moment the store was opened.

This breaks the bitemporal contract. Transaction time should record **when the system learned a fact**, not when the process started.

Additionally, the WAL record layout (72 bytes) stores `ValidFromTicks` and `ValidToTicks` but **no transaction time field**. On recovery, `ApplyToIndexes` creates new index entries that acquire fresh transaction timestamps from the (newly constructed) `QuadIndex`, assigning different transaction times than the original writes.

The test coverage (`QuadIndexTests.cs:427`) only asserts that transaction time is non-zero.

## Decision

Fix all three violations. These are not documentation issues — they require structural changes to the WAL format, batch protocol, and transaction time propagation.

### Phase 1: WAL transaction boundaries

**Add `BeginTx` and `CommitTx` record types to `LogOperation`:**

```csharp
internal enum LogOperation : byte
{
    Add = 1,
    Delete = 2,
    BeginTx = 3,     // new
    CommitTx = 4,     // new
    Checkpoint = 255
}
```

**Modify the batch protocol:**

| Operation | Current behavior | New behavior |
|-----------|-----------------|-------------|
| `BeginBatch()` | Returns TxId, writes nothing | Returns TxId, writes `BeginTx` record |
| `CommitBatch()` | Advances TxId, fsyncs | Writes `CommitTx` record, advances TxId, fsyncs |
| `RollbackBatch()` | Clears TxId | Writes no commit marker (absence = uncommitted) |

**Modify recovery to respect transaction boundaries:**

`LogRecordEnumerator` must:
1. Scan forward, collecting TxIds that have a `CommitTx` record
2. On second pass (or single-pass with buffering), only yield records whose TxId appears in the committed set
3. Records with TxId that has `BeginTx` but no `CommitTx` are discarded — this is a crashed/rolled-back transaction

**WAL format compatibility:**
- The record size (72 bytes) does not change — `BeginTx`/`CommitTx` use the existing layout with zeroed atom fields
- Old WAL files without `BeginTx`/`CommitTx` markers: treat all records as committed (backward-compatible recovery)
- Add a WAL version byte in the reserved field `[9]` to distinguish old vs new format

### Phase 2: Batch rollback with index undo

**Two options, choose one:**

**Option A — Deferred index application (simpler, recommended):**

Do not call `ApplyToIndexes` during `AddBatched`/`DeleteBatched`. Instead, buffer the WAL records and apply to indexes only in `CommitBatch()`. This means batched data is invisible to queries until commit — true transaction isolation.

Implementation:
- `AddBatched`/`DeleteBatched`: write to WAL only, accumulate atom strings in a batch buffer
- `CommitBatch()`: write `CommitTx`, fsync, then apply all buffered mutations to indexes
- `RollbackBatch()`: discard the buffer, release lock — indexes are untouched

Trade-off: Batched writes are not queryable mid-transaction. This is the correct semantic for a transactional system.

**Option B — Undo log (complex):**

Maintain a per-batch undo log of index mutations. `RollbackBatch()` replays the undo log in reverse. This preserves the current behavior of mid-transaction visibility but adds complexity and memory pressure.

**Recommendation: Option A.** Mid-transaction visibility is not a feature anyone relies on — it's an accident of the current implementation. Deferred application is simpler, correct, and aligns with how every real database works.

### Phase 3: Transaction time per-write with WAL persistence

**Extend WAL record to include transaction time:**

The reserved bytes `[9-15]` (7 bytes) in the current layout are unused. Use bytes `[9-14]` (6 bytes) for a 48-bit millisecond timestamp, leaving `[15]` reserved. 48 bits of milliseconds covers ~8,900 years — sufficient.

Alternatively, expand the record to 80 bytes and add a full 8-byte `TransactionTimeTicks` field. This is cleaner but breaks the existing record size.

**Decision:** Expand to 80 bytes with a full 8-byte `TransactionTimeTicks` field. No production stores exist, so there is no migration cost — and the cleaner layout avoids version-byte complexity and bit-packing fragility.

**Modify `QuadIndex`:**

- Remove `_currentTransactionTime` field
- `Add()` and `Delete()` require an explicit `transactionTime` parameter (no default)
- `QuadStore` generates `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` at call site and passes it to both WAL and indexes

**Modify recovery:**

- Read transaction time from WAL record
- Pass it explicitly to `ApplyToIndexes` → `QuadIndex.Add(transactionTime: record.TransactionTime)`
- Original transaction times are preserved through crash/recovery

### Phase 4: Test coverage

Each phase must include tests that verify the **invariant**, not just the mechanism:

| Violation | Test invariant |
|-----------|---------------|
| Rollback | After `RollbackBatch()`, queries must not return any data from the abandoned batch |
| WAL boundaries | After crash mid-batch, recovery must not replay uncommitted records |
| Transaction time | After recovery, transaction times must equal the original write times, not recovery time |

## Consequences

### Positive

- **Rollback means rollback** — callers can trust the API contract
- **Crash recovery is correct** — partial batches are discarded, not silently committed
- **Transaction time is meaningful** — bitemporal queries return accurate system-time provenance
- **WAL format is forward-compatible** — version byte enables future extensions

### Negative

- **WAL format is a breaking change** — record size increases from 72 to 80 bytes. No production stores exist, so no migration is needed.
- **Deferred application changes batch semantics** — batched writes are invisible until commit. Any code that queries mid-batch (none known) would break.

### Risks

- **Performance regression in batch path** — buffering atom strings until commit adds memory pressure for large batches. Mitigate by streaming from WAL on commit rather than buffering in memory.
- **Record alignment** — 80-byte records must be validated for correct struct layout and memory-mapped access.

## Implementation Order

Phase 1 (WAL boundaries) is the foundation — phases 2 and 3 depend on it. Phase 4 is concurrent with each phase.

Suggested order: **1 → 2 → 3**, with tests written alongside each phase.

## Success Criteria

- [ ] `RollbackBatch()` leaves indexes unchanged — verified by query after rollback
- [ ] WAL recovery discards uncommitted batch records — verified by crash simulation
- [ ] Transaction time varies per-write in a long-lived store — verified by temporal query
- [ ] Transaction time survives recovery — verified by comparing pre/post-crash query results
- [ ] Existing 329 storage tests continue to pass
- [ ] Batch write throughput regression < 10% (benchmark)
