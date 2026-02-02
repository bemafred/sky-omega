# ADR-017: Test Environment Independence

## Status

Proposed

## Context

Integration tests for Mercury CLI tools (`Mercury.Cli.Turtle`, `Mercury.Cli.Sparql`) currently use `dotnet run --project <path>` to execute the CLI tools. This approach has a fundamental dependency on the source tree being present at runtime.

**The Problem:**

NCrunch (and potentially other test runners) copies compiled test assemblies to isolated directories for parallel execution:

```
Source location:     C:\Users\...\source\repos\sky-omega\
NCrunch location:    C:\Users\...\AppData\Local\NCrunch\35348\15\tests\Mercury.Tests\bin\Debug\net10.0\
```

The test code walks up the directory tree looking for `SkyOmega.sln` to locate project files. This fails under NCrunch because the assembly is no longer within the source tree.

**Why This Matters:**

1. **Epistemics:** A test that only runs in some environments creates false confidence. We cannot trust a test suite that behaves differently based on how it's invoked.
2. **EEE Violation:** Skipping tests or marking them as "NCrunch-incompatible" hides information rather than surfacing it.
3. **Parallel Execution:** NCrunch's isolation model is correct for parallel test execution. The tests are wrong, not NCrunch.

## Decision Drivers

- Tests must produce identical results regardless of execution environment
- No skipping, no conditional execution, no "works only in environment X"
- Solution must be maintainable as CLI tools evolve
- BCL-only constraint applies to production code, not test infrastructure

## Options Considered

### Option 1: Embed CLI Assemblies via ProjectReference

Add the CLI projects as ProjectReferences to the test project. Configure MSBuild to copy CLI outputs to the test output directory.

```xml
<ProjectReference Include="..\..\src\Mercury.Cli.Turtle\Mercury.Cli.Turtle.csproj">
  <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
  <OutputItemType>Content</OutputItemType>
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</ProjectReference>
```

Tests then run: `dotnet exec Mercury.Cli.Turtle.dll -- <args>`

**Pros:**
- Tests are fully self-contained
- Works in any environment (NCrunch, CI, local)
- CLI binaries always match the code being tested

**Cons:**
- Increases test project build time
- Test output directory grows (includes CLI dependencies)
- Need to handle CLI subdirectory structure

### Option 2: Publish CLIs to Known Location During Build

Use a pre-test build target to publish CLI tools to a known location (e.g., `artifacts/cli/`).

```xml
<Target Name="PublishCliTools" BeforeTargets="Build">
  <Exec Command="dotnet publish ../Mercury.Cli.Turtle -o $(ArtifactsDir)/cli/turtle" />
</Target>
```

Tests locate CLIs via environment variable or well-known path relative to solution.

**Pros:**
- Clear separation between test code and CLI artifacts
- Can use `--self-contained` for true isolation

**Cons:**
- Still requires solution-relative path knowledge
- Build coordination complexity
- May not work with NCrunch's isolated execution model

### Option 3: Test CLI Logic Directly, Not via Process

Refactor CLI projects to separate entry point from logic. Test the logic directly without process spawning.

```csharp
// Mercury.Cli.Turtle/TurtleCommands.cs (testable)
public static class TurtleCommands
{
    public static int Validate(string path, TextWriter output) { ... }
    public static int Convert(string input, string output, RdfFormat format) { ... }
}

// Mercury.Cli.Turtle/Program.cs (thin entry point)
TurtleCommands.Validate(args[0], Console.Out);
```

**Pros:**
- No process spawning complexity
- Fastest test execution
- Direct access to internals for verification

**Cons:**
- Doesn't test actual CLI invocation (argument parsing, exit codes, stdout/stderr)
- May miss integration issues (encoding, process lifecycle)
- Requires refactoring existing CLI code

### Option 4: Hybrid - Unit Test Logic, Integration Test in CI Only

Use Option 3 for most coverage. Have a small set of true CLI integration tests that only run in CI where the full source tree is available.

**Pros:**
- Best of both worlds
- Fast local development

**Cons:**
- **EEE Violation:** Tests behave differently in different environments
- Defeats the purpose of local test validation

### Option 5: NCrunch Workspace Configuration

Configure NCrunch to copy additional files/folders to the isolated workspace, or to run certain tests in-place rather than isolated.

**Pros:**
- No code changes required
- NCrunch-specific solution for NCrunch-specific problem

**Cons:**
- Couples test suite to specific tool configuration
- Other test runners may have same issue
- Configuration can drift from intent

## Recommendation

**Option 1 (Embed CLI Assemblies)** with elements of **Option 3 (Direct Logic Testing)**.

**Rationale:**

1. Option 1 makes tests truly environment-independent. The test project carries everything it needs.
2. Option 3 principles should be applied regardless - CLI projects should have testable logic separate from the entry point. This is good design independent of the NCrunch issue.
3. The combination provides defense in depth: unit tests for logic, integration tests for CLI invocation, both working everywhere.

## Implementation Plan

### Phase 1: Refactor CLI Projects for Testability

1. Extract command logic from `Program.cs` into testable classes
2. Add unit tests for command logic (no process spawning)
3. Keep existing CLI integration tests temporarily

### Phase 2: Embed CLI Assemblies in Test Project

1. Add ProjectReferences with appropriate MSBuild configuration
2. Update `RunCliAsync` to use `dotnet exec <dll>` from test output directory
3. Verify tests pass under NCrunch

### Phase 3: Validate and Clean Up

1. Run full test suite in multiple environments (dotnet test, VS, NCrunch, CI)
2. Remove any NCrunch-specific workarounds
3. Document the pattern for future CLI tools

## Success Criteria

- [ ] All CLI integration tests pass under NCrunch parallel execution
- [ ] All CLI integration tests pass under `dotnet test`
- [ ] All CLI integration tests pass in CI
- [ ] No conditional test execution based on environment
- [ ] No test skipping
- [ ] CLI command logic has direct unit test coverage

## References

- [NCrunch Documentation: Workspace Configuration](https://www.ncrunch.net/)
- [MSBuild ProjectReference Documentation](https://docs.microsoft.com/en-us/visualstudio/msbuild/common-msbuild-project-items)
- EEE Methodology (see CONTRIBUTING.md)
