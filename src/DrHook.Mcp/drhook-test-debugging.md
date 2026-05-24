# DrHook Test Debugging: `dotnet test`, Test Hosts, and `dbgshim`

Status: working analysis / design note  
Scope: DrHook, DrHook.Engine, .NET test debugging, `dotnet test`, xUnit, NUnit, MSTest, VSTest, Microsoft.Testing.Platform, IDE runners, NCrunch, `dbgshim`, and `ICorDebug`.

## Executive summary

A debugger should not treat `dotnet test` itself as the normal debuggee.

For the traditional VSTest-based path, `dotnet test` is primarily the driver process. It builds, discovers, coordinates, and launches a separate test execution process. The actual test code normally runs inside a test host process, commonly `testhost.exe` on Windows or a `dotnet exec testhost.dll` style process on Unix-like systems. That test host is the process where breakpoints in xUnit, NUnit, or MSTest code must bind.

For a DrHook-style debugger built on `dbgshim` and `ICorDebug`, the correct model is:

```text
DrHook launches or observes the test runner
    ↓
The runner creates or identifies the real test host
    ↓
DrHook attaches to or launches the CLR process that executes the test assembly
    ↓
DrHook binds pending source breakpoints when modules and PDBs are loaded
    ↓
The test runner remains responsible for discovery, filtering, scheduling, and framework semantics
```

The debugger does not set an “xUnit breakpoint”, “NUnit breakpoint”, or “MSTest breakpoint”. Once the test assembly is executing, breakpoints are normal managed source breakpoints resolved through metadata, IL, and PDB information.

The test framework affects discovery and execution lifecycle. It does not change the low-level CoreCLR breakpoint primitive.

## Core conclusion

DrHook should implement test debugging as an explicit launch/attach strategy, not as a special breakpoint type.

There are two main execution models to support:

1. Classic VSTest model.
2. Microsoft.Testing.Platform / direct executable model.

The VSTest model requires DrHook to attach to the test host process. The MTP/direct model can often be handled by launching the test executable itself under the debugger.

## Classic `dotnet test` / VSTest model

In the traditional VSTest path, the process tree is conceptually:

```text
dotnet test
    ↓
VSTest infrastructure
    ↓
framework adapter: xUnit / NUnit / MSTest
    ↓
testhost process
    ↓
test assembly + product assemblies
    ↓
CoreCLR
```

The important distinction is that `dotnet test` is not normally where user test code executes. It is the orchestration process.

### Debugging the test host

VSTest supports explicit debugging waits through environment variables.

For the test host:

```bash
VSTEST_HOST_DEBUG=1 dotnet test ./tests/My.Tests/My.Tests.csproj \
  —no-build \
  —filter “FullyQualifiedName~MyNamespace.MyTests.MyTest”
```

The runner prints a message asking for a debugger to attach to the test host process and includes the process ID. DrHook can parse that output, identify the PID, and attach using its `dbgshim` / `ICorDebug` attach path.

For the runner itself:

```bash
VSTEST_RUNNER_DEBUG=1 dotnet test ./tests/My.Tests/My.Tests.csproj
```

That is useful when debugging VSTest infrastructure or adapter-level problems, not usually when debugging the user’s test code.

### DrHook launch strategy for VSTest

Recommended sequence:

```text
1. User requests: debug this test / test method / test project.
2. DrHook creates a pending breakpoint set from source file + line or method name.
3. DrHook launches `dotnet test` with:
   - `VSTEST_HOST_DEBUG=1`
   - optional `—no-build`
   - optional `—filter ...`
   - optional logger/diagnostic arguments
4. DrHook captures stdout/stderr from the runner.
5. DrHook detects the test host wait message and extracts the PID.
6. DrHook attaches to that PID via `dbgshim` / `ICorDebug`.
7. DrHook waits for module-load callbacks.
8. When the target assembly or product assembly loads, DrHook resolves pending breakpoints using PDB information.
9. DrHook activates the breakpoints.
10. DrHook continues the stopped test host.
11. Normal debugging begins: breakpoint hit, step, locals, arguments, eval, continue.
```

