# ADR-017: Test Environment Independence

## Status

Superseded by [ADR-018: CLI Library Extraction](ADR-018-cli-library-extraction.md)

## Context

Integration tests for Mercury CLI tools (`Mercury.Cli.Turtle`, `Mercury.Cli.Sparql`) originally used `dotnet run --project <path>` to execute the CLI tools. This approach had a fundamental dependency on the source tree being present at runtime.

**The Problem:**

NCrunch (and potentially other test runners) copies compiled test assemblies to isolated directories for parallel execution:

```
Source location:     C:\Users\...\source\repos\sky-omega\
NCrunch workspace:   C:\Users\...\AppData\Local\NCrunch\25056\15\tests\
```

The test code walked up the directory tree looking for `SkyOmega.sln` to locate project files. This failed under NCrunch because the assembly was no longer within the source tree.

**Why This Matters:**

1. **Epistemics:** A test that only runs in some environments creates false confidence. We cannot trust a test suite that behaves differently based on how it's invoked.
2. **EEE Violation:** Skipping tests or marking them as "NCrunch-incompatible" hides information rather than surfacing it.
3. **Parallel Execution:** NCrunch's isolation model is correct for parallel test execution. The tests were wrong, not NCrunch.

## Decision Drivers

- Tests must produce identical results regardless of execution environment
- No skipping, no conditional execution, no "works only in environment X"
- Solution must be test-runner agnostic (not coupled to NCrunch, VS, or any specific tool)
- Solution must be maintainable as CLI tools evolve

## Decision

**Copy CLI DLLs to the test output directory at build time.**

When the test assembly is copied anywhere (NCrunch workspace, CI agent, etc.), the CLI DLLs travel with it.

### Implementation

**Test project configuration** (`Mercury.Tests.csproj`):

```xml
<!-- CLI projects for integration tests - build only, no assembly reference -->
<ProjectReference Include="..\..\src\Mercury.Cli.Turtle\Mercury.Cli.Turtle.csproj">
  <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
</ProjectReference>
<ProjectReference Include="..\..\src\Mercury.Cli.Sparql\Mercury.Cli.Sparql.csproj">
  <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
</ProjectReference>

<!-- Copy CLI DLLs to test output directory -->
<Target Name="CopyCliTools" AfterTargets="Build">
  <PropertyGroup>
    <TurtleCliSource>$(MSBuildThisFileDirectory)..\..\src\Mercury.Cli.Turtle\bin\$(Configuration)\$(TargetFramework)\Mercury.Cli.Turtle.dll</TurtleCliSource>
    <SparqlCliSource>$(MSBuildThisFileDirectory)..\..\src\Mercury.Cli.Sparql\bin\$(Configuration)\$(TargetFramework)\Mercury.Cli.Sparql.dll</SparqlCliSource>
  </PropertyGroup>
  <Copy SourceFiles="$(TurtleCliSource)" DestinationFolder="$(OutputPath)" SkipUnchangedFiles="true" />
  <Copy SourceFiles="$(SparqlCliSource)" DestinationFolder="$(OutputPath)" SkipUnchangedFiles="true" />
</Target>
```

**Test code finds CLI DLLs adjacent to itself:**

```csharp
var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
var turtleCliPath = Path.Combine(assemblyDir, "Mercury.Cli.Turtle.dll");
var sparqlCliPath = Path.Combine(assemblyDir, "Mercury.Cli.Sparql.dll");

// Run CLI using dotnet exec
var psi = new ProcessStartInfo
{
    FileName = "dotnet",
    Arguments = $"exec \"{turtleCliPath}\" {args}"
};
```

## Alternatives Considered

### Build-time JSON config with absolute paths

Generate a JSON file at build time containing absolute paths to CLI DLLs.

**Rejected because:** NCrunch builds in an isolated workspace, so MSBuild path resolution (`$(MSBuildThisFileDirectory)`) points to the workspace, not the original source tree. The generated paths would be invalid.

### NCrunch-specific configuration

Configure NCrunch to copy source files or run tests in-place.

**Rejected because:** Couples tests to specific tool. Other test runners may have the same issue.

### Test CLI logic directly without process spawning

Refactor CLIs to expose testable command classes.

**Not rejected, but orthogonal:** This is good design regardless, but doesn't replace integration tests that verify actual CLI behavior (argument parsing, exit codes, stdout/stderr).

## Consequences

### Positive

- Tests work identically in all environments (dotnet test, VS, NCrunch, CI)
- No test skipping or conditional execution
- Faster test execution (`dotnet exec` vs `dotnet run --project`)
- Test-runner agnostic - no tool-specific configuration
- Simple implementation - just copy files at build time

### Negative

- Test output directory grows slightly (adds ~90KB for CLI DLLs)
- CLI DLLs are duplicated (original location + test output)

## Validation

- [x] All CLI integration tests pass under `dotnet test`
- [x] Tests pass in Visual Studio
- [ ] Tests pass under NCrunch parallel execution (pending verification)
- [x] No conditional test execution based on environment
- [x] No test skipping

## References

- EEE Methodology (see CONTRIBUTING.md, "Forbidden Patterns" section)
