# Finding 42: Probe 33 Outcome — PASSED: `DebugSession.Launch` (attach BEFORE main runs)

**Status:**   **PASSED, exit 0**, no orphan after PID-kill cleanup; 47 unit tests still pass. Third
Phase 3 substrate gap closed: `DebugSession.Launch(program, args, cwd, sink)` spawns a .NET process
under debug control via dbgshim's `RegisterForRuntimeStartup` flow, attaching BEFORE any managed
code runs. The target's `Debugger.Break()` at the top of `Main` is the first stop received. Backs
`drhook_step_run` (and, via PID discovery, `drhook_step_test`) in the MCP rewrite.
**Date:**     2026-05-23
**Probe:**    `poc/drhook-engine/33-launch-smoke.cs` + `33-launch-target/` (Launch.csproj +
              Program.cs — a precompiled csproj target, not a file-based app)

## Design — `RegisterForRuntimeStartup` flow

`ICorDebug` has no `CreateProcess`; debugger-launch on dbgshim is a four-step dance:

1. **`CreateProcessForLaunch(cmdLine, suspendProcess=TRUE, env, cwd, &pid, &resumeHandle)`** —
   spawn the process **suspended** so it cannot run before we register.
2. **`RegisterForRuntimeStartup(pid, callback, ctx, &unregisterToken)`** — install a static callback
   that will be invoked once the runtime has initialized. Our `[UnmanagedCallersOnly]` thunk
   `StartupCallbackThunk(pCordb, parameter, hr)` recovers a `GCHandle.ToIntPtr`'d
   `StartupContext` (a small reference object with `PCordb`, `HResult`, and a `ManualResetEventSlim`)
   and signals the waiter.
3. **`ResumeProcess(resumeHandle)` + `CloseResumeHandle(resumeHandle)`** — let the process run; the
   runtime initializes and calls our callback.
4. **`Signaled.Wait(30s)`** — block until the callback fires (or fail on timeout). On success the
   context holds an `ICorDebug*` and we proceed to `DebugActiveProcess(pid)` like the Attach path.

The post-cordbg setup (`ICorDebug.Initialize` → build pump + callback vtable → `SetManagedHandler`
→ `DebugActiveProcess` → controller → `pump.Start`) is factored into a private static
`FromCordbg(dbgShim, sink, pid, pUnknown)` so Attach and Launch share it by construction.

## End-to-end

```
target     : dotnet /…/33-launch-target/bin/Debug/net10.0/Launch.dll
plan       : Launch -> expect Break (Debugger.Break before main loop) -> set bp at Program.cs:16
             -> resume -> expect Breakpoint with local v in scope.
launched   : pid=32659 (debugger attached before main)
launched                          ← target's Console.WriteLine, before Debugger.Break
stop 1     : Break  (proves attach-before-main: Debugger.Break fired)
breakpoint : Program.cs:16 id=1
stop 2     : Breakpoint  v=0
PROBE 33 PASSED
```

That first `launched` line in the smoke's output is **the target's** stdout (see the fd-inheritance
note below) — it confirms `Main` actually ran. The Break stop next then confirms attachment is in
place when `Debugger.Break()` executes.

## Probe target — precompiled csproj, not a file-based app

I chose a real csproj (`33-launch-target/Launch.csproj` + `Program.cs`) and ran `dotnet build`
once. A file-based app target would have gone through `dotnet`'s compile-then-exec — the spawned
`dotnet` process would compile the script, exec a CHILD process for the actual run, and our
`RegisterForRuntimeStartup` would have attached to the WRONG process (the parent compiler, not the
child runner). Same class as the `drhook_step_test` VSTEST_HOST_DEBUG indirection.

The target's source is the canonical "attach before main" shape:

```csharp
Console.WriteLine("launched");
Debugger.Break();
for (int i = 0; i < 100; i++) { int v = i * 2; GC.KeepAlive(v); /* PROBE_BREAK */ Thread.Sleep(20); }
```

`Debugger.Break()` requires `Debugger.IsAttached == true` — and it fires here, which is the
unambiguous proof that the debugger was attached by then.

