# Finding 90 — VSTest integration tests orphaned a debugger-waiting testhost per run; a process-group kill reaches it

**Date:** 2026-10-03
**Status:** Root cause **PROVEN** (orphan attached → immediate `Break`); **fix IMPLEMENTED + validated** (harness).

## Symptom

`drhook_processes` listed **346 orphaned VSTest testhosts** (`dotnet exec … DrHook.Engine.IntegrationTargets.Vstest …`,
PPID 1, state S) holding **17 GB RSS** — accumulated at ~3–4 per `DrHook.Engine.IntegrationTests` run over one day of
repeat-run gates. The oldest came from the first suite run of the day, before any of the day's engine changes.
Per-test isolation: `PauseDuringStoppingFlood` and `ThrowingSink` leaked one each; a standalone replica of the
ThrowingSink sequence leaked in 5 of 6 runs.

## Root cause (proven)

- The leaked process was **not the testhost DrHook attached to** — the attached one always died. The orphan was a
  *different* testhost, its `--parentprocessid` pointing at the same run's `vstest.console`: vstest.console launches a
  **second** testhost during the run.
- That testhost inherits `VSTEST_HOST_DEBUG=1`, so it parks waiting for a debugger (main thread in `Monitor.Wait`).
  Proof: attaching to an orphan produced an immediate **`Break`** stop — it was sitting in VSTest's debug-wait loop.
- vstest.console then exits; the waiting testhost is reparented to launchd and **leaves the bootstrap's process
  tree**, so the tests' `dotnetTest.Kill(entireProcessTree: true)` (finding 68 / probe 54: correct for a *tree*) never
  reached it. Nothing ever attaches, so it waits forever.

## Fix

`TargetSpawn.Vstest` starts `dotnet test` as a job in its **own process group** (`/bin/sh -c 'set -m; dotnet "$@" &
echo "DRHOOK_PGID $!"; wait $!'` — under `set -m` the background job's pgid is its pid; verified on macOS `/bin/sh`)
and returns a `VstestRun` (`Shell`, `ProcessGroup`). `KillGroup()` / `Dispose()` send `SIGKILL` to `-pgid`. A process
group **survives reparenting** — the orphans observed kept their original pgid — so the group kill reaches every
process the run started, including the debug-waiting testhost. All six VSTest tests use it (`using VstestRun run`,
`run.KillGroup()` in `finally`).

## Validation

- 5 consecutive full integration runs: 16/16 each, **0 orphaned testhosts** after every run (was ~3–4 per run).

## Open (related, pre-existing, not fixed here)

- `AttachAndOwn_VstestTestHost_GetLocalsExcessiveDepth_DepthClampedAnomalyFires` **fails deterministically when run
  in isolation** (first stop `ProcessExited`, not `Break`) yet passes inside the full suite; it fails the same way on
  the session-start commit (`2ab2212`), so it predates the 2026-10-03 changes. A standalone replica showed the
  VSTest debug hold is **racy** against attach (one run stopped on `Break`, another ran the test to completion).
  **Hypothesis (unverified):** with two testhosts per run, the test sometimes attaches to the one that does not hold —
  `ExtractPid` takes the first `Process Id:` line. To probe next.

## References

- `tests/DrHook.Engine.IntegrationTests/TargetSpawn.cs` (`Vstest`, `VstestRun`), the six VSTest tests.
- Finding 68 (probe 54: SIGTERM does not cascade; tree-kill does — but only over the *current* tree), finding 62
  (first sighting of PPID-1 testhosts, in the SIGBUS context), finding 87 (the same family: harness topology vs.
  process lifecycle).
