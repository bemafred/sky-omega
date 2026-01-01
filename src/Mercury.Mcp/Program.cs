// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Adapters;
using SkyOmega.Mercury.Mcp;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Sparql.Protocol;
using SkyOmega.Mercury.Storage;

// Parse command line arguments
string? storePath = null;
int httpPort = MercuryPorts.Mcp;
bool enableHttpUpdates = false;
bool showHelp = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-h":
        case "--help":
            showHelp = true;
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

if (showHelp)
{
    Console.Error.WriteLine("""
        Mercury MCP Server

        Usage: mercury-mcp [options] [store-path]

        Options:
          -h, --help              Show this help message
          -d, --data <path>       Path to data directory
          -p, --port <port>       HTTP port (default: 3030)
          --enable-http-updates   Allow SPARQL UPDATE via HTTP

        Examples:
          mercury-mcp                          # Default store at ./mcp-store
          mercury-mcp ./mydata                 # Custom store path
          mercury-mcp -p 3031 --enable-http-updates

        The MCP server exposes:
          - MCP protocol on stdin/stdout (for Claude)
          - SPARQL HTTP endpoint at http://localhost:{port}/sparql
          - Named pipe 'mercury-mcp' for CLI attachment
        """);
    return 0;
}

// Default store path
storePath ??= "./mcp-store";

Console.Error.WriteLine("Mercury MCP Server starting...");
Console.Error.WriteLine($"  Store: {Path.GetFullPath(storePath)}");

// Create store
using var store = new QuadStore(storePath);

// Create session factory for pipe connections
ReplSession CreateSession() => StoreAdapter.CreateSession(store);

// Start HTTP server
using var httpServer = new SparqlHttpServer(
    store,
    $"http://localhost:{httpPort}/",
    new SparqlHttpServerOptions { EnableUpdates = enableHttpUpdates });

httpServer.Start();
Console.Error.WriteLine($"  HTTP: http://localhost:{httpPort}/sparql");
Console.Error.WriteLine($"  Updates: {(enableHttpUpdates ? "enabled" : "disabled")}");

// Start pipe server for CLI attachment
using var pipeServer = new PipeServer(
    MercuryPorts.McpPipeName,
    CreateSession,
    welcomeMessage: $"Connected to Mercury MCP (store: {storePath})",
    prompt: "mcp> ");

pipeServer.Start();
Console.Error.WriteLine($"  Pipe: {MercuryPorts.McpPipeName}");

Console.Error.WriteLine();
Console.Error.WriteLine("Ready. Waiting for MCP messages on stdin...");
Console.Error.WriteLine();

// Run MCP protocol on stdin/stdout
await McpProtocol.RunAsync(
    store,
    Console.OpenStandardInput(),
    Console.OpenStandardOutput());

Console.Error.WriteLine("MCP Server shutting down...");
await pipeServer.StopAsync();
await httpServer.StopAsync();

return 0;