### Breakpoint binding

A source breakpoint should be represented internally as a pending breakpoint until the relevant module and PDB are loaded.

Conceptually:

```text
source file + line
    ↓
PDB sequence point
    ↓
method metadata token + IL offset
    ↓
ICorDebugFunction / ICorDebugCode
    ↓
ICorDebugBreakpoint
```

This means DrHook does not need framework-specific breakpoint logic for xUnit, NUnit, or MSTest. It needs reliable module-load handling, PDB reading, source mapping, and pending breakpoint activation.

## Microsoft.Testing.Platform / direct executable model

In .NET 10 and later, `dotnet test` can use either VSTest or Microsoft.Testing.Platform, depending on project and configuration. Microsoft.Testing.Platform changes the debugging model because test projects are executable applications.

The conceptual process tree can become much simpler:

```text
test executable
    ↓
test framework integration
    ↓
test assembly + product assemblies
    ↓
CoreCLR
```

For DrHook, this is attractive because the process launched under the debugger can be the same process that runs the tests.

Recommended MTP strategy:

```text
1. Resolve the test executable for the test project and target framework.
2. Launch the test executable under DrHook.Engine / ICorDebug.
3. Pass test selection arguments if supported by the project/framework.
4. Register pending source breakpoints.
5. Bind breakpoints on module load.
6. Continue and debug normally.
```

This mode should be preferred when the project is clearly MTP-based and direct executable debugging is supported.

## xUnit, NUnit, and MSTest

xUnit, NUnit, and MSTest differ mainly at the adapter and test framework layer.

They affect:

- test discovery;
- test case identity and filtering;
- fixture setup and teardown;
- parameterized test representation;
- parallelization behavior;
- lifecycle hooks;
- adapter diagnostics;
- framework-specific metadata.

They do not fundamentally change the low-level breakpoint mechanism once managed code is executing inside the CLR.

A debugger should generally treat framework differences as runner/adapter concerns, not debugger substrate concerns.

### xUnit

xUnit commonly integrates with `dotnet test` and VSTest through `xunit.runner.visualstudio`. That adapter lets VSTest-compatible runners discover and execute xUnit tests.

### NUnit

NUnit integrates with VSTest through the NUnit test adapter. The adapter allows NUnit tests to run through Visual Studio, `vstest.console`, and `dotnet test`.

### MSTest

MSTest uses its own framework and adapter packages. `MSTest.TestAdapter` provides discovery and execution integration for VSTest-style runners.

## IDE and runner differences

Visual Studio, VS Code, Rider, and NCrunch do not have to use identical test execution paths, even when they all appear to “debug tests”.

At the user level, they expose a similar capability:

```text
click test → debug test → breakpoint is hit
```

Internally, they may differ significantly.

### Visual Studio

Visual Studio Test Explorer integrates deeply with Microsoft’s test platform. It supports discovery, execution, and debugging of tests. Third-party frameworks require adapters.

Visual Studio’s debugger is not merely shelling out to `dotnet test` in the same naive way a CLI user might. It can coordinate with test infrastructure and attach to the correct process.

### VS Code

VS Code with C# Dev Kit exposes test discovery, run, and debug actions through the VS Code testing UI. The implementation may involve C# Dev Kit, the C# extension stack, Debug Adapter Protocol, and test platform integration.

From DrHook’s perspective, the useful lesson is not the UI behavior. The useful lesson is that VS Code identifies or launches the real debuggee process and binds breakpoints in that process.

### Rider

Rider has its own test runner infrastructure and debugger integration. JetBrains supports NUnit, xUnit, MSTest, VSTest, and Microsoft.Testing.Platform scenarios. Rider should be treated as a rich test runner/debugger integration, not as a thin wrapper around one CLI command.

### NCrunch

NCrunch is special.

It is not just “another way to run `dotnet test`”. It is a continuous and concurrent test runner with runtime data inspection, coverage tracking, separate-process execution, parallelization, and IDE integration.

