// Shared path resolution for integration targets (MTP + Legacy VSTest).
//
// Phase 8 (ADR-008 Increment 4) integration tests all need the same paths.
// Extracted to a single helper to keep resolution logic in one place.

using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DrHook.Engine.IntegrationTests;

internal static class IntegrationTargetPaths
{
    /// <summary>Resolve the MTP integration target's executable path. On Unix the apphost
    /// has no extension; Windows uses .exe.</summary>
    public static string MtpTargetExe()
    {
        string testBin = System.AppContext.BaseDirectory;
        DirectoryInfo? dir = new(testBin);
        for (int up = 0; up < 4 && dir is not null; up++) dir = dir.Parent;
        Assert.IsNotNull(dir, $"Couldn't walk up from {testBin} to find tests/ directory.");

        string targetBaseDir = Path.Combine(dir!.FullName, "DrHook.Engine.IntegrationTargets.Mtp", "bin");
        Assert.IsTrue(Directory.Exists(targetBaseDir), $"Integration target bin directory missing: {targetBaseDir}");

        string[] configDirs = Directory.GetDirectories(targetBaseDir);
        Assert.IsTrue(configDirs.Length > 0, $"No build configurations under {targetBaseDir}");

        string targetName = "DrHook.Engine.IntegrationTargets.Mtp";
        string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? targetName + ".exe" : targetName;

        string currentConfig = new DirectoryInfo(testBin).Parent?.Parent?.Name ?? "Release";
        string candidateConfigDir = configDirs.FirstOrDefault(d => Path.GetFileName(d) == currentConfig) ?? configDirs[0];

        string[] tfmDirs = Directory.GetDirectories(candidateConfigDir);
        Assert.IsTrue(tfmDirs.Length > 0, $"No TFM directories under {candidateConfigDir}");
        string tfmDir = tfmDirs[0];

        return Path.Combine(tfmDir, exeName);
    }

    /// <summary>Resolve the Legacy VSTest integration target's csproj path.</summary>
    public static string VstestTargetProjectPath()
    {
        string testBin = System.AppContext.BaseDirectory;
        DirectoryInfo? dir = new(testBin);
        for (int up = 0; up < 4 && dir is not null; up++) dir = dir.Parent;
        Assert.IsNotNull(dir, $"Couldn't walk up from {testBin} to find tests/ directory.");

        return Path.Combine(dir!.FullName, "DrHook.Engine.IntegrationTargets.Vstest", "DrHook.Engine.IntegrationTargets.Vstest.csproj");
    }

    /// <summary>Resolve the ADR-012 snapshot integration target's launchable DLL (a plain console app the
    /// substrate launches via <c>DebugSession.Launch</c> + the entry-module hold-gate). Built as a sibling
    /// artifact via the test project's ProjectReference — so it shares the test's exact config + TFM, which
    /// we read straight off the test's own bin path (deterministic; no config-name guessing).</summary>
    public static string SnapshotTargetDll()
    {
        // AppContext.BaseDirectory = .../DrHook.Engine.IntegrationTests/bin/<config>/<tfm>/
        var tfmDir = new DirectoryInfo(System.AppContext.BaseDirectory);
        string tfm = tfmDir.Name;                       // e.g. net10.0
        string config = tfmDir.Parent?.Name ?? "Debug"; // e.g. Debug / Release
        return Path.Combine(TestsDir().FullName, "DrHook.Engine.IntegrationTargets.Snapshot", "bin", config, tfm, "DrHookSnapshotTarget.dll");
    }

    /// <summary>Resolve the snapshot target's <c>Program.cs</c> — the test scans it for the breakpoint
    /// marker (function-token resolution is stale-proof, but a build-dependency target's PDB always
    /// matches its source, so a line breakpoint at the marker is safe and gives a richer stop).</summary>
    public static string SnapshotTargetSource()
        => Path.Combine(TestsDir().FullName, "DrHook.Engine.IntegrationTargets.Snapshot", "Program.cs");

    /// <summary>Walk up from the test bin to the <c>tests/</c> directory (shared by the resolvers above).</summary>
    private static DirectoryInfo TestsDir()
    {
        string testBin = System.AppContext.BaseDirectory;
        DirectoryInfo? dir = new(testBin);
        for (int up = 0; up < 4 && dir is not null; up++) dir = dir.Parent;
        Assert.IsNotNull(dir, $"Couldn't walk up from {testBin} to find tests/ directory.");
        return dir!;
    }
}
