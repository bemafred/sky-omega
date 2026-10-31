# ADR-016: Deterministic Target Teardown Across Platforms — Characterize mscordbi on Each Platform, Natively, Before Changing DrHook

**Status:** Proposed — 2026-10-30 (Emergence)

## Context

[Finding 100](../../../poc/drhook-engine/findings/100-orphaned-transport-read-holds-dac-lock.md) located and
reproduced a hang. When a target dies while mscordbi's Win32 event thread is reading its memory over the debugger
transport, that read never returns. The thread is the one that would handle the target's exit, so nothing releases
the read, and it keeps the DAC's lock. Every later attach and launch in the debugger process hangs. DrHook's owned
`Dispose` opens the window: it resumes a running target, then terminates it while events are still being
dispatched. Probe 100 reproduces it on omega, 5 of 5 runs. On the GitHub macOS runners it hit none of 8 jobs before a
test change of ours (4b9812a) altered the timing of the test that runs before the one that hangs, 4 of 8 with that
change, and 1 of 6 after the change was removed (8ed9701). So the hang occurs with today's code; whether the change
made it more frequent, counts this small do not show. Two earlier single-test
timeouts on those runners, after which the suite ran normally, were a different failure whose cause is unknown.

All of that evidence is from macOS, mostly from one machine. DrHook supports six platforms, and the mechanism rests
on behaviour that differs between them, some of it known and most of it not measured:

- **Which debugger code runs.** dbgshim loads mscordbi and the DAC from the *target's* runtime. The machines run
  different builds: Microsoft's .NET on zbook and elite, Ubuntu's own build on omega-ubuntu (`/usr/lib/dotnet`,
  10.0.13), patch levels from 10.0.9 to 10.0.14. Ubuntu's build already behaved differently once: it links the
  system libunwind and crashes under load ([runbook](../../operations/runbook-linux-dotnet-runtime.md)).
- **How target memory is read.** `ShimRemoteDataTarget::ReadVirtual` reads directly when `PAL_OpenProcessMemory`
  succeeds and over the transport otherwise. On macOS that needs `task_for_pid`. On Linux it opens
  `/proc/<pid>/mem`, which Yama's `ptrace_scope` restricts: zbook and omega-ubuntu have `ptrace_scope=1`, under
  which only a descendant can be read, and DrHook's targets are not descendants. So Linux may read over the transport
  too. Windows is built without the transport (`FEATURE_DBGIPC` is set only for Unix,
  `src/coreclr/clrfeatures.cmake`). None of this is measured on a Linux or Windows machine.
- **How a target's death is noticed, and by which thread.** On Linux and macOS, mscordbi's PAL; on Windows, a real
  process handle.
- **What the machine is.** Metal or VM, 3 to 18 cores, macOS 26 or 27, kernel and libc versions, `/bin/sh` as dash
  (zbook, omega-ubuntu) or bash, signal numbers (SIGSTOP is 17 on macOS, 19 on Linux).

A platform cannot stand in for another: a result from omega says nothing certain about zbook. Two more facts limit
what each machine can tell us: elite's kernel logs memory-corruption warnings (suspected RAM, not proven), and
macOS x64 and Windows x64 run only on GitHub's runners.

## Decision (proposed)

Change nothing in DrHook's teardown until each platform's behaviour is established **on that platform**, by a probe
built for it, and recorded. Then decide the teardown, with the evidence, and prove it on every platform before it
ships.

### Principles

1. **Native, per platform.** Every claim about a platform comes from a run on that platform. No emulation, no
   inference from another platform. A CI runner counts as the platform it is.
2. **One unknown at a time.** Each step answers one question; the next step starts from its recorded answer.
3. **Deterministic outcomes.** A probe result is reported as N of N. A result that varies is itself the finding,
   and is characterized before moving on.
4. **Recorded before acted on.** Each step's result goes into a finding and into Mercury before the next step.
5. **Constructions reviewed first.** Each platform's probe construction is reviewed with Martin before it runs,
   since the constructions differ (below).

### The platforms and where each is observed

