# ADR-017: Debugging a Process Tree — a Breakpoint in a Worker, from the Supervisor's Session

**Status:** Accepted — 2026-11-08 (Martin: "I accept the ADR"). Proposed the same day. The scenario, the principles
and the lead candidate are approved. Steps 2 to 5 confirm or replace the mechanism; engineering waits for their
evidence and for ADR-016's worker, which principle 4 builds on. Every probe below is reviewed before it runs.

## The question

Martin, 2026-11-08: "What happens if we debug a supervisor but want to set a breakpoint in a worker? Because we
suspect a flaw in a worker? Should we be able to debug such a scenario? Without creating a separate debugging
scenario?"

The flaw is often in how the two processes work together: what the supervisor passes, when it starts the worker,
what it does with the result. A separate setup for the worker loses exactly that context.

## Context

### What DrHook does today (verified 2026-11-08)

- **One session, one target.** `drhook-mcp` holds one `EngineSteppingSession`. A second `drhook_launch` or
  `drhook_attach` while it is active is refused: "A stepping session is already active. Use drhook_stop first."
  (`src/DrHook.Mcp/EngineSteppingSession.cs`, the launch and attach entries).
- **Breakpoints bind in that target only.** The worker is another process with its own runtime. A breakpoint set in
  the supervisor's session never reaches it, even when both load the same assembly.
- **The ways in today are separate scenarios.**
  - Launch the worker alone. That loses the supervisor's arguments, environment and timing.
  - Attach to the worker by pid. That needs the supervisor's session ended first (`drhook_stop` ends a launched
    supervisor, ADR-016), and misses whatever the worker does before the attach lands. Without the pause measured
    in finding 107, a `dotnet` program reached main 34–48 ms after its spawn on zbook.

### Where the shape occurs

- **`dotnet run`**, including `dotnet run x.cs`: the SDK's process starts the app as its child. Attaching means
  finding the apphost child, not the launcher (recorded 2026-06-29).
- **VSTest:** `vstest.console` starts the test hosts. Its own hold, `VSTEST_HOST_DEBUG=1`, also travels in the
  environment. A second test host inherited it and waited forever for a debugger (finding 90), and an early attach
  can skip the hold (finding 91).
- **Programs built as a supervisor with workers:** worker pools, a process per job, build servers.
- **DrHook itself**, once ADR-016's worker exists: `drhook-mcp` as the supervisor, the native debugger in a worker.
  Probe 106 already has this shape. Debugging DrHook with DrHook is this scenario.

### What established debuggers do

- **gdb** follows `fork` and `exec` (`follow-fork-mode`, `detach-on-fork off`) and keeps several inferiors in one
  session. It is a native debugger, built on `ptrace`.
- **Visual Studio** has the Child Process Debugging Power Tool (Microsoft, 2014-11-24). It is an unsupported
  extension and Windows only. For .NET code it needs mixed-mode (native) debugging enabled.
- **Rider** has "Attach to child .NET processes" (since 2025.1, per JetBrains support). It is offered for .NET Project
  and .NET Executable run configurations, and covers child and grandchild processes. It is not supported for tests run
  from the Tests window (RIDER-129067). The documentation does not say how the child is held. Rider is DrHook's
  reference for correctness, so how it does this can be observed.
