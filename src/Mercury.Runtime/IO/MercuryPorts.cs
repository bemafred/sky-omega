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
    /// Get the SPARQL endpoint URL for a port, on localhost.
    /// </summary>
    public static string GetEndpoint(int port) => GetEndpoint("localhost", port);

    /// <summary>
    /// Get the SPARQL endpoint URL for a host and a port.
    /// </summary>
    public static string GetEndpoint(string host, int port) => GetBaseUrl(host, port) + "sparql";

    /// <summary>
    /// Get the base URL for a port, on localhost.
    /// </summary>
    public static string GetBaseUrl(int port) => GetBaseUrl("localhost", port);

    /// <summary>
    /// Get the base URL a listener listens on for a host and a port (ADR-065 D4). The host is a name, an IP address,
    /// or <c>+</c> or <c>*</c> for every network; an IPv6 address is put in brackets, as a URL has it.
    /// </summary>
    public static string GetBaseUrl(string host, int port) =>
        host.Contains(':') && !host.StartsWith('[') ? $"http://[{host}]:{port}/" : $"http://{host}:{port}/";
}
