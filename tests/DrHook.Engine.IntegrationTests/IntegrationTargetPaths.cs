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
}
