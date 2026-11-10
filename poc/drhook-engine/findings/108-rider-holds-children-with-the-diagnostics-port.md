# Finding 108 — Rider catches child processes with the runtime's diagnostics-port pause: a listener in its backend, one debugger worker per process

**Date:** 2026-11-10
**Status:** **Observed** on omega (macOS 27.0.1 arm64, runtime 10.0.9), Rider 2026.2.4 (RD-262.11879.110), probe 108,
2026-11-10 18:03–18:14 UTC. One run with the option on, three with it off, five without Rider. Every process ended
exit 0. Answers [ADR-017](../../../docs/adrs/drhook/ADR-017-debugging-a-process-tree.md)'s Q9 (Step 2). Nothing in
DrHook changed.

## The question

ADR-017's lead candidate for holding a child is the runtime's diagnostics-port pause
([finding 107](107-diagnostics-port-hold-is-inherited.md)). Rider has caught child .NET processes since 2025.1
("Attach to child .NET processes"), and its documentation does not say how. Rider is DrHook's reference for
correctness, so the probe watches it do so. The hypotheses, stated in the probe before any run:

- **H1, the diagnostics port:** with the option on, the workers' environment holds `DOTNET_DiagnosticPorts` with
  `suspend`, and the worker's socket connects to one a JetBrains process binds.
- **H2, a startup hook:** the supervisor's environment holds `DOTNET_STARTUP_HOOKS` naming a JetBrains assembly, which
  is loaded.
- **H3, neither.**

## What was run

`108-rider-child-hold/` is a project (`RiderChildHold.csproj`, `Program.cs`), not a file-based app, because a .NET 10
file-based app disables startup hooks (finding 107) and would have hidden H2. A supervisor starts worker 1 with
`Process.Start`, worker 1 starts worker 2, and each waits for its child. Every process writes marks to
`bin/Debug/net10.0/marks/<run>/<pid>-<role>.txt`:

- its module initializer's time and `Debugger.IsAttached`;
- the top of main;
- its environment, values only for `DOTNET_`, `COMPlus_` and `CORECLR_` variables and names for the rest;
- its loaded assemblies;
- the times either side of `Process.Start`;
- a checkpoint on the worker's breakpoint line (`Program.cs:72`).

`108-rider-child-hold-read.sh` reads what a process cannot see about itself. It is read-only (`ps`, `lsof`, `ls`):

- parent, process group and threads;
- ancestry, by executable name only;
- the runtime's `clr-debug-pipe` and `dotnet-diagnostic` files in the user's temp folder;
- each unix socket, and the process holding its other end.

| Run | Who | Debugger | Runs |
|-----|-----|----------|------|
| A | sky-omega, the apphost from Claude Code's Bash tool | none | 5 |
| B | Martin, Rider Debug, a .NET Project configuration, breakpoint at line 72 | the option off | 3 |
| C | Martin, the same | the option on; Continue at each stop | 1 |

During run C, two watchers in the evidence folder ran the read script:

- **Worker 1** (`watch-worker1.sh`): run C had started 43 s before the watcher, which took run C's own folder as its
  baseline. Its one snapshot is therefore labelled "early" in the script, but worker 1 had already written its facts
  and was stopped at line 72 (its checkpoint came at 18:13:11). The copy here is `read-c-worker1-held.txt`. Worker 1's
  moment before its own code was not captured.
- **Worker 2** (`watch-worker2.sh`): one read before worker 2 wrote any mark, and one at its stop.

Who holds each debugger pipe was read by hand while worker 1 was stopped (`pipes-c-worker1-held.txt`), and again by
the second watcher. What was left after the run is in `end-c.txt`.

Evidence, in [`108-rider-holds-children-with-the-diagnostics-port/`](108-rider-holds-children-with-the-diagnostics-port/).
The marks copies keep the `env-name` lines only for the variables Rider sets, and give per process how many other names
were dropped; nothing else is changed.

| File | Lines | sha256 (first 16) |
|------|------:|-------------------|
| `marks-a.txt` | 345 | `ab25b25da1ba1feb` |
| `marks-b.txt` | 263 | `51465fc4e2483516` |
| `marks-c.txt` | 91 | `51f40f0279d589c0` |
| `read-c-worker1-held.txt` | 54 | `8b181e3eb463dacd` |
| `pipes-c-worker1-held.txt` | 27 | `6138bde44e6f5094` |
| `read-c-worker2-before-marks.txt` | 84 | `de9ab6729dbaa6f6` |
| `read-c-worker2-held.txt` | 89 | `046c4364d295c8f8` |
| `end-c.txt` | 37 | `ba2474d4dfddf726` |

## What was observed

- **H1 holds.** With the option on, Rider sets
  `DOTNET_DiagnosticPorts=<temp>/rider_autoattach/<guid>,connect,suspend`. Both workers inherit it unchanged; the three
  environments in run C are identical. The supervisor, worker 1 and worker 2 each have a unix socket connected to
  `Rider.Backend` (pid 54328), bound at that path.
- **H2 does not.** No run has `DOTNET_STARTUP_HOOKS`, and no JetBrains assembly is loaded. All 27 processes, in
  every run, load the same 11 assemblies.
- **Each child is held until a debugger is attached, before its own code.**
  - Worker 2, read before it wrote any mark: 4 threads, connected to the backend's socket, and no debugger pipes of its
    own yet. At its stop: 11 threads and its own pipe pair.
  - Both workers report `attached=True` in their module initializer, the first managed code of the assembly.
  - The time from the parent's `Process.Start` to that initializer:

    | | worker 1 | worker 2 |
    |---|---:|---:|
    | A, no debugger (5 runs) | 19.2–26.0 ms | 18.8–24.6 ms |
    | B, the option off (3 runs) | 37.6–44.0 ms | 30.9–37.0 ms |
    | C, the option on | 1,581 ms | 1,378 ms |
