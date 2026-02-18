// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Mcp;
using SkyOmega.Mercury.Mcp.Services;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Storage;

// Parse command line arguments before building host
string? storePath = null;
int httpPort = MercuryPorts.Mcp;
bool enableHttpUpdates = false;
bool showHelp = false;
bool showVersion = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-h":
        case "--help":
            showHelp = true;
            break;
        case "-v":
        case "--version":
            showVersion = true;
            break;
        case "-d":
        case "--data":
            if (i + 1 < args.Length)
                storePath = args[++i];
            break;
        case "-p":
        case "--port":
            if (i + 1 < args.Length && int.TryParse(args[++i], out var port))
                httpPort = port;
            break;
        case "--enable-http-updates":
            enableHttpUpdates = true;
            break;
        default:
            if (!args[i].StartsWith('-') && storePath == null)
                storePath = args[i];
            break;
    }
}

if (showVersion)
{
    var version = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "unknown";
    Console.WriteLine($"mercury-mcp {version}");
    return 0;
}

if (showHelp)
{
    Console.Error.WriteLine("""
        Mercury MCP Server

        Usage: mercury-mcp [options] [store-path]

        Options:
          -v, --version           Show version information
          -h, --help              Show this help message
          -d, --data <path>       Path to data directory
          -p, --port <port>       HTTP port (default: 3030)
          --enable-http-updates   Allow SPARQL UPDATE via HTTP

        Examples:
          mercury-mcp                          # Persistent store at ~/Library/SkyOmega/stores/mcp/
          mercury-mcp ./mydata                 # Custom store path
          mercury-mcp -p 3031 --enable-http-updates

        The MCP server exposes:
          - MCP protocol on stdin/stdout (for Claude)
          - SPARQL HTTP endpoint at http://localhost:{port}/sparql
          - Named pipe 'mercury-mcp' for CLI attachment
        """);
    return 0;
}

// Default store path - persistent per-user location
storePath ??= MercuryPaths.Store("mcp");

Console.Error.WriteLine("Mercury MCP Server starting...");
Console.Error.WriteLine($"  Store: {Path.GetFullPath(storePath)}");

// Create pool (auto-migrates flat stores on first run)
var pool = new QuadStorePool(storePath);

Console.Error.WriteLine($"  Updates: {(enableHttpUpdates ? "enabled" : "disabled")}");

// Build host with MCP SDK
var builder = Host.CreateApplicationBuilder();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Register QuadStorePool as singleton
builder.Services.AddSingleton(pool);

// Register MCP server with stdio transport and tools
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "mercury-mcp",
            Version = "1.1.0"
        };
    })
    .WithStdioServerTransport()
    .WithTools<MercuryTools>();

// Register hosted services for HTTP and pipe servers
builder.Services.AddSingleton<HttpServerHostedService>(
    _ => new HttpServerHostedService(pool, httpPort, enableHttpUpdates));
builder.Services.AddHostedService(sp => sp.GetRequiredService<HttpServerHostedService>());

builder.Services.AddSingleton<PipeServerHostedService>(
    _ => new PipeServerHostedService(pool, CreateSession, storePath));
builder.Services.AddHostedService(sp => sp.GetRequiredService<PipeServerHostedService>());

Console.Error.WriteLine();
Console.Error.WriteLine("Ready. Waiting for MCP messages on stdin...");
Console.Error.WriteLine();

await builder.Build().RunAsync();

Console.Error.WriteLine("MCP Server shutting down...");
pool.Dispose();

return 0;

// --- Session factory for pipe connections ---

ReplSession CreateSession(QuadStorePool pool) => new ReplSession(
    executeQuery: sparql => SparqlEngine.Query(pool.Active, sparql),
    executeUpdate: sparql => SparqlEngine.Update(pool.Active, sparql),
    getStatistics: () => SparqlEngine.GetStatistics(pool.Active),
    getNamedGraphs: () => SparqlEngine.GetNamedGraphs(pool.Active));
