# Limit: Hard-coded FD cap (8K) is platform-conservative; runtime detection deferred

**Status:**        Latent
**Surfaced:**      2026-05-06, after cycle 8 fixed three FD-ceiling crashes by hard-coding the merge-pool hard cap at 8K. The 8K value is conservative for macOS launchd-spawned children (effective ~10,240 limit). On Linux servers and other hosts, the actual FD limit is typically 65K-1M, leaving the 8K cap leaving substantial headroom unused.
**Last reviewed:** 2026-05-06

## Description

Mercury's merge-pool hard cap is currently a compile-time constant: `MergeFileStreamPoolHardCap = 8 * 1024` (commit `880bfe1`). The 8K value was chosen empirically to fit under the macOS launchd-applied effective FD limit of ~10,240.

This is **conservative on every other platform**:
- Linux servers (typical): 65,536 soft / 1M+ hard
- Linux containers (typical): 65,536 soft, often raised
- Windows (HANDLEs, not FDs): no comparable per-process limit at this scale
- macOS without launchd intermediation (e.g., shell-spawned, screen sessions): up to 1,048,576

For workloads where chunkCount > 8K (e.g., the 21.3 B trigram drain with 10,456 chunks), the cap forces ~23% LRU eviction. On Linux/Windows this eviction is unnecessary — the pool could hold all chunks open with the actual OS limit having plenty of headroom.

The fix shape is well-understood: detect the actual per-process FD limit at runtime via `getrlimit(RLIMIT_NOFILE)` and size the cap dynamically. macOS and Linux both expose this via `libSystem.dylib` / `libc` P/Invoke; Windows doesn't have FDs as a concept at this scale (HANDLEs are effectively unlimited, the cap just wouldn't engage).

## Why this is a register entry

The current 8K hard cap is correct for macOS launchd, suboptimal elsewhere. Fixing requires P/Invoke (already used in Mercury for other Unix interop) plus per-platform dispatch. Not blocking — the 8K cap is structurally safe everywhere, just leaves performance on the table on non-macOS-launchd hosts.

## Trigger condition

This limit moves toward an ADR / fix when one of:

1. **Linux production deployment.** Mercury running as a Sky Omega substrate on Linux-server class hardware would benefit from full chunkCount-sized pools without the 8K artificial limit.
2. **Round 2 trigram-drain optimization** (sibling: `trigram-drain-cap-eviction.md`) chooses runtime-detection as the path rather than larger-chunks/hierarchical-merge.
3. **Cross-platform substrate validation.** Sky Omega 2.0 trajectory mentions cross-instance epistemic exchange; if instances run on heterogeneous hosts (macOS dev + Linux server), the hard cap creates inconsistent behavior.

## Current state

- macOS launchd children: 8K cap is correct; no headroom wasted.
- macOS with raised soft limit (interactive shells, certain runtime configs): could use up to ~245K (kern.maxfilesperproc) — 30× more than the cap allows.
- Linux: typically 65K available; cap leaves ~57K unused.
- Windows: HANDLEs not constrained at this scale; cap leaves ~16M unused.

## Candidate mitigations

1. **`getrlimit` via P/Invoke** (`libSystem.dylib` / `libc.so.6`) at `BoundedFileStreamPool` construction time. Compute effective cap = min(getrlimit_limit - headroom, requested_size). One-time call, cached. Fallback to 8K constant if P/Invoke fails.
2. **Read `/proc/self/limits`** on Linux as a BCL-only path (no P/Invoke). macOS lacks `/proc`; would need either P/Invoke or shell-out (worse).
3. **Configuration override via env var** — `MERCURY_MERGE_POOL_HARD_CAP=N` lets operators raise/lower without recompile. Cheap workaround until proper detection lands.

The natural sequencing: (3) immediately as a same-day relief valve; (1) as the architectural fix; (2) as the BCL-only-on-Linux path if P/Invoke is judged worth avoiding.

## Why this matters beyond cycle 8

The hard-coded 8K cap couples Mercury's behavior to one platform's specific limit. As Sky Omega expands beyond a single dev host, this coupling becomes a portability liability. Runtime detection makes the substrate adapt to its environment rather than impose macOS's quirks on every deployment.

## References

- `docs/validations/adr-034-21b-2026-05-06.md` — cycle 8 validation; the run that surfaced the cap
- `docs/limits/trigram-drain-cap-eviction.md` — sibling; describes the eviction overhead the cap imposes
- Commit `880bfe1` — current hard-coded 8K cap with rationale
- `MergeFileStreamPoolHardCap` constant in `src/Mercury/Storage/SortedAtomStoreExternalBuilder.cs`
