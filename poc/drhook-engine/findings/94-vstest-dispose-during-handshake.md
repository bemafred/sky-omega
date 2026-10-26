# Finding 94 — a kill during the VSTest handshake stalls `dotnet test` for 90 s; two tests synced on the clock

**Date:** 2026-10-26
**Status:** Root cause **PROVEN** (VSTest diag logs, process sampling and a falsification run on elite). **Fixed** in
both tests.

## Symptom

Two VSTest integration tests failed **deterministically on elite** (Linux x64, Ubuntu 26.04, 4 cores, 15 GB), and
passed on zbook and on the Mac:

- `AttachAndOwn_VstestTestHost_BriefWork_NaturalExit`
- `AttachAndOwn_VstestTestHost_PauseThenDisposeWithoutResume_WorkerSilentBreakOnly`

Both failed the same way: "VSTest dotnet-test bootstrap did not exit … within 10s after Dispose". They failed at
1.8.14 (`e93af01`) and at 1.8.15. They also failed when the suite ran alone, which rules out CPU contention from a
parallel `dotnet test` run.

## Root cause (proven)

1. **Neither a slow exit nor a DrHook hang.** With the wait raised to 90 s, `dotnet test` exited after **89.9 s with
   exit code 1**: the full VSTest connection timeout.
2. **The testhost was killed by Dispose mid-handshake.** VSTest's `--diag` logs, one run:
   - `09:03:45.46`: the testhost starts and connects to vstest.console. At `45.70` it receives `ProtocolVersion`.
   - `09:03:45.80`: vstest.console logs `Testhost processId: … exited with exitcode: 143`. 143 is SIGTERM, from
     DrHook's Dispose Stage 1, about 500 ms after attach. The `[Fact]` never started.
   - `09:05:15.69`: `Failed to negotiate protocol`, then `Exiting with exit code of 1`. vstest.console logged the host's
     exit but kept waiting for the version reply until its 90 s timeout.
3. **Both tests synced on the clock.** Each slept 500 ms after `AttachAndOwn`, then disposed (or paused, then
   disposed), assuming the `[Fact]` was "already wrapping up its brief work OR was already done". Where the kill lands
   depends on machine speed. On elite, 500 ms is inside the handshake. The VSTest tests that pass there Pause
   immediately, so their kill lands *before* the handshake starts, and vstest.console notices the exit at once.
4. **Falsification.** Moved to 3 s, the same kill lands after the handshake, and `dotnet test` exited **36 ms** after
   Dispose. Even at 3 s the testhost was still SIGTERMed rather than exiting by itself. The likely cause is that it was
   parked at the VSTEST_HOST_DEBUG hold's Break, which nobody resumed ([finding 91](91-vstest-host-debug-attach-race.md)).
   So the "natural exit" the first test is named for didn't happen on any machine.

## Fix

Sync on the target's progress, never the clock. The VSTest target's `[Fact]` carries a `TEST_RUNNING_HERE` marker on
its first statement. Both tests now do this:

1. `Pause`, which gives a stop however early the attach landed (finding 91).
2. Set a line breakpoint at the marker. It is **pending** until the test assembly loads, and the pump binds it on
   `LoadModule`.
3. Resume past any queued `Break` (the hold) or `Pause`, until the `Breakpoint` stop. Its hit proves the handshake is
   over and the `[Fact]` is running.

Then:

- **BriefWork_NaturalExit** resumes and waits for `ProcessExited`: a real natural exit while attached. Dispose has
  nothing to kill, and the test now also asserts `dotnet test` exit code 0.
- **PauseThenDispose** removes the breakpoint, resumes, and pauses inside the running `[Fact]`, then disposes without
  resuming, as before.

## Validation

- **elite:** integration suite 17/17 in five runs in a row (37 s each, down from 58 s with the two 10 s waits failing).
  No leftover testhost, vstest.console or target processes.
- **omega (macOS, M5):** integration suite 17/17 in three runs in a row.

## Open

- vstest.console's behaviour (logging the host's exit, then waiting out the 90 s handshake timeout anyway) is
  upstream (`microsoft/vstest`, testhost 17.13.0). It isn't filed. It only bites a harness that kills a testhost
  mid-handshake, which these tests no longer do.
