// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SkyOmega.Mercury.Runtime.IO;

/// <summary>
/// Well-known ports and pipe names for Mercury instances.
/// </summary>
public static class MercuryPorts
{
    /// <summary>
    /// Default HTTP port for MCP server.
    /// </summary>
    public const int Mcp = 3030;

    /// <summary>
    /// Default HTTP port for CLI instance.
    /// </summary>
    public const int Cli = 3031;

    /// <summary>
    /// Named pipe for MCP server.
    /// </summary>
    public const string McpPipeName = "mercury-mcp";

    /// <summary>
    /// The pipe a target of <c>mercury --attach</c> names: <c>mcp</c> (in any case) is mercury-mcp's pipe, and any other
    /// target is the name of a pipe as it is. The CLI hosts no pipe, so there is no <c>cli</c> target (ADR-065 D7).
    /// </summary>
    public static string AttachPipeName(string target) =>
        string.Equals(target, "mcp", StringComparison.OrdinalIgnoreCase) ? McpPipeName : target;

    /// <summary>
    /// Get the SPARQL endpoint URL for a port.
    /// </summary>
    public static string GetEndpoint(int port) => $"http://localhost:{port}/sparql";

    /// <summary>
    /// Get the base URL for a port.
    /// </summary>
    public static string GetBaseUrl(int port) => $"http://localhost:{port}/";
}
