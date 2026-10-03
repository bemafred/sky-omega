// The substrate's own mapped-file lookup (finding 88 — locating the target's runtime without dbgshim's
// EnumerateCLRs). Self-inspection, CI-safe: the test host is a framework-dependent .NET process, so its
// shared-framework runtime module is mapped and must be found by file name.

using System.Diagnostics;
using SkyOmega.DrHook.Engine.Interop;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class MappedFilesTests
{
    private static readonly string RuntimeModuleName =
        OperatingSystem.IsWindows() ? "coreclr.dll" :
        OperatingSystem.IsMacOS() ? "libcoreclr.dylib" :
                                    "libcoreclr.so";

    [Fact]
    public void FindByFileName_Self_FindsTheLoadedRuntimeModule()
    {
        string? path = MappedFiles.FindByFileName(Environment.ProcessId, RuntimeModuleName);

        Assert.NotNull(path);
        Assert.Equal(RuntimeModuleName, Path.GetFileName(path), ignoreCase: true);
        Assert.True(File.Exists(path), $"mapped runtime module path does not exist on disk: {path}");
    }

    [Fact]
    public void FindByFileName_NameNotMapped_IsNull()
    {
        Assert.Null(MappedFiles.FindByFileName(Environment.ProcessId, "drhook-no-such-module.dylib"));
    }

    [Fact]
    public void FindByFileName_ExitedProcess_IsNull()
    {
        if (OperatingSystem.IsWindows()) return;

        using Process child = Process.Start(new ProcessStartInfo("/usr/bin/true") { UseShellExecute = false })!;
        child.WaitForExit();

        Assert.Null(MappedFiles.FindByFileName(child.Id, RuntimeModuleName));
    }
}
