using System.Runtime.InteropServices;

namespace SkyOmega.DrHook.Stepping;

/// <summary>
/// Discovers the netcoredbg binary on the current platform.
///
/// Search order:
///   1. DRHOOK_NETCOREDBG_PATH environment variable (explicit override)
///   2. Common installation paths per platform
///   3. PATH lookup via 'which' / 'where'
///
/// Cross-platform: Windows, macOS, Linux.
/// </summary>
public static class NetCoreDbgLocator
{
    private const string EnvOverride = "DRHOOK_NETCOREDBG_PATH";

    public static string? Locate()
    {
        // 1. Explicit override
        var envPath = Environment.GetEnvironmentVariable(EnvOverride);
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return envPath;

        // 2. Platform-specific common locations
        var candidates = GetPlatformCandidates();
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // 3. PATH lookup
        return LocateViaPath();
    }

    public static string LocateOrThrow()
    {
        return Locate()
            ?? throw new FileNotFoundException(
                $"netcoredbg not found. Install it (MIT license, https://github.com/Samsung/netcoredbg) " +
                $"or set {EnvOverride} environment variable to the binary path.");
    }

    private static IReadOnlyList<string> GetPlatformCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new[]
            {
                Path.Combine(home, ".dotnet", "tools", "netcoredbg", "netcoredbg.exe"),
                @"C:\Program Files\netcoredbg\netcoredbg.exe",
                @"C:\Tools\netcoredbg\netcoredbg.exe",
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new[]
            {
                "/usr/local/bin/netcoredbg",
                "/opt/homebrew/bin/netcoredbg",
                Path.Combine(home, ".dotnet", "tools", "netcoredbg"),
                "/usr/local/netcoredbg/netcoredbg",
            };
        }

        // Linux
        return new[]
        {
            "/usr/local/bin/netcoredbg",
            "/usr/bin/netcoredbg",
            Path.Combine(home, ".dotnet", "tools", "netcoredbg"),
            "/usr/local/netcoredbg/netcoredbg",
        };
    }

    private static string? LocateViaPath()
    {
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "netcoredbg.exe"
            : "netcoredbg";

        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];

        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