- **The parent is not held.** `Process.Start` returned 10.3 ms (the supervisor) and 14.1 ms (worker 1) after it was
  called; run A took 6.2–12.8 ms.
- **One debugger worker per process.** Each `clr-debug-pipe` pair is held by exactly one `JetBrains.Debugger.Worker`:

  | Target | Its debugger | That debugger's parent | Started |
  |--------|--------------|------------------------|---------|
  | supervisor 60254 | 60250, which also launched it | `rider` 72032 | 19:10:07 local |
  | worker 1 60257 | 60265 | `rider` 72032 | 19:10:08, the second worker 1 was spawned |
  | worker 2 71044 | 71076 | `rider` 72032 | during worker 2's hold |

  The listener is in `Rider.Backend`, which outlives every debugger worker. A debugger worker is started for each
  child that dials it.
- **The grandchild is caught like the child.** Worker 2's ancestry is worker 1, the supervisor, debugger worker 60250,
  `rider`.
- **With the option off, Rider still sets a suspending port.** Run B's variable is
  `<temp>/DPA-DiagPort-<guid>,connect,suspend`, for JetBrains' Dynamic Program Analysis (DPA), with
  `JB_DPA_ROOT_SESSION_ID` set beside it. The workers inherit it, nothing attaches to them, and they reach their
  initializer 12–20 ms later than in run A. The ranges do not overlap. That fits a short pause that DPA releases.
  `COMPLUS_FORCEENC=1` is also set and could slow a startup by itself, so the delay is not attributed.
- **One port, not two.** With the option on, the auto-attach path replaces DPA's in the variable;
  `JB_DPA_ROOT_SESSION_ID` stays. The rest of Rider's runtime environment is the same in B and C: `COMPLUS_FORCEENC=1`,
  `DOTNET_MODIFIABLE_ASSEMBLIES=debug`, and the names `RIDER_BIN_DIR` and `_NO_DEBUG_HEAP`.
- **The run ended cleanly.** Every process exited 0 after Continue, and the three debugger workers exited. The runtimes
  removed their own pipe and socket files.

## Reading

Rider's child debugging is ADR-017's lead candidate, built the way ADR-017 sketches it:

- The variable is set at the launch, travels to every .NET descendant, and holds each one before its first managed
  code.
- A long-lived process owns the listening path: Rider's backend, not a debugger. That is the shape ADR-017's Q4 expects
  of DrHook's supervisor.
- Each caught process gets its own debugger process, as ADR-017's principle 4 requires.

So the mechanism is not only measured on Linux (finding 107). It runs in an established debugger on macOS arm64.

The cost is a hold of about 1.4–1.6 s per child, in Rider's implementation. A supervisor with a short timeout for its
worker would see that (ADR-017's lifecycle hazards).

Run B shows a second consequence. A program debugged in Rider runs with a suspending port even with the option off, so
every .NET program it starts pauses briefly at startup and dials DPA. A tool that adds its own port to such a program
has to share one variable with Rider.

## Side findings

- **A child inherits its ancestors' debugger pipes.**
  - Worker 1 holds the supervisor's pipe pair open (fd 11 read on `-in`, fd 14 write on `-out`): the same ends the
    supervisor's runtime holds.
  - Worker 2 holds the supervisor's and worker 1's pairs.
  - So the runtime's debug-transport descriptors leak through `Process.Start` on macOS.
  - By reasoning, not observed: while a child lives, a debugger reading `-out` would not see end-of-file when its own
    target dies, because the child still holds a write end. Whether any debugger relies on that end-of-file is not
    assessed. DrHook's own detection (ADR-016) and finding 100's pipe files are where it would show.
- **The runtime dials again after a session closes.**
  - Worker 2's connection to the backend changed between its two reads: a new socket, and a new descriptor in the
    backend (fd 859, then 861).
  - After the run the backend still held the listener (fd 782) and five accepted connections whose processes had
    exited (855, 856, 857, 859 and 861), 16 s after the end.
  - Five connections for three processes means at least two of them dialled twice. This is read from the counts, not
    the source.
- **Rider's backend keeps the path bound after the run,** and keeps the dead connections open.
- **omega's temp folder holds 500 `clr-debug-pipe` files and 438 `dotnet-diagnostic` sockets** from earlier sessions,
  like zbook's 382 stale pairs (finding 107). They were counted and left untouched.
- **One unexplained descriptor.** In worker 2's read before its marks, `lsof` showed fd 16 open for reading on a FIFO
  named `clr-debug-pipe-71044-1794334391-co`. That file was not in the temp-folder listing, and the read at its stop no
  longer showed it. Not followed.

## Not established

- **A held child when its debugger dies** (ADR-017 Q4, Step 5): neither Rider's debugger worker nor its backend was
  stopped during a hold.
- **How the root process is attached.** The supervisor was launched by its debugger worker and is connected to the
  backend. Whether its own attach used the pause or a suspended launch is not visible from here.
- **Which children** (ADR-017 Q3). Every child in this tree was caught. No child that Rider might skip was tried.
- **What makes up the 1.4–1.6 s,** between starting a debugger worker and attaching.
- **A child started through `dotnet <dll>`.** The probe ran the apphost only.
- **Rider on Linux and Windows.**
- **Whether the runtime takes two ports in one variable.** Rider used one.
