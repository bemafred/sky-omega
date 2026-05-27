# ADR-009: Test-debugging MCP surface — MTP-first launch + universal attach + project-aware mode selection

**Status:** Proposed — 2026-05-27

## Context

[ADR-007](ADR-007-teardown-concurrency-test-debug.md) Phase 3 ("Child-process attach substrate") was retired on 2026-05-27. Its premise — that the substrate would need OS-level process-tree observation to attach to a `testhost` descendant of `dotnet test` — was dissolved by two parallel realities:

1. **MTP-first strategic position.** Microsoft.Testing.Platform makes the test project itself the executable (no `vstest.console`, no `testhost` descendant). The existing substrate API `DebugSession.AttachAndOwn(pid, sink)` suffices; what's missing is the *project-inspection + executable-resolution + launch-coordination* surface above it. Consolidated in [`docs/architecture/technical/drhook-test-debugging-assessment.md`](../../architecture/technical/drhook-test-debugging-assessment.md).
2. **Phase 8 integration tests demonstrate end-to-end coverage.** `tests/DrHook.Engine.IntegrationTests/` covers both MTP (`TargetSpawn.Mtp` using documented `--debug`) and legacy VSTest (`TargetSpawn.Vstest` using `VSTEST_HOST_DEBUG=1` + stdout parse) end-to-end on macOS-arm64. 12/12 tests pass. The substrate works; the orchestration above it does not yet exist at the MCP-tool surface.

What remains unbuilt is therefore not engine-level substrate — it's the MCP-tool surface that lets an AI agent (or developer via CLI) say "debug this test" without already knowing the PID of the runner-spawned process. The assessment doc's "Recommended DrHook 1.8.x strategy" section specifies three modes:

1. **MTP direct-launch** (primary): `drhook debug-test --project <csproj> --filter <test>` — substrate inspects project, detects MTP, resolves test executable, launches under engine.
2. **Attach mode** (universal): `drhook attach-test --pid N` or `--select` — substrate enumerates candidate processes; caller picks; substrate attaches.
3. **VSTest env-var mode** (legacy compat): `drhook debug-test --project <csproj> --runner vstest` — substrate spawns `dotnet test` with `VSTEST_HOST_DEBUG=1`, parses PID from stdout, attaches.

This ADR scopes the work to expose those three modes as MCP tools (or one tool with explicit modes) on top of the existing substrate, and to ship the project-inspection + executable-resolution primitives the MTP path requires.

### What's NOT in scope

- **NCrunch process-tree attach** — [ADR-007](ADR-007-teardown-concurrency-test-debug.md) Phase 5 (probe 65) owns this. The MCP `drhook attach-test --select` from this ADR composes with `drhook_processes` (existing) for the manual NCrunch case; the *pattern-matched* / *auto-discovery* version is Phase 5.
- **Multi-process debug sessions** — assessment §"Multiple testhost processes" notes "initial support: one target with explicit selection." True simultaneous multi-session is ADR-007 Phase 5 (probe 64) substrate work.
- **Cross-platform validation** — design discipline cross-platform-shape from day 1; macOS-arm64 validated in this ADR; Linux + Windows in ADR-007 Phase 9.
- **`Microsoft.TestPlatform.TranslationLayer` adoption** — explicitly rejected per assessment §"BCL-only constraint: TranslationLayer analysis."
- **`drhook_step_test` removal** — owned by [ADR-007](ADR-007-teardown-concurrency-test-debug.md) Phase 7 (substrate-surface cleanup). This ADR provides the replacement tool(s); Phase 7's deletion of the old tool sequences after.

## Decision

Five accepted positions. All Proposed; subject to validation evidence from the increment work.

### Decision 1 — Three MCP-tool modes, one decision-tree, no flags

Expose the three modes as the assessment doc names them. Following the no-flags discipline ([ADR-007](ADR-007-teardown-concurrency-test-debug.md) Constraint 4 / [`feedback_no_behavior_flags`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_no_behavior_flags.md)), modes are surfaced as distinct MCP tools, not as flags on a generic `drhook_step_test`:

| Tool | Mode | Substrate path |
|---|---|---|
| `drhook_debug_test_mtp` | MTP direct-launch | `DebugSession.LaunchTestExecutable(...)` → existing `Launch` |
| `drhook_debug_test_vstest_legacy` | VSTest env-var mode | `DebugSession.SpawnVstestAndAttach(...)` → existing `AttachAndOwn` |
| `drhook_attach_test` | Attach mode | existing `drhook_processes` enumeration + `drhook_step_launch` composition (no new tool — composition already exists) |

Project inspection decides which of the two `drhook_debug_test_*` modes the caller invokes. The MCP tool name encodes the runner; the agent picks based on project shape. Detection helper: `drhook_inspect_test_project` returns `{mode: "mtp"|"vstest"|"unknown", reason: "...", executable: "..."|null}` — pure inspection, no debug session created.

