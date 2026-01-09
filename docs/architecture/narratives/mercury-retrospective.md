# The Mercury Retrospective: EEE Under Industrial Load

*How we built a 46K-line zero-GC triple store using LLM-assisted development, and what it proved about epistemic methodology.*

——

## The Experiment

We wanted the hardest possible test.

Not a proof-of-concept. Not a tutorial exercise. An industrial-grade implementation at the edge of what the selected stack could deliver—and at the edge of what LLM-assisted development had ever been asked to do.

Mercury is a zero-GC, bitemporal triple store with full W3C RDF and SPARQL conformance. The core engine is ~46K lines of C# 14 on .NET 10, with no external dependencies beyond the Base Class Library. Storage uses memory-mapped B+Trees capable of TB-scale datasets. Every hot path is zero-allocation, using `ref struct` and `Span<T>` to keep the heap clean.

This isn’t the kind of system you prototype in a weekend. It’s the kind of system where architectural mistakes compound, where edge cases hide in specification gaps, where the difference between “works” and “works correctly under load” is months of effort.

We built it using Claude App for ideation, Claude Code for implementation, and W3C test suites for verification.

The question wasn’t whether we could build it. The question was whether EEE—Emergence, Epistemics, Engineering—could guide the process through genuinely novel territory where the LLM’s training data was thin and the problems were real.

——

## The Constraints

What made this tractable wasn’t the tooling. It was the constraints.

### Semantic Boundaries

The BCL-only rule wasn’t arbitrary minimalism. External packages bring external semantics—naming conventions, abstraction patterns, implicit assumptions. Every dependency is a potential source of semantic drift. By restricting to BCL, we ensured that every name, every type, every abstraction was *ours to control*.

