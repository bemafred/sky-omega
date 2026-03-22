# ADR-023: Transactional Integrity — WAL, Batch Rollback, and Transaction Time

## Status

**Accepted** — 2026-03-22

## Context

An external code review (Codex, March 2026) identified three structurally related integrity violations in Mercury's storage layer. These are not edge-case bugs — they are contract violations in a system that claims transactional batching and bitemporal semantics.

All 329 storage tests pass. The test suite documents and accepts the current behavior rather than guarding against it.

### Violation 1: RollbackBatch() does not roll back

`QuadStore.RollbackBatch()` (line 456) clears `_activeBatchTxId` and releases the write lock. It does **not** undo the index mutations that `AddBatched`/`DeleteBatched` already applied via `ApplyToIndexes` (line 362) and `ApplyDeleteToIndexes` (line 409).

After rollback:
- **In-memory state is dirty** — indexes contain mutations from the abandoned batch
- **Queries return rolled-back data immediately** — the caller has no isolation from its own aborted writes
- **The corruption may already be durable** — `QuadIndex` flushes the mapped accessor during page mutation, so a restart or checkpoint is not required for the abandoned batch to persist on disk

The doc comment on line 453-454 says *"Recovery will not replay these records"* — that is misleading on both visibility and durability. The damage is already done before recovery runs because the indexes were mutated directly.

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

### Phase 1: WAL v2 with explicit transaction boundaries

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

**WAL format decision:**
- Make a single WAL format change to **v2, 80-byte records**
- Add `TransactionTimeTicks` to the record layout from the start; `BeginTx`/`CommitTx` set it to `0`
- Do **not** carry dual-format recovery logic or a version-byte shim in the reserved bytes
- Mercury has no production stores; existing development/test WAL files are disposable and must be recreated

This keeps the design coherent: transaction markers and transaction time ship in one WAL revision rather than in two partially overlapping formats.

### Phase 2: Batch rollback with deferred materialization

**Two options, choose one:**

**Option A — Deferred index application (simpler, recommended):**

Do not call `ApplyToIndexes` during `AddBatched`/`DeleteBatched`. Instead, buffer the WAL records and apply to indexes only in `CommitBatch()`. This means batched data is invisible to queries until commit — true transaction isolation.

Implementation:
- `AddBatched`/`DeleteBatched`: write to WAL only, accumulate the batch's records and any required atom/string materialization state
- `CommitBatch()`: append `CommitTx`, fsync, then materialize the committed records into the indexes
- `RollbackBatch()`: discard the buffer, release lock — indexes are untouched

Trade-off: Batched writes are not queryable mid-transaction. This is the correct semantic for a transactional system.

**Critical requirement: committed replay must be idempotent.**

With deferred materialization, a crash can occur **after** `CommitTx` is durable but **before** every index mutation has been applied. Recovery must therefore be allowed to re-apply the batch safely. That requires exact-record idempotence:

- Re-applying the same committed `Add` record must be a no-op if the exact temporal row already exists
- Re-applying the same committed `Delete` record must be a no-op if the exact row is already deleted/absent
- Historical replays must not create duplicate versions or re-truncate `ValidTo` a second time

This is the part the current `QuadIndex` does **not** guarantee. `InsertIntoLeaf` (line ~802) only deduplicates when both entries have matching GSPO dimensions **and** `ValidTo >= year 9000` (the "current fact" sentinel). For historical triples and temporal updates, the current control flow is worse than simple duplication: it can truncate the existing row via `HandleTemporalUpdate` and then return **without inserting the new committed row at all**. A crash in that window leaves history incomplete, and replay safety depends on changing that control flow, not just adding one more equality check.

**Fix:** Split the insertion logic into three explicit cases:

1. **Exact full-key duplicate** (`Graph`, `Primary`, `Secondary`, `Tertiary`, `ValidFrom`, `ValidTo`, `TransactionTime` all equal) → skip as a replayed no-op
2. **Same dimensions, different temporal key** → perform any required truncation of the predecessor, then continue to insert the new row
3. **Distinct dimensions** → insert normally

The existing GSPO-only check for current facts may remain as a fast-path optimization, but it must preserve the semantics above. The key point is that "truncate predecessor" and "insert committed row" must be part of one replay-safe algorithm; the former cannot return early and suppress the latter.

Option A is only sound with this index-layer upgrade.

**Option B — Undo log (complex):**

Maintain a per-batch undo log of index mutations. `RollbackBatch()` replays the undo log in reverse. This preserves the current behavior of mid-transaction visibility but adds complexity and memory pressure.

**Recommendation: Option A, but only with replay-idempotent materialization.** Mid-transaction visibility is not a feature anyone relies on — it's an accident of the current implementation. Deferred application remains the simplest correct approach once replay safety is made explicit.

### Phase 3: Transaction time per-write with WAL persistence

**Use the WAL v2 `TransactionTimeTicks` field for all add/delete records.**

The WAL format decision is already made in Phase 1: the new record size is 80 bytes, and transaction time is a first-class field rather than a packed value in reserved bytes.

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
| Replay idempotence | After crash after durable `CommitTx` but before full materialization, recovery must not duplicate or re-truncate history |
| Transaction time | After recovery, transaction times must equal the original write times, not recovery time |

## Consequences

### Positive

- **Rollback means rollback** — callers can trust the API contract
- **Crash recovery is correct** — partial batches are discarded, not silently committed
- **Transaction time is meaningful** — bitemporal queries return accurate system-time provenance
- **The WAL design is internally coherent** — one record format carries both commit markers and transaction time

### Negative

- **WAL format is a breaking change** — record size increases from 72 to 80 bytes. Existing development/test stores must be recreated.
- **Deferred application changes batch semantics** — batched writes are invisible until commit. Any code that queries mid-batch (none known) would break.

### Risks

- **Replay-idempotence bugs are the main correctness risk** — if historical `Add`/`Delete` replay is not exact-record idempotent, a crash after `CommitTx` but before full materialization can still distort history
- **Performance regression in batch path** — buffering batch state until commit adds memory pressure for large transactions. Mitigate by buffering compact record state and/or streaming the committed transaction back from WAL during materialization.
- **Record alignment** — 80-byte records must be validated for correct struct layout and memory-mapped access.

## Implementation Order

Phase 1 (WAL v2) is the foundation — phases 2 and 3 depend on it. Phase 4 is concurrent with each phase.

Suggested order: **1 → 2 → 3**, with tests written alongside each phase.

## Success Criteria

- [x] `RollbackBatch()` leaves indexes unchanged — verified by query after rollback
- [x] WAL recovery discards uncommitted batch records — verified by crash simulation
- [x] Crash after durable `CommitTx` but before full index materialization does not duplicate or truncate history on recovery
- [x] Transaction time varies per-write in a long-lived store — verified by temporal query
- [x] Transaction time survives recovery — verified by comparing pre/post-crash query results
- [x] Existing 3976 tests continue to pass (was 329 storage-only; full suite is 3976+25)
- [ ] Batch write throughput regression < 10% (benchmark)
