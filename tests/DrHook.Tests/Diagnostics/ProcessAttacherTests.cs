using Xunit;
using SkyOmega.DrHook.Diagnostics;

namespace SkyOmega.DrHook.Tests.Diagnostics;

public class ProcessAttacherTests
{
    [Fact]
    public async Task ListDotNetProcesses_ReturnsAtLeastOneProcess()
    {
        // The test runner itself is a .NET process, so we should always find at least one
        var attacher = new ProcessAttacher();
        var result = await attacher.ListDotNetProcessesAsync(CancellationToken.None);

        Assert.NotEmpty(result.Processes);
    }

    [Fact]
    public async Task ListDotNetProcesses_EntriesHaveRequiredFields()
    {
        var attacher = new ProcessAttacher();
        var result = await attacher.ListDotNetProcessesAsync(CancellationToken.None);

        foreach (var entry in result.Processes)
        {
            Assert.True(entry.Pid > 0);
            Assert.NotNull(entry.Name);
            Assert.NotNull(entry.Path);
            Assert.NotNull(entry.Version);
        }
    }

    [Fact]
    public async Task ListDotNetProcesses_ToJsonProducesValidJson()
    {
        var attacher = new ProcessAttacher();
        var result = await attacher.ListDotNetProcessesAsync(CancellationToken.None);
        var json = result.ToJson();

        Assert.Contains("\"processes\"", json);
        Assert.Contains("\"count\"", json);
    }
}