### Decision 2 — Project inspection chain in substrate (BCL-only)

Project mode detection per assessment §"MTP detection":

1. SDK declaration: `<Project Sdk="MSTest.Sdk">` → MTP.
2. MSBuild properties: `EnableMSTestRunner` / `UseMicrosoftTestingPlatformRunner` → MTP.
3. Build output ground truth: `<ProjectName>.exe` (or `.dll` with apphost) → MTP.
4. Else → VSTest.

Implemented as BCL-only csproj XML parsing + filesystem inspection. No `Microsoft.Build` NuGet dependency; substrate-independence preserved per [ADR-009](../ADR-009-substrate-dependency-policy.md) (Sky Omega's substrate dependency policy ADR — distinct from this DrHook ADR with the same number; both ADR-009s coexist in their respective ADR families).

### Decision 3 — `EnumerateClrProcesses` substrate primitive (already partially shipped)

`drhook_processes` MCP tool exists and lists `.NET` processes (current implementation TBD — verify cross-platform shape during Increment 1). Substrate-level: `DebugSession.EnumerateClrProcesses() → IEnumerable<ClrProcessInfo>` exposes the same data programmatically for the `drhook_attach_test` selection workflow and for future Phase 5 NCrunch process-tree work.

`ClrProcessInfo` shape (proposed): `{ Pid, ExecutablePath, CommandLine, ParentPid, ClrVersion? }`. ClrVersion is opportunistic — `dbgshim`-detected if cheap, omitted otherwise.

Cross-platform back-ends: macOS `proc_listpids` + `proc_pidinfo`; Linux `/proc/*/{status,cmdline}`; Windows `CreateToolhelp32Snapshot`. Substrate has one interface, three back-ends.

### Decision 4 — VSTest legacy compat is documented exception, not feature

`drhook_debug_test_vstest_legacy` exists and uses `VSTEST_HOST_DEBUG=1`. The tool description explicitly names it "Legacy" and references the assessment doc. Future deprecation path: when MTP adoption is broad enough that VSTest legacy debugging is rare, the tool is removed via standard ADR process. No timeline commitment now.

This is the canonical answer to ADR-007 Constraint 2 ("no env-flag tricks"): MTP-first is the substrate's posture; env-flag mode is *named legacy and contained* in a single tool rather than diffused through the substrate.

### Decision 5 — Validation via integration tests, not new probes

The 12/12 Phase 8 integration tests already validate the substrate primitives `LaunchTestExecutable` and `SpawnVstestAndAttach` would wrap. New work for this ADR validates the *MCP-tool surface + project inspection chain*, not new substrate behavior. Validation tests live as additional integration tests in `tests/DrHook.Engine.IntegrationTests/` (or `tests/DrHook.Mcp.IntegrationTests/` if MCP-layer-specific tests warrant a separate project — decided in Increment 1).

No new probes in `poc/drhook-engine/`. The substrate is closed; this is orchestration layer work.

## Increments

### Increment 1 — Project inspection + executable resolution substrate

- `DrHook.Engine.TestProjects.Inspector` (or similar internal class): csproj parsing + MTP detection chain (Decision 2)
- `DrHook.Engine.TestProjects.ExecutableResolver`: given a csproj + target framework, returns the test executable path (BCL-only path resolution using build output conventions)
- Unit tests for both: feed synthetic csproj XML + filesystem layouts, assert mode + executable.
- MCP tool `drhook_inspect_test_project` exposes the result.

**Validation:** unit tests + manual MCP invocation against existing integration target csprojs (`tests/DrHook.Engine.IntegrationTargets.Mtp/*.csproj`, `tests/DrHook.Engine.IntegrationTargets.Vstest/*.csproj`).

### Increment 2 — `drhook_debug_test_mtp` MCP tool

- `DebugSession.LaunchTestExecutable(executablePath, args, sink)` — thin wrapper over existing `Launch` that sets up the substrate session correctly for MTP test exes.
- `drhook_debug_test_mtp` MCP tool: takes `project`, `filter`, `breakpoint` args; inspects project (Increment 1), resolves executable, launches under substrate, sets pending breakpoint, returns session ID.
- Existing stepping tools (`drhook_step_next/into/out/continue/pause`) compose with the new session as-is.

**Validation:** new integration test exercising `drhook_debug_test_mtp` end-to-end against the existing MTP integration target. Replaces the manual `TargetSpawn.Mtp` invocation that the current tests use.

### Increment 3 — `drhook_debug_test_vstest_legacy` MCP tool

- `DebugSession.SpawnVstestAndAttach(projectPath, filter, sink, vstestHostDebugTimeout)` — wrapper that spawns `dotnet test` with `VSTEST_HOST_DEBUG=1`, parses stdout for PID, attaches via `AttachAndOwn`. Includes the `TargetSpawn.ExtractPid` regex logic.
- `drhook_debug_test_vstest_legacy` MCP tool: takes same args as MTP variant, uses legacy substrate path, explicitly documents legacy status in tool description.
- Surface `TargetStuckHandshake` anomaly when stdout parse times out.

**Validation:** new integration test against the existing VSTest integration target. Replaces the manual `TargetSpawn.Vstest` invocation.

### Increment 4 — `EnumerateClrProcesses` substrate primitive + `drhook_processes` audit

- `DebugSession.EnumerateClrProcesses()` substrate method.
- Audit existing `drhook_processes` MCP tool: confirm cross-platform back-end, document `ClrProcessInfo` shape in tool response, add ParentPid + CommandLine if missing.

**Validation:** unit tests for each back-end (mock the OS surface where possible); manual validation against running .NET processes on macOS-arm64.

### Increment 5 — ADR-007 Phase 7 closure dependency

This ADR's Increments 2 + 3 provide the replacement tools for `drhook_step_test`. Once they ship, ADR-007 Phase 7 can remove `drhook_step_test` without leaving a capability gap. Cross-link from ADR-007 Phase 7 to this ADR's increments.

## Validation

This ADR moves **Proposed → Accepted** when:
- The three-mode framing (Decision 1) is reviewed and confirmed
- Project-inspection BCL-only feasibility is validated against representative csprojs (Increment 1 prototype)
- Phase 5/9 boundary (NCrunch + cross-platform) is agreed

It moves **Accepted → Completed** when:
- Increments 1–4 are done (each with code + tests + commit)
- ADR-007 Phase 7 closes `drhook_step_test` (Increment 5 cross-link)
- Integration tests use the new MCP tools end-to-end (not manual `TargetSpawn.*` invocations)
- Tool descriptions in `DrHookTools.cs` reflect the new surface; assessment doc updated to point at shipped tools rather than spec'd commands

## Consequences

**For substrate code:**
- New internal classes: `TestProjects.Inspector`, `TestProjects.ExecutableResolver`.
- New public API: `DebugSession.LaunchTestExecutable(...)`, `DebugSession.SpawnVstestAndAttach(...)`, `DebugSession.EnumerateClrProcesses()`.
- No changes to existing `AttachAndOwn` / `Launch` / `Attach` / `Dispose` semantics. ADR-008 lifecycle discipline applies to the new sessions identically.

**For MCP surface:**
- 3 new tools: `drhook_inspect_test_project`, `drhook_debug_test_mtp`, `drhook_debug_test_vstest_legacy`.
- 1 tool retired: `drhook_step_test` (via ADR-007 Phase 7 Increment 5 cross-link).
- 1 tool audited: `drhook_processes` — likely no surface change, possible response-shape additions.

**For integration tests:**
- New tests exercise `drhook_debug_test_mtp` + `drhook_debug_test_vstest_legacy` end-to-end. Existing tests' manual `TargetSpawn.*` invocations migrate to the new MCP tools.
- Optional: `tests/DrHook.Mcp.IntegrationTests/` project if MCP-layer tests warrant separation.

**For assessment doc:**
- The "Recommended DrHook 1.8.x strategy" section's CLI examples (`drhook debug-test --project ... --filter ...`) become shipped MCP tool descriptions. Update assessment doc post-Completed to point at shipped surface, mark prior content as historical.

## References

- [ADR-007](ADR-007-teardown-concurrency-test-debug.md) — substrate-correctness arc; Phase 3 retired 2026-05-27 in favor of this ADR; Phase 7 cleanup sequenced after this ADR's Increments 2-3.
- [ADR-008](ADR-008-process-lifecycle-discipline.md) — process lifecycle discipline applies to new sessions identically.
- [`docs/architecture/technical/drhook-test-debugging-assessment.md`](../../architecture/technical/drhook-test-debugging-assessment.md) — MTP-first strategic position; this ADR ships the substrate work the assessment specifies.
- [`docs/architecture/technical/drhook-test-debugging.md`](../../architecture/technical/drhook-test-debugging.md) — earlier analysis doc; superseded by the assessment.
- [finding 61](../../../poc/drhook-engine/findings/61-promotion-meta-probe.md) — Probe 46 outcome; MTP `--debug` switch as documented attach handshake.
- [finding 62](../../../poc/drhook-engine/findings/62-legacy-vstest-promotion.md) — Legacy VSTest path validated under the env-var mode.
- [`feedback_no_behavior_flags`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_no_behavior_flags.md) — discipline that drives Decision 1's distinct-tools-not-flags choice.
- [`feedback_no_vibe_coding`](../../../.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_no_vibe_coding.md) — discipline that drives Decision 5's "validation via integration tests, not new probes" choice (substrate is closed; orchestration ships through tests).
