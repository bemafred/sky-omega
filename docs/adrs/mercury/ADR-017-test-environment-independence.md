# ADR-017: Test Environment Independence

## Status

Accepted

## Context

Integration tests for Mercury CLI tools (`Mercury.Cli.Turtle`, `Mercury.Cli.Sparql`) originally used `dotnet run --project <path>` to execute the CLI tools. This approach had a fundamental dependency on the source tree being present at runtime.

**The Problem:**

NCrunch (and potentially other test runners) copies compiled test assemblies to isolated directories for parallel execution:

```
Source location:     C:\Users\...\source\repos\sky-omega\
NCrunch location:    C:\Users\...\AppData\Local\NCrunch\35348\15\tests\Mercury.Tests\bin\Debug\net10.0\
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

Use a **build-time generated configuration file** containing absolute paths to CLI DLLs.

### How It Works

1. **Build order**: ProjectReferences ensure CLI projects build before the test project
2. **Path generation**: MSBuild target generates `cli-paths.json` with absolute paths to CLI DLLs
3. **Config copied**: The JSON file is written to test output directory (gets copied by any test runner)
4. **Runtime discovery**: Tests read the config file to find CLI DLLs, then run with `dotnet exec`

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

<!-- Generate config file with absolute paths to CLI DLLs -->
<Target Name="GenerateCliPaths" AfterTargets="Build">
  <PropertyGroup>
    <TurtleCliPath>$([System.IO.Path]::GetFullPath('$(MSBuildThisFileDirectory)..\..\src\Mercury.Cli.Turtle\bin\$(Configuration)\$(TargetFramework)\Mercury.Cli.Turtle.dll'))</TurtleCliPath>
    <SparqlCliPath>$([System.IO.Path]::GetFullPath('$(MSBuildThisFileDirectory)..\..\src\Mercury.Cli.Sparql\bin\$(Configuration)\$(TargetFramework)\Mercury.Cli.Sparql.dll'))</SparqlCliPath>
  </PropertyGroup>
  <WriteLinesToFile
    File="$(OutputPath)cli-paths.json"
    Lines="{&quot;TurtleCli&quot;: &quot;$(TurtleCliPath)&quot;, &quot;SparqlCli&quot;: &quot;$(SparqlCliPath)&quot;}"
    Overwrite="true" />
</Target>
```

**Generated config file** (`cli-paths.json`):

```json
{"TurtleCli": "/absolute/path/to/Mercury.Cli.Turtle.dll", "SparqlCli": "/absolute/path/to/Mercury.Cli.Sparql.dll"}
```

**Test code reads config and runs CLI**:

```csharp
var configPath = Path.Combine(assemblyDir, "cli-paths.json");
var paths = JsonSerializer.Deserialize<CliPaths>(File.ReadAllText(configPath));

// Run CLI using dotnet exec (not dotnet run --project)
var psi = new ProcessStartInfo
{
    FileName = "dotnet",
    Arguments = $"exec \"{paths.TurtleCli}\" {args}"
};
```

## Alternatives Considered

### Embed CLI DLLs in test output directory

Copy CLI DLLs to the test output directory so tests are fully self-contained.

**Rejected because:** More complex MSBuild configuration, increases test output size, and the CLI DLLs are already built - we just need to know where they are.

### Test CLI logic directly without process spawning

Refactor CLIs to expose testable command classes.

**Not rejected, but orthogonal:** This is good design regardless, but doesn't replace integration tests that verify actual CLI behavior (argument parsing, exit codes, stdout/stderr).

### NCrunch-specific configuration

Configure NCrunch to copy source files or run tests in-place.

**Rejected because:** Couples tests to specific tool. Other test runners may have the same issue.

## Consequences

### Positive

- Tests work identically in all environments (dotnet test, VS, NCrunch, CI)
- No test skipping or conditional execution
- Faster test execution (`dotnet exec` vs `dotnet run --project`)
- Test-runner agnostic - no tool-specific configuration
- Clear error messages if CLI DLLs not found

### Negative

- Requires JSON file generation during build
- Absolute paths in config file (not portable between machines, but not needed to be)

## Validation

- [x] All CLI integration tests pass under `dotnet test`
- [x] Tests pass in Visual Studio
- [ ] Tests pass under NCrunch parallel execution (to be verified on Windows)
- [x] No conditional test execution based on environment
- [x] No test skipping

## References

- EEE Methodology (see CONTRIBUTING.md, "Forbidden Patterns" section)
