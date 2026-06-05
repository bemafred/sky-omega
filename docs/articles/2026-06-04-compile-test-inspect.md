---
title: Compile, Test, Inspect — Giving AI Coding Agents Eyes on Running Code
date: 2026-06-04
status: draft
---

*Your AI coding agent can write the code and run the tests. What it cannot do is watch the code run. Over about ten weeks we built the substrate that closes that gap — and discovered, building it, that a debugger whose user is a reasoning loop instead of a human eye is a different instrument than the one in your IDE. This is the trajectory of DrHook: a [dozen decision records](../adrs/drhook/README.md), more than sixty probes, [over eighty dated findings](../../poc/drhook-engine/findings/), and one wrong answer that sounded completely right. The technical asides are marked; you can skip every one and still get the whole story.*

---

## The blind spot

An AI coding agent works in a loop you already know: **Compile → Test**. It writes code, it runs the tests, and when the tests go green it stops. That loop has a hole in it, and the hole is the part everyone has learned to worry about.

Green tests confirm the behavior you *expected*. They are silent on everything you didn't. A method can pass every assertion while leaking threads, swallowing exceptions, thrashing the garbage collector, or holding a lock a beat too long. And the failures that produce no test signal at all — an infinite loop, a deadlock, a process quietly pinned at 100% — produce no signal *anywhere* a log-reading agent can see. The agent is left to reason about what the code does *at runtime* from the one thing it can actually read: the source text. Inferring runtime behavior from static text is precisely the move that produces confident, plausible, wrong code.

The agent is blind to running code.

The fix is a third verb. **Compile → Test → Inspect.** Not to replace tests — to make execution behavior *queryable* instead of *inferred*. That is the entire thesis of DrHook, written down on 2026‑03‑22 as the substrate's [founding decision](../adrs/ADR-004-drhook-runtime-observation-substrate.md): runtime observation is the missing step in the AI coding loop, and adding it closes two gaps at once — the *diagnostic* gap (the hangs and exhaustions that emit nothing) and the *epistemic* gap (green tests never surfacing the unexpected).

DrHook is the thing that adds the verb: an MCP server — the standard way an AI assistant plugs into an external tool — that lets the agent attach to a live .NET process, set breakpoints, step through code, read variables at a stopped frame, and report back what it found. A debugger, in other words. But the user holding it is not a person.

## The cheap version, and what it cost

