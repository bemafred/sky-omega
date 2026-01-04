# ADR-001: Mercury Compaction Strategy

## Status

Accepted

## Context

Mercury uses soft deletes with bitemporal tracking - triples are never physically removed, only marked with end-validity timestamps. This serves Sky Omega’s primary use case well, where history preservation is essential.

However, Mercury is proving to be a capable general-purpose RDF substrate. Other use cases may require hard deletes - scenarios where storage reclamation matters and history is not needed.

The question: should Mercury support in-place hard deletes?

## Options Considered

### Option 1: In-place hard delete support

Add hole management, index rebalancing, and atom garbage collection to the write path.

**Drawbacks:**

- Significant complexity in hot path
- Hole tracking and defragmentation logic
- AtomStore reference counting or mark-sweep
- Potential zero-GC violations during cleanup
- Every write path now has two modes

### Option 2: Copy-and-switch compaction

Keep Mercury simple. Support hard deletes through external orchestration:

1. Create new Mercury instance
1. Iterate live triples from source, assert to target
1. Switch application to new instance
1. Discard old instance

**Benefits:**

- Read/write path stays simple - no hole management
- AtomStore prunes naturally - only referenced atoms copy forward
- Indexes rebuild dense and unfragmented
- Implementation is near-trivial
- Zero-GC properties preserved during normal operation
- Well-understood pattern (SQLite VACUUM, LSM compaction, Git gc)

## Decision

**Option 2: Copy-and-switch compaction**

Mercury remains a simple, fast, zero-GC substrate. Compaction is an orchestration concern, not a storage engine concern.

## Implementation Notes

### Write handling during compaction

Three approaches, choose based on availability requirements:

1. **Brief pause** - Simplest. Suspend writes, copy, switch, resume. Sufficient for personal/team Sky Omega instances and most scenarios.
1. **Copy then replay delta** - More complex. Log writes during copy, replay to new instance before switch. Only needed for “always available” requirements.
1. **Accept small inconsistency window** - BASE semantics. Writes during copy may be lost. Acceptable for some use cases.

### Orchestration

Compaction can be scheduled at a higher level - James could trigger it during low-activity periods. The decision of *when* to compact is separate from *how*.

### Filesystem compression

Separately, OS-level filesystem compression (ZFS, Btrfs, NTFS) is recommended as the default deployment pattern for AtomStore. IRIs share structural patterns that compress well. This is transparent to Mercury - zero code changes, zero-GC preserved, often improves effective I/O throughput.

## Consequences

- Mercury stays architecturally simple
- Hard delete support available without engine complexity
- Compaction timing controlled externally
- Storage reclamation is batched, not incremental
- Brief unavailability during compaction (unless option 2 chosen)