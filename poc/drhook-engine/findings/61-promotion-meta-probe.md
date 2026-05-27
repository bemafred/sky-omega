# Finding 61: Probe 46 outcome — integration-test promotion mechanism (MTP exemplar)

**Status:**   PASSED on macOS-arm64 2026-05-24. Probe 46 (the meta-probe, ADR-007 Phase 2) validates that the substrate's existing `Launch`/`Attach` API + a real-shaped MTP integration target + `MSTest.Sdk`-based integration-test project together produce a working CI-shape integration test. One [TestMethod] (`AttachDisposeTest.AttachToMtpTarget_BriefIdle_Dispose_TargetSurvives`) lifts probe 42's substrate-validation shape into the integration-test mechanism. Test exe runs in 982 ms. **Closes Phase 2 for the MTP path; Probe 46b queued for the Legacy VSTest path.**
**Date:**     2026-05-24
**Scope:**    Phase 2 / Probe 46 strict scope per finding-54 discipline (each phase isolates one unsolved thing). MTP integration target only; Legacy VSTest target requires `DrHook.Testing` orchestration layer + env-var + stdout-parse work — that's Probe 46b, separate phase boundary, not Phase 2.

## Vocabulary lock-in (use these names everywhere)

The four-layer model from the conversation now committed. Every subsequent integration-test artifact uses these names:

| Layer | Name | Concrete artifact |
|---|---|---|
| 1 | **Integration test** | `[TestMethod]` in `tests/DrHook.Engine.IntegrationTests/` (MSTest.Sdk, MTP-based runner — itself a real MTP exe) |
| 2 | **DrHook substrate** | `src/DrHook.Engine/` — `DebugSession`, `CallbackPump`, `ManagedCallbackHost`, etc. The "engine" |
| 3 | **Integration target** | A real-shaped test-platform project under `tests/DrHook.Engine.IntegrationTargets.{Platform}/`. **Mtp** is the first; **Vstest** is Probe 46b's deliverable |
| 4 | **Target test code** | The `[TestMethod]` body inside the integration target — where breakpoints land. Rarely needs naming (the breakpoint location is just a line in test code) |

Plus the existing "probe target" term unchanged: `poc/drhook-engine/NN-target.cs` — the simple file-based or local-exe targets used by file-based probes 02–45. **Probe targets and integration targets are different artifacts** — probe targets are minimal substrate-validation scaffolds; integration targets are real-platform-shaped test projects.

## What was tested

Phase 2's unsolved thing per ADR-007: *"DrHook integration tests are shaped like X, prove Y, explicitly do not try to prove Z."* The exemplar answers all three at the MTP level.

### Shape (X)

Two new projects under `tests/`:

```
tests/
├── DrHook.Engine.IntegrationTargets.Mtp/   # Layer 3 — MTP integration target
│   ├── DrHook.Engine.IntegrationTargets.Mtp.csproj   # MSTest.Sdk/4.2.3 (version pin required, see Discovery #4)
│   └── IdleTarget.cs                                 # one [TestMethod], sleeps 30s
├── DrHook.Engine.IntegrationTests/         # Layer 1 — integration-test host
│   ├── DrHook.Engine.IntegrationTests.csproj         # MSTest.Sdk/4.2.3 + ProjectReference DrHook.Engine + ProjectReference Mtp target (build-only)
│   └── AttachDisposeTest.cs                          # one [TestMethod], lifts probe 42's attach+Dispose shape
```

Both projects use `MSTest.Sdk/4.2.3` (MTP-first per assessment doc — BCL-clean, sovereignty-preserving). `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>` is set on both per the .NET 10 SDK requirement.

### Proof (Y)

The integration test passes on macOS-arm64:

```
$ ./tests/DrHook.Engine.IntegrationTests/bin/Release/net10.0/DrHook.Engine.IntegrationTests

MSTest v4.2.3 (UTC 5/14/2026) [osx-arm64 - .NET 10.0.0]

Test run summary: Passed!
  total: 1
  failed: 0
  succeeded: 1
  skipped: 0
  duration: 982ms
```

The integration test:
1. Resolves the MTP integration target's exe path via `tests/.../bin/{config}/{tfm}/` walk.
2. Launches the target with `--debug` argument.
3. Parses `Process Id: NNNN` from target's stdout (MTP-native format — see Discovery #1).
4. `DebugSession.Attach(pid, sink)` — substrate validates here.
5. 200 ms observation window — pump initializes, drains setup callbacks.
6. `DebugSession.Dispose()` — substrate's detach-leave-running path (finding 59).
7. Assert `proc.HasExited == false` — target's [TestMethod] is mid-30s-sleep; substrate kept it alive.

### Not proved (Z)

