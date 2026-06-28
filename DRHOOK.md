# DRHOOK.md

DrHook is Sky Omega's runtime observation substrate — the debugging counterpart to Mercury's semantic memory ([MERCURY.md](MERCURY.md)). Use it when you need to understand what code is actually doing — not what you think it's doing.

The substrate is `DrHook.Engine` — a BCL + P/Invoke + source-gen COM implementation of `ICorDebug` interop, with `libdbgshim` bundled per-RID via NuGet. `netcoredbg` was retired at 1.8.2; the previous DAP-over-stdio path no longer exists.

> **Surface-redesign note (2026-05-31):** the MCP tool names below are the **post-rename** surface shipped by [ADR-010](docs/adrs/drhook/ADR-010-mcp-tool-surface-redesign.md) (Accepted) Tier 1. Names now follow established IDE-debugger convention (`launch` starts a new process, `attach` connects to a running one) and agree with the substrate's own `DebugSession.Launch` / `.Attach` vocabulary. The previous `drhook_step_*` names are shown in the "Former name" column for one transition cycle. Capabilities are unchanged from before the rename.
>
> **Production-suitability status:** substrate-correctness on macOS-arm64 is CI-enforced via [ADR-008](docs/adrs/drhook/ADR-008-process-lifecycle-discipline.md) (Completed) + [ADR-007](docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md) Phases 1, 2, 8 (Complete). 12/12 integration tests pass reliably. Cross-platform validation is ADR-007 Phase 9 (Open). Use with care under test runners on platforms other than macOS-arm64.

## When to Use DrHook

**Use DrHook when:**
- A bug persists after your first fix attempt — you're guessing, not observing.
- The behavior depends on runtime state (buffer positions, loop iterations, async continuations).
- You need to verify an assumption about control flow or data at a specific point.
- Increasing a constant or adding a workaround feels like the right fix — it never is.
- You're about to change code you haven't observed running.

**The rule:** If you've made two attempts at fixing something and it's still broken, stop coding and start observing. DrHook exists for exactly this moment.

## Methodology: Observe Before Fixing

1. **Reproduce with minimal input.** Create the smallest possible test case that triggers the bug. Use file-based apps (`.cs` scripts) for quick iteration.
2. **Set breakpoints at the decision points.** Not at the error — at the code paths that lead to the error. The bug is in the decision, not the exception.
3. **Step through and record state.** Use `drhook_locals` at each stop to capture actual values. Compare what you see with what you expected.
4. **Identify the invariant violation.** The bug is where reality diverges from your mental model. Once you see the actual state, the fix is usually obvious.
5. **Fix and verify.** Change the code, re-run with the same breakpoints to confirm the fix.

## DrHook MCP Tools

23 tools. Every state-changing operation and every inspection that reads target state takes a `hypothesis` parameter — state what you expect *before* you observe (Sky Omega epistemic discipline; ADR-010 Decision principle 5).

### Session lifecycle

