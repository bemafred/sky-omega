# Finding 62: Probe 46b outcome — Legacy VSTest integration-test promotion (community-utility path)

**Status:**   PASSED in isolation on macOS-arm64 2026-05-24. The Legacy VSTest path is substrate-validated: `dotnet test --no-build` with `VSTEST_HOST_DEBUG=1` spawns testhost, halts it, prints `Process Id: NNNN, Name: dotnet` on stdout; integration test parses PID, attaches via `DebugSession.Attach`, observes briefly, Disposes; testhost continues running its [Fact] (substrate's detach-leave-running for Attached holds). Test passes in ~1.1 s in isolation.
**Date:**     2026-05-24
**Numbering note:** Finding 62. **Phase 2 fully closed for substrate-correctness.** Probe 46 (MTP) and Probe 46b (Legacy VSTest) both validate the substrate against their respective real-platform-shaped integration targets.

## ⚠ Critical discovery for Phase 8: same-MSTest-exe substrate-attaching tests SIGBUS

**Running multiple substrate-attaching integration tests in the same MSTest exe crashes with SIGBUS (exit code 138).** Both Probe 46 and Probe 46b tests pass in isolation (~1 s each); running them together in the same MSTest invocation yields:

```
exit code = 138   # SIGBUS — bus error on macOS
=== Output ===
MSTest v4.2.3 (UTC 5/14/2026) [osx-arm64 - .NET 10.0.0]
(no further output — test runner crashed before reporting any test result)
```

Orphaned children (`testhost`, `dotnet test`) get reparented to init (PPID 1). Process tree is left in a state requiring explicit cleanup.

### Suspected cause

Substrate per-process state pollution. Each test's `DebugSession.Attach` loads `libdbgshim` via `DbgShim.Load`, creates an `ICorDebug` instance, registers managed callbacks, etc. `Dispose` releases these but mscordbi / libdbgshim per-process state (mapped memory, COM proxy tables, GCHandles, callback registrations) may not fully reset within the same hosting process between tests.

Aligned with finding 59's MCH-RE-1 discovery (same-TARGET re-attach hits mscordbi accumulation after ~2 cycles). MCH-RE-1 was about repeated attaches to the *same target*; this is repeated attaches to *different targets* from the same *host process*. Likely related substrate-grade phenomenon.

### Workaround for Phase 2 + Phase 8

**One substrate-attaching test per MSTest exe invocation.** Three implementation options:

- **CI filter-per-invocation** (lowest sprawl). One IntegrationTests project; CI script invokes once per test:
  ```bash
  ./DrHook.Engine.IntegrationTests --filter "FullyQualifiedName~AttachToMtpTarget"
  ./DrHook.Engine.IntegrationTests --filter "FullyQualifiedName~AttachToVstestTestHost"
  ```
  Each invocation gets a clean substrate state.

- **Project-per-test** (most isolation). `DrHook.Engine.IntegrationTests.Mtp/` + `DrHook.Engine.IntegrationTests.Vstest/`. Heavy sprawl when Phase 8 promotes probes 41–45 (would mean 5+ projects).

- **Process-spawn-per-test** (custom test framework wrapper). Each `[TestMethod]` actually spawns a child process to do the substrate work; parent process is the test runner, never touches mscordbi. Substantial engineering.

**Chosen for now:** CI filter-per-invocation (option 1). Documented in finding 62 + adds to Phase 8 prerequisites.

### Substrate investigation deferred

The "why does same-host-process substrate-attaching twice SIGBUS?" question is substrate-grade work but out of Phase 2's scope. Possible investigation paths for the future:

- Probe whether explicit `DbgShim.Dispose` + `GC.Collect` + `Thread.Sleep` between tests resolves the crash → if yes, substrate cleanup is incomplete.
- Probe whether AppDomain or process isolation is required for second-Attach to succeed → if yes, substrate has process-global state that's not cleanable.
- Probe whether the SIGBUS originates in libdbgshim, mscordbi, or DrHook's CCW (likely libdbgshim or mscordbi based on native-level signal).

**Engineering follow-up surfaced (BLOCKING for Phase 8):** **MCH-RE-2** — same-host-process multi-substrate-session SIGBUS. Must be characterised + bounded before Phase 8 can deliver probes-41-45-as-integration-tests in a single project.

## What was tested (substrate path)

ADR-007 Probe 46b's mandate: *"Stand up tests/DrHook.Engine.IntegrationTargets.Vstest/ (classic VSTest test project, not MSTest.Sdk). Build the first piece of DrHook.Testing orchestration: spawn dotnet test with VSTEST_HOST_DEBUG=1, capture stdout, parse Process Id: NNNN from VSTest's undocumented format, attach to testhost. ONE integration test against the Legacy target."*

### Layer 3 — Legacy VSTest integration target

`tests/DrHook.Engine.IntegrationTargets.Vstest/`:
- `Microsoft.NET.Sdk` (NOT `MSTest.Sdk` — classic VSTest shape).
- `<IsTestProject>true</IsTestProject>` for tool integration.
- PackageReferences: `Microsoft.NET.Test.Sdk 17.13.0`, `xunit 2.9.3`, `xunit.runner.visualstudio 3.0.1`.
- One `[Fact]` `IdleFact.IdleForDebuggerObservation` — sleeps 30 s.

Real classic VSTest community shape — the long-tail consumer pattern DrHook must support.

### Layer 1 — Integration test

`tests/DrHook.Engine.IntegrationTests/VstestAttachDisposeTest.cs`:
- `[TestMethod]` `AttachToVstestTestHost_BriefIdle_Dispose_TestHostSurvives`.
- Spawns `dotnet test <legacy target> --no-build -c Release --nologo` with `VSTEST_HOST_DEBUG=1` in the env block.
- Reads stdout, parses `Process Id:\s*(\d+)` (same regex as Probe 46's MTP test — VSTest's format is identical to MTP's `--debug` here, captured empirically).
- `DebugSession.Attach(pid, NullSink)`, 500 ms observation window, `Dispose()`, 200 ms settle.
- Asserts testhost is alive via `Process.GetProcessById(pid).HasExited == false`.
- Finally: `proc.Kill(entireProcessTree: true)` to clean dotnet-test + vstest.console + testhost.

## Captured discoveries

### Discovery D6: `VSTEST_HOST_DEBUG=1` stdout format on .NET 10.0.100 SDK

Empirically captured:

```
Host debugging is enabled. Please attach debugger to testhost process to continue.
Process Id: NNNN, Name: dotnet
```

The "Name: dotnet" is because testhost runs as `dotnet exec testhost.dll`, not as a named `testhost.exe`. Format is **identical regex shape** to MTP's `--debug` (`Process Id:\s*(\d+)`); the same parser works for both paths.

Assessment doc characterised the format as "undocumented and shifts across versions" — captured here as a baseline for .NET 10.0.100. Future SDK versions may shift; the parser should accommodate variants when seen.

### Discovery D7: Process tree depth for Legacy VSTest path

```
DrHook.Engine.IntegrationTests   (Layer 1, MSTest.Sdk MTP exe)
  └── dotnet test                 (intermediate orchestrator we spawned)
        └── vstest.console        (intermediate runner)
              └── testhost.dll    (Layer 3 — DrHook attaches HERE)
                    └── xUnit adapter executes [Fact]
```

3 intermediate processes between Layer 1 and Layer 3 (vs MTP's direct Layer-1-to-Layer-3 launch). Process-tree cleanup is correspondingly more careful — `Process.Kill(entireProcessTree: true)` on the dotnet-test process must propagate through 3 levels. Observed reliable on macOS-arm64; behavior on Linux/Windows is Phase 9 verification.

### Discovery D8: PPID-1 orphan risk when test-exe crashes

When the integration test exe (Layer 1) crashes mid-spawn-chain (e.g., the SIGBUS above), `dotnet test` and `testhost` get reparented to init (PPID 1). They survive the crash and continue running until natural exit or external kill. CI hygiene must include orphan cleanup for the Legacy path. Not a concern for the MTP path (direct exe launch — no intermediate).

## Engine design impact

**Per-probe: none.** Substrate's existing `Launch`/`Attach` handle the Legacy testhost identically to MTP's direct exe — testhost is just a CLR process from the substrate's view.

**Substrate-investigation (MCH-RE-2): blocks Phase 8.** The same-host-process multi-substrate-session SIGBUS is a NEW substrate-grade issue, surfaced by integration-test promotion. Phase 8's deliverable assumes substrate is reusable across test cases; that assumption is violated. Must investigate before delivering probes 41-45 as integration tests.

**`DrHook.Testing` orchestration layer (assessment doc): still deferred.** Phase 2's exemplar inlined the orchestration (env-var + spawn + parse) in the integration test itself. Phase 6+ extracts to `DrHook.Testing` when needed.

## Cross-references

- [ADR-007](../../../docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md) Phase 2, Probe 46b.
- [finding 61](61-promotion-meta-probe.md) — Probe 46 outcome (MTP path); shared vocabulary + 5 prior discoveries.
- [finding 59](59-detach-exit-race-outcome.md) — substrate's detach-leave-running (Attached); MCH-RE-1 (same-target re-attach mscordbi accumulation; MCH-RE-2 here is the same-host-process analogue).
- Assessment doc [`docs/architecture/technical/drhook-test-debugging-assessment.md`](../../../docs/architecture/technical/drhook-test-debugging-assessment.md) — MTP-first strategy + Legacy VSTest env-var fallback.

## Phase 2 status — CLOSED with caveat

- **Probe 46 (MTP integration-test promotion exemplar):** ✓ PASSED (finding 61).
- **Probe 46b (Legacy VSTest integration-test promotion exemplar):** ✓ PASSED in isolation (this finding).
- **MCH-RE-2 (same-host-process multi-substrate-session SIGBUS):** **NEW BLOCKER for Phase 8 mass promotion.** Must investigate + characterize + bound before delivering probes 41-45 as in-process integration tests. Workaround for now: one test per MSTest exe invocation (CI filter).

Phase 2 is **closed for the substrate-correctness validation**: both the MTP and Legacy VSTest paths are substrate-validated, the promotion mechanism is documented, and the Phase 8 prerequisite (MCH-RE-2 investigation) is explicit. Phase 8 mass promotion does NOT begin until MCH-RE-2 is resolved.
