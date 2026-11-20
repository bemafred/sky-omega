// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;

namespace SkyOmega.Mercury.Runtime;

/// <summary>
/// The version a Mercury tool reports: what <c>--version</c> prints, and what the tool writes as its first line in the
/// store's log (ADR-065 D11).
/// </summary>
public static class ToolVersion
{
    /// <summary>The assembly's informational version (the package version, with the commit it was built from), or
    /// <c>unknown</c> when it has none.</summary>
    public static string Of(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>The line <c>--version</c> prints: the command, then its version.</summary>
    public static string Line(string command, Assembly assembly) => $"{command} {Of(assembly)}";
}
