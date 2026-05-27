# DrHook Test Debugging: Consolidated Assessment

Status: working analysis / design note
Scope: DrHook 1.8.x rewrite, `dotnet test`, VSTest, Microsoft.Testing.Platform, xUnit / NUnit / MSTest, IDE runners, NCrunch, `dbgshim`, `ICorDebug`, BCL-only constraint.
Supersedes: earlier note “DrHook Test Debugging: dotnet test, Test Hosts, and dbgshim”.

## Executive summary

A debugger should not treat `dotnet test` as the debuggee. `dotnet test` is orchestration. The actual debug surface depends on which of two test platforms the project uses:

- **Classic VSTest projects** run tests in a separate `testhost` process spawned by `vstest.console`. The debugger must attach to `testhost`, not to `dotnet test`.
- **Microsoft.Testing.Platform (MTP) projects** are themselves executables. The test project *is* the test runner. The debugger can launch the executable directly under its control.

For DrHook under Sky Omega’s BCL-only discipline, this distinction is load-bearing. The MTP path is BCL-clean and architecturally simple — DrHook launches an executable, no test-platform plumbing required. The VSTest path requires either an undocumented stdout-parsing fallback (`VSTEST_HOST_DEBUG=1`) or `Microsoft.TestPlatform.TranslationLayer` (a NuGet, BCL-violating). The strategic position is therefore: MTP-first, env-var as legacy compat, TranslationLayer never.

This document consolidates the architecture, the constraints, the detection logic, and the recommended implementation strategy for DrHook 1.8.x.

## The debug stack: layered model

```text
IDE button / CLI command
    ↓
Test platform orchestration (vstest.console OR MTP exe)
    ↓
Test host process (testhost.dll OR the test exe itself)
    ↓
Test framework adapter (xUnit / NUnit / MSTest)
    ↓
Test assembly + product assemblies
    ↓
CoreCLR
    ↑
dbgshim (bootstrap) → mscordbi (ICorDebug surface)
    ↑
Debugger engine (vsdbg / netcoredbg / Rider / DrHook.Engine)
    ↑
Editor / UI / protocol layer (DAP / VS / Rider / DrHook.Mcp)
```

The test framework is invisible to the debug substrate. Once managed code is JITted, breakpoints are normal source breakpoints resolved via PDB sequence points → metadata token + IL offset → `ICorDebugBreakpoint`. xUnit, NUnit, and MSTest differ only in discovery, lifecycle, filtering, and reporting — none of which touch the breakpoint primitive.

## The two control planes

There are two ways to make the test host stop and wait for a debugger. They are not equivalent, and the difference matters for DrHook.

### Plane 1: environment variables (CLI / container path)

The test platform supports `VSTEST_HOST_DEBUG=1` (testhost halts and prints its PID) and `VSTEST_RUNNER_DEBUG=1` (vstest.console halts and prints its PID). The debugger reads the PID from stdout and attaches.

Properties:

- Works from any shell, no IDE required.
- Wire format (the “Process Id: NNNN” message) is undocumented and has shifted between vstest versions.
- The testhost is past CLR startup by the time the debugger attaches — some module-load events have already fired.
- `IFrameworkHandle.LaunchProcessWithDebuggerAttached(...)` from inside an adapter **fails** in this mode with “operation not allowed in non-debug run” — child-process debugging is broken.
- The only viable VSTest debugging path that does not require consuming Microsoft.TestPlatform.TranslationLayer.

### Plane 2: `ITestHostLauncher` (IDE path)

The IDE references `Microsoft.TestPlatform.TranslationLayer`, constructs a `VsTestConsoleWrapper`, and registers an `ITestHostLauncher`. When `vstest.console` would normally spawn testhost, it instead sends the `TestProcessStartInfo` upstream over a JSON-over-TCP protocol. The IDE’s `LaunchTestHost` runs, calls into its debugger to spawn the process *with debugger already attached from process start*, and returns the PID.

