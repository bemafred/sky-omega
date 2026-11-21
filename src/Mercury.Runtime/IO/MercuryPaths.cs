// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SkyOmega.Mercury.Runtime.IO;

/// <summary>
/// Well-known persistent store paths for Mercury tools.
/// </summary>
/// <remarks>
/// <para>Platform resolution:</para>
/// <list type="bullet">
/// <item>macOS: ~/Library/SkyOmega/stores/{name}/</item>
/// <item>Linux/WSL: $XDG_DATA_HOME/SkyOmega/stores/{name}/, and ~/.local/share/SkyOmega/stores/{name}/ when
/// XDG_DATA_HOME is unset</item>
/// <item>Windows: %LOCALAPPDATA%\SkyOmega\stores\{name}\</item>
/// </list>
/// <para>
/// The user's data directory may not be made yet: a user that has never run a program that keeps data there, such as
/// the account a service runs as. .NET gives an empty path for a special folder that does not exist unless it is told
/// not to look, and a store was then made in the current directory (found on zbook, 2026-11-21, ADR-065 Step 5).
/// </para>
/// </remarks>
public static class MercuryPaths
{
    /// <summary>
    /// Returns the persistent store path for the given well-known name.
    /// Creates the directory if it does not exist.
    /// </summary>
    /// <param name="name">Store name (e.g., "mcp", "cli").</param>
    public static string Store(string name)
    {
        var basePath = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify), "Library")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);

        var path = Path.Combine(basePath, "SkyOmega", "stores", name);

        Directory.CreateDirectory(path);
        return path;
    }
}