NCrunch can differ from ordinary `dotnet test` in several ways:

- process isolation;
- parallel execution;
- batching;
- workspace/shadow-copy behavior;
- coverage instrumentation;
- test scheduling;
- runtime data capture;
- automatic versus manual test execution.

For DrHook, NCrunch should be treated as an external execution environment. The clean initial integration is attach-mode debugging: identify the actual CLR process executing the desired test and attach to it.

A deeper NCrunch integration would require understanding NCrunch’s own process model and APIs, and should not be assumed to behave like VSTest.

## Do all debuggers rely on `dbgshim`?

For CoreCLR managed source debugging, serious debuggers converge on the CoreCLR debugging interfaces, especially `ICorDebug`.

`dbgshim` is the official bootstrap layer used to obtain the correct `ICorDebug` implementation for the target runtime. On modern .NET, this is the practical entry point for creating or locating the debugging interface for a specific runtime version.

However, not every runtime inspection tool is an `ICorDebug` debugger.

Different tools may use:

- `ICorDebug` for managed source debugging;
- SOS + native debugger integration for dump/live runtime inspection;
- ClrMD for managed heap and dump/process inspection;
- EventPipe / diagnostics IPC for tracing and runtime events;
- profiling APIs;
- private or product-specific debugger infrastructure.

So the precise statement is:

> CoreCLR managed source debuggers generally rely on the CoreCLR debugging stack, usually reached through `dbgshim` and `ICorDebug`. But not every diagnostics tool is an `ICorDebug` debugger, and not every tool exposes its `dbgshim` usage directly.

For DrHook.Engine, the target substrate is clearly `dbgshim` → `ICorDebug`.

## Recommended DrHook modes

DrHook should expose test debugging as explicit modes instead of hiding all behavior behind a generic launch command.

### 1. VSTest host-attach mode

Example conceptual command:

```bash
drhook debug-test \
  —project ./tests/My.Tests/My.Tests.csproj \
  —framework net10.0 \
  —filter “FullyQualifiedName~MyNamespace.MyTests.MyTest” \
  —runner vstest
```

Internal behavior:

```text
- launch `dotnet test`;
- set `VSTEST_HOST_DEBUG=1`;
- capture runner output;
- extract testhost PID;
- attach to testhost with DrHook.Engine;
- bind pending breakpoints;
- continue testhost.
```

This is the most important compatibility path for existing VSTest-based projects.

### 2. MTP direct-launch mode

Example conceptual command:

```bash
drhook debug-test \
  —project ./tests/My.Tests/My.Tests.csproj \
  —framework net10.0 \
  —runner mtp
```

Internal behavior:

```text
- resolve the test executable;
- launch it directly under DrHook.Engine;
- pass filter arguments if supported;
- bind pending breakpoints;
- debug normally.
```

This should be the preferred path where Microsoft.Testing.Platform is used and direct executable debugging is available.

### 3. Attach mode

Example conceptual command:

```bash
drhook attach-test —pid 12345
```

or:

```bash
drhook attach-test —select
```

Internal behavior:

```text
- enumerate candidate dotnet/testhost processes;
- let user or agent select target;
- attach via DrHook.Engine;
- bind pending breakpoints;
- continue/debug normally.
```

This is useful for:

- NCrunch;
- manually launched tests;
- IDE-launched test hosts;
- unusual runner topologies;
- diagnosing runner-specific behavior.

## Capability checklist for DrHook test debugging

Minimum credible VSTest support:

- launch `dotnet test` with controlled environment;
- set `VSTEST_HOST_DEBUG=1`;
- capture stdout/stderr;
- parse testhost PID;
- attach to the testhost PID;
- register pending breakpoints before module load;
- resolve source breakpoints from PDBs;
- continue the waiting testhost;
- hit breakpoints in test code and product code;
- step over/into/out;
- inspect locals and arguments;
- handle test process exit;
- detach or terminate cleanly;
- surface runner output and test result status.

Next-level support:

