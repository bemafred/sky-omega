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
    /// Named pipe for CLI instance.
    /// </summary>
    public const string CliPipeName = "mercury-cli";

    /// <summary>
    /// Get the SPARQL endpoint URL for a port.
    /// </summary>
    public static string GetEndpoint(int port) => $"http://localhost:{port}/sparql";

    /// <summary>
    /// Get the base URL for a port.
    /// </summary>
    public static string GetBaseUrl(int port) => $"http://localhost:{port}/";
}