- **Mass probe promotion (probes 41–45 → integration tests).** Phase 8's responsibility. The exemplar's shape is the rail Phase 8 will follow; Phase 8 hasn't executed.
- **Legacy VSTest integration target.** Requires the `DrHook.Testing` orchestration layer the assessment doc names (env-var management, stdout parsing for `Process Id: NNNN` from VSTest's undocumented format, attach to testhost rather than direct-launch). Different unknown; **Probe 46b**, queued.
- **NCrunch / IDE-runner-launched test process attachment.** Both Probe 46b territory (after Legacy path lands) and Phase 6+ territory.
- **`dotnet test` invocation.** See Discovery #2 — .NET 10.0.100 SDK doesn't yet expose `--use-mtp` flag or equivalent; the integration test exe must be invoked directly for now. Forward-state requires SDK update OR new wrapper script.
- **Multi-process / parallel integration tests.** Single test, single target. Substrate-correctness around parallel cases is finding-53/54 territory and not specifically tested in the integration-test layer here.

## Discoveries (unknown unknowns surfaced)

The two analysis docs (`docs/architecture/technical/drhook-test-debugging.md` + `docs/architecture/technical/drhook-test-debugging-assessment.md`) covered MTP-first strategy + VSTest env-var fallback + sovereignty + BCL-cleanliness. Probe 46's actual execution surfaced five additional concrete details that should land in the next revision of the docs.

### Discovery #1: MTP `--debug` is the documented attach-handshake — supersedes the assessment doc's recommendation

The assessment doc proposed parsing `Process Id: NNNN` from VSTest's `VSTEST_HOST_DEBUG=1` env-var output (undocumented + unstable format). For MTP the canonical mechanism is the built-in `--debug` CLI flag: any MTP test executable supports it, prints `Waiting for debugger to attach... Process Id: NNNN, Name: <name>` on stdout, and blocks until `Debugger.IsAttached` becomes true.

This is:
- **Documented** — `--help` lists it explicitly.
- **Stable** — part of MTP's CLI contract, not an env-var convention that shifts across versions.
- **Cleanest possible handshake** — no env-var management, no per-runner output-format negotiation. Just launch with `--debug`, parse PID, attach.

Implications:
- For MTP integration targets: no READY-PID handshake needed (no `Console.WriteLine($"READY {Environment.ProcessId}")` in the test method). MTP's `--debug` provides it.
- For the assessment doc's recommended `drhook debug-test` MCP tool surface: `--debug` is the MTP equivalent of `VSTEST_HOST_DEBUG=1`. The orchestration layer differs trivially between the two — MTP is a CLI flag, VSTest is an env var.
- For Probe 46b (Legacy VSTest): the env-var + stdout-parse complexity is genuinely Legacy-only. MTP doesn't need that orchestration.

**Substrate-design impact: none.** This is integration-test orchestration, not engine substrate. Documented for `DrHook.Testing` layer design.

### Discovery #2: `dotnet test` doesn't yet have native MTP invocation on .NET 10.0.100 SDK

Attempting `dotnet test path/to/Mtp.IntegrationTests.csproj` fails with:

```
error: Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on
.NET 10 SDK and later. If you use dotnet test, you should opt-in to the new dotnet test
experience. For more information, see https://aka.ms/dotnet-test-mtp-error
```

The aka.ms link suggests opt-in flags / env-vars. Tried `--use-mtp`: rejected as unknown switch. Setting `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>` in csproj didn't bypass this error either.

**Workaround:** invoke the integration-test exe directly:

```bash
./tests/DrHook.Engine.IntegrationTests/bin/Release/net10.0/DrHook.Engine.IntegrationTests
```

This is the canonical MTP execution model anyway — MTP test projects ARE executables. The `dotnet test` orchestration is a legacy convenience that's transitioning to MTP-native invocation in .NET 10+.

**For CI:** invoke the integration-test exe directly (or wrap in a small script that finds all `tests/*IntegrationTests/bin/{config}/{tfm}/*` exes and runs them).

**For developer-IDE workflows:** Rider's MTP support (per the assessment doc) handles this natively when running an MTP test from the Test Explorer.

**Substrate-design impact: none.** This is dotnet-CLI evolution, not engine. Documented for `DrHook.Testing` orchestration layer.

### Discovery #3: MTP captures per-test `Console.WriteLine`

The first attempt at the MTP integration target used a `Console.WriteLine($"READY {Environment.ProcessId}")` handshake (same pattern as file-based probe targets). It didn't work — MTP captures per-test stdout and emits it in the test results, not on the parent stdout stream.

Resolved by Discovery #1 (use MTP's `--debug` flag, not `Console.WriteLine`).

**Implication:** any future integration target that needs to communicate with the integration test from inside a [TestMethod] body must use:
- MTP-native mechanisms (`--debug`, test-result attachments), OR
- Out-of-band channels (file-based signaling, named pipes, env-var-encoded paths).

NOT `Console.WriteLine` — that's captured.

**Substrate-design impact: none.** Test-platform behavior, not engine.

### Discovery #4: `MSTest.Sdk` requires explicit version pin in csproj

`<Project Sdk="MSTest.Sdk">` (no version) fails `dotnet sln add` with `The SDK 'MSTest.Sdk' specified could not be found`. The SDK is a NuGet package, not a built-in `dotnet` SDK, so version pinning is required either:
- In the csproj: `<Project Sdk="MSTest.Sdk/4.2.3">`
- Or in `global.json` under `msbuild-sdks`: `{ "msbuild-sdks": { "MSTest.Sdk": "4.2.3" } }`

**Decision for this commit:** version pin in each csproj (no `global.json` exists). When more integration-target/integration-test projects land (Probe 46b, Phase 8 promotions), a `global.json` would centralize the version. Defer that until at least 3 projects share the pin.

**Substrate-design impact: none.** Build configuration.

### Discovery #5: Debugger-NEXT-to-debugger is the correct framing — confirmed in practice

The 4-layer model means the integration-test process (Layer 1, hosting DrHook substrate code) and the integration-target process (Layer 3, being debugged) are SEPARATE OS processes. They don't share mscordbi state. The substrate works against the target without any cross-process debugger interference.

The remaining "debugger-NEXT-to-debugger" concern (a developer attaches Rider/VS Code to the Layer 1 process while it's running) is captured here as a documentation item for any developer who wants to debug INTO an integration test:

> Do not attach Rider/VS Code to the `DrHook.Engine.IntegrationTests` exe while substrate code inside it is attached to an integration target. The Layer 1 debugger may pause `DrHook.CallbackPump`'s worker thread, which holds the single-consumer-of-`_events` invariant; pausing it stalls mscordbi's event dispatch for the target.

**Recommended developer workflow:** if a substrate behavior is unclear, write a probe (`poc/drhook-engine/NN-smoke.cs`) instead of stepping into the integration test. Probes are designed for interactive iteration; integration tests are for CI repeatability.

**Substrate-design impact: none.** Documentation discipline for developer-experience.

## Engine design impact from this exemplar

**None required.** Per the assessment doc's recommendation and confirmed by this probe: DrHook.Engine remains the debug substrate; `Launch`/`Attach` already cover the MTP direct-launch path (an MTP target exe is just an executable; substrate doesn't know or care it runs tests).

The `DrHook.Testing` orchestration layer the assessment doc names is where test-platform awareness (MTP detection, exe resolution, `--debug` arg construction, VSTest env-var management, NCrunch process discovery) belongs. That layer is **not** Phase 2's deliverable; it's downstream. For Phase 2's exemplar, we replicated the necessary orchestration directly in the integration test's `[TestMethod]` body — `--debug` arg, stdout parse, attach. That's fine for an exemplar; not the long-term shape.

**Long-term path** (post-Phase-2):
1. `DrHook.Testing` library — encapsulates MTP detection, exe resolution, `--debug`/env-var orchestration, PID discovery.
2. `DrHook.Mcp` exposes `drhook_debug_test` MCP tool that uses `DrHook.Testing`.
3. Phase 8 integration tests use `DrHook.Testing` instead of inline orchestration.

That's Phase 6+ work per ADR-007's existing sequence. Not Phase 2.

## Cross-references

- [ADR-007](../../../docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md) Phase 2, Probe 46.
- [`docs/architecture/technical/drhook-test-debugging.md`](../../../docs/architecture/technical/drhook-test-debugging.md) — first analysis doc (superseded).
- [`docs/architecture/technical/drhook-test-debugging-assessment.md`](../../../docs/architecture/technical/drhook-test-debugging-assessment.md) — second analysis doc, MTP-first strategy + sovereignty argument.
- [finding 59](59-detach-exit-race-outcome.md) — substrate's detach-leave-running for Attached (Phase 1); this integration test depends on it (asserts target alive after Dispose).
- [Memory `feedback_dont_compound_unknowns`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_dont_compound_unknowns.md) — the discipline rule that justified splitting MTP and Legacy VSTest targets into Probe 46 and Probe 46b.

## Phase 2 status

- **Probe 46 (MTP integration-test promotion exemplar):** ✓ PASSED (this finding).
- **Probe 46b (Legacy VSTest integration target + `DrHook.Testing` orchestration first piece):** ⏸ queued — must land before Phase 8 mass promotion.

Phase 2 is **partially closed** — MTP path done, Legacy path remaining. Phase 8 (mass probe promotion) does NOT begin until Probe 46b also passes.