## Cleanup — what the first run surfaced

The first probe-33 run **passed semantically** but the launched target outlived the smoke. Root
cause: at probe end the process was at the Breakpoint stop; the probe called `session.Dispose()`,
whose `Detach()` path leaves a currently-synchronized process synchronized indefinitely (no
implicit Continue across Detach). The target hung at the breakpoint forever — and because the
target had inherited the smoke's stdout fd (POSIX default; the orphan kept the parent's pipe open),
the visible symptom was "no output from the smoke" until I killed the orphan.

Fix: the probe now kills the launched PID before `Dispose`:

```csharp
try { Process.GetProcessById(session.ProcessId).Kill(entireProcessTree: true); } catch { }
Thread.Sleep(200);
try { session.Dispose(); } catch { }
```

Re-run: exit 0, no orphan.

## Engine-side follow-ons surfaced by the probe

- **Terminate-on-dispose for Launched sessions.** For an Attached session, Dispose-without-resume
  is fine (the user owns the target's lifecycle). For a Launched session, *the engine owns the
  target* — Dispose should `ICorDebugProcess::Terminate(exitCode)` (or implicit Continue then
  Terminate) so the target doesn't outlive the session. Tracked as a Phase 3 polish item.
- **stdout/stderr isolation.** `CreateProcessForLaunch` inherits parent fds by default — the
  target's output landed in the smoke's stdout. For `drhook_step_run`, the MCP layer wants to
  **capture** the target's stdout (so it can return it as a tool response), not have it leak. The
  fix is dedicated pipes for the child's stdout/stderr; today we'd need to enrich the env/handles
  argument to `CreateProcessForLaunch`. Out of scope for "Launch works," in scope for "Launch is
  production-ready."

## Engine increment

- **`DbgShim`** — five new function-pointer fields wired in the constructor:
  `CreateProcessForLaunch`, `ResumeProcess`, `CloseResumeHandle`, `RegisterForRuntimeStartup`,
  `UnregisterForRuntimeStartup`.
- **`DbgShim.LaunchWithDebugger(commandLine, cwd, startupTimeout, out pid, out pUnknown)`** —
  orchestrates the four-step flow; unregisters + frees the GCHandle in `finally`.
- **`DbgShim.StartupCallbackThunk`** — `[UnmanagedCallersOnly]` static method; recovers the
  `StartupContext` via `GCHandle.FromIntPtr(parameter)` and signals the event.
- **`DebugSession.Launch(program, args, cwd, sink)`** — static factory; builds the command line
  with simple whitespace quoting, calls `LaunchWithDebugger`, then `FromCordbg`.
- **`DebugSession.FromCordbg`** (private static) — the post-cordbg setup factored out and now
  shared by both Attach and Launch.

47 unit tests still pass (no new ones — the orchestration is timing-dependent and best validated
live by the probe).

## Scope / next

ADR-006 Phase 3 status:

- [x] AsyncBreak (probe 31, finding 40)
- [x] Breakpoint registry (probe 32, finding 41)
- [x] **Launch** (this finding)
- [ ] Persistent exception filter — generalize `WaitForExceptionPolicyStop` to a register-once form
- [ ] Object inspection (depth ≥ 1) — `ICorDebugType` / `GetFieldValue` / string + array rendering
  (still the longest pole)
- [ ] `SteppingSessionManager` rewrite + regression suite + netcoredbg retirement
- [ ] Polish: Terminate-on-dispose for Launched sessions; stdout/stderr capture pipes

## References

- Probe: `poc/drhook-engine/33-launch-smoke.cs`, `33-launch-target/Launch.csproj`,
  `33-launch-target/Program.cs`
- Fixture: `fixtures/33-launch-osx-arm64-20260523T043511Z.txt`
- Engine: `Interop/DbgShim.cs` (Launch wiring + `StartupCallbackThunk`), `DebugSession.Launch`,
  `DebugSession.FromCordbg` (factored)
- Findings 40 (AsyncBreak — same Phase 3 arc), 41 (breakpoint registry — same arc)
- Mercury session 2026-05-22 observation `probe-33-launch`