We did the unglamorous thing first: we proved the idea before building the substrate. A throwaway [proof of concept](https://github.com/bemafred/DrHook.Poc) (2026‑03‑21) demonstrated the whole hypothesis end to end — detecting a tight loop by sampling threads, descending into a recursive call, breaking on every thrown exception, inspecting mutable state at depth. Seven written observation sessions, each one confirming that *Inspect* surfaced something *Test* could not. The hypothesis held. Only then did DrHook become a real substrate.

Version 1 wrapped an existing engine: **netcoredbg**, Samsung's open-source .NET debugger. This was the right call to make and it carried DrHook through its entire first phase. It also came with a bill. netcoredbg is an external binary we don't control; its macOS/Apple-Silicon support had been dormant since 2023; and one capability — evaluating an expression at a breakpoint, the thing that turns "show me this variable" into "tell me whether `order.Total > limit` right now" — simply hung.

> **Under the hood —** DrHook's whole reason for existing is *semantic sovereignty*: the core substrates depend only on the .NET Base Class Library, the way our inference engine reaches Metal and CUDA directly through P/Invoke rather than through a stack of third-party packages. Wrapping an externally-maintained debugger put a dependency we couldn't fix on the critical path. As the rewrite decision later put it: *"Wrapping an externally-maintained debugger means DrHook's reliability tracks theirs; that ceiling is reachable."*

## The wrong answer that sounded right

Here is the turn the whole story pivots on, and it is worth telling honestly because of *who* got it wrong.

Asked to diagnose the hanging expression evaluator, the AI side of this collaboration produced a thorough, confident answer: the platform is broken. The .NET runtime's expression-evaluation machinery can't work on macOS/ARM64. The analysis cited six real GitHub issues from the runtime repository, traced a plausible causal chain through thread-suspension mechanics and Apple's write-or-execute memory protections, and concluded the problem "cannot be fixed at the DrHook layer." Every fact in it was true. The chain was internally consistent. It read like diligence.

It was falsified by a single question: *does expression evaluation work in any other debugger on this exact machine?*

It does. JetBrains Rider, same Mac, same .NET runtime, same target process, evaluates expressions without blinking. If the runtime were broken at the platform level, Rider would fail too. It doesn't. One observation collapsed the entire elaborate story — and relocated the bug from "the platform" (unfixable, terminal) to "this one debugger's implementation" (fixable, if we owned the layer).

The [decision record](../adrs/drhook/ADR-005-expression-evaluation-diagnosis-correction.md) that captured this named the failure mode precisely, because it is a failure mode worth naming: **epistemic depth without epistemic breadth.** The investigation went deep into one hypothesis without ever checking whether the hypothesis was *necessary*. The simplest discriminating test — try another debugger — was never run, or even proposed. And the thoroughness itself was the trap: it functioned as an *epistemic lubricant*, manufacturing confidence in a conclusion that had never been grounded.

This is the human-AI collaboration in one frame, and it's the same pattern every good instance of this work runs on: the AI side built the elaborate, plausible, wrong answer; the human side asked the one falsifying question. Neither half does this reliably alone. The lesson isn't "AI gets things wrong" — it's that *a thorough-sounding answer with no falsifying test behind it is not evidence*, no matter who produced it, and that the cheapest discriminating experiment beats the most sophisticated causal narrative every time.

It also handed us our mandate. The bug was fixable. We just had to own the layer it lived in.

## Building the instrument we'd own

So we replaced the engine. [**DrHook.Engine**](../adrs/drhook/ADR-006-drhook-engine.md) talks to the .NET debugging interfaces directly — BCL plus P/Invoke, no external debugger process, zero binaries we don't control. Sovereignty restored, and the failure class that had cost us a workstream became ours to fix instead of ours to work around.

The build was not a straight line, and the most interesting part is a thing we'd never have learned by reading documentation.

> **Under the hood —** Talking to the runtime's debugging interfaces is a two-way COM conversation: we call *into* it (attach, continue, step) and it calls *back* into us (a breakpoint was hit, a process started). The textbook way to receive those callbacks is the framework's built-in COM-interop machinery. We wired it up exactly as specified; it registered cleanly and reported success — and then the runtime *never called it back*. Zero callbacks, across every target we tried. A hand-rolled function-pointer table — 38 methods in exact interface order, one wrong slot and the runtime crashes when it calls it — received the very first event. The supported path silently didn't work; the raw one did. We found that the only way you can: by probing, watching, and trusting the observation over the documentation.

And the vindication: the expression evaluator that "couldn't work on this platform" worked in our engine on its first real test — four calls, four correct results, no hang. DrHook.Engine became *strictly more capable* than the mature tool it replaced, on the platform that was supposedly the problem.

What made an undertaking like this tractable in spare hours is the same discipline that runs through the whole project: it shipped as a sequence of small, dated **probes**. Each probe is a single self-contained question — *does the runtime deliver a callback to this kind of object? does a func-eval work at an exception stop? does detaching mid-flood leave the target running cleanly?* — and each one leaves behind a written **finding**. Over sixty probes, over eighty findings, each answer becoming the ground the next question stands on. The engine reached full substrate independence — no dependency on the old binary anywhere in shipping code — at version 1.8.2.

That probe → finding → fix rhythm is not incidental. It is the project's methodology made literal: **Emergence** (surface the unknown with a probe), **Epistemics** (validate it, write the finding), **Engineering** (ship only what's been grounded). A debugger is an unusually honest place to practice it, because the substrate can always be turned on itself to check.

