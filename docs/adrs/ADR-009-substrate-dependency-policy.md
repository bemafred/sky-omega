# ADR-009: Substrate Dependency Policy — Four-Axis Admission Rule

**Status:** Accepted — 2026-05-19 (clarified 2026-05-21 — native runtime-substrate assets, see Clarification section)

## Context

Sky Omega's three substrates — Mercury (knowledge), Minerva (thought), DrHook (runtime observation) — have followed an informal "BCL only" rule: no NuGet references in substrate `.csproj` files. Mercury and Minerva implement this strictly. DrHook v1 does not — it references `Microsoft.Diagnostics.NETCore.Client`, which has sat as a latent anomaly since the substrate was integrated into sky-omega on 2026-04-06.

The intuition behind "BCL only" was substrate independence — avoid external version evolution, deprecation, and naming churn we don't control. But "BCL only" interpreted as `System.*` literally turns out to be too brittle a line. Three reasons surfaced in the 2026-05-18 / 2026-05-19 review:

1. **`System.*` itself isn't an absolute stability guarantee.** Parts of `System.Web.Mail`, `System.AppDomain`, `System.Remoting`, and `System.Configuration` have been deprecated, moved, or had API breakage across .NET versions. BCL is a strong line, not an immovable one.

2. **The "trust nothing" reductio.** Pure substrate independence would mean writing our own .NET runtime. We don't, and we shouldn't. Sky Omega rests on .NET as a substrate of its own — the question isn't whether to depend on Microsoft, but where the substrate-relevant boundary lies inside that dependency.

3. **Same-vendor doesn't mean same-stability — but it also doesn't mean same-risk-as-third-party.** `Microsoft.Diagnostics.NETCore.Client` (owned by the `dotnet/diagnostics` team, runtime-adjacent, versioned in lockstep with .NET releases, wire-protocol versioned via `DOTNET_IPC_V1` magic) is qualitatively different from netcoredbg (Samsung, separate cadence, no protocol stability mechanism). Treating both as equally "excluded" produces engineering effort that doesn't actually improve substrate independence.

The discipline question this ADR settles: **what is the rule for deciding whether a NuGet can be admitted to a substrate?**

## Scope

This policy applies to:

- **Substrate code** — Mercury, Minerva, DrHook (current substrates; future substrates by extension).
- **Cognitive component code** — Lucy, James, Mira, Sky. The substrate-independence value carries forward into the components built on substrates.

Out of scope (permissive policies apply):

- **Test projects** (`*.Tests/`) — may use xUnit, FluentAssertions, and similar test-frame NuGets.
- **Benchmark projects** (`benchmarks/`) — BenchmarkDotNet is admitted by convention.
- **Examples** (`examples/`) — illustrative code, not substrate.
- **Tools and dev infrastructure** (`tools/`, `scripts/`) — operational tooling, separate from substrate sovereignty.

The reason for the substrate / cognitive-component scope is that those components are what Sky Omega *ships*. Dev-time infrastructure can use whatever serves the discipline of building and validating the shipping substrate.

## Clarification (2026-05-21): native runtime-substrate assets are not managed dependencies

The four-axis test governs **managed NuGet dependencies** — packages whose managed API surface the substrate binds to, carrying their own versioning, naming, and package semantics. It does **not** govern **native runtime-substrate assets**: the .NET runtime's own native components (`libcoreclr`, `libdbgshim`, `libmscordbi`, the apphost), the operating system, and the hardware. These are the platform Sky Omega *executes on*, reached via P/Invoke — the same category as Minerva's direct hardware access. They are depended-upon the way the OS kernel and the CPU are: not "admitted" through any test, because there is no alternative and no meaningful "walk away." They are below the dependency line — they are the substrate itself.

**Surfaced by DrHook.Engine probe 02 (2026-05-21).** `dbgshim` — the native shim that bridges to `ICorDebug` — is absent from the .NET runtime install for .NET 7+; it moved to `dotnet/diagnostics` and now ships via the `Microsoft.Diagnostics.DbgShim[.<rid>]` NuGet. But that NuGet is a **redistribution vehicle for a native binary** (`runtimes/<rid>/native/libdbgshim.dylib`), not a managed API the substrate references. The engine `dlopen`s / P/Invokes `libdbgshim`; it binds to no managed surface. dbgshim is therefore part of the .NET debugging substrate — the same category as `libcoreclr` — and is relied upon as platform, not admitted via the four axes. As put during the 2026-05-21 review: *we have no choice but to rely on it; it must be viewed as very much a part of .NET itself.*

