// The held-spawn primitive behind DebugSession.Launch on POSIX, and the libc ABI it is declared against.
// The struct sizes are the headers' facts (glibc 2.43 measured on zbook, the macOS SDK's opaque pointers):
// a test fails if an edit changes a layout. The spawn round trip runs the real platform implementation:
// the child is held, runs nothing until Continue, and on Linux exec keeps the pid the caller registered.

using System.Runtime.InteropServices;
using SkyOmega.DrHook.Engine.Interop;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed unsafe class PosixSpawnTests
{
    [Fact]
    public void GlibcSpawnTypes_MatchTheHeaderSizes()
    {
        Assert.Equal(336, sizeof(LinuxSpawn.SpawnAttr));
        Assert.Equal(80, sizeof(LinuxSpawn.SpawnFileActions));
    }

    [Fact]
    public void DarwinSpawnTypes_AreOpaquePointers()
    {
        Assert.Equal(sizeof(nint), sizeof(DarwinSpawn.SpawnAttr));
        Assert.Equal(sizeof(nint), sizeof(DarwinSpawn.SpawnFileActions));
    }

    [Fact]
    public void HeldSpawn_RunsOnlyAfterContinue_AndKeepsThePid()
    {
        if (OperatingSystem.IsWindows()) return;   // POSIX-only primitive

        // The child prints its own pid. On Linux it is exec'd from the holding shell, so the printed pid
        // proves exec kept the pid the caller got back (the one RegisterForRuntimeStartup is armed on).
        int rc = PosixSpawn.SpawnSuspendedRedirected("/bin/sh", new[] { "/bin/sh", "-c", "echo $$" }, cwd: null, env: null,
            out int pid, out int stdoutFd, out int stderrFd);
        Assert.Equal(0, rc);
        try
        {
            if (OperatingSystem.IsLinux())
            {
                string stat = File.ReadAllText($"/proc/{pid}/stat");
                Assert.Equal('T', stat[stat.LastIndexOf(')') + 2]);   // held: stopped, not running
            }

            PosixSpawn.Continue(pid);

            byte[] buffer = new byte[256];
            int total = 0;
            fixed (byte* p = buffer)
                for (nint n; (n = PosixSpawn.Read(stdoutFd, p + total, buffer.Length - total)) > 0;)
                    total += (int)n;
            string printed = System.Text.Encoding.ASCII.GetString(buffer, 0, total).Trim();

            Assert.Equal(pid.ToString(System.Globalization.CultureInfo.InvariantCulture), printed);
        }
        finally
        {
            PosixSpawn.Kill(pid);          // a child still held (an assert failed before Continue) would never exit
            PosixSignals.ReapChild(pid);
            PosixSpawn.Close(stdoutFd);
            PosixSpawn.Close(stderrFd);
        }
    }

    [Fact]
    public void PortableRid_IsOsFamilyPlusArchitecture()
    {
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string expectedOs =
            OperatingSystem.IsWindows() ? "win" :
            OperatingSystem.IsMacOS() ? "osx" :
            LinuxLibc.IsMusl ? "linux-musl" : "linux";

        Assert.Equal(expectedOs + "-" + arch, DbgShim.PortableRid());
    }

    [Fact]
    public void CandidateRids_TryTheReportedRidFirst_ThenThePortableOne()
    {
        string[] rids = DbgShim.CandidateRids().ToArray();

        Assert.Equal(RuntimeInformation.RuntimeIdentifier, rids[0]);
        Assert.Contains(DbgShim.PortableRid(), rids);
        Assert.Equal(rids.Length, rids.Distinct(StringComparer.Ordinal).Count());
    }
}
