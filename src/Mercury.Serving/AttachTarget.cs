// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Runtime.IO;

namespace SkyOmega.Mercury.Serving;

/// <summary>What <c>mercury --attach</c> attaches to: a Mercury process that serves its REPL on a pipe.</summary>
/// <param name="Description">The process, as the client names it to the user.</param>
/// <param name="PipeName">Its pipe.</param>
public sealed record AttachTarget(string Description, string PipeName)
{
    /// <summary>The target named: <c>mcp</c> for mercury-mcp, else the name of a pipe
    /// (<see cref="MercuryPorts.AttachPipeName"/>).</summary>
    public static AttachTarget Named(string target) => new(target, MercuryPorts.AttachPipeName(target));

    /// <summary>
    /// The mercuryd that serves the store at <paramref name="storePath"/>, on the pipe mercuryd makes for that store
    /// (<see cref="MercuryPorts.DaemonPipeName"/>; ADR-065 D7).
    /// </summary>
    public static AttachTarget DaemonOf(string storePath) =>
        new($"the mercuryd of {Path.TrimEndingDirectorySeparator(Path.GetFullPath(storePath))}",
            MercuryPorts.DaemonPipeName(storePath));
}