**Why axis 4 (replaceability) does not apply.** Replaceability tests whether a *dependency* can hold us hostage. A runtime-substrate asset cannot be meaningfully reimplemented — you cannot rewrite `libcoreclr`, the kernel, or the debugging shim — and that impossibility is exactly what marks it as platform rather than dependency. Substrate independence was never about avoiding the platform (that would mean writing our own runtime — the Context #2 reductio); it was about avoiding application-framework dependencies with their own semantics.

**The distinction in one line:** bind to a *managed API* → it faces the four axes; P/Invoke a *native binary that is part of the runtime / OS / hardware* → it is platform substrate and faces none.

## Decision

Sky Omega substrates and cognitive components use `System.*` and platform P/Invoke by default. **A NuGet may be admitted only if it satisfies all four of the following axes.** Each admission is decided per-NuGet and recorded in an ADR — this one for the inaugural admission, or a successor ADR for future admissions. Default is exclusion. Admission requires explicit justification on every axis.

### Axis 1 — Origin

The NuGet must be **Microsoft-owned, by the .NET runtime team or a directly adjacent infrastructure team.** Concrete admissible origins:

- `dotnet/runtime` — the runtime, BCL, base libraries
- `dotnet/diagnostics` — diagnostic IPC, EventPipe tooling, dump generation
- `dotnet/symstore` — symbol store and source-link infrastructure
- `dotnet/command-line-api` — runtime-adjacent argv infrastructure (when stable)

Concrete excluded origins:

- Application-stack teams: `aspnetcore`, `efcore`, `machinelearning`, `extensions` (`Microsoft.Extensions.*`)
- Azure / Power Platform / Office teams
- Third-party teams regardless of license — Samsung's netcoredbg, JetBrains, etc.

**Rationale:** runtime-team ownership means the package's evolution is tied to runtime evolution. Application-stack teams pivot independently of runtime cadence; same vendor does not imply same release lifecycle.

### Axis 2 — Shape

The NuGet must **extend a runtime or platform primitive** that BCL doesn't surface — "lets you reach a capability you can't get from `System.*` alone." It must **not impose a design pattern**, opinion, or framework.

Extending (admissible):

- Access to a runtime-level protocol — diagnostic IPC, EventPipe NetTrace, perf counters
- Access to a kernel-level capability — kernel events, eBPF, ETW
- Access to a platform primitive — Windows registry, mach kernel APIs not covered by `System.*`

Imposing (excluded):

- DI containers, IoC frameworks (e.g., `Microsoft.Extensions.DependencyInjection`)
- Logging abstractions and provider patterns (e.g., `Microsoft.Extensions.Logging`)
- Configuration frameworks (e.g., `Microsoft.Extensions.Configuration`)
- Object-relational mappers, query builders (e.g., `EntityFrameworkCore`)
- Web frameworks, middleware, hosting models (e.g., `AspNetCore`)
- Serialization with opinion (most JSON converters that bind to a worldview)

**Rationale:** extending primitives doesn't bind us to a design philosophy. Imposing patterns does. Substrate code that imports `Microsoft.Extensions.DependencyInjection` inherits a specific philosophy of dependency injection that becomes part of the substrate's identity. Substrate code that imports `Microsoft.Diagnostics.NETCore.Client` inherits no philosophy — it gets access to a wire protocol it could not otherwise reach.

### Axis 3 — Stability mechanism

The dependency must have **explicit versioning in the dependency itself**, not just vendor reputation:

- Wire-protocol versioning (e.g., `DOTNET_IPC_V1` magic string; intended `V2` if breaking changes occur)
- Documented semantic versioning on public types
- Documented deprecation path with announced support windows

**Rationale:** vendor reputation is necessary but not sufficient. A package with a wire-protocol version field can be reasoned about across versions; a package whose stability is "Microsoft probably won't break it" is a hope, not a contract. Sky Omega substrates are built to outlast individual .NET releases — they need the contractual mechanism, not the hope.

### Axis 4 — Replaceability

The dependency must be **reimplementable from public spec in bounded effort.** Our use of the dependency is a simplification choice for v1, not a long-term moat.

Test: if Microsoft deprecated or abandoned the package tomorrow, could we reimplement equivalent functionality from publicly-available specs in **2–6 weeks** of focused work? If yes, the dependency is a v1 simplification we can walk back. If no, we are locked in and the admission is rejected.

**Rationale:** substrate independence is the destination; v1 admission is the staging. The "could rewrite it" option is what preserves the substrate from being held hostage to vendor decisions, even when we choose not to exercise the option. A dependency that fails this axis is structurally captured; a dependency that passes it is voluntarily simplified.

### Admission record format

Each admission ADR (this one or a successor) includes a four-axis evaluation table:

```markdown
## Substrate dependency admission: <PackageName>

| Axis | Justification |
|---|---|
| 1. Origin | <org/team owner of the NuGet> |
| 2. Shape | <"extends X primitive" — required form> |
| 3. Stability | <protocol or semver mechanism, cite documentation> |
| 4. Replaceability | <effort estimate to reimplement from spec> |

**Admitted under:** ADR-009 substrate dependency policy.
**Substrate(s) using:** <list>.
**v1 vs destination:** <"keep" or "v1 simplification, native in v2 driven by X">.
```

This record lives in the ADR that admits the dependency — either inline (this ADR for the inaugural admission) or in a successor ADR's body.

## Consequences

### Immediate admission: `Microsoft.Diagnostics.NETCore.Client`

| Axis | Justification |
|---|---|
| 1. Origin | `dotnet/diagnostics`, runtime-team-adjacent. Versioned in lockstep with .NET releases. |
| 2. Shape | **Extends** — provides client access to the Diagnostic IPC protocol and EventPipe NetTrace format that aren't reachable from `System.*` alone. Does not impose a debugger or observability pattern; the substrate (DrHook.Engine) decides what to do with the wire-level access. |
| 3. Stability | Wire protocol versioned via `DOTNET_IPC_V1` magic (V2 would change the magic, return `DS_IPC_E_UNKNOWN_MAGIC`). C# public surface semver-versioned. Both mechanisms documented in `dotnet/diagnostics/documentation/design-docs/ipc-protocol.md`. |
| 4. Replaceability | Reimplementable from the public IPC protocol spec in ~1–2 weeks for Layer 1 (Diagnostic IPC client) and Layer 2 (EventPipe NetTrace parser) combined. Survey at `poc/drhook-engine/findings/01-ipc-protocol-survey.md` confirms the spec is sufficient. |

**Admitted under:** this ADR.
**Substrate(s) using:** DrHook.Engine (planned; current DrHook v1 already uses it informally — this admission canonizes it).
**v1 vs destination:** **keep.** The dependency satisfies all four axes; substrate independence is not improved by reimplementing it. If a substrate-driven reason surfaces later (version drift, an unhandled quirk, vendor deprecation, or a substrate-correctness investigation that requires owning the protocol decoder), the public spec makes the rewrite tractable on the timeline established under axis 4.

### Immediate exclusion: netcoredbg (codifies existing state)

| Axis | Justification |
|---|---|
| 1. Origin | Samsung, MIT-licensed, separate maintenance cadence from .NET runtime. Fails. |
| 2. Shape | Imposes a DAP-server-plus-ICorDebug-consumer architecture. Fails. |
| 3. Stability | Loose semver on its own surface; the underlying ICorDebug surface it consumes has no explicit stability mechanism beyond Microsoft's general COM-ABI promises (which are about the *interface*, not netcoredbg's use of it). macOS/ARM64 maintenance dormant since 2023 — empirical evidence that the stability mechanism is insufficient. Fails. |
| 4. Replaceability | The replacement *is* native ICorDebug interop — substantial work, but the only path that produces a substrate-grade runtime-inspection layer. Passes the replaceability axis; fails because three preceding axes already exclude it. |