```text
IDE  ── RunTestsWithCustomTestHost(launcher) ──>  vstest.console
                                                       │
                                                  spawn testhost?
                                                       │
            LaunchTestHost(TestProcessStartInfo) ──────┘
                       (IDE launches under its debugger)
                       (returns PID)
                                                       │
                                                  connect via socket
                                                       │
                                               adapter receives
                                               IFrameworkHandle
```

Properties:

- Debugger attached before any CLR code runs — full module-load coverage.
- `IFrameworkHandle.LaunchProcessWithDebuggerAttached` works because the testhost-side implementation relays back over the same protocol; the IDE attaches to the child.
- Documented, versioned, stable protocol surface.
- **Requires a NuGet dependency** on `Microsoft.TestPlatform.TranslationLayer`.

The category clarification: `IFrameworkHandle` is the adapter-side interface, given to `ITestExecutor.RunTests` by the platform inside testhost. `ITestHostLauncher` is the IDE-side interface, registered upstream into `VsTestConsoleWrapper`. They are two ends of one logical channel; `vstest.console` brokers between them.

## What runs where: process topology

### Classic VSTest

```text
dotnet test  (orchestrator; does not execute user code)
    │
    └──> vstest.console  (test platform runner)
              │
              └──> testhost  (loads adapter, executes user code, CoreCLR runs here)
                       │
                       └──> adapter (xUnit / NUnit / MSTest)
                                  │
                                  └──> test assembly + product
```

Note: on .NET Core 5+ both Windows and Unix use `dotnet exec testhost.dll`. The `testhost.exe` name is the .NET Framework path; it is not the modern shape.

### Microsoft.Testing.Platform

```text
test executable  (IS the runner; executes user code; CoreCLR runs here)
    │
    └──> MTP framework integration
              │
              └──> test assembly + product
```

This is structurally simpler. No vstest.console. No testhost. No translation layer. The test project compiles to an exe that runs tests when invoked. From DrHook’s perspective, debugging this is identical to debugging any other executable — which is exactly what DrHook.Engine is for.

## Where breakpoints actually live

A source breakpoint must be modeled as a pending breakpoint until the relevant module loads.

```text
source file + line
    ↓ (resolve via PDB at module load)
PDB sequence point
    ↓
method metadata token + IL offset
    ↓
ICorDebugFunction → ICorDebugCode
    ↓
ICorDebugBreakpoint
```

DrHook does not need framework-specific breakpoint logic. It needs:

- reliable module-load callback handling;
- portable PDB reading;
- source path normalization (shadow copy, NCrunch workspaces, CI artifacts);
- a pending breakpoint registry that can hold breakpoints prior to module load and activate them when the module appears.

## Test frameworks (xUnit / NUnit / MSTest)

All three plug into the same vstest adapter contract (`ITestDiscoverer` / `ITestExecutor`) and the same MTP surface. They differ only above the debug substrate:

- **xUnit**: VSTest via `xunit.runner.visualstudio` (v2). MTP via `xunit.v3` (v3 was designed around MTP).
- **NUnit**: VSTest via `NUnit3TestAdapter`. MTP support in recent versions, integration is younger.
- **MSTest**: VSTest via `MSTest.TestAdapter`. MTP is first-class — MSTest was the launch vehicle for MTP. `MSTest.Sdk` rolls the entire MTP setup into a single SDK reference.

From DrHook’s perspective these are runner choices, not debugger concerns.

## IDE and runner differences

