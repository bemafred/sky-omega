using Xunit;
using SkyOmega.DrHook.Stepping;

namespace SkyOmega.DrHook.Tests.Stepping;

public class SteppingSessionManagerTests
{
    [Fact]
    public void NewSession_IsNotActive()
    {
        var session = new SteppingSessionManager();
        Assert.False(session.IsActive);
    }

    [Fact]
    public void ListBreakpoints_EmptySession_ReturnsStructuredJson()
    {
        var session = new SteppingSessionManager();
        var json = System.Text.Json.Nodes.JsonNode.Parse(session.ListBreakpoints());

        Assert.Equal(0, json!["totalCount"]!.GetValue<int>());
        Assert.Empty(json["source"]!.AsArray());
        Assert.Empty(json["function"]!.AsArray());
        Assert.Empty(json["exception"]!.AsArray());
    }
}
