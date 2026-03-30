using Xunit;
using System.Text.Json.Nodes;
using SkyOmega.DrHook.Stepping;

namespace SkyOmega.DrHook.Tests.Stepping;

public class SteppingSessionManagerTests
{
    [Fact]
    public void IsActive_ReturnsFalseInitially()
    {
        var manager = new SteppingSessionManager();
        Assert.False(manager.IsActive);
    }

    [Fact]
    public async Task StepNext_ReturnsErrorWhenNoSession()
    {
        var manager = new SteppingSessionManager();
        var result = await manager.StepNextAsync(null, CancellationToken.None);

        var json = JsonNode.Parse(result);
        Assert.NotNull(json?["error"]);
        Assert.Contains("No active stepping session", json!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task StepInto_ReturnsErrorWhenNoSession()
    {
        var manager = new SteppingSessionManager();
        var result = await manager.StepIntoAsync(null, CancellationToken.None);

        var json = JsonNode.Parse(result);
        Assert.NotNull(json?["error"]);
    }

    [Fact]
    public async Task StepOut_ReturnsErrorWhenNoSession()
    {
        var manager = new SteppingSessionManager();
        var result = await manager.StepOutAsync(null, CancellationToken.None);

        var json = JsonNode.Parse(result);
        Assert.NotNull(json?["error"]);
    }

    [Fact]
    public async Task Stop_ReturnsErrorWhenNoSession()
    {
        var manager = new SteppingSessionManager();
        var result = await manager.StopAsync(CancellationToken.None);

        var json = JsonNode.Parse(result);
        Assert.NotNull(json?["error"]);
        Assert.Contains("No active stepping session", json!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pause_ReturnsErrorWhenNoSession()
    {
        var manager = new SteppingSessionManager();
        var result = await manager.PauseAsync(CancellationToken.None);

        var json = JsonNode.Parse(result);
        Assert.NotNull(json?["error"]);
    }

    [Fact]
    public async Task SetBreakpoint_ReturnsErrorWhenNoSession()
    {
        var manager = new SteppingSessionManager();
        var result = await manager.SetBreakpointAsync("/tmp/test.cs", 10, null, CancellationToken.None);

        var json = JsonNode.Parse(result);
        Assert.NotNull(json?["error"]);
    }

    [Fact]
    public async Task InspectVariables_ReturnsErrorWhenNoSession()
    {
        var manager = new SteppingSessionManager();
        var result = await manager.InspectVariablesAsync(1, CancellationToken.None);

        var json = JsonNode.Parse(result);
        Assert.NotNull(json?["error"]);
    }

    // ─── Breakpoint registry tests ─────────────────────────────────────────

    [Fact]
    public async Task RemoveBreakpoint_ReturnsErrorWhenNoSession()
    {
        var manager = new SteppingSessionManager();
        var result = await manager.RemoveBreakpointAsync("/tmp/test.cs", 10, CancellationToken.None);

        var json = JsonNode.Parse(result);
        Assert.NotNull(json?["error"]);
        Assert.Contains("No active stepping session", json!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task RemoveFunctionBreakpoint_ReturnsErrorWhenNoSession()
    {
        var manager = new SteppingSessionManager();
        var result = await manager.RemoveFunctionBreakpointAsync("Fibonacci", CancellationToken.None);

        var json = JsonNode.Parse(result);
        Assert.NotNull(json?["error"]);
    }

    [Fact]
    public async Task RemoveExceptionBreakpoint_ReturnsErrorWhenNoSession()
    {
        var manager = new SteppingSessionManager();
        var result = await manager.RemoveExceptionBreakpointAsync("all", CancellationToken.None);

        var json = JsonNode.Parse(result);
        Assert.NotNull(json?["error"]);
    }

    [Fact]
    public async Task ClearBreakpoints_ReturnsErrorWhenNoSession()
    {
        var manager = new SteppingSessionManager();
        var result = await manager.ClearBreakpointsAsync(null, CancellationToken.None);

        var json = JsonNode.Parse(result);
        Assert.NotNull(json?["error"]);
    }

    [Fact]
    public async Task EvaluateExpression_ReturnsErrorWhenNoSession()
    {
        var manager = new SteppingSessionManager();
        var result = await manager.EvaluateExpressionAsync("myList.Count", 1, CancellationToken.None);

        var json = JsonNode.Parse(result);
        Assert.NotNull(json?["error"]);
        Assert.Contains("No active stepping session", json!["error"]!.GetValue<string>());
    }

    [Fact]
    public void ListBreakpoints_ReturnsEmptyWhenNoBreakpoints()
    {
        var manager = new SteppingSessionManager();
        var result = manager.ListBreakpoints();

        var json = JsonNode.Parse(result);
        Assert.Equal(0, json!["totalCount"]!.GetValue<int>());
        Assert.Empty(json["source"]!.AsArray());
        Assert.Empty(json["function"]!.AsArray());
        Assert.Empty(json["exception"]!.AsArray());
    }
}