- **The runtime offers nothing for this.** [dotnet/runtime#43924](https://github.com/dotnet/runtime/issues/43924)
  (2020-10-27) says there is no way for a debugger to debug child .NET processes automatically on all platforms. It
  proposes an ICorDebug opt-in under which `Process.Start` starts the child suspended and notifies the debugger. It
  is open, with milestone Future since 2021-03-12, and no team member has replied.

### What finding 107 measured

On Linux x64 ([finding 107](../../../poc/drhook-engine/findings/107-diagnostics-port-hold-is-inherited.md)), with
`DOTNET_DiagnosticPorts=<path>,connect,suspend`:

- The runtime pauses before its first managed code and before dbgshim's semaphore check. A registration made during
  the pause catches the startup (10 of 10; without the pause, 0 of 10).
- Every .NET child inherits the variable unchanged, pauses and dials the same path (3 of 3).
- A child with nobody listening waits in startup until a listener returns. Watched for 30 s, it then re-dialled
  within 380–387 ms of the path's return (3 of 3).
- Only the suspending port's `ResumeRuntime` releases the pause; the default port's does not (5 of 5).
- A runtimeconfig property is not a channel (1 of 1).

That is why the default Linux launch keeps its shell. The same results make the pause a candidate for catching
children: it already reaches every .NET descendant and holds each one before any of its code runs.

## The scenario

In the columns of ADR-016's scenario table:

| Scenario | How it starts | Whose program | Its natural end | Ending the session before that | Lifecycle hazards |
|----------|---------------|---------------|-----------------|--------------------------------|-------------------|
| **Debugging a process tree:** a flaw suspected in a worker, with the supervisor's context | the supervisor launched with child-catching chosen; breakpoints set once for the session | DrHook's for the supervisor it launched; each caught child is the supervisor's | the supervisor ends, after its workers | release every held child, then end as the supervisor's scenario says | a child started after DrHook leaves waits forever (finding 107); a held child may hold up the supervisor, whose own timeouts may expire (a worker that must report within a bound); the program can read the variable DrHook added |

## Decision

### Principles

1. **The session is the scenario, not the process.** One session can hold several targets. Each has its own
   identity (pid and start time, ADR-016), its own state and its own stops.
2. **Catching children is the operator's choice.** The default launch never catches them (finding 107). The tool that
   turns it on states its limit: a child started after DrHook leaves waits.
3. **A breakpoint belongs to the session.** It is set once by file and line, or by function. It binds in every
   process of the session that loads the code, as a pending breakpoint binds today when its module loads. Each stop
   names its process, and stepping, inspection and continue act on that process.
4. **Each process gets its own worker** (ADR-016). A failure in one process's debugger cannot take the others down,
   and finding 86's runtime-version lock applies per worker, so a supervisor and a worker on different runtimes do
   not conflict.
5. **Every caught child is accounted for.** It is either attached or released without an attach, and either way it is
   recorded. Before a session ends by stop or detach, every held child is released. No child is left held without a
   record.
6. **Holding is the only change.** DrHook clears nothing and injects nothing inside a program to limit the spread. If
   a mechanism needs more, that is stated as an intervention, as function evaluation is (ADR-016).

### Candidate mechanisms for the hold

| Mechanism | Platforms | What it holds | Its cost or gap |
|-----------|-----------|---------------|-----------------|
| **The diagnostics-port pause**, the variable set at the supervisor's launch | Linux x64 measured; macOS and Windows not | every .NET descendant, before its first managed code | a child started after DrHook leaves waits forever; the program can read the variable |
| **The variable added per child**, at a breakpoint in the supervisor before it starts the process | where `Process.Start` is the path | only the children DrHook chooses | changes the start info the program built (an intervention); misses children started by native code; the chosen child's own children still inherit it |
| **Attach by pid after the start** | all | nothing: a race | the child's runtime checks dbgshim's semaphores before main, which comes 34–48 ms after the spawn; rejected without a barrier |
| **The runtime's own notification** (dotnet/runtime#43924) | none yet | each child, at `Process.Start` | does not exist |
| **Operating-system process tracing** (Linux `ptrace` fork following, Windows native debug events) | per platform; nothing known on macOS | every child, at `fork` or `exec` | makes DrHook a native debugger, and is not one path on every platform |

The first row is the lead candidate, because it is measured and is the same mechanism on every platform the runtime
supports. That second point is still a claim until it is measured.

## Open questions

- **Q1. Other platforms.** Does the pause hold and spread the same way on macOS (omega) and on Windows
  (omega-windows)? Probe 107 reads `/proc`, so it needs another way to observe there.
- **Q2. A full attach after the release.** After `ResumeRuntime`, do `DebugActiveProcess`, breakpoints armed before
  main, and continue reach main with the breakpoints bound? Finding 107 stopped at the startup callback.
- **Q3. Which children.** All .NET descendants, or the ones that match a program or module the operator names? A
  child that does not match is released at once without an attach. How long does that delay its start?
- **Q4. When DrHook goes away.** Stop and detach release every held child first (principle 5). After a crash, a held
  child waits until a listener returns. Finding 107 showed a child re-dial within about 0.5 s once its path is bound
  again. So a supervisor that owns the listening path (ADR-016's supervisor, which outlives a worker's crash) could
  release the children of a crashed worker. A crash of `drhook-mcp` itself would leave them waiting, unless a new one
  binds the same recorded path. That is a candidate, not a decision.
- **Q5. An attached supervisor.** Can the children of a program that is already running be caught? The diagnostics
  protocol has a command to set an environment variable in a running process (not yet read in the source). Whether
  a variable set that way reaches the children `Process.Start` creates is unknown, and it changes the program's
  environment.
- **Q6. A child that another debugger will debug.** When DrHook debugs DrHook, the variable reaches the inner worker's
  target too. The outer DrHook must release that target without attaching, and the inner launch must still work.
- **Q7. The MCP surface.** How a stop names its process, how stepping and inspection pick a process, and how
  `drhook_processes` shows the tree, under ADR-010's rule that the tools mirror the substrate.
- **Q8. One timeline.** One braid for the session, with every process's stops ordered by time.
- **Q9. Rider.** How does Rider's "Attach to child .NET processes" hold a child? A child's environment and threads
  under Rider can be read without Rider's source.

## Steps

1. **Martin's assessment** of this draft: the scenario, the principles, the lead candidate. *Done 2026-11-08:
   accepted.*
2. **Rider as the reference** (Q9). Martin runs a supervisor under Rider with the option on, and the child's
   environment and threads are read. This is read-only and cheap.
3. **Probe 107's modes on macOS and Windows** (Q1), with an observation layer for each platform.
4. **A probe with DrHook.Engine** (Q2, Q3): a supervisor starts a worker; a breakpoint in the worker's startup code,
   set before either starts, must stop the worker; the supervisor runs on; both end naturally.
5. **The crash case** (Q4): held children released by a listener bound again on the same path.
6. **Decide** the mechanism, the scope and the surface from that evidence. Engineering follows, once ADR-016's worker
   exists; the ADR is Completed when it is built and dogfooded.

## Not in this ADR

- Changing the default launch on any platform (finding 107 keeps the Linux shell).
- Children that are not .NET programs.
- Process trees that cross machines or containers.

## References

- [Finding 107](../../../poc/drhook-engine/findings/107-diagnostics-port-hold-is-inherited.md): the diagnostics-port
  pause holds, and is inherited.
- [Finding 90](../../../poc/drhook-engine/findings/90-vstest-debug-wait-orphans.md) and
  [finding 91](../../../poc/drhook-engine/findings/91-vstest-host-debug-attach-race.md): VSTest's own hold, inherited
  and racing.
- [ADR-016](ADR-016-debugger-resilience-and-recovery.md): the worker process, target identity, the scenario table.
- [ADR-010](ADR-010-mcp-tool-surface-redesign.md): the MCP surface mirrors the substrate.
- [dotnet/runtime#43924](https://github.com/dotnet/runtime/issues/43924): child process debugging support (open).
- [Rider: attach to child .NET processes](https://www.jetbrains.com/help/rider/2026.2/attach-to-process.html).
- [Visual Studio: the Child Process Debugging Power Tool](https://devblogs.microsoft.com/devops/introducing-the-child-process-debugging-power-tool/).
- Mercury: `urn:sky-omega:obs:2026-11-08-process-tree-question`, in graph
  `urn:sky-omega:session:2026-11-07-adr016-step5`.
