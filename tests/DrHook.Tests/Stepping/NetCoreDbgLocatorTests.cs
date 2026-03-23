using Xunit;
using SkyOmega.DrHook.Stepping;

namespace SkyOmega.DrHook.Tests.Stepping;

public class NetCoreDbgLocatorTests
{
    [Fact]
    public void Locate_ReturnsNullOrValidPath()
    {
        var result = NetCoreDbgLocator.Locate();

        // May or may not be installed — both are valid outcomes
        if (result is not null)
        {
            Assert.True(File.Exists(result), $"Locate returned path that doesn't exist: {result}");
        }
    }

    [Fact]
    public void LocateOrThrow_ThrowsWhenNotFound()
    {
        // If netcoredbg is installed, this won't throw — skip in that case
        var located = NetCoreDbgLocator.Locate();
        if (located is not null)
            return; // Can't test the throw path when the binary exists

        Assert.Throws<FileNotFoundException>(() => NetCoreDbgLocator.LocateOrThrow());
    }
}
