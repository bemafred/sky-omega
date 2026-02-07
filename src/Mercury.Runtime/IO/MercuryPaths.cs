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
/// <item>Linux/WSL: ~/.local/share/SkyOmega/stores/{name}/</item>
/// <item>Windows: %LOCALAPPDATA%\SkyOmega\stores\{name}\</item>
/// </list>
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
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SkyOmega", "stores", name);

        Directory.CreateDirectory(path);
        return path;
    }
}
