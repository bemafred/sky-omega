# Mercury Retrospective #2: The Last Two Percent

*January 2026*

## The Claim

Six weeks ago, Mercury reached 98% W3C conformance. Impressive, but incomplete. The remaining 2% contained the edge cases that separate “works well” from “dependable.”

Today: **100% conformance across all core W3C test suites.** 1,181 tests passing. Zero failures.

This retrospective documents what it took to close that gap.

## The Numbers

|Metric              |December|January|Change  |
|———————|———|-——|———|
|Mercury source lines|~46K    |71,832 |+56%    |
|Total codebase      |~95K    |145,849|+54%    |
|W3C core conformance|98%     |100%   |Complete|
|Test cases          |1,785   |3,830  |+115%   |

**W3C Core Conformance (1,181/1,181):**

- Turtle 1.2: 309/309
- TriG 1.2: 352/352
- RDF/XML 1.1: 166/166
- N-Quads 1.2: 87/87
- N-Triples 1.2: 70/70
- SPARQL 1.1 Syntax: 103/103
- SPARQL 1.1 Update: 94/94

**Extended conformance:** 99% (2,060/2,066) — 6 intentionally skipped legacy/non-standard tests.

## What the Last 2% Cost

The final push revealed problems that don’t show up at 98%:

**Stack overflows.** QueryResults structs had grown to 90KB. Parallel test execution triggered stack exhaustion. Fix: pooled enumerator arrays, boxed patterns moved to heap. Result: 93-99% reduction in stack allocation.

|Struct          |Before      |After      |
|-—————|————|————|
|QueryResults    |89,640 bytes|6,128 bytes|
|MultiPatternScan|18,080 bytes|384 bytes  |

**Infinite loops.** Two edge cases in SPARQL evaluation that Claude Code couldn’t diagnose — no debugger, no stack traces. Manual debugging in Rider, root cause analysis, then back to Claude Code with explicit constraints.

**Test suite instability.** When regression tests fail unpredictably, everything stops. We separated concerns: Claude Code handles targeted fixes, I run full regression in Rider, export dependable results to a bugs folder. Stable feedback loop restored.

The pattern: the last 2% isn’t about missing features. It’s about making the existing 98% trustworthy under load.

## The Methodology

EEE (Emergence, Epistemics, Engineering) under industrial conditions:

**Emergence:** Let Claude Code explore solutions. When it spirals, stop. Don’t push through — the spiral is information.

**Epistemics:** Analyze the spiral collaboratively. What assumption failed? Document it in an ADR before proceeding. ADR-011 (stack safety) emerged from this — we didn’t plan it, we discovered it.

**Engineering:** Only after the epistemic work is solid. The code that survives is code that’s grounded in verified understanding.

The discipline that matters: **regression tests are sacred.** An unstable test process corrupts everything downstream. When Claude Code lost track after test failures, the fix wasn’t better prompting — it was separating the feedback loop so that test results were always dependable.

## What We Learned

**AI-assisted development scales.** 72K lines of zero-dependency C# with full W3C conformance, built in weeks. The velocity is real.

**AI-assisted development breaks predictably.** Infinite loops, stack overflows, test instability — these require human intervention. The pattern: Claude Code handles breadth, manual debugging handles depth. Know when to switch.

**Constraints enable velocity.** BCL-only dependencies. EBNF-derived parsers. W3C test suites as acceptance criteria. Every constraint reduces the search space for both human and AI.

**100% is a different category than 98%.** At 98%, users wonder which 2% will bite them. At 100%, the question shifts from “does it work?” to “what can we build with it?”

## What’s Next

**Sky Omega 1.0** will make Mercury production-ready:

- SPARQL LOAD wiring across CLI, MCP, and HTTP surfaces
- SPARQL Update sequences (semicolon-separated statements)
- USING / USING NAMED clause propagation
- Accurate SPARQL service description

**Sky Omega 2.0** will introduce the cognitive components:

- **Lucy** — semantic memory with temporal RDF and epistemic metadata
- **James** — tail-recursive orchestration loop
- **Sky** — LLM interaction layer
- **Minerva** — local inference substrate, BCL-only

The recursive quality remains: infrastructure for AI-assisted development, built using AI-assisted development, enabling AI-assisted reasoning.

## Verify It Yourself

```bash
# Clone
git clone https://github.com/bemafred/sky-omega.git
cd sky-omega

# Run W3C conformance tests
dotnet test —filter “W3C”

# Check for external dependencies
grep PackageReference src/Mercury/*.csproj

# Count lines
find src/Mercury -name “*.cs” -exec wc -l {} + | tail -1
```

MIT license. The code is the evidence.

——

**Repository:** https://github.com/bemafred/sky-omega