| Tool | What it does | Former name |
|------|---------------|-------------|
| `drhook_launch` | **Launches a NEW process** under debugger control (Owned session). | `drhook_step_run` |
| `drhook_attach` | **Attaches to an already-running .NET process** by PID (Borrowed session — target survives the session). | `drhook_step_launch` |
| `drhook_stop` | **Ends the session** — the normal finish. Borrowed: detaches, target keeps running. Owned: target asked to exit gracefully — SIGTERM → SIGKILL if it doesn't exit within the ~2s window (ADR-008). | `drhook_step_stop` → `drhook_detach` |
| `drhook_detach` | Detaches and **leaves the target running** (deliberate). Borrowed: supported. Owned: not yet — pending finding **F-010-2** (the launched target is the debugger's child); returns an error pointing to `drhook_stop` / `drhook_kill`. | *(new — ADR-011 D1)* |
| `drhook_kill` | **Forcibly terminates** the target — anomaly escape hatch, *not* normal cleanup; every call is worth investigating. Owned: SIGTERM brief-grace → SIGKILL (`DebugSession.Abandon`, ADR-008). Borrowed: not yet — pending finding **F-010-1**. | *(new — ADR-011 D1)* |

`drhook_step_test` was **removed** in this rename (it only ever returned "not implemented") — see [What's NOT yet shipped](#whats-not-yet-shipped).

### Execution control

| Tool | What it does | Former name |
|------|---------------|-------------|
| `drhook_continue` | Resume execution; optionally wait for next stop | `drhook_step_continue` |
| `drhook_pause` | Pause running process (`ICorDebugController.Stop`) | `drhook_step_pause` |
| `drhook_step_over` | Step over (execute the current line without descending into calls) | `drhook_step_next` |
| `drhook_step_into` | Step into the method call on the current line | *(unchanged)* |
| `drhook_step_out` | Step out of the current method, back to the caller | *(unchanged)* |

### Breakpoints

| Tool | What it does | Former name |
|------|---------------|-------------|
| `drhook_break_source` | Set source breakpoint at file:line. Optional policy: `condition` (C# expression evaluated each hit — typed via Roslyn → System.Linq.Expressions → `LambdaExpression.Compile()` so int/double/bool operands keep their CLR types), `hitCount` (fire only on Nth matching hit), `logMessage` (template like `"counter={counter} times2={counter*2}"` — `{expr}` fragments compile the same way, including typed arithmetic, and render via `Convert.ToString(InvariantCulture)`), `suspend` (`"all"` to stop, `"none"` for a non-stopping logpoint that auto-resumes after each emit). | `drhook_step_breakpoint` |
| `drhook_break_function` | Set function breakpoint by method name. Same optional policy parameters as `drhook_break_source`; both `condition` and `logMessage` see method arguments + locals at entry. | `drhook_step_break_function` |
| `drhook_break_exception` | Set exception breakpoint by `typeName` (full CLR name, or `"*"` wildcard). Optional `phase` (`"any"`/`"first-chance"`/`"user-first-chance"`/`"catch-handler-found"`/`"unhandled"`), `condition`, `hitCount`, `logMessage`, `suspend`. SUBCLASS-AWARE — a filter on a base type matches every subclass, including types defined in the target's own module (verified for target-defined hierarchies by probe 57). Multiple filters compose with OR. | `drhook_step_break_exception` |
| `drhook_break_list` | List all breakpoints (source + function + exception) with full descriptors per entry — `id`, location, `hits` running count, and `policy` (when attached — `condition`/`hitCount`/`logMessage`/`suspend` as the agent supplied). | `drhook_step_breakpoint_list` |
| `drhook_break_remove` | Remove a breakpoint or exception filter by its substrate-assigned `id`. Use `drhook_break_list` to discover ids; dispatches to the right substrate path automatically. | `drhook_step_breakpoint_remove` |
| `drhook_break_clear` | Clear all breakpoints, or by category (`source` / `function` / `exception`) | `drhook_step_breakpoint_clear` |

### Inspection

| Tool | What it does | Former name |
|------|---------------|-------------|
| `drhook_locals` | Inspect locals + arguments at current stop. Depth parameter (≥ 2) expands object fields and arrays (SZARRAY). **Top frame only** — frame selection comes via ADR-010 Tier 2 (`drhook_frames` / `drhook_locals(frame=N)`). | `drhook_step_vars` |
| `drhook_snapshot_image` | Render the CURRENT debug-state as a PNG **image** you can see — the same compact view `drhook-viz-console` shows (session, stop, call stack, source-on-step pane, locals/args, breakpoints), rasterized through the SHARED renderer so the image is exactly what a view shows. Returns a caption + the image. Requires a `hypothesis`. (ADR-012 visual snapshot.) | *(new)* |

### Observation (no session required)

| Tool | What it does | Former name |
|------|---------------|-------------|
| `drhook_processes` | List .NET processes (EventPipe-driven enumeration) | *(unchanged)* |
| `drhook_snapshot` | Passive thread/stack snapshot of running process (EventPipe). Requires `hypothesis` parameter. | *(unchanged)* |

### Substrate diagnostics

| Tool | What it does | Former name |
|------|---------------|-------------|
| `drhook_drain_anomalies` | Drain the engine's structured-anomaly buffer (substrate-correctness signals the engine detected but didn't raise as exceptions — late mscordbi callbacks, depth-clamped inspections, unexpected HRESULTs, worker-thread exceptions). Substrate-grade observation that no IDE debugger exposes. | *(unchanged)* |
| `drhook_drain_console` | Drain a LAUNCHED debuggee's captured stdout/stderr (isolated to a DrHook pipe per D2) — chunks tagged Stdout/Stderr, newest-last, with a dropped count. Pull while stepping a console app to see what it printed. | *(new — ADR-011 D3)* |
| `drhook_drain_log` | Drain logpoint output — rendered `logMessage` templates from `suspend='none'` breakpoints (+ condition faults, `isFault`). Closes the gap where logpoint output was dropped at the MCP layer. | *(new — ADR-011 D3)* |

**Every step, continue, and pause response includes process metrics** — working set, private bytes, thread count, GC heap size, collection counts with deltas from the previous capture. No extra tool call needed.

## Debug-State Visualization (ADR-012 Phase 2)

The MCP tools above are how the **agent** drives DrHook. A parallel surface lets a **human watch the same session live**: the engine publishes a unified debug-state to a rendezvous socket, and standalone views render it. This is [ADR-012](docs/adrs/drhook/ADR-012-debug-state-surfaces.md) (Phases 1–2 built and dogfooded end-to-end, 2026-06-26).

**Ownership.** The debug session is **owned by the LLM** (the MCP server). Views are **standalone, human-launched** processes that *connect in* — at Phase 2 they are read-only and never spawn or drive the session. Terminating a view never affects the ongoing session; ending the session cleanly drops the views.

### The rendezvous socket

While a session is active, the engine binds a fixed, well-known Unix-domain socket and publishes to it:

- **macOS:** `~/Library/SkyOmega/drhook/session.sock`
- **Other POSIX:** `~/.local/share/sky-omega/drhook/session.sock` (Windows AF_UNIX is a later refinement)

One active session at a time, so a fixed path is the whole rendezvous — no broker, no registry. The socket is created on `drhook_launch` / `drhook_attach` and unlinked on session end (a stale socket is unlinked on bind). A connecting view receives a **snapshot on connect**, then a live **delta stream**, as **NDJSON** — one JSON message per line (`{"type":"snapshot"|"delta", …}`).

### The console view: `drhook-viz-console`

```bash
# while a DrHook session is active (the agent has launched/attached a target):
dotnet run --project src/DrHook.Viz.Console          # optional: pass a socket path as the one argument
```

It prints the snapshot-on-connect — session (pid, owned/borrowed, runtime, exec state), execution position (stop reason, top frame, call stack), locals + arguments, breakpoints, and the console/log/anomaly stream counts — then one line per delta as the agent steps, and a clean `disconnected` when the session ends. It is also **the first proper DrHook debuggee** — a clean .NET app to `drhook_launch` and step.

### Architecture — one client, many views

Layered like Mercury's `Mercury.Abstractions ← Mercury.Sparql.Tool ← Mercury.Cli.Sparql`, so console / TUI / GUI share one client + model:

| Layer | Project | Scope |
|-------|---------|-------|
| Protocol contract | `DrHook.Wire` | The NDJSON wire DTOs + source-gen `WireCodec` + `WireRendezvous`. Zero-dependency; both the server and every view depend on it. |
| Client library | `DrHook.Viz` | `DebugStateClient` (connect, read, parse, update model, push to a view), `DebugStateClientModel` (snapshot + bounded delta tail), `IDebugStateView` (the seam views implement). BCL-only; references **only** `DrHook.Wire`. |
| View | `DrHook.Viz.Console` | The console renderer (`ConsoleDebugStateView` over a `TextWriter`). The planned TUI (Phase 4) and Avalonia GUI (Phase 6) are richer renderings of the **same** client + model. |

**Server side:** `DrHook.Engine/Transport/DebugStateServer` publishes a snapshot after each stop and the delta stream throughout; `EngineSteppingSession` wires it as a 4th `CompositeEventSink` member. The server **never calls into `DebugSession`** — the request-thread *driver* captures the immutable snapshot after each stop and pushes it, so there is no transport↔stepping concurrency hazard. Best-effort throughout: a transport fault never breaks a debug response.

**Not yet:** the `(hypothesis, observation)` braid recorded in the model (Phase 3), a full TUI (Phase 4), command-in / control (Phase 5), the Avalonia GUI sibling (Phase 6).

## What's NOT yet shipped

Substrate work is required before these surfaces become functional:

- **Watch expressions.** Substrate has narrow func-eval for parameterless and single-int-arg static methods (`DebugSession.TryEvalStaticCall`, `TryEvalStaticCallInt`). General agent-driven Roslyn-based expression evaluation against locals/arguments/`this` outside breakpoint policies is not surfaced as its own MCP tool. ADR-010 Tier 3 (`drhook_watch`, finding F-010-3 — small, since Increment 7 shipped the typed `CSharpCondition` translator).
- **Call stack frame switching.** `GetStackFrames` returns frames as formatted strings; locals are read from the top frame only. Frame-selection state and rich frame records are ADR-010 Tier 2 (`drhook_frames`, verify) / Tier 3 (substrate).
- **Set next statement.** ICorDebug `SetIP` is not exposed at the substrate level. ADR-010 Tier 3.
- **Data breakpoints.** Not in the substrate today; ICorDebug support level is an Open Question per ADR-010 §Open. ADR-010 Tier 3.
- **Run to cursor.** Composable from existing primitives (`SetBreakpointAtLine` + `Resume` + remove-on-hit); not yet packaged as a tool. ADR-010 Tier 2.
- **Owned detach-and-leave-running** — `drhook_detach` on an Owned (`drhook_launch`) target is pending finding **F-010-2** (the launched target is currently the debugger's child); returns an error meanwhile. Use `drhook_stop` (graceful end) or `drhook_kill` (force).
- **Borrowed force-kill** — `drhook_kill` on a Borrowed (`drhook_attach`) target is pending finding **F-010-1** (the substrate doesn't own an attached target's lifecycle); returns an error meanwhile. Use `drhook_detach`.
- **Test-project launch.** `drhook_step_test` was removed in ADR-010 Tier 1 (it only returned "not implemented"). Replacement: ADR-010 Tier 3 lets `drhook_launch` accept a `.csproj` target and dispatches MTP / VSTest internally. Until then, attach to the testhost child with `drhook_attach`.
- **Multi-session.** `EngineSteppingSession` is a DI singleton; only one debug session per MCP server. Substrate's `DebugSession` is per-session; the singleton is the MCP-layer constraint. ADR-010 §Open Question 9.
- **Cross-platform.** Only macOS/arm64 is exercised. ADR-007 Phase 9 (Open).

## Workflow Example: Parser Buffer Bug

```
# 1. Write minimal repro as file-based app
tools/repro-parser-bug.cs

# 2. Build it to a DLL (do NOT use `dotnet run --file` — see Launch Requirements)
dotnet build tools/repro-parser-bug.cs

# 3a. Launch a NEW process under debugger control (Owned session):
drhook_launch: program=dotnet, args=["exec", "tools/bin/Debug/net10.0/repro.dll"],
               sourceFile="...", line=..., hypothesis="..."

# 3b. OR attach to an already-running process (Borrowed session):
drhook_processes              # find pid
drhook_attach: pid=<pid>, sourceFile="...", line=..., hypothesis="..."

# 4. Add more breakpoints at decision points (with optional policy)
drhook_break_source: file="src/Mercury/Turtle/Buffer.cs", line=17   # Peek
drhook_break_source: file="src/Mercury/Turtle/Buffer.cs", line=240, # FillBufferSync, only when buffer is near empty
                     condition="_bufferLength - _bufferPosition < 8"
drhook_break_source: file="src/Mercury/Turtle/Buffer.cs", line=240, # FillBufferSync, sample every 5th hit
                     hitCount=5

# 5. Run to breakpoint; inspect state
drhook_continue
drhook_locals                 # locals + arguments at the stop

# 6. Step through the refill logic
drhook_step_over              # execute current line without descending
drhook_locals                 # verify positions after shift
drhook_step_into              # follow into a method call
drhook_step_out               # return to caller

# 7. End session
drhook_stop                   # normal end — Owned: target gracefully terminated; Borrowed: target survives
```

## Launch Requirements

**Pre-build targets before launching.** `dotnet run --file` compiles before executing — when used with `drhook_launch`, the compilation step delays the attach window and the launched-suspended state expected by `RegisterForRuntimeStartup`.

```bash
# Build first
dotnet build path/to/Project.csproj -c Debug

# Then launch:
drhook_launch: program=dotnet, args=["exec", "path/to/bin/Debug/net10.0/Project.dll"], ...
```

## Debugging targets: compiled apps and single-file harnesses

Both target kinds must be **Debug-compiled** — a Release build optimizes away locals and sequence points. Launch mechanics are in [Launch Requirements](#launch-requirements) above; this section covers the two kinds and the friction each brings.

### The two kinds

- **Stand-alone compiled target** — a built `.dll`. Pre-build (`dotnet build … -c Debug`), then `drhook_launch` (own the process) or `drhook_attach` (borrow a running one).
- **Single-file app / tool / harness** — a `.cs` file-based app (`#:project …`) that `dotnet run` builds (a *file-based* app — distinct from a `PublishSingleFile` *deployment*, covered in [Single-file deployments](#single-file-deployments-publishsinglefile) below). The probe corpus is this kind; a harness usually **spawns its target and attaches** (`poc/drhook-inspection-robustness/adr014-*`). Debug-compiled by default.

### Manage the file-based-app cache when iterating

File-based apps cache their compiled output. A stale cache silently serves an old build, so the running code and its **PDB line-map stop matching your source** — and `drhook_break_source` then binds to the wrong line or not at all (hit in ADR-014: a rebuilt multi-shape target left a stale PDB and the line breakpoint never bound). Defenses:

```bash
# iterate with --no-cache so the build matches your source:
dotnet run --no-cache --file harness.cs -- <args>
```

If a harness **spawns** a target file-based app, spawn THAT with `--no-cache` too — otherwise the debugged target is stale even when the harness is fresh:

```csharp
new ProcessStartInfo("dotnet", $"run --no-cache --file \"{targetPath}\"")
```

### Breakpoints and module resolution

- **Prefer function breakpoints for iterative work.** `drhook_break_function` / `DebugSession.SetBreakpoint(module, type, method)` resolve via **metadata tokens** — immune to stale PDB line-maps and unambiguous on multi-method files. `drhook_break_source` / `SetBreakpointAtLine` depend on the PDB line table, which a stale cache breaks. When a line breakpoint won't bind, switch to a function breakpoint and rebuild `--no-cache`.
- **Type names are namespace-qualified** (`SkyOmega.Mercury.Sparql.Types.BindingTable`, not `BindingTable`), and **private methods resolve fine** — metadata ignores accessibility, so you can break in `EnsureStringCapacity`.
- **The module hint is a substring, resolved most-specific-first** — `"Mercury"` resolves to `SkyOmega.Mercury.dll`, not `.Abstractions`/`.Runtime` (ADR-014 taught `FindModule` to prefer an exact assembly-name / trailing-namespace-segment match over the old first-substring-wins). A unique substring still resolves to its one match.

### Single-file *deployments* (`PublishSingleFile`)

Distinct from the file-based "single-file app / harness" above: a **`PublishSingleFile=true`** deployment bundles the managed assemblies into one native apphost. DrHook debugs these — **launch the apphost directly**, not via `dotnet exec`:

```bash
dotnet publish App.csproj -c Debug -r osx-arm64 --self-contained false -p:PublishSingleFile=true
drhook_launch: program=path/to/publish/App, args=[], sourceFile=…, line=…   # the apphost itself, no "dotnet exec"
```

It works because the app still runs on CoreCLR, so ICorDebug attaches. The wrinkle is symbols: the app assembly loads **from the bundle**, so ICorDebug reports it by a bare name with **no on-disk PE**. DrHook recovers the symbols automatically, keyed on `DebugType`:

- **`DebugType=portable`** (the default) — DrHook reads the sidecar `App.pdb` next to the apphost.
- **`DebugType=embedded`** — the PDB is inside the bundle; DrHook reads the loaded module's PE image from target memory and extracts the embedded PDB.

Either way you get source breakpoints, **local names** (from the PDB) and **argument names** (resolved from the loaded module's metadata, since the `Param` table is in the bundle, not the PDB). Still **Debug-compiled** — a Release single-file optimizes away locals and sequence points. See [finding 85](poc/drhook-engine/findings/85-single-file-breakpoints.md) and the `single-file{,-embedded}-smoke.cs` probes.

**NativeAOT is _not_ debuggable.** It compiles to native machine code with **no managed runtime**, so ICorDebug doesn't apply — use native tooling (lldb / the `.dSYM`). Watch out: **`dotnet publish app.cs` on a file-based app defaults to NativeAOT** — pass `-p:PublishAot=false -p:PublishSingleFile=true` to get a debuggable *managed* single file.

### Check the flags first

Before scripting around a tool, read its flags — `--help`, or `/?` on Windows. The MTP runner's real `--filter` / `--list-tests` / `--output` flags were found this way; guessing them wastes a cycle.

## Lifecycle discipline & common pitfalls (for AI operators)

> **Why this section exists.** DrHook is the first interactive debugger built for an LLM operator — there is no training corpus on *driving* a debugger, so the mistakes below are easy to make and **will recur every session unless they are written down**. This section is the training surface. Read it before a debugging session; these are real mistakes made while building the tool, not hypotheticals.

- **No `Debugger.Break()` crutch.** `drhook_launch` takes a `sourceFile`+`line` and arms that breakpoint *before main runs* — the engine holds the target at the entry module's load (ADR-011 Layer-2 hold-gate, derived from the launched module) and binds it there. A `dotnet exec X.dll` launch stops **directly at your breakpoint with no `Debugger.Break()` in the target**. Inserting `Debugger.Break()` is an unnecessary crutch that also litters the source — rely on the launch's initial breakpoint. (A probe *smoke* that calls `DebugSession.Launch` directly gets the same hold-gate by passing `entryModule:`; only without it does it need a self-stop.)

- **End sessions cleanly — `drhook_stop`, not `drhook_kill`.** A well-behaved target ends by running to completion (`drhook_continue` past the last breakpoint → `processExited`) or via `drhook_stop` (graceful SIGTERM-with-grace). **`drhook_kill` is the anomaly escape hatch** — every kill is worth investigating, because it means the target was *stuck*. Do not reach for `drhook_kill` to reset a demo or switch targets; `drhook_stop` does that cleanly. Only one session is active at a time (`drhook_launch`/`drhook_attach` refuse with "a stepping session is already active — use drhook_stop first").

- **Process hygiene — check for orphans, clean them up.** A *failed* `drhook_launch` can leave the spawned target orphaned (dbgshim spawns it suspended, then the launch errors before cleanup); a session you don't end cleanly leaks one too. Periodically run `drhook_processes` and scan for orphaned debuggee targets (your `*-target` / apphost names, or unnamed suspended/`<defunct>` processes) and `kill <pid>` the strays from a shell. Leaked targets hold FDs and memory. (Reap `<defunct>` zombies by letting the parent exit; `kill` only the live `S`/`T`-state strays.)

- **One debugger process debugs ONE runtime version — never mix net10 and net11 targets in a session.** A DrHook debugger process loads the debug components (`mscordbi` + DAC) of the *first* target's runtime version, **process-globally and persisting across session end**; a later target on a *different* runtime version fails with `HRESULT 0x80131C3C` (`CORDBG_E_DEBUG_COMPONENT_MISSING`). This bites because the substrate targets **net10.0** but a file-based app (`dotnet run x.cs`) under the SDK 11 preview builds **net11.0** — so debugging a file-based app (net11) and then a net10 target (a single-file app, a net10 project) mixes versions and the second fails. **Workaround:** keep every target in a session on one runtime version (pin file-based probes with `#:property TargetFramework=net10.0`, or use a net10 project), or **reconnect the MCP when you switch target runtime version**. (Proven root cause + the `lsof`/two-version reproduction: `poc/drhook-engine/findings/86-debugger-locked-to-one-runtime-version.md`. The earlier "single-file fails via MCP" framing here was a misdiagnosis — single-file was just a net10 target debugged after net11.)

- **The launch's initial breakpoint isn't `drhook_break_remove`-able.** It is engine-registered (appears in `drhook_break_list`) but not in the MCP layer's removable set, so `drhook_break_remove` returns "not tracked at the MCP layer". Choose the launch's initial `line` deliberately — if you need a *conditional* breakpoint at that line, launch at a different one-shot line rather than trying to remove the initial one.

## Running the tests

The repo has **two kinds** of test project, and on the .NET 10 SDK they run by **different commands**. Knowing which is which saves a confusing error.

### Standard test projects — `dotnet test`

`Microsoft.NET.Test.Sdk` (xUnit/MSTest-over-VSTest) projects run the usual way:

| Project | Command | Count |
|---------|---------|-------|
| Mercury (W3C conformance + unit) | `dotnet test tests/Mercury.Tests/Mercury.Tests.csproj` | ~4,700 |
| DrHook.Engine (unit) | `dotnet test tests/DrHook.Engine.Tests/DrHook.Engine.Tests.csproj` | 130 |
| Mercury.Solid | `dotnet test tests/Mercury.Solid.Tests/Mercury.Solid.Tests.csproj` | — |
| SkyOmega.Bcl | `dotnet test tests/SkyOmega.Bcl.Tests/SkyOmega.Bcl.Tests.csproj` | — |

Mercury's W3C suites need the test-data submodules first: `./tools/update-submodules.sh`. Run one test with `--filter "FullyQualifiedName~SomeName"`.

### MTP integration tests — run the executable, **not** `dotnet test`

`tests/DrHook.Engine.IntegrationTests` is a **Microsoft.Testing.Platform (MTP)** app (`MSTest.Sdk`). On the .NET 10 SDK, `dotnet test` over the legacy VSTest target is **no longer supported** and fails fast:

> error : Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later.

Build it, then run the produced **executable** directly — it spawns its own debuggee targets and attaches via `DrHook.Engine`:

```bash
dotnet build tests/DrHook.Engine.IntegrationTests/DrHook.Engine.IntegrationTests.csproj -c Debug
./tests/DrHook.Engine.IntegrationTests/bin/Debug/net10.0/DrHook.Engine.IntegrationTests
#   --list-tests                                 # enumerate without running
#   --filter "FullyQualifiedName~AttachToMtpTarget"   # run one
#   --output Detailed                            # verbose
```

These are the **12** substrate-correctness integration tests (ADR-007 Phase 8), macOS/arm64-only today (Phase 9 Open). `DrHook.Engine.IntegrationTargets.{Mtp,Vstest}` are their *targets*, not test projects — don't run them directly.

> **Whole-solution caveat.** `dotnet test SkyOmega.sln` trips the same VSTest error on the MTP project (it is in the solution). Run the standard projects individually (above) and the integration suite via its executable.

### Inspection-robustness fault probes (ADR-014)

The ref-struct inspection fault closed by [ADR-014](docs/adrs/drhook/ADR-014-inspection-fault-containment.md) has a dedicated repro/localizer in `poc/drhook-inspection-robustness/` — separate from the numbered probe corpus below:

```bash
cd poc/drhook-inspection-robustness
dotnet run --no-cache --file adr014-faultrepro-smoke.cs -- adr014-faultrepro-target.cs <Type> <step>
#   <Type> = NormalBox | PlainRef | Mimic        <step> = args0 | args1 | expand_all | f:<field>
```

Each run attaches, breaks in `<Type>.Touch`, and inspects `this` (`Mimic` is a `BindingTable`-shaped ref struct). The dir README has the shape matrix and the size-law finding.

## PoC probes and findings

The substrate is backed by a probe corpus in `poc/drhook-engine/` — `NN-name-smoke.cs` driver scripts plus optional `NN-name-target.cs` targets, each documenting one falsifiable epistemic act. Numbered ranges:

- **02–40**: pre-substrate-independence probes; baseline ICorDebug interop validation. (PASS at HEAD on macOS/arm64.)
- **41–46**: ADR-007 Phase 1 + Phase 2 substrate-correctness probes (anomaly injection, dispose-during-resume race, pause/stopping race, detach-exit race, worker exception, MTP / legacy-VSTest promotion meta-probes). Complete.
- **47**: external target death (ADR-007 Phase 1 extension; ADR-008 Layer-2 guard).
- **48 / 48b / 48c**: multi-session investigation (led to ADR-008 framing).
- **49–54**: ADR-008 Phase 0 process-lifecycle ground truth probes (signal disposition, ignoring targets, tight CPU loop, default disposition, process tree).
- **55**: substrate two-stage escalation (ADR-008 Increment 1).
- **56**: break-stopped Dispose (ADR-008 Increment 1b).
- **57**: target-defined exception hierarchy (ADR-010 Increment 6).
- **58**: logpoint template typed-arithmetic validation (ADR-010 Increment 7).

Probes call `DrHook.Engine.DebugSession` directly (not the MCP tool layer), so the ADR-010 Tier 1 rename does not affect them.

Run probes with `dotnet run --no-cache <probe-smoke.cs> <target>` from `poc/drhook-engine/`. `--no-cache` matters — file-based-app re-runs use a cached build by default, which can mask engine source edits ([feedback_filebased_app_stale_cache](.claude/projects/-Users-bemafred-src-repos-sky-omega/memory/feedback_filebased_app_stale_cache.md)). Probes 02–06 need a live .NET PID; probes 07+ spawn their own target. `DBGSHIM_PATH` is not required on a machine that has built `DrHook.Engine` once — both the engine's `DbgShim.Resolve` and the probes' local `ResolveDbgShim` walk the per-RID NuGet cache automatically.

## ADR cross-references

- [ADR-006](docs/adrs/drhook/ADR-006-drhook-engine.md) — DrHook.Engine substrate; substrate-independence at 1.8.2. **Accepted.**
- [ADR-007](docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md) — substrate-correctness arc. **Accepted.** Phase 1 Complete, Phase 2 Complete, Phase 3 Superseded (by ADR-009 → ADR-010), Phases 4-6 Closed (substrate proved runner-agnostic), Phase 7 (MCP surface cleanup) subsumed by ADR-010 Tier 1, Phase 8 Complete (12/12 CI), Phase 9 Open (cross-platform campaign).
- [ADR-008](docs/adrs/drhook/ADR-008-process-lifecycle-discipline.md) — natural-exit-by-default, explicit `Abandon` for forced termination; SIGTERM-then-SIGKILL escalation. **Completed.**
- [ADR-010](docs/adrs/drhook/ADR-010-mcp-tool-surface-redesign.md) — MCP tool surface redesign. **Accepted.** Tier 1 (this rename pass) shipped; Tier 2 (substrate-verification tools) and Tier 3 (substrate-addition tools) land in successor increments.
- [ADR-011](docs/adrs/drhook/ADR-011-lifecycle-console-dashboard.md) — debug-session lifecycle (stop / detach / kill) + debuggee console-I/O isolation + the dashboard seam. **Accepted.**
- [ADR-012](docs/adrs/drhook/ADR-012-debug-state-surfaces.md) — debug-state surfaces: a surface-agnostic model + human views (console / TUI / Avalonia) over a Unix-domain-socket transport. **Proposed; Phases 1–2 built (2026-06-26)** — the model + transport + first console view (`DrHook.Wire` / `DrHook.Viz` / `DrHook.Viz.Console`), dogfooded end-to-end. See [Debug-State Visualization](#debug-state-visualization-adr-012-phase-2).

## Key Principle

DrHook closes the gap between "what the code says" and "what the code does." When those diverge, reading the code harder doesn't help — only observation does. This is the EEE methodology applied to debugging: move from Emergence (unknown unknowns) to Epistemics (observed knowns) before Engineering (fixes).
