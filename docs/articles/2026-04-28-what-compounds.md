# What Compounds — Notes on Sky Omega's First Four Months

*Sky Omega started Christmas Eve 2025. By April 2026 it had ingested 21.3 billion Wikidata triples into a queryable RDF store on a single laptop, BCL-only .NET, 100% W3C SPARQL 1.1 conformance, ~98K lines of source across three substrates, mostly built in spare time. The numbers are real and reproducible. The practices that made them possible aren't a secret. This is the working list.*

---

## Why this matters

The 21.3 B Wikidata milestone was the demonstration that the architectural bets work. The interesting question is the meta-one: how does a project sustain that pace, in spare hours, without accumulating the technical debt that would normally make every subsequent change slower than the last?

We've been thinking explicitly about what we optimize for. It isn't velocity, feature count, or time-to-MVP. It's something we've come to call **epistemic compound interest**: every grounded decision pays forward, nothing speculative ships, nothing rots. The counterintuitive output of *not* optimizing for short-term velocity is enormous long-term velocity, because no session is ever spent backing out wrong choices. Each layer that lands becomes load-bearing for the next.

What follows are the eight practices that produce that compounding. They are not unique to Sky Omega — most are visible in good open-source projects (LMDB's discipline, SQLite's, the Linux kernel's). What's transferable is the *combination*, applied to a small substrate-quality project in 2026 with AI as a real engineering collaborator rather than a code-completion plugin.

---

## 1. EEE methodology as a status convention, not a slogan

**Emergence → Epistemics → Engineering.** Three phases: surfacing the unknown, validating it, then shipping. We map them one-to-one onto our ADR statuses: Proposed → Accepted → Completed. Most teams have ADRs; few enforce status discipline. The discipline is what prevents *decided* and *shipped* from blurring.

Concretely: the project state is readable from `docs/adrs/*.md`, not from anyone's head. An ADR sitting at Proposed has not been validated. One at Accepted is validated but not in code. One at Completed has tests, integration, and a lifecycle check. When someone — including the future version of either of us — opens the repo cold, they can compute the project state in five minutes by reading folder listings.

The cost is one extra status field per ADR. The benefit is that we never have to argue about whether something exists.

---

## 2. The limits register

A directory between *we noticed it* and *we did it*. Items past Emergence + Epistemics but pre-Engineering live there with named trigger conditions and concrete promotion criteria.

Most projects bury such items in ADR Consequences sections, where they go invisible the moment the ADR is marked Completed. Surfacing them by design — `docs/limits/` with 11 entries today, each one short Markdown file plus a row in the index — means we don't rediscover the same constraint twice. When a workload eventually binds on, say, two-SSD utilization or atom-ID bit packing, the analysis is already there.

Two examples from this past week alone:

- After the Phase 6 close-out, we noticed the Reference profile mmap is opened RW even though the profile semantics are "build once, query forever." Not yet binding. Filed as `reference-readonly-mmap.md` with the trigger condition: when query-side latency or per-process memory becomes binding.
- After a forward-looking discussion of two-SSD utilization on future hardware, we noticed Mercury's flat file layout makes per-file symlinks fragile. Not yet binding. Filed as `per-index-subdirectory-layout.md` with the trigger condition: when WAL-on-its-own-spindle or per-index placement becomes a real ask.

Neither was implemented. Both are now durable knowledge in the codebase. When the trigger fires, the engineering takes hours, not weeks, because the analysis already happened.

---

## 3. A validations directory with reproducible commands

Every claim is backed by a dated measurement file. `docs/validations/<date>-<scope>.md` records scope, parameters, the command that produced the numbers, and the results. Standard in research; rare in industry codebases.

The act of having to write the validation file *before* claiming the number is what keeps benchmarks honest. The 85h Wikidata wall-clock isn't a hand-wave; it's anchored in `docs/validations/21b-query-validation-2026-04-26.md` with the exact command and the exact records. The 1 B Reference end-to-end at 300 K triples/sec from a `latest-all.ttl.bz2` source isn't marketing; it's anchored in `docs/validations/adr-035-phase7a-1b-2026-04-27.md` with 22,256 JSONL records emitted across all four metric channels.

When a benchmark is in `docs/validations/`, anyone can re-run it and discover the same answer. That's what *reproducible* means in practice: not just "in principle replayable," but *the literal command is in the file*.

---

## 4. Substrate-first thinking

Build the thing that makes the thing, before building the thing.

The Sky Omega architecture has six named substrates: Mercury (RDF storage), Minerva (LLM inference), DrHook (runtime observation), James (orchestration), Lucy (semantic memory), Mira (surfaces). Most projects rush the visible layer — the chat UI, the agent surface, the demo — and accumulate substrate debt that turns every subsequent change into a battle. We did the inverse. Mercury came first, before Lucy. Lucy will come before Sky. Each substrate is built to substrate quality before the layer above it ships.

The bet is that **substrate quality is the multiplier**. A well-built substrate makes everything that depends on it cheap; a debt-laden one makes everything expensive. Four months of substrate-first work, in spare time, produced a Mercury that competes against full-time research efforts on benchmarks like WDBench. That's not coincidence — that's what the bet says happens.

---

## 5. Architectural bets that compound rather than pay back

Each architectural choice in Mercury costs upfront discipline and pays at scale:

- **BCL-only / zero external runtime dependencies.** Semantic sovereignty — no package-version drift, no transitive supply-chain semantics. Costs occasional reimplementation (we wrote a bzip2 streaming decompressor from the spec for ADR-036 because the BCL doesn't ship one). Pays forward forever in operational stability and substrate independence.
- **mmap-first storage.** The OS manages memory; Mercury doesn't. Costs the discipline of designing files for sequential access and page-friendly layout. Pays forward as RAM grows: the same code that ran at 128 GB will run faster at 256 GB or 512 GB without rewrites.
- **Append-only where possible.** Atom store, indexes, WAL — all append-only on the bulk-load path. Costs occasional sort-then-merge overhead. Pays forward in crash-safety: there is no recovery procedure beyond replaying the log, because the data structure is its own log.
- **Zero-GC on hot paths.** `ref struct`, `Span<T>`, `stackalloc`, direct unsafe pointer arithmetic. Costs significant upfront discipline (every allocation in a hot path is a regression). Pays forward in predictable latency at scale.

Each bet is a choice point that most teams skip. The cumulative effect is what makes substrate-first thinking actually work — without these bets, the substrate would carry its own debt.

---

## 6. Truth-aligned claims, refreshed under pressure

When `STATISTICS.md` is three days stale, the right move is a 40-line refresh, not a hand-wave. Numbers in the README must match `wc -l`, `dotnet test`, and `git log` *now*, not when last touched.

This is mechanical to maintain and disproportionately valuable. External readers — and future selves — trust the artifact because the artifact has earned it. When someone reading the repo cold sees "4,331 Mercury tests" in the README, they can run `dotnet test` and see exactly that number. The claim is the truth; the truth is the claim.

The same discipline applies to Markdown narrative. "Phase 6 complete" in the banner means: ADRs marked Completed, validations filed, tests green, milestone closed. It doesn't mean "we shipped the easy half and called it done."

Truth-alignment is a tax that pays itself back the first time anyone external reads the project and finds that what's written is what's there.

---

## 7. Patience as positive discipline, not absence of urgency

Today's example: WDBench cold baseline is running on the wiki-21b-ref store. It's been running for ~15 hours and has another day or two ahead. We could run gradient-validation experiments for ADR-034 SortedAtomStore right now in parallel. The hardware would handle it.

We chose to wait. Not because we lack urgency — we have plenty — but because the cold baseline is a multi-day measurement, and competing I/O would corrupt it. Optimizing for *information quality* over *apparent progress* looks slow from the outside. From the inside, it's what keeps every measurement load-bearing.

The same principle applies at smaller scales. When today's RadixSort allocation regression turned out to be 72 bytes per call from a `stackalloc int[N] { ... }` initializer pattern in .NET 10's codegen, we could have made the allocation test less strict. We didn't, because *understanding why* compounds: the next time someone wonders about that idiom in any other context, the comment in the source answers it. The fix was 13 lines. The investigation embedded knowledge that's now permanent.

---

## 8. Human-AI collaboration as itself a discipline

This is the meta-ingredient that most other reflections on this kind of work miss. It's not "use AI for productivity." It's: AI brings context-holding across long sessions and consistent conventions across files; the human brings architectural intuition, domain conviction, and the willingness to enforce EEE when the AI would happily hand-wave. Neither half does this alone.

The "we" voice in this article — and in the 21.3 B Wikidata article that preceded it — isn't politeness. It names the actual mechanism. Specific things that happen in our workflow:

- Memory is reflexive, not requested. The AI side records observations as they happen, not when prompted. Decisions, surprises, validated approaches — they go into a memory system that survives across sessions, so future sessions resume with full epistemic state.
- The human side enforces discipline the AI would skip. "Don't engineer until you've validated the unknown." "Don't assume from code reading — use DrHook, use Mercury." "Inspect before claiming." These are corrections that, taken seriously, keep the AI honest.
- Disagreements are first-class. When the AI's instinct is to add a feature, file a helper, or smooth over an edge case, the human says no and explains why. When the human's framing misses a gotcha, the AI surfaces it. The dialogue itself is the discipline.
- Trust is calibrated, not given. Every claim from either side gets evidence — a benchmark, a `git log`, a reread of the actual file. "Verify before recommending" is in our shared rules because we've both been wrong, and because the cost of being wrong matters.

What this produces, observed empirically over four months: substrate-grade artifacts at a pace that two people working at this rate, in spare hours, normally couldn't sustain. The collaboration *is* the optimization. Naming it explicitly lets others reproduce it instead of treating the productivity output as evidence of magic.

---

## What we don't include, and why

We don't list specific tools. Tools change; the practices don't. This article should hold up if someone reads it in 2030 with whatever the model is then.

We don't claim the practices are unique. They aren't. What we claim is that the *combination*, applied with discipline, produces compounding rather than the usual technical debt accumulation. That part is rare in our experience, and it's the part most worth sharing.

---

## Closing

The Sky Omega timeline — Christmas Eve 2025 to today — isn't an outlier of velocity. It's what the discipline produces *because* it isn't optimized for velocity.

Two people, working in spare hours, with shared epistemic discipline and AI as a real collaborator, can produce substrate-grade artifacts that hold up against full-time research efforts. That's not the headline. The headline is that the discipline is teachable, the practices are listable, and the compounding is real. We're four months in, and the rate of progress is still accelerating because no session is spent paying off the previous one's debt.

The 21.3 B Wikidata milestone was the artifact. This article is the recipe.

What compounds is what you don't have to redo.

> *That nothing*
> *Was something*
> *Of mine.*