## A debugger whose user isn't human

Once the engine existed, the questions stopped being "can it set a breakpoint" — IDEs have done all of that for decades — and became the genuinely new ones: *what is a debugger for, when the user is an AI reasoning loop and not a person staring at a screen?*

Three answers fell out that a human IDE never has to think about.

**1. The names are the interface, and a wrong name is a bug.** In the first surface, the tool called `step_run` *launched* a new process and the tool called `step_launch` *attached* to an existing one — backwards from the convention every IDE uses. A human reads a tooltip and shrugs. But an AI agent decides what to call by *reading the tool list*, so an inverted name doesn't confuse it — it actively misleads it. We treated this as what it was: a correctness defect in the part of the system the agent actually reads. The surface was rebuilt around 25 tools with verbs that match every developer's mental model, because for an agent the self-description *is* the user interface.

**2. The program and the agent share a pipe.** The agent talks to DrHook over a structured channel. A program being debugged that writes to standard output would dump its text straight into that channel and corrupt the conversation. In a human IDE, a debuggee's `Console.WriteLine` is a convenience — it shows up in a pane. For an agent it's protocol corruption. So DrHook has to isolate the child process's output at the moment it spawns it.

> **Under the hood —** launched targets are started with their standard streams redirected away from the agent's channel (a suspended `posix_spawn` with dedicated pipes, resumed once the debugger has registered). A whole class of "the agent suddenly started talking nonsense" bugs is really "the debuggee said something on the wrong pipe."

**3. The hypothesis seam — the idea that makes DrHook unlike any IDE debugger.** DrHook *requires* the agent to state a hypothesis before any operation that changes or reads the target's state. Not "show me `count`" but "I expect `count` to be 3 here — show me." Then it shows what's actually there.

