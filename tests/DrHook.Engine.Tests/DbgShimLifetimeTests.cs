// Finding 98: libdbgshim stays loaded for the process's lifetime. Freeing it (as DbgShim.Dispose did) left its
// embedded PAL's pthread keys registered with destructors in unmapped memory, so on Linux a thread that had called
// into dbgshim crashed the whole host with SIGSEGV when it later exited (probe 65: 5 of 5 on arm64, 3 of 3 on x64).

using System.IO;
using SkyOmega.DrHook.Engine.Interop;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class DbgShimLifetimeTests
{
    [Fact]
    public void Dispose_LeavesLibdbgshimMapped_AndLoadReusesIt()
    {
        DbgShim first = DbgShim.Load();
        string path = DbgShim.LoadedPath!;
        first.Dispose();

        string fileName = Path.GetFileName(path);
        string? stillMapped = MappedFiles.FindByFileName(Environment.ProcessId, fileName);
        Assert.True(stillMapped is not null, $"{fileName} was unmapped by DbgShim.Dispose; a thread that called into it would crash on exit (finding 98).");

        DbgShim second = DbgShim.Load();
        Assert.Equal(path, DbgShim.LoadedPath);
        second.Dispose();
    }
}