**Conclusion:** netcoredbg remains excluded. The DrHook.Engine substrate work concentrates on native ICorDebug interop — Layer 3 of the original PoC design. That's the layer where substrate independence actually matters.

### Downstream effects

**DrHook.Engine scope narrows.** ADR-006 (DrHook engine) will be amended to reflect:

- **Layer 1 (Diagnostic IPC client):** use `Microsoft.Diagnostics.NETCore.Client` as substrate-admitted dependency. No native reimplementation under v1.
- **Layer 2 (EventPipe NetTrace parser):** same — use the NuGet's `EventPipeSession` / `EventPipeEventSource` surface.
- **Layer 3 (ICorDebug native interop):** the substrate work. BCL-only with P/Invoke, replaces netcoredbg, is where the substrate-independence claim is paid for.

Engine v1 scope reduction: an estimated 60–70% of the originally-planned protocol-replacement work moves from "substrate engineering" to "substrate-admitted dependency."

**`poc/drhook-engine/` scope narrows** to Layer 3 emergence. The IPC protocol survey at `findings/01-ipc-protocol-survey.md` is retained as **characterization of the admitted dependency's protocol**, not as implementation prep. Probe 01 (BCL-only Diagnostic IPC round-trip) is no longer load-bearing; the equivalent operation is `DiagnosticsClient.GetProcessInfo()`.

