# Finding 95 — the flood test's target exited before the test disposed; two tests synced on the clock

**Date:** 2026-10-26
**Status:** Root cause **PROVEN** (probe on omega-ubuntu, 3/3 runs). **Fixed** in both flood tests.

## Symptom

`InformationalFloodTest.AttachAndOwn_VstestTestHost_DisposeDuringFlood_NoWorkerExceptionOrSilentBreak` failed
**every run** on omega-ubuntu (Parallels VM, Ubuntu 26.04.1, linux-arm64, 4 cores, 8 GB) and passed on omega (macOS),
elite and zbook (Linux x64):

```
The only stop this target can produce is the VSTEST_HOST_DEBUG hold's Break.
expected: Break
actual:   ProcessExited
```

It was the first DrHook run on Linux arm64; the other 16 integration tests passed there.

## Root cause (proven)

After `AttachAndOwn`, the test waited up to 1 s for a stop, accepted only the hold's `Break` (or none), then slept
500 ms and disposed. A probe repeating that sequence on the VM, logging callbacks with timestamps and the target's
`dotnet test` output, gave the same result in 3 runs:

| ms after start | what happened |
|---:|---|
| ~270 | vstest.console prints the testhost's `Process Id`; attach |
| ~280 | attached; `CreateProcess` callback |
| — | **no `Break` callback**: the attach landed before the testhost reached its hold ([finding 91](91-vstest-host-debug-attach-race.md)) |
| ~1,140 | `ExitProcess`: the `[Fact]` ran (≈560 ms) and passed, and the testhost exited |
| ~1,140 | the 1 s `WaitForStop` returns `ProcessExited` |

So on this machine the flood (the `[Fact]`'s 10 × `Thread.Start`/`Join`) finished and the process exited inside the
test's wait. The test never disposed during a flood there: the assertion that failed was only its cheapest symptom.
The MTP variant had the same construction (sleep 200 ms, dispose) and passed by timing alone.

## Fix

Sync on the target, never the clock ([finding 94](94-vstest-dispose-during-handshake.md)'s rule). Both tests now:

1. `Pause`, for a stop however early the attach landed.
2. Set a line breakpoint on the work loop's `TEST_RUNNING_HERE` marker (added to the MTP target's `IdleTarget.cs`;
   the VSTest target had it since finding 94). It binds when the test assembly loads.
3. Resume past any `Pause` or `Break` until the `Breakpoint` stop: the target is in the work loop.
4. Remove the breakpoint, arm the sink, resume, and wait for the first `CreateThread` callback (a
   `ManualResetEventSlim`, waited with the test's cancellation token, no clock).
5. Dispose: mid-flood, with no stop pending.

The anomaly assertions (0 `WorkerException`, 0 `WorkerSilentBreak`) and the natural-exit check are unchanged.

## Validation

Integration suite 17/17 in every run: omega ×3, omega-ubuntu ×3, elite ×2, zbook ×2 (zbook is where the hold's `Break`
usually comes first, so both orderings are covered).

## Related, found the same day

- **`dotnet test` reports success when its front end gets SIGTERM.** Under Microsoft.Testing.Platform (SDK 10.0.301
  macOS, 10.0.113 linux-arm64), SIGTERM to the `dotnet test` process exits **0** with no summary; the testhost lingers
  idle ~15 s, then exits. SIGTERM/SIGKILL to the testhost, or Ctrl+C, gives a nonzero exit. A timeout or CI
  cancellation that SIGTERMs the front end would show a green run. Not reported upstream as of this date.