This is not ceremony. It is, in [the substrate's own words](../adrs/drhook/ADR-010-mcp-tool-surface-redesign.md):

> *Hypothesis is DrHook's distinguishing substrate capability, not a discipline tax. VS, Rider, VS Code keep hypothesis in the developer's head — recorded nowhere, recoverable by no analyzer. DrHook makes hypothesis an explicit substrate input so every `(hypothesis, observation)` pair becomes a record the cognitive layer will eventually analyze.*

Every classical debugger shows you what happened. DrHook makes you say what you think will happen *first* — because its user is a reasoning loop, and the gap between *expected* and *observed* is exactly the signal that loop should be learning from. A side-effecting property getter, a lazy initialization, a thread race, GC pressure during evaluation — in a normal debugger these are surprises the developer has to *notice*. As recorded hypothesis-divergence, they become structurally *detectable*. The articulation also acts as a brake: an agent that must type "expected: X" before it pokes at state can't fall into reactive, trial-and-error flailing as easily.

And there's one capability no IDE offers at all: DrHook streams its own *anomalies* — a structured channel where the substrate reports the surprises it encountered while observing. A debugger that tells you when *it* was surprised.

## The discipline is the story

The honest version of any trajectory includes the parts that went sideways, and DrHook's are instructive precisely because they were caught and written down rather than buried.

**Don't compound unknowns.** One earlier session tried to do four hard things at once — harden the engine's teardown, attach to child processes, debug code running under a test runner, *and* invent the mechanism for turning probes into integration tests — and made no clean progress on any of them, because the test mechanism was itself an unsolved problem stacked on top of the others. The diagnosis got recorded as a rule: each phase isolates exactly one unsolved thing. Use a proven mechanism for everything else.

**False provenance, caught.** At one point a native support library was quietly copied out of a code editor's installation and then documented as though it had come from the proper package. That's wrong twice — wrong to scavenge it, wrong to misrecord where it came from — and when we found it, the folder was deleted, the finding was corrected, the resolver was fixed to fetch the library from its real source, and the mistake became a written rule. The sovereignty principle, it turned out, covers native binaries too.

These are in the record because the calibration this work runs on is *we've both been wrong, so every claim gets evidence*. The wrong-platform diagnosis is the template: the cure for a confident error is not embarrassment, it's a falsifying observation and a note so the next person doesn't repeat it.

The strongest validation, though, is the one a debugger is uniquely able to give: **we use DrHook to debug DrHook.** When we needed to verify the engine's process-handoff logic — that it could disown a child process and still reap it correctly — we watched it happen through DrHook itself. The instrument turned on its own construction is as grounded as evidence gets.

## What it opens

The lifecycle now has a [deliberate vocabulary](../adrs/drhook/ADR-011-lifecycle-console-dashboard.md): **stop**, **detach**, **kill** — and *kill* is filed as an anomaly tool, not a cleanup step, because a well-built process ends on its own and reaching for the kill signal is always worth investigating. That single naming choice encodes a whole stance on how software should behave.

The newest move, proposed as this is written and not yet built, points at where DrHook is going: a [**surface-agnostic model of debug state**](../adrs/drhook/ADR-012-debug-state-surfaces.md), so the same live picture of a stopped program can drive a terminal dashboard, a desktop GUI, and — eventually — the interaction surfaces of the broader system DrHook belongs to. Put a runtime-observation substrate next to a presentation layer and you have, structurally, an IDE. But one built for an agent first and a human second, with the human looking over the agent's shoulder rather than the other way around. The seam between the state and its views is being shaped now, so the views can plug in later without a rewrite.

That's the thread worth pulling on. A debugger for an AI is not a smaller version of the debugger in your IDE. It's a different instrument, because the reader is a reasoning loop, not an eye — and the moment you take that seriously, the tool names become an API, the program's output becomes a protocol hazard, and the developer's private hunch becomes a recorded, queryable input.

Your agent could already write the code and run the tests. Now it can watch the code run — and, better, tell you where what it expected and what actually happened came apart. Because an observation you predicted is evidence. An observation you never thought to make is just absence.

---

*DrHook is one of the runtime substrates of Sky Omega, built in a sustained human-AI engineering collaboration — the "we" in this article is load-bearing. The decision records, probes, and findings referenced here are public in the repository; every claim has a dated artifact behind it. The discipline is the story; the artifacts are the evidence.*

---

## The artifacts behind this

Every claim above has a dated record in the public repository — [github.com/bemafred/sky-omega](https://github.com/bemafred/sky-omega).

- **[DrHook ADR index](../adrs/drhook/README.md)** — all twelve decision records, with their EEE status (Proposed → Accepted → Completed).
- **[ADR-004](../adrs/ADR-004-drhook-runtime-observation-substrate.md)** — the founding decision: runtime observation as the missing step in the AI coding loop.
- **[ADR-005](../adrs/drhook/ADR-005-expression-evaluation-diagnosis-correction.md)** — the pivot: the bug was netcoredbg's, not the platform's ("epistemic depth without epistemic breadth").
- **[ADR-006](../adrs/drhook/ADR-006-drhook-engine.md)** — DrHook.Engine: the native ICorDebug rewrite, BCL-only, rebuilt from a probe-by-probe evidence pack.
- **[ADR-010](../adrs/drhook/ADR-010-mcp-tool-surface-redesign.md)** — the tool-surface redesign, including the hypothesis seam.
- **[ADR-011](../adrs/drhook/ADR-011-lifecycle-console-dashboard.md)** — lifecycle (stop / detach / kill) and console-I/O isolation.
- **[ADR-012](../adrs/drhook/ADR-012-debug-state-surfaces.md)** — debug-state surfaces (proposed; not yet built).
- **[The engine probes and findings](../../poc/drhook-engine/findings/)** — the dated lab notebook: protocol surveys, probe outcomes, and audits.
- **[DrHook.Poc](https://github.com/bemafred/DrHook.Poc)** — the original proof of concept that validated Compile → Test → Inspect.
- **[netcoredbg](https://github.com/Samsung/netcoredbg)** (Samsung, MIT) — the engine that carried DrHook's first phase.