- framework-aware test filtering helpers;
- parameterized test display names;
- module/source disambiguation;
- multi-target framework selection;
- parallel test host handling;
- child process tracking;
- hot attach to NCrunch or IDE-owned test hosts;
- symbolic condition/logpoint support during test debugging;
- eval support at breakpoints;
- structured anomaly capture for failed attach/bind/eval cases.

## Important edge cases

### Multiple testhost processes

Parallel test execution or multi-targeting may create more than one testhost process. DrHook must not assume a single child process forever.

Possible strategy:

```text
- initially support one target process;
- detect multiple candidates;
- require explicit selection or filter narrowing;
- later support multi-process debug sessions.
```

### Source path mismatch

PDB source paths may not match the current repository checkout exactly, especially with generated files, CI artifacts, shadow copying, or NCrunch workspaces.

DrHook should support source path normalization and source lookup policies.

### Test filters

Filtering semantics are runner/framework-specific. DrHook should pass filters through rather than inventing its own semantic model too early.

### Shadow copying and isolated workspaces

Some runners may execute copied assemblies from temporary directories. Breakpoint binding should depend on PDB/source mapping, not naive assembly location assumptions.

### Build versus no-build

`—no-build` is useful when debugging stable compiled output. But DrHook should not silently force it unless the user or command mode asks for it.

### Test host timeout

When `VSTEST_HOST_DEBUG=1` is set, the test host waits for attach. DrHook should have a clear timeout and diagnostic error if no PID appears.

### Adapter diagnostics

Some failures are adapter or runner failures, not debugger failures. DrHook should preserve runner output and expose it clearly.

## Recommended implementation boundary

DrHook.Engine should remain focused on the debug substrate:

- attach;
- launch;
- module load;
- breakpoint binding;
- stepping;
- locals/arguments;
- eval;
- callbacks;
- continue/pause/stop lifecycle.

A higher DrHook.Mcp or DrHook.Testing layer should own test-specific orchestration:

- `dotnet test` command construction;
- VSTest/MTP mode detection;
- environment variables;
- stdout/stderr parsing;
- PID discovery;
- test filter arguments;
- runner-specific diagnostics;
- NCrunch/IDE attach affordances.

This keeps the engine clean and prevents test-runner complexity from contaminating the debugging substrate.

## Design position

DrHook should not attempt to become a test runner.

DrHook should become capable of debugging the CLR processes that test runners create.

That is the correct separation:

```text
Test runner responsibility:
- discover tests;
- select tests;
- schedule tests;
- execute framework lifecycle;
- report test results.

DrHook responsibility:
- identify or launch the managed debuggee process;
- attach through dbgshim/ICorDebug;
- bind breakpoints;
- step;
- inspect;
- eval;
- surface runtime truth.
```

In Sky Omega terms: test runners provide execution choreography; DrHook provides runtime observability sovereignty.

## References

- Microsoft: `dotnet test` command: <https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test>
- Microsoft: Microsoft.Testing.Platform run and debug tests: <https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-run-and-debug>
- Microsoft VSTest diagnostics: <https://github.com/microsoft/vstest/blob/main/docs/diagnose.md>
- Microsoft: `CreateDebuggingInterfaceFromVersionEx`: <https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/createdebugginginterfacefromversionex-function>
- Microsoft: `ICorDebug` interface: <https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebug-interface>
- Microsoft: Visual Studio Test Explorer: <https://learn.microsoft.com/en-us/visualstudio/test/run-unit-tests-with-test-explorer>
- Microsoft: VS Code C# testing: <https://code.visualstudio.com/docs/csharp/testing>
- JetBrains Rider unit testing: <https://www.jetbrains.com/help/rider/Unit_Testing__Index.html>
- NCrunch: <https://www.ncrunch.net/>
- xUnit VSTest adapter: <https://www.nuget.org/packages/xunit.runner.visualstudio>
- NUnit VSTest adapter: <https://docs.nunit.org/articles/vs-test-adapter/Index.html>
- MSTest adapter: <https://www.nuget.org/packages/MSTest.TestAdapter/>