This matters for LLM-assisted development. When Claude Code generates code, it draws on patterns from training. If those patterns conflict with your architecture’s semantics, you get drift. If your semantics are grounded in well-documented, stable foundations (.NET 10, C# 14, BCL), the LLM’s suggestions align more naturally.

### Mechanical Verification

The W3C test suites for RDF and SPARQL are extensive, precise, and externally maintained. They define correctness independent of our opinions. When a test fails, it’s not a matter of interpretation. Red means wrong. Green means the spec is satisfied.

This created a tight feedback loop:

1. Implement feature
1. Run tests
1. Observe pass/fail
1. Fix or proceed

The tests made failure *observable and unambiguous*. No debates about whether the code was “good enough.” Either it conformed to the specification or it didn’t.

### Grammar-Driven Parsing

EBNF grammars for Turtle, SPARQL, N-Triples, and other formats provided mechanical specifications for parsing. Not prose descriptions. Not examples. Grammars that could be translated directly into parser structure.

This reduced ambiguity at a critical layer. When the grammar said something was valid, the parser accepted it. When the grammar excluded something, the parser rejected it. The mapping from specification to implementation was direct.

### Well-Documented Foundations

.NET 10 and C# 14 are extensively documented. Memory-mapped files, B+Trees, span-based processing—these are well-known data structures and patterns with established literature. Claude Code could draw on this foundation without inventing novel approaches to solved problems.

The innovation was in *composition*, not in reinventing fundamentals.

——

## The Workflow

The development process had a rhythm.

### Ideation → Prototype → Implementation

Architectural decisions and design ideation happened in Claude App—conversational, exploratory, iterative. When a design solidified, it became a prototype: TurtleParser skeleton, SparqlEngine structure, storage layer architecture.

These prototypes seeded Claude Code. Not as finished code, but as *intent with structure*. Claude Code then took them forward, filling in implementation details, handling edge cases, writing the mechanical parts that don’t require creative insight.

### Tests as the Constraint Layer

From the beginning, W3C test suites were integrated. Not as an afterthought. Not as a “we’ll add tests later” promise. As the mechanical verification layer that determined whether implementation attempts succeeded or failed.

This wasn’t test-driven development in the strict sense—we didn’t write tests first for every function. It was *spec-driven development with tests as the verification mechanism*. The W3C specs defined correctness. The tests verified conformance. TDD operated inside this larger loop for implementation details.

### Terminal Logs as Distress Signals

Claude Code runs in a terminal. Its progress—and its struggles—are visible in the output.

We learned to read the signs. When Claude Code was making steady progress, the terminal showed purposeful iteration: attempt, test, adjust, proceed. When it was struggling, the pattern changed: repeated attempts at the same problem, circular refactoring, trial-and-error spiraling into the void.

These spirals were the distress signal. They indicated that Claude Code had hit an architectural edge case where training data was insufficient—a genuinely novel problem that couldn’t be solved by pattern-matching against known solutions.

### ADRs as Epistemic Intervention

When spirals were detected, we stopped.

Not to blame the tooling. Not to abandon the approach. To *analyze*. What was Claude Code trying to do? What was it failing to achieve? What was the actual constraint it couldn’t satisfy?

These analyses became Architecture Decision Records. ADRs captured:

- The problem as we understood it
- The options considered
- The decision made
- The rationale

This was EEE in action:

- **Emergence** had surfaced an unknown unknown—an edge case we hadn’t anticipated
- **Epistemics** made it explicit, documented, falsifiable—transformed it into a known known
- **Engineering** could then proceed on solid ground

After creating the ADR, Claude Code resumed with the architectural question resolved. The constraint was now explicit. The path was clear.

——

## The Spec Question

There’s a criticism of spec-driven development, articulated by Kent Beck among others: you can’t spec properly. Specifications are incomplete. They have gaps. Following them blindly leads you off cliffs.

The criticism is valid—for a certain mode of working.

If your process is “Write spec → Implement spec → Done,” you will fail. Specs are not omniscient. They don’t anticipate every edge case. They contradict themselves. They leave things unspecified.

But that’s not what we did.

### Specs as Falsifiable Hypotheses

We treated specifications—both the W3C specs and our own high-level architectural specs—as *hypotheses about correctness*. They were our best current understanding, not eternal truths.

When implementation revealed a gap in the spec, we didn’t pretend the gap didn’t exist. We surfaced it. We decided how to handle it. We documented the decision. We moved on.

The W3C test suites themselves sometimes revealed ambiguities in the W3C specs. Tests that seemed to contradict each other. Edge cases where the “correct” behavior wasn’t clear. These weren’t failures of our process. They were *discoveries*—unknown unknowns becoming known unknowns, then known knowns through explicit decision.

### TDD Inside the Loop

Test-driven development wasn’t replaced by specs. It operated *inside* the spec-driven loop.

For implementation details—specific functions, edge case handling, optimization paths—TDD worked normally. Write a test for the expected behavior. Implement until the test passes. Refactor. Proceed.

But TDD doesn’t answer architectural questions. It doesn’t tell you what to build, only whether what you built works. Specs provided the *what*. TDD verified the *implementation*. EEE handled the *gaps*.

### Adaptive Rigor

The result was neither blind spec compliance nor unstructured exploration.

Specs provided direction and constraints. Tests provided mechanical verification. EEE provided the protocol for handling incompleteness. ADRs captured decisions for future reference.

Rigor that acknowledges incompleteness. Structure that adapts to discovery. This is what we mean by *epistemic discipline*.

——

## The Outcome

Mercury exists. ~46K lines of working code. Zero-GC. Bitemporal. Full W3C conformance for the implemented features. Storage layer capable of TB-scale datasets.

But the code isn’t the interesting outcome.

### Substrate Independence Validated

The same epistemic methodology—EEE, Semantic Architecture, E-Clean—operated successfully across:

- **Claude App** (conversational reasoning, ideation)
- **Claude Code** (industrial implementation)
- **.NET/C#** (execution platform)
- **W3C specifications** (constraint framework)
- **EBNF grammars** (mechanical parsing)

When Claude Code hit edge cases where training was thin, the response wasn’t “get a better model” or “switch tools.” It was “apply the same discipline we use everywhere else.”

The methodology is the invariant. The substrates are interchangeable.

This suggests something about cognitive architecture generally: if you constrain the epistemics tightly enough—explicit assumptions, falsifiable claims, grounded semantics—the reasoning substrate becomes a variable, not a dependency.

### LLM Tooling Assessed

We now have empirical data on Claude Code’s capabilities:

**Quality of code generation:** Sufficient for industrial use when constrained by tests, specs, and semantic architecture. Not sufficient for unconstrained greenfield exploration of novel architectural territory.

**Quality of tooling use:** The terminal provides enough signal to detect failure modes. Spiraling is observable. Intervention is possible.

**Failure modes:** Predictable. Edge cases with thin training data cause trial-and-error loops. These are surfaceable through observation and addressable through epistemic intervention (ADRs).

**Success factors:** Hard constraints (BCL-only, W3C tests, EBNF grammars), well-documented foundations (.NET, C#), semantic architecture (meaningful names, explicit boundaries), and human oversight for architectural decisions.

### The EEE Stress Test

This was the hardest experiment we could design. Edge-of-stack implementation. Novel territory. Industrial quality requirements. LLM tooling under conditions it hadn’t been validated for.

EEE held.

Not because the methodology is magic. Because it’s honest about what it knows and doesn’t know. It expects unknowns to surface. It has a protocol for handling them. It doesn’t pretend completeness.

When emergence produced surprises, we didn’t panic. We did epistemics. When epistemics clarified the ground, we did engineering. The loop continued.

——

## What This Means for Sky Omega

Mercury is a component of Sky Omega—the storage substrate for Lucy’s long-term memory. Its successful development validates the approach we’re using for the larger system.

But more importantly, it validates the *methodology* that Sky Omega embodies.

Sky Omega is designed to be a cognitive architecture that maintains epistemic discipline—explicit assumptions, grounded reasoning, falsifiable claims. The Mercury development process was Sky Omega’s methodology applied to its own construction.

We built a component of Sky Omega using the principles Sky Omega will eventually enforce. The methodology bootstrapped itself.

This is what we mean by *eating our own cooking*. Not just using the tools we build, but using the *principles* we advocate, under conditions harsh enough to falsify them if they were wrong.

They weren’t wrong.

——

## Lessons for Others

If you’re considering LLM-assisted development for serious projects:

### Constrain Heavily

The more constraints you provide, the better LLM tooling performs. Test suites, specifications, grammars, type systems, naming conventions—every hard boundary reduces the space of possible errors.

Unconstrained generation produces unconstrained quality. Constrained generation produces verifiable output.

### Make Failure Observable

You need to see when the process is failing. Terminal logs, test results, build output—whatever gives you signal. If you can’t tell the difference between progress and spiraling, you can’t intervene.

### Have a Protocol for Unknowns

You will hit edge cases. The LLM will encounter problems it can’t solve through pattern matching. You need a process for handling these: stop, analyze, document, decide, resume.

ADRs work. Other documentation forms work. What doesn’t work is pretending unknowns won’t appear.

### Human Judgment for Architecture

LLM tooling is excellent at implementation within architectural constraints. It’s unreliable for architectural decisions themselves. Keep humans in the loop for:

- What to build (not just how)
- How components relate
- What constraints matter
- When to stop and rethink

### Trust the Loop

EEE is a loop: Emergence surfaces unknowns. Epistemics clarifies them. Engineering builds on the clarified ground. Then emergence surfaces more unknowns.

This isn’t a failure mode. It’s the process working correctly. Trust it.

——

## Conclusion

We set out to validate whether EEE could guide LLM-assisted development through genuinely novel territory.

It can.

Mercury is the evidence. Not a toy. Not a proof-of-concept. A working system at industrial scale, built at the edge of what the tools and the stack could deliver.

The methodology held because it doesn’t promise completeness. It promises a protocol for incompleteness. That’s what you need when you’re building something that hasn’t been built before.

*Powered by Mercury. Validated by EEE.*