// The substrate's thread-name lookup (finding 99 — an attach waits for the target's ".NET Debugger" thread, which
// the runtime starts only after its debugger is marked initialized). Self-inspection, CI-safe: the test host is a
// .NET process with debugging enabled, so its runtime has started that thread. On macOS this also checks the
// proc_threadinfo layout (pth_name @ 48) against a live process.

using System.Diagnostics;
using SkyOmega.DrHook.Engine.Interop;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class TargetThreadsTests
{
    [Fact]
    public void HasThreadNamed_Self_FindsTheRuntimeDebuggerHelper()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The attach readiness check exists for the debug-transport platforms (Linux, macOS).");

        Assert.True(TargetThreads.HasThreadNamed(Environment.ProcessId, TargetThreads.DebuggerHelperThreadName),
            $"No \"{TargetThreads.DebuggerHelperThreadName}\" thread found in the test host; an attach to any target would wait and be refused.");
    }

    [Fact]
    public void HasThreadNamed_NameNotPresent_IsFalse()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The attach readiness check exists for the debug-transport platforms (Linux, macOS).");

        Assert.False(TargetThreads.HasThreadNamed(Environment.ProcessId, "drhook no such thread"));
    }

    [Fact]
    public void HasThreadNamed_ExitedProcess_IsFalse()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The attach readiness check exists for the debug-transport platforms (Linux, macOS).");

        using Process child = Process.Start(new ProcessStartInfo("/usr/bin/true") { UseShellExecute = false })!;
        child.WaitForExit();

        Assert.False(TargetThreads.HasThreadNamed(child.Id, TargetThreads.DebuggerHelperThreadName));
    }
}
