using System.Text.Json.Nodes;
using SkyOmega.DrHook.Stepping;
using Xunit;

namespace SkyOmega.DrHook.Tests.Stepping;

/// <summary>
/// Integration tests that exercise DrHook tools against a live DAP session
/// using the pre-built VerifyTarget as the target. Requires netcoredbg to be installed.
/// </summary>
[Trait("Category", "Integration")]
public class SteppingIntegrationTests : IAsyncLifetime
{
    // Resolved once — the absolute path to the pre-built VerifyTarget DLL and its source
    private static readonly string VerifyTargetDll = ResolveVerifyTargetDll();
    private static readonly string VerifyTargetSource = ResolveVerifyTargetSource();

    // Each test gets a fresh session
    private SteppingSessionManager _session = null!;

    public Task InitializeAsync()
    {
        _session = new SteppingSessionManager();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_session.IsActive)
            await _session.StopAsync(CancellationToken.None);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static string ResolveRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SkyOmega.sln")))
            dir = Path.GetDirectoryName(dir);

        if (dir is null)
            throw new InvalidOperationException("Cannot locate repo root (SkyOmega.sln)");

        return dir;
    }

    private static string ResolveVerifyTargetDll()
    {
        var root = ResolveRepoRoot();
        var path = Path.Combine(root, "tests", "DrHook.Tests", "Stepping", "VerifyTarget",
            "bin", "Debug", "net10.0", "VerifyTarget.dll");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"VerifyTarget.dll not found at {path}. Run: dotnet build tests/DrHook.Tests/Stepping/VerifyTarget/VerifyTarget.csproj");

        return path;
    }

    private static string ResolveVerifyTargetSource()
    {
        var root = ResolveRepoRoot();
        var path = Path.Combine(root, "tests", "DrHook.Tests", "Stepping", "VerifyTarget", "Program.cs");
        if (!File.Exists(path))
            throw new FileNotFoundException($"VerifyTarget Program.cs not found at {path}");

        return path;
    }

    /// <summary>
    /// Find the line number in the VerifyTarget source that contains the given text.
    /// </summary>
    private static int FindLine(string text)
    {
        var lines = File.ReadAllLines(VerifyTargetSource);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(text))
                return i + 1; // 1-based
        }
        throw new InvalidOperationException($"Cannot find '{text}' in {VerifyTargetSource}");
    }

    private async Task<JsonObject> LaunchAtDoWork(CancellationToken ct)
    {
        // Set breakpoint inside the DoWork loop: "sum += i;"
        var line = FindLine("sum += i;");
        var resultJson = await _session.RunAsync(
            "dotnet", ["exec", VerifyTargetDll],
            cwd: null,
            "Program.cs", line,
            "Verify target stops at loop body",
            env: null, ct);

        var result = JsonNode.Parse(resultJson)!.AsObject();
        Assert.Null(result["error"]);
        return result;
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    // ─── Group 1: Session lifecycle ───────────────────────────────────────

    [Fact]
    public async Task Run_StopsAtBreakpoint()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await LaunchAtDoWork(cts.Token);

        Assert.Equal("launched", result["status"]!.GetValue<string>());
        Assert.NotNull(result["currentState"]);
        var state = result["currentState"]!.AsObject();
        Assert.NotNull(state["file"]);
        Assert.NotNull(state["line"]);
    }

    [Fact]
    public async Task Run_RejectsDoubleSession()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await LaunchAtDoWork(cts.Token);

        // Second launch should be rejected
        var line = FindLine("sum += i;");
        var secondJson = await _session.RunAsync(
            "dotnet", ["exec", VerifyTargetDll],
            cwd: null,
            "Program.cs", line,
            "Should fail",
            env: null, cts.Token);

        var second = Parse(secondJson);
        Assert.NotNull(second["error"]);
        Assert.Contains("already active", second["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Stop_ReturnsSummary()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await LaunchAtDoWork(cts.Token);

        var stopJson = await _session.StopAsync(cts.Token);
        var stop = Parse(stopJson);

        Assert.Equal("stopped", stop["status"]!.GetValue<string>());
        Assert.NotNull(stop["totalSteps"]);
        Assert.NotNull(stop["sessionHypothesis"]);
    }

    // ─── Group 2: Stepping ────────────────────────────────────────────────

    [Fact]
    public async Task StepNext_AdvancesLine()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await LaunchAtDoWork(cts.Token);

        var step1Json = await _session.StepNextAsync(null, cts.Token);
        var step1 = Parse(step1Json);

        Assert.Null(step1["error"]);
        Assert.Equal(1, step1["step"]!.GetValue<int>());
        Assert.NotNull(step1["currentState"]);
    }

    [Fact]
    public async Task StepNext_TwoSteps_IncrementStepCount()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await LaunchAtDoWork(cts.Token);

        await _session.StepNextAsync(null, cts.Token);
        var step2Json = await _session.StepNextAsync(null, cts.Token);
        var step2 = Parse(step2Json);

        Assert.Equal(2, step2["step"]!.GetValue<int>());
    }

    // ─── Group 3: Variable inspection ─────────────────────────────────────

    [Fact]
    public async Task Vars_ReturnsLocals()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await LaunchAtDoWork(cts.Token);

        var varsJson = await _session.InspectVariablesAsync(1, cts.Token);
        var vars = Parse(varsJson);

        Assert.Null(vars["error"]);
        Assert.NotNull(vars["variables"]);
        var variables = vars["variables"]!.AsArray();
        Assert.True(variables.Count > 0, "Should have at least one local variable");

        // Should contain 'sum' and 'i'
        var names = variables.Select(v => v?["name"]?.GetValue<string>()).ToList();
        Assert.Contains("sum", names);
        Assert.Contains("i", names);
    }

    // ─── Group 4: Breakpoints ─────────────────────────────────────────────

    [Fact]
    public async Task Breakpoint_AddAndList()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await LaunchAtDoWork(cts.Token);

        // Add a second breakpoint at a different line
        var writeLineLine = FindLine("Console.WriteLine($\"  i={i}, sum={sum}\")");
        var addJson = await _session.SetBreakpointAsync(VerifyTargetSource, writeLineLine, null, cts.Token);
        var add = Parse(addJson);
        Assert.Equal("setBreakpoint", add["operation"]!.GetValue<string>());

        var listJson = _session.ListBreakpoints();
        var list = Parse(listJson);
        Assert.True(list["totalCount"]!.GetValue<int>() >= 2); // initial + new
    }

    [Fact]
    public async Task Breakpoint_Remove()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await LaunchAtDoWork(cts.Token);

        var writeLineLine = FindLine("Console.WriteLine($\"  i={i}, sum={sum}\")");
        await _session.SetBreakpointAsync(VerifyTargetSource, writeLineLine, null, cts.Token);
        await _session.RemoveBreakpointAsync(VerifyTargetSource, writeLineLine, cts.Token);

        var listJson = _session.ListBreakpoints();
        var list = Parse(listJson);
        // Should only have the initial breakpoint
        var sourceArray = list["source"]!.AsArray();
        Assert.DoesNotContain(sourceArray, bp => bp?["line"]?.GetValue<int>() == writeLineLine);
    }

    [Fact]
    public async Task Breakpoint_Clear()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await LaunchAtDoWork(cts.Token);

        var clearJson = await _session.ClearBreakpointsAsync(null, cts.Token);
        var clear = Parse(clearJson);
        Assert.Equal("clearBreakpoints", clear["operation"]!.GetValue<string>());

        var listJson = _session.ListBreakpoints();
        var list = Parse(listJson);
        Assert.Equal(0, list["totalCount"]!.GetValue<int>());
    }

    // ConditionalBreakpoint test removed — netcoredbg evaluates breakpoint conditions
    // using the same func-eval machinery that hangs on macOS/ARM64. Conditional breakpoints
    // are blocked until DrHook.Engine replaces netcoredbg.
}
