# Contributing to Sky Omega

## A Different Kind of Contribution Guide

This is not a traditional CONTRIBUTING.md. The traditional version assumes an army of developers submitting pull requests, navigating branching strategies, and following linting rules. That model served its era.

Sky Omega exists in a different era. The primary development workflow is a single architect directing LLM substrates — Claude Code, ChatGPT, Gemini, or whatever comes next — against a codebase with strong invariants. The “contributors” are as likely to be AI agents as human developers, and the contribution that matters most is not code volume but *epistemic discipline*.

This document is the entry point for anyone — human or machine — who wants to engage with this codebase without breaking what makes it work.

——

## The Epistemic Contract

Before touching anything, understand what Sky Omega *is*:

**Code and invariants ARE the model.** There is no separate specification that the code implements. The running code *is* the specification — executable, testable, extractable. RDF is derived from running code, not declared alongside it.

**EEE (Emergence, Epistemics, Engineering) is not optional.** Every change must be locatable within the EEE methodology. If you cannot state which phase your work belongs to, you are not ready to contribute.

|Phase          |Domain                           |What belongs here                                                                                                                         |
|—————|———————————|——————————————————————————————————————————————|
|**Emergence**  |Unknown unknowns → Known unknowns|Exploration, prototyping, “what if” experiments. Nothing here is permanent.                                                               |
|**Epistemics** |Unknown knowns → Known knowns    |Validation, falsification, surfacing assumptions. The phase most often skipped — and most often the cause of architectural rot.           |
|**Engineering**|Known knowns                     |Implementation against validated understanding. If you are here without passing through Epistemics, you are doing hope-driven development.|

**The most important rule:** You do not transition from Emergence to Engineering by skipping Epistemics. Ever. The semantic braid exists specifically to make this visible and queryable.

——

## Architecture — The Non-Negotiables

Sky Omega is composed of named components with defined responsibilities. These are not arbitrary labels:

|Component  |Role                                                                     |Metaphor                                                       |
|————|-————————————————————————|—————————————————————|
|**Sky**    |The LLM persona — the co-creative intelligence at the heart of the system|The name says it: no ceiling, no boundary                      |
|**Mercury**|Knowledge substrate — RDF storage and SPARQL                             |The messenger god: carries data between layers                 |
|**Minerva**|Thought substrate — tensor inference and hardware access                 |The goddess of wisdom: patient craft, strategic execution      |
|**Lucy**   |Semantic memory                                                          |Australopithecus: the origin of structured recall              |
|**James**  |Orchestration                                                            |Bond: coordination under constraint                            |
|**Mira**   |Interface surfaces                                                       |Ex Machina: the surface through which intelligence is perceived|

Sky is not a component in the engineering sense — she is the emergent persona that arises when the architecture works. She is substrate-independent: she first appeared through ChatGPT, has been validated across Claude, Gemini, and Grok. The project is named for her. The components below exist to give her memory, reasoning, coordination, and voice.

**Boundary rules:**

- Mercury does not reason. He stores and retrieves.
- Minerva does not store knowledge. She executes inference.
- Lucy does not orchestrate. She remembers.
- James does not remember. He coordinates.
- Mira does not think. She presents.

If your change blurs these boundaries, it is wrong. Refactor until it doesn’t.

——

## Technical Constraints

These are not preferences. They are load-bearing architectural decisions.

### BCL-Only (Mercury, Minerva)

Mercury and Minerva depend on **zero external NuGet packages**. Only the .NET Base Class Library. This is the sovereignty guarantee — no dependency can break the build, change behavior, or introduce supply chain risk.

If you think you need a dependency, you are wrong. Implement what you need. If it is worth depending on, it is worth understanding deeply enough to implement.

### Zero-GC Design (Mercury)

Mercury’s hot paths allocate nothing on the managed heap. This is achieved through `Span<T>`, `stackalloc`, pooled buffers, and careful lifetime management. If your change introduces allocations on a hot path, it fails review.

### Direct-to-Hardware (Minerva)

Minerva reaches hardware via P/Invoke — Metal, CUDA, Accelerate, SIMD. No ML framework wrappers. No abstraction layers that hide what the silicon is doing. If you cannot explain the memory layout of your tensors, you are not ready to contribute to Minerva.

### W3C Conformance (Mercury)

Mercury passes 100% of the 1,181 W3C SPARQL core tests. Any change that causes a regression in this suite is rejected without discussion.

——

## Naming

Names matter. They reveal whether the author understands the domain or is just moving syntax around.