|Tool                |Test platform                        |Debug attach mechanism                                                       |
|———————|-————————————|——————————————————————————|
|Visual Studio       |VSTest (+ MTP)                       |TranslationLayer + `ITestHostLauncher` + `IVsDebugger.LaunchDebugTargets`    |
|VS Code (C# Dev Kit)|VSTest (+ MTP)                       |TranslationLayer + DAP launch to vsdbg                                       |
|Rider               |VSTest, MTP, or direct framework APIs|TranslationLayer or native framework execution + Rider’s own ICorDebug client|
|NCrunch             |Own runner (not vstest)              |Spawns processes, hands PID to IDE debugger                                  |
|`dotnet test` CLI   |VSTest or MTP                        |Env-var halt + manual attach by PID                                          |

NCrunch is structurally different: continuous, concurrent, process-pooling, with coverage instrumentation and runtime data inspection. It emulates xUnit / NUnit / MSTest execution rather than going through vstest. For DrHook, NCrunch is best treated as an external execution environment — identify the CLR process running the desired test and attach.

## The dbgshim question

`dbgshim` is the bootstrap layer for managed debugging on .NET Core / 5+. It is small. It does three things:

- `GetStartupNotificationEvent(pid)` — signal when CoreCLR loads in a target process;
- `EnumerateCLRs` — find CLR instances in a target process;
- `CreateVersionStringFromModule` + `CreateDebuggingInterfaceFromVersion` — locate the correct `mscordbi.dll` for the target’s CLR version and produce an `ICorDebug`.

The actual debugging surface is `ICorDebug` (in `mscordbi.dll`). That is where breakpoints, stepping, callstacks, locals, and eval live.

So the precise statement: **CoreCLR managed source debuggers converge on `ICorDebug`, reached through `dbgshim`. Not every diagnostics tool is an `ICorDebug` debugger.**

Distinct stacks:

- `ICorDebug` (via `dbgshim`) — managed source debugging. vsdbg, netcoredbg, Rider, DrHook.
- SOS + native debugger — dump and live runtime inspection.
- ClrMD (`Microsoft.Diagnostics.Runtime`) — managed heap and dump inspection.
- EventPipe / diagnostics IPC — tracing, runtime events, dotnet-counters, dotnet-trace.
- Profiling API — sampling, instrumentation, allocation tracking.

DrHook’s target substrate is `dbgshim` → `ICorDebug`. The other stacks are out of scope for the debugger but relevant for the broader observability story.

## BCL-only constraint: TranslationLayer analysis

`Microsoft.TestPlatform.TranslationLayer` has BCL-like surface properties: first-party, pure managed, stable contract, ships with the SDK. But it is not BCL, and the gap matters.

What BCL-only actually defends:

- **Deployment determinism**. A clean `netX` csproj with no `<PackageReference>` either builds or it doesn’t. PackageReference means `dotnet restore`, NuGet feed access, mirror config, offline caches — the full supply-chain surface that BCL-only exists to eliminate.
- **Reproducibility horizon**. The BCL versions with the runtime. TranslationLayer versions independently. A Mercury-style “bit-identical from same source after N years” guarantee requires no NuGet graph in the build.
- **Transitive closure**. BCL has no transitive NuGet graph. TranslationLayer’s transitive deps are a question to re-ask on every release.

The rule’s power is that it is binary. “Microsoft-owned and SDK-shipped” as an exception turns a rule into a taste, and every future package becomes a judgment call.

Sovereignty argument (orthogonal to packaging): TranslationLayer is a client of `vstest.console`. Adopting it makes DrHook downstream of the test platform — DrHook’s behavior becomes a function of what vstest decides across versions, which `ITestHostLauncher` revision is current, which testhost protocol is negotiated. That is the inverse of what DrHook should be. DrHook should be upstream of test execution: it owns the debug surface; test runners are one of several launch shapes it supports.

MTP direct-launch preserves both constraints simultaneously. No NuGet, no vstest protocol, no downstream coupling.

## MTP detection: how a project chooses its runner

MTP does not activate by accident. Three signals, in order of authority:

1. **SDK declaration**: `<Project Sdk=“MSTest.Sdk”>` → MTP (cleanest signal).
1. **MSBuild properties**: `<EnableMSTestRunner>true</EnableMSTestRunner>` or `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>` → MTP. Add `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>` to route `dotnet test` through MTP rather than VSTest.
1. **Build output ground truth**: a `<ProjectName>.exe` (or `.dll` with `Main` + apphost) → MTP. This is the most reliable signal because it survives property overrides in `Directory.Build.props`.

DrHook detection chain:

```text
1. Read csproj. SDK == “MSTest.Sdk”? → MTP.
2. Else, evaluate EnableMSTestRunner / UseMicrosoftTestingPlatformRunner. True? → MTP.
3. Else, inspect build output. Executable produced? → MTP.
4. Else → VSTest.
```

Implication: mode selection is project inspection, not a user flag. The CLI surface stays clean (`drhook debug-test —project ... —filter ...`); DrHook figures out the rest. A `—force-vstest` / `—force-mtp` override exists for the unusual case but is not the default surface.

Distribution forecast: greenfield projects increasingly default to MTP; existing projects remain VSTest indefinitely because migration is a per-project decision. Both paths must work for the foreseeable future.

## Recommended DrHook 1.8.x strategy

Priority order (revised from the earlier note):

### 1. MTP direct-launch — primary

```bash
drhook debug-test \
    —project ./tests/My.Tests/My.Tests.csproj \
    —framework net10.0 \
    —filter “FullyQualifiedName~MyNamespace.MyTests.MyTest”
```

Internal behavior:

```text
- detect MTP via project inspection
- resolve test executable for target framework
- launch under DrHook.Engine
- pass filter arguments
- register pending breakpoints
- bind on module load
- normal debug flow
```

This is the BCL-clean, sovereignty-preserving, architecturally simplest path. No special test plumbing in the engine. Should be the default for projects where MTP is detected.

### 2. Attach mode — universal

```bash
drhook attach-test —pid 12345
drhook attach-test —select          # interactive picker
```

Internal behavior:

```text
- enumerate candidate dotnet / testhost / MTP-exe processes
- user or agent selects
- attach via DrHook.Engine
- register pending breakpoints
- bind on module load (already-loaded modules require eager binding)
- continue
```

Handles NCrunch, IDE-launched test hosts, manually launched tests, unusual topologies. No assumptions about runner.

### 3. VSTest env-var mode — legacy compatibility

```bash
drhook debug-test \
    —project ./tests/Legacy.Tests/Legacy.Tests.csproj \
    —runner vstest
```

Internal behavior:

```text
- launch `dotnet test` with VSTEST_HOST_DEBUG=1
- capture stdout/stderr
- parse testhost PID (acknowledge: fragile, undocumented format)
- attach to testhost
- register pending breakpoints
- bind on module load
- continue stopped testhost
- accept that IFrameworkHandle.LaunchProcessWithDebuggerAttached will not work
```

Required for the long tail of pre-MTP projects. Marked as legacy in docs. No `ITestHostLauncher` / TranslationLayer integration ever.

## Edge cases

- **Multiple testhost processes**. Parallel execution and multi-targeting can produce more than one. Initial support: one target with explicit selection. Later: multi-process debug sessions.
- **Source path mismatch**. PDB source paths may not match the current checkout — generated files, CI artifacts, shadow copy, NCrunch workspaces. Source path normalization and lookup policies required.
- **Test filter semantics**. Runner / framework-specific. Pass through verbatim; do not invent a unified semantic model.
- **Shadow copying and isolated workspaces**. Some runners execute from temp directories. Breakpoint binding depends on PDB / source mapping, not assembly location.
- **Build vs no-build**. `—no-build` is valuable for debugging stable compiled output. Do not silently force it; expose as an explicit option.
- **Test host timeout**. In env-var mode the testhost waits indefinitely for attach. DrHook needs a configurable timeout and clear diagnostic if no PID appears.
- **Adapter diagnostics**. Some failures are adapter / runner failures, not debugger failures. Preserve runner stdout / stderr and surface it explicitly. Do not swallow.
- **CLR startup race in env-var mode**. The testhost is already past some module-load events by the time the debugger attaches. Pending breakpoints in early-loaded modules must be resolved against already-loaded modules at attach time, not only via future module-load callbacks.
- **Mixed-target solutions**. A project with `<TargetFrameworks>` produces multiple executables / testhosts. Framework selection is a first-class CLI argument, not an inference.

## Implementation boundary

DrHook.Engine — debug substrate only:

- launch / attach
- module load callbacks
- breakpoint binding (pending → active)
- stepping
- locals / arguments
- eval
- callbacks
- continue / pause / stop lifecycle
- detach / terminate

DrHook.Testing (or DrHook.Mcp’s testing surface) — orchestration:

- project inspection (MTP vs VSTest detection)
- test executable resolution
- `dotnet test` command construction (legacy path only)
- environment variable management (legacy path only)
- stdout / stderr capture and parsing (legacy path only)
- PID discovery (legacy path only)
- test filter arguments
- runner-specific diagnostics
- NCrunch and IDE-launched process discovery

This separation keeps the engine clean. Test runner complexity does not contaminate the debugging substrate. The engine remains a general-purpose managed debugger that happens to be useful for tests because tests run in CLR processes.

## Design position

DrHook does not become a test runner. DrHook becomes capable of debugging the CLR processes that test runners create.

```text
Test runner responsibility:
- discover tests
- select tests
- schedule tests
- execute framework lifecycle
- report test results

DrHook responsibility:
- identify or launch the managed debuggee process
- attach through dbgshim / ICorDebug
- bind breakpoints
- step
- inspect
- eval
- surface runtime truth
```

In Sky Omega terms: test runners provide execution choreography; DrHook provides runtime observability sovereignty. The MTP path makes this clean because there is no choreography layer between DrHook and the CLR — the test executable is just an executable.

## Open questions

- **MTP filter argument stability**. The MTP CLI argument surface for test filtering is younger than VSTest’s. Verify stability across MSTest, xUnit.v3, NUnit MTP integrations before committing to a unified filter pass-through.
- **Portable PDB vs Windows PDB**. Modern .NET emits portable PDBs by default; older builds may produce Windows PDBs. The PDB reader must handle both. Decide whether to implement portable PDB reading from spec (BCL-clean) or accept `System.Reflection.Metadata` (likely already in BCL surface, verify).
- **Attach-mode discovery on Linux**. Process enumeration and CLR detection on Linux for the `—select` path. Verify what’s possible via `/proc` alone vs requiring additional surface.
- **NCrunch integration depth**. Initial position is attach-only. Future question: is there value in any deeper NCrunch integration, and does NCrunch expose a stable surface for it?
- **Symbol server policy**. PDBs may live on Microsoft’s symbol server, on a private symbol server, or alongside the assembly. Policy for symbol resolution under BCL-only — explicit configuration vs convention-based discovery.

## References

- `dotnet test` command: <https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test>
- MTP run and debug: <https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-run-and-debug>
- VSTest diagnostics: <https://github.com/microsoft/vstest/blob/main/docs/diagnose.md>
- VSTest Test Host Runtime Provider RFC (ITestHostLauncher): <https://github.com/microsoft/vstest-docs/blob/main/RFCs/0025-Test-Host-Runtime-Provider.md>
- VSTest TranslationLayer RFC: <https://github.com/microsoft/vstest-docs/blob/main/RFCs/0008-TranslationLayer.md>
- `CreateDebuggingInterfaceFromVersionEx`: <https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/createdebugginginterfacefromversionex-function>
- `ICorDebug` interface: <https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebug-interface>
- VS Test Explorer: <https://learn.microsoft.com/en-us/visualstudio/test/run-unit-tests-with-test-explorer>
- VS Code C# testing: <https://code.visualstudio.com/docs/csharp/testing>
- Rider unit testing: <https://www.jetbrains.com/help/rider/Unit_Testing__Index.html>
- NCrunch: <https://www.ncrunch.net/>
- MSTest.Sdk: <https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-sdk>
- xUnit v3: <https://xunit.net/docs/getting-started/v3/cmdline>
- NUnit3TestAdapter: <https://docs.nunit.org/articles/vs-test-adapter/Index.html>