**Mercury and Minerva are unaffected.** Their substrates have zero NuGets because no candidate has cleared the four axes for them — not because of the strict-BCL line. The policy is permissive, not prescriptive. Each substrate decides per-candidate; neither Mercury nor Minerva has surfaced one warranting admission.

### Forward effects

The policy enables future cases without re-litigating the framing:

- If `System.CommandLine` reaches GA, it can be admitted to substrates that benefit from richer argv parsing — passes axis 1 (`dotnet/command-line-api`, runtime-adjacent), axis 2 (extends), axis 3 (semver), axis 4 (parsing is well-understood).
- If a Windows-port substrate needs registry access, `Microsoft.Win32.Registry` is admittable — axes 1–3 trivial; axis 4 trivial (registry via P/Invoke is ~100 lines).
- `Microsoft.Extensions.DependencyInjection` remains excluded. Substrates that need composition do it manually or via custom factories. The cost of writing that is the price of not inheriting a DI philosophy into the substrate identity.

### What this policy does NOT change

- **Mercury and Minerva's existing zero-NuGet stance.** They have it because no candidate has cleared the four axes for them. The policy isn't a license to add NuGets — it's a discipline for deciding.
- **The destination.** Substrate independence remains the long-term value. The policy admits dependencies *that don't compromise it* under the four-axis test. A package that passes does not compromise the property; the substrate could walk away from it in bounded effort.
- **The ADR-back-reference requirement.** Every admission ties to an ADR that documents the axis evaluation. Admissions cannot slip in via build-time NuGet adds without a paper trail.
- **The DrHook v1 → DrHook.Engine transition.** DrHook v1's continued use of the NuGet is no longer an "anomaly to fix" but a **substrate-admitted dependency.** The actual driver of the engine rewrite — replacing netcoredbg with native ICorDebug — remains.

## Alternatives considered

**Strict BCL-only (`System.*` literally).** Rejected: too brittle. Forces reimplementation of substrate-irrelevant infrastructure (e.g., the Diagnostic IPC client) for the sake of a categorical line. Engineering effort is spent without measurable improvement in substrate independence. Mercury and Minerva have implemented this line; DrHook v1 did not, and the resulting anomaly produced 6 weeks of latent churn (the original DrHook.Engine concept, the routing asymmetry between Mercury and DrHook MCP servers, the ADR-006 draft).

**Free-for-all on Microsoft-shipped NuGets.** Rejected: erases the line between substrate primitives and application frameworks. Would admit `Microsoft.Extensions.DependencyInjection` and lock substrates to a specific DI philosophy, defeating the substrate-independence value at its core.

**Trust-by-vendor (any Microsoft NuGet is allowed).** Rejected: conflates vendor reputation with substrate-grade dependency fitness. Microsoft has shipped and deprecated app-stack packages on independent timelines (Silverlight is the easy example, but more usefully: parts of WCF on Linux/Mac, WPF on Mac, parts of WinForms cross-platform). Same vendor isn't enough; the package's *position within the vendor* matters.

**Case-by-case judgment without a written rule.** Rejected: the original DrHook v1 NuGet decision was case-by-case, and the result was a latent anomaly that consumed substantial discussion in 2026-04-17 (the original DrHook.Engine concept), 2026-04-30 (the substrate-discipline framing in ADR-006), 2026-05-16 (the deferred-to-1.8.x roadmap amendment), and 2026-05-18 / 2026-05-19 (this policy review). A rule eliminates recurrence of that pattern by making the question answerable on first contact.

## References

- `docs/adrs/drhook/ADR-006-drhook-engine.md` — first ADR to be amended under this policy; engine scope narrows to Layer 3
- `docs/limits/drhook-testability.md` — substrate testability constraint, independent of this policy
- `poc/drhook-engine/README.md` — PoC scope narrowing follows from this policy
- `poc/drhook-engine/findings/01-ipc-protocol-survey.md` — characterization of the admitted dependency's wire protocol
- Mercury session 2026-05-17 decision `drhook-engine-poc-direction` — pre-policy framing; the substrate-independence intent stands, the strict-BCL operationalization is superseded by this ADR
- Mercury session 2026-05-19 (this date) — policy decision recording
- Mercury obs 2026-04-06 `dh-001` and the dh-010 correction (2026-05-17) — the trust-but-verify pattern that motivated the explicit axis-3 stability-mechanism test
- `CLAUDE.md` — "Mercury has no external dependencies (BCL only)" — the original framing this ADR refines
- `AI.md` — substrate-independence narrative for external readers
