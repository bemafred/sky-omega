# 21.3 Billion Triples on a Laptop, in .NET

*Sky Omega's Mercury substrate ingested the full Wikidata `latest-all.nt` dump — 21,316,531,403 triples — into a queryable RDF store on a single MacBook Pro, BCL-only .NET, no third-party runtime dependencies. Combined wall-clock: 85h 35m. This is the trajectory and the discipline that made it possible.*

---

## The numbers

```
Triples loaded:        21,316,531,403
GPOS index entries:    17,029,283,265
Trigram index entries:  7,457,242,193

Bulk-load:    73.93 h    (266,153 s, avg 80,091 triples/sec)
Rebuild:      11.65 h    (41,922 s — GPOS + trigram secondary indexes)
─────────────────────────────────────
COMBINED:     85.58 h    (3 days, 13 hours, 35 minutes)

Hardware: M5 Max, 18 cores, 128 GB unified memory, internal NVMe.
          No RAID. No add-in cards. Consumer laptop.
Software: .NET 10, Mercury 1.7.44, BCL-only core.
          78,878 lines of C# (Mercury core). Zero third-party runtime deps.
Tests:    4,205 + 25 green throughout.
```

This is also Round 1.

---

## What's interesting isn't the milestone

It's that almost every claim someone might want to make about why this *shouldn't* work was something we measured, verified, or sometimes inverted along the way.

**"You'd need a server-class machine."** No — 128 GB RAM and a single internal NVMe. The same hardware most engineers carry to meetings.

**"Java/C++ infrastructure. .NET can't compete on this kind of work."** Mercury is BCL-only .NET, used with native-language discipline (`ref struct` everywhere, `Span<T>`, `stackalloc`, direct unsafe pointer arithmetic, `System.Runtime.Intrinsics` for SIMD where it pays). On the hot paths the JIT-emitted machine code is within ~10-15% of equivalent C++ — and the surprise isn't that managed code can match native, it's that *disciplined* managed code can match native within that margin.

**"You'd need RAID."** No — peak iostat during bulk drain hit ~14 GB/s. Single NVMe, kernel orchestrating reads and writes asynchronously beneath synchronous Mercury I/O. The bottleneck was never disk bandwidth.

**"That triplestore size has historical scaling walls."** Wikidata's own infrastructure (Blazegraph WDQS) ran into capacity issues somewhere around 12-13 billion triples — public, well-documented, motivating the WDQS graph-split project and the QLever evaluation. Mercury crossed that range at hour 24 of the run. Past that point we were in territory where incumbent triplestore infrastructure has historically given up.

**"You'd need months of engineering."** Days to weeks of focused work. Single developer, AI-assisted. Solution parsimony made it tractable.

**"And surely lots of crashes along the way?"** None. Zero process failures across 85 continuous hours. The architecture didn't fight the workload; it matched the workload's actual access pattern. (More on this below.)

Each of those framings would be jingoistic if there were no honest counter-evidence. There is. The 4× super-linear penalty past RAM is real. The 85h is itself longer than I'd extrapolated from 1B (where the same pipeline ran in 60 minutes). The atom-store hash drift from 351 K triples/sec at hour 0.6 down to 80 K at hour 60+ is a measured, structural cost we now have a documented architectural fix for but haven't shipped. The honesty of those numbers is what makes the rest of the story credible.

---

## The arc

The interesting story isn't the result — it's how we got here.

Phase 5 of the production-hardening roadmap targeted parallel rebuild and sort-insert as the architectural moves that would unlock 21.3 B from a 1 B baseline. Phase 5.1.b shipped parallel rebuild via broadcast channel; Phase 5.1.c shipped sort-insert via `Array.Sort` with comparator. Both were validated at 100 M scale as **wall-clock-neutral** — they cost about as much as they saved.

Wall-clock-neutral could mean either "the architectures are wash" (no harm, no benefit) or "the architectures shifted cost from somewhere visible to somewhere hidden." Phase 5.2 ran a `dotnet-trace` and `iostat` capture to find out.

The trace told a precise story: **the parallel + sort-insert architecture was paying 453 seconds of `GC.RunFinalizers` per 100M rebuild, plus 552 seconds of `Monitor.Enter_Slowpath`, plus 14 extra threads — all of which the sequential 1.7.34 baseline had zero of.** Wall-clock equality at 100M was hardware luck on an M5 Max with 128 GB RAM, not architectural neutrality. At 21.3 B (working set well past RAM), those costs would dominate.