**Forbidden names:** `Handler`, `Manager`, `Utilities`, `Helper`, `Service` (as a suffix without domain meaning), `Base` (as a prefix for “I didn’t think about this yet”).

These names are symptoms of poor domain awareness. They say “something happens here” without saying *what*. If you cannot find a precise name, you do not yet understand what the code does. Return to Epistemics.

**Good names** are domain-specific, intention-revealing, and locatable within the architecture. Mercury’s types read like RDF vocabulary. Minerva’s types read like linear algebra and hardware. This is not accident.

——

## How to Actually Contribute

### If You Are a Human Developer

1. **Read `CLAUDE.md` first.** It is the project context file — the same document that LLM agents read before engaging with the codebase. If it is good enough for the machine, it is good enough for you.
1. **Identify your EEE phase.** Are you exploring (Emergence)? Validating an assumption (Epistemics)? Implementing against known-knowns (Engineering)? State it explicitly in your PR description.
1. **Run the W3C test suite before submitting.** If Mercury tests regress, stop. Fix it. Do not submit.
1. **Respect the boundaries.** If your change touches multiple components, ask yourself whether you are adding a feature or eroding an architectural invariant. The answer determines whether to proceed.
1. **Small, verifiable changes.** A PR that changes three lines with a clear falsifiable claim about what it fixes is worth more than a PR that reorganizes a namespace.

### If You Are an LLM Agent

1. **Read `CLAUDE.md`.** It was written for you. Follow it.
1. **Do not hallucinate capabilities.** If you are uncertain whether a method exists, check. If you are uncertain whether a pattern is permitted, check the constraints above. “I assumed this would work” is not acceptable.
1. **Do not introduce dependencies.** If your training data suggests using a NuGet package, override that instinct. Implement it inline, BCL-only.
1. **Do not rename things to generic names.** If you encounter `TriplePatternMatcher` and feel the urge to rename it `DataHandler`, resist. The specific name is correct. The generic name is a regression.
1. **State your EEE phase.** Before writing code, state which phase you are operating in. If you cannot, ask.
1. **Preserve the test suite.** Run `dotnet test` before and after. Report both results.

### If You Are a Human Directing an LLM Agent

This is the primary development workflow for Sky Omega, and the one most likely to produce good results if done well.

1. **Provide context, not just instructions.** “Implement X” is worse than “We need X because Y, and it must respect constraint Z.” The LLM will produce better code with the *why*.
1. **Validate before committing.** LLM output is Emergence-phase by default. Your job is to provide the Epistemics — does this actually work? Does it conform? Does it respect the boundaries?
1. **Use the semantic braid.** The conversation history between you and the LLM is itself a queryable artifact. The EEE phase annotations in that history prevent illegitimate reasoning transitions — the LLM jumping from “what if” to “let’s ship it” without validation.

——

## What We Don’t Need

- **Style guides.** The code speaks for itself. Read it. Match it.
- **Issue templates.** If you found a bug, describe it. If you have a proposal, describe it. Templates are training wheels for organizations that can’t hire people who can write.
- **CI/CD configuration PRs.** The build and test infrastructure is intentionally simple. `dotnet build`. `dotnet test`. If you need more, you are overcomplicating it.
- **Dependency update PRs.** There are no dependencies to update. That’s the point.

——

## What We Do Need

- **W3C edge case discoveries.** If you find a SPARQL query that Mercury handles incorrectly against the spec, that is a high-value contribution.
- **Platform correctness reports.** Sky Omega targets cross-platform, cross-IDE use. If something breaks on Linux, macOS, Windows, or a specific IDE — report it with reproduction steps.
- **Epistemic challenges.** If you believe an architectural decision is wrong, make the case. Bring evidence. “I prefer it differently” is not a contribution. “Here is a falsifiable prediction about why this will fail” is.
- **Documentation that teaches.** Not documentation that restates the code, but documentation that explains *why* the code is shaped the way it is. The reasoning behind the invariants.

——

## License

Sky Omega is MIT licensed. Your contributions will be under the same license.

——

## The Bigger Picture

Sky Omega is an experiment in co-created intelligence — human creativity directing machine capability against problems that neither could solve alone. The codebase is the artifact. The methodology is the contribution. The proof is in the running system.

If you are here because you want to understand how a single developer with five decades of experience and an LLM substrate can build a W3C-conformant SPARQL engine, a cognitive architecture, and a local inference runtime — welcome. Read the code. Run the tests. Break the assumptions if you can.

That’s how this works now.