| Platform | Our machines | GitHub runner | Notes |
|----------|--------------|---------------|-------|
| macOS arm64 | omega (metal, macOS 27, Developer Mode on) | `macos-latest` (VM, macOS 26.6, 3 cores) | where finding 100 was found and reproduced |
| macOS x64 | — | `macos-26-intel` (VM, macOS 26.6) | runner only |
| Linux x64 | zbook (Microsoft .NET 10.0.14, `ptrace_scope=1`, dash); elite (Microsoft 10.0.13, suspected RAM) | `ubuntu-24.04` | |
| Linux arm64 | omega-ubuntu (VM, Ubuntu's .NET 10.0.13, `ptrace_scope=1`, dash) | `ubuntu-24.04-arm` | the only Linux arm64 machine runs the distribution's build |
| Windows x64 | — | `windows-latest` | runner only; no transport |
| Windows arm64 | omega-windows (VM, 10.0.14) | `windows-11-arm` | no transport |

### The facts to establish on each platform

- **F1 — the debugger code.** The path and version of the mscordbi, DAC and dbgshim the debugger process loads, and
  whose build they are.
- **F2 — how memory is read.** Whether `PAL_OpenProcessMemory` succeeds for DrHook's targets (macOS: `task_for_pid`;
  Linux: `/proc/<pid>/mem`, with the `ptrace_scope` value and the target's ancestry), so whether reads go over the
  transport.
- **F3 — how a death is noticed.** Which thread handles a target's exit, and whether a read pending on another thread
  is released (probe 100's kill mode).
- **F4 — the flood-kill state.** Whether a target dying while the event thread reads leaves that read blocked, the
  debugger process unable to attach again, and the target's pipes in place (probe 100's flood-kill mode).
- **F5 — what is left behind.** Pipe files and blocked transport threads after a normal suite run.
- **F6 — the environment.** OS, kernel, libc, CPU count, VM or metal, `/bin/sh`, and which stack-capture tools work
  without prompts (macOS `sample` needs Developer Mode or sudo; Linux `gdb` or `eu-stack` is also limited by
  `ptrace_scope`; Windows needs its own).

### Steps

Each step ends with a recorded result and a review before the next.

**Step 0 — Preconditions (Martin's decisions).**
- elite: run memtest86+ before any result from it counts.
- Linux arm64: whether to add Microsoft's .NET beside Ubuntu's on omega-ubuntu, so that F1 can be compared on one
  machine (as the libunwind comparison was).
- Linux stack capture: `ptrace_scope=1` also stops `gdb` from attaching to a non-descendant. Options: sudo, a
  temporary `ptrace_scope=0` during the probe, or a construction that needs no capture.
- Every machine at the same commit, with its runtimes listed (`dotnet --list-runtimes`).

**Step 1 — Platform facts (a read-only probe, probe 101).** Attach to an idle target, change nothing, and record F1,
F2 and F6. It kills nothing and leaves the target running. Runs on each of our machines, and on the six runners
through a `workflow_dispatch` job (a workflow change, reviewed when proposed). Result: the platform table above,
filled with measured values.

**Step 2 — Probe 100 per platform.** Build each platform's construction from Step 1's facts:
- **macOS:** as now (SIGSTOP, `sample`).
- **Linux:** SIGSTOP and SIGCONT at Linux's numbers; detecting "the event thread is in a read" needs a stack-capture
  method chosen in Step 0. If Step 1 shows direct reads, the transport modes do not apply, and the probe asks
  instead what a direct read does when its target dies.
- **Windows:** no transport and no SIGSTOP. The question is the same (can the event thread be left blocked by a
  target's death?), but the construction is new, built from the Windows debugger's own mechanism, and reviewed
  before it runs.

**Step 3 — Run the current code (no fix) on every platform.** Probe 100's modes, ten runs each. Also DrHook's own
`Dispose` under a flood, without SIGSTOP, to measure how often a real teardown hits the window on each machine.
Result: an F3/F4/F5 matrix, platform by platform.

**Step 4 — Decide (Epistemics).** From the matrix: where the hazard exists, and which teardown removes it on each
platform. Candidates so far: kill only while the target is synchronized; detach before terminating (finding 63's
host crash came from a kill right after a detach, so this needs its own proof); direct reads. Platforms may need
different sequences, as separate implementations, not a flag. This ADR moves to Accepted with that decision.

**Step 5 — Prove the decision before building it.** A probe of the chosen sequence on every platform: probe 100's
modes against it, finding 63's crash reproduction, and the death-detection probes of finding 66. Ten of ten on each.

**Step 6 — Engineering.** Implement, add an integration test that fails on today's code wherever the hazard exists,
and run CI with the hang watcher until the criteria below hold; then remove the watcher. Update ADR-007's Phase 8
and 9 notes and DRHOOK.md's status.

### What "deterministic" means here (the acceptance criteria)

- On every platform, every probe 100 mode gives one outcome, N of N.
- After the change, no construction leaves a debugger process unable to attach or launch: 10 of 10 on each of our
  machines, and 10 consecutive green CI runs on all six runners with the watcher recording nothing.
- A full integration suite leaves no blocked transport threads and no pipe files behind (F5), on every platform.

## Not in this ADR

These were seen while building probe 100 and are separate items, to be taken up with Martin:

- After a target is killed, `GetStackFrames` and `GetLocals` threw `NullReferenceException` instead of a structured
  error.
- A line breakpoint set on an attached file-based target stayed pending and never bound.
- An upstream report to dotnet/runtime of the mscordbi behaviour.

## References

- [Finding 100](../../../poc/drhook-engine/findings/100-orphaned-transport-read-holds-dac-lock.md) and probe 100
  (`poc/drhook-engine/100-orphaned-read-smoke.cs`)
- [ADR-007](ADR-007-teardown-concurrency-test-debug.md) Phase 9 (the cross-platform validation campaign this narrows)
- [ADR-008](ADR-008-process-lifecycle-discipline.md) (natural exit by default; the two-stage termination)
- Findings 63 (the detach-then-kill crash), 66 (target-death detection), 87 (a target that is the debugger's child),
  92, 96 and 99 (first runs on Linux and Windows; early-attach crash)
- [Runbook: Microsoft's .NET on Linux](../../operations/runbook-linux-dotnet-runtime.md)
- `tools/watch-drhook-hang.sh` and the macOS hang watcher in `.github/workflows/test.yml`