Worse — Phase 5.2 also revealed the *actual* binding constraint: write amplification. The B+Tree random-insert pattern was touching each leaf page ~3× more than necessary. The SSD was at 7% of bandwidth and 2% of IOPS. The bottleneck was access pattern, not CPU and not raw bandwidth.

The right response was to revert. Both Phase 5.1.b and Phase 5.1.c were rolled back in 1.7.38.

Reverts feel like failure. They're not. Reverts are how you separate *concept* from *execution*. The concept of sort-insert (touch each leaf once, sequential writes) was correct. The execution (`Array.Sort` with a comparator on 32-byte composite keys, plus a 3.2 GB monolithic `List<T>` buffer, plus broadcast channel coordination) cost as much as it saved. Different execution of the same concept could deliver the architectural promise the implementation hadn't.

ADR-032 was that different execution: LSD radix sort on 32-byte `ReferenceKey` values (comparator-free byte-bucketing, zero allocations inside the sort), external chunked merge (16M-entry chunks spilled to disk, k-way merged via min-heap), and `AppendSorted` to the rightmost B+Tree leaf during drain. Phase 1 added the radix primitive. Phase 2 added the external sorter. Phase 3 wired it into GPOS rebuild — measured **3× faster at 100M, peak iostat 2,463 MB/s vs the 327 MB/s baseline**. Phase 4 wired it into the trigram path — measured **17× faster on the trigram portion alone, total rebuild 10.5× faster than baseline at 100M**.

ADR-033 applied the same architecture to the bulk-load primary GSPO path. 1B end-to-end (bulk + rebuild) dropped from ~3h57m baseline to **60m36s** — a 3.92× combined speedup at 1B.

Phase 6 was the 21.3 B run.

---

## What the limits register did

There's a directory in the repo called `docs/limits/`. Each entry catalogs something that's *characterized but not yet shipped* — a known architectural gap, with explicit trigger conditions for when it would matter, candidate mitigations, and references. As of Phase 6 completion, ten entries.

Three of those entries became operationally load-bearing during the run:

**Mmap remap (`btree-mmap-remap.md`).** Pre-flight, I noticed the `ReferenceQuadIndex` opens its mmap at fixed size and never remaps. At BulkMode floor 256 GB, a 21.3 B Reference store would have crashed at ~8.2B triples — about 38% of the way through. Two-line fix: bump the floor to 1 TB. The limits entry got written before the run started, both as documentation of the latent issue and as the rationale for the floor bump. Phase 6 sailed past 8.2B at hour 24 without the file growing past 1 TB. The latent crash was caught and contained.

**Streaming source decompression (`streaming-source-decompression.md`).** Mid-Phase-6, mid-trigram-emission, with disk free at 124 GB and chunks accumulating at ~376 GB/h, the run was about to hit the `--min-free-space` floor and abort. The limits entry I'd written 30 minutes earlier observed that the 3.1 TB uncompressed `latest-all.nt` source file was 2.94 TB more disk than the 160 GB compressed `.bz2` original. We deleted the intermediate. Disk free recovered from 124 GB to 3.2 TB in 27 milliseconds (APFS metadata-only). The crisis became a non-crisis. The limits entry validated itself within an hour of being written.

**Rebuild progress observability (`rebuild-progress-observability.md`).** Trigram emission at 21.3B was a 4-hour silent middle. Mercury's existing rebuild metrics fire only at phase boundaries. The operator (Martin) had to use indirect signals — chunk file count in `rebuild-tmp/trigram/`, `du -sh` deltas, iostat patterns — to estimate progress. Workable for a live-attended single-machine session; inadequate for unattended runs or CI automation. The limits entry catalogs exactly what to add (`OnRebuildProgress` event mirroring the bulk-load callback, sub-phase identification, throttled JSONL emission, estimated completion time) and when.

The pattern: characterize a latent issue *before* it becomes a crisis, with explicit trigger conditions. When a trigger fires, you already have the language to name it and the candidate mitigations to execute. None of this is novel — it's just disciplined documentation. The novelty is *committing* to it: each limits entry is a git commit, public on the remote, available for anyone (including future-me) to reference.

---

## What it took to do this

A list of things that didn't happen during 85 continuous hours:

- No process crashes
- No data corruption
- No swap thrashing (cumulative 7,072 swapouts over 85h ≈ 18 KB/min — trivial)
- No mid-run code changes
- No rolled-back validation arcs (the rolled-back arcs happened *before* Phase 6, characterized in commits)

What did happen:

- 14 commits on `main` describing the architectural arc, each with rationale
- 10 limits register entries cataloging known gaps, three of which became load-bearing
- One pre-flight checklist item (mmap floor bump) that prevented an 8.2B crash
- One mid-flight intervention (deleting the 3.1 TB intermediate) when a limits entry's trigger fired
- Continuous monitoring with explicit confirmation of stability — the kind of "nothing happened, again" status check that becomes evidence over hours

