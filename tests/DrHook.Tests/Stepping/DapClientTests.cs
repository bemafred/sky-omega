using Xunit;
using SkyOmega.DrHook.Stepping;

namespace SkyOmega.DrHook.Tests.Stepping;

public class DapClientTests
{
    [Fact]
    public void IsConnected_ReturnsFalseBeforeLaunch()
    {
        var client = new DapClient();
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task StepNext_ThrowsWhenNotConnected()
    {
        var client = new DapClient();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StepNextAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task Dispose_DoesNotThrowWhenNeverConnected()
    {
        var client = new DapClient();
        await client.DisposeAsync(); // Should not throw
    }
}
