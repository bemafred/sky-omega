// Parent-pid lookup (finding 87 — the dual-reaper race guard's primitive). CI-safe: this test process
// never loads dbgshim, so the CoreCLR PAL that competes for child reaping is not resident here and a
// Process.Start child is reaped by the BCL alone.

using System.Diagnostics;
using SkyOmega.DrHook.Engine.Interop;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class ProcessParentageTests
{
    [Fact]
    public void ParentOf_ProcessStartChild_IsThisProcess()
    {
        if (OperatingSystem.IsWindows()) return;   // no parent-reaping model; ParentOf returns null by design

        using Process child = Process.Start(new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false })!;
        try
        {
            Assert.Equal(Environment.ProcessId, ProcessParentage.ParentOf(child.Id));
        }
        finally
        {
            child.Kill();
            child.WaitForExit();
        }
    }

    [Fact]
    public void ParentOf_Self_IsSomeOtherLiveProcess()
    {
        if (OperatingSystem.IsWindows()) return;

        int? parent = ProcessParentage.ParentOf(Environment.ProcessId);
        Assert.NotNull(parent);
        Assert.NotEqual(Environment.ProcessId, parent);
        Assert.True(parent > 0);
    }

    [Fact]
    public void ParentOf_ReapedChild_IsNull()
    {
        if (OperatingSystem.IsWindows()) return;

        using Process child = Process.Start(new ProcessStartInfo("/usr/bin/true") { UseShellExecute = false })!;
        child.WaitForExit();
        int reapedPid = child.Id;

        Assert.Null(ProcessParentage.ParentOf(reapedPid));
    }
}