Kjell Silverstein has a poem that captures this:

> *I looked and looked*
> *And found nothing there.*
> *No answer, no signal, no sign.*
>
> *But nothing's not nothing*
> *When nothing's been checked—*
> *That nothing*
> *Was something*
> *Of mine.*

Untrusted nothing is just absence. Checked nothing is evidence. Phase 6's 85 hours of confirmed quiet *are* the achievement. A 21.3 B triple count is the headline; the body of the story is the accumulating record of monitored stability.

This is what's missing from large IT initiatives that fail. Not the technical talent — the *discipline of looking*. Status reports tick off planned milestones; the substrate's quiet disagreements with the plan accumulate into eventual collapse because no one was checking. Mercury's silence over 85 hours had to be re-confirmed every status check, in a register of monitored signals, because that's what made it mean something.

---

## This is Round 1

The 85h is not the architectural ceiling. It's a baseline on consumer hardware, before any of the seven optimization rounds documented in the limits register have shipped:

| Round | Limit entry | Estimated impact | Status |
|---|---|---|---|
| 1 | Sorted atom store for Reference (QLever-style sorted vocabulary, eliminates atom-store hash drift) | 30-40% wall-clock | Latent — likely first ADR-034 |
| 2 | Bit-packed atom IDs (ReferenceKey 32B → 16B at Wikidata scale) | 20-30% rebuild + bulk | Latent |
| 3 | Hardware-accelerated XxHash3 (BCL `System.IO.Hashing.XxHash3`, NEON on Apple Silicon) | 5-15% on hash hot path | Latent — small change |
| 4 | Prefetching + pipelined batch intern | 20-30% on probe cost | Latent |
| 5 | MPHF on sorted vocab (BBHash) | O(1) lookup query-side | Latent — Phase 2 of #1 |
| 6 | B+Tree mmap remap | Unblocks > 1 TB | Latent — past 21.3 B |
| 7 | Streaming source decompression | 2.94 TB disk + workflow | Latent |

Compounding conservatively, Rounds 1-4 would put a fully-tuned 21.3 B run somewhere around **15-25 hours on the same laptop** — and the equivalent comparison reference point shifts: QLever (Hannah Bast et al., Univ. Freiburg, multi-year mature C++ codebase) reports ~6-10 hours for full Wikidata on server-class hardware (256-512 GB RAM, dual-socket Xeon, possibly RAID). That's a meaningful comparison for the *trajectory*, not the ceiling.

Mercury at Round 1 on laptop hardware is not faster than QLever at Round N on server hardware. That's not a useful frame anyway. **Mercury at Round 1 is competitive enough to demonstrate that the architectural bets work, with seven documented rounds ahead, each anchored in measurement.** That's the more honest and more interesting claim.

---

## Sky Omega context

Mercury is one substrate in Sky Omega. The other substrates — Minerva (LLM inference, BCL-only .NET, mmap-backed model weights, Metal/CUDA via P/Invoke), James (orchestration), Lucy (semantic memory), Mira (surfaces), DrHook (runtime observation) — are at various stages of development. Mercury is the most mature today because it's the foundation: every other substrate ultimately stores into or queries from Mercury.

The Sky Omega thesis is **semantic sovereignty** — that meaningful AI systems should run under user control on user-class hardware, with knowledge stored in open standards (RDF), portable, queryable, and unowned by any platform. Phase 6 turns that thesis from aspiration into empirical capability. Full Wikidata on a laptop is the proof-of-concept that the substrate can carry its weight at scale.

The repo is open source (MIT) at [github.com/bemafred/sky-omega](https://github.com/bemafred/sky-omega). The validation docs (`docs/validations/`) record every measurement of the arc described above. The limits register (`docs/limits/`) catalogs the next rounds with explicit trigger conditions. The poetry (`docs/poetry/`) — Kjell Silverstein's "Nothing" among others — provides non-technical entry points to the same ideas.

---

## Closing

The achievement here is not 21.3 B triples on a laptop. The achievement is *that we knew the substrate was healthy throughout 85 continuous hours, because we kept checking and the checks kept agreeing.*

Engineering discipline is not glamorous. Limits registers are not Twitter-shaped. Reverting an architectural commit two weeks before a public milestone does not feel like progress. But the discipline produced the result, and the result wouldn't exist without the discipline. They are not separable.

Phase 6 was the demonstration. Round 2 is what we do next.

> *That nothing*
> *Was something*
> *Of mine.*
