// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Hosting;
using SkyOmega.Mercury.Mcp;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Serving;

// The host of mercury-mcp: its options, its words, and the MCP server. Opening the store, the HTTP endpoint, the
// attach pipe and the stop handling are in Mercury.Serving and Mercury.Hosting (ADR-065 D11).

var store = new StoreArguments(StoreOptions.Path | StoreOptions.Port | StoreOptions.ServiceConnectTimeout);
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
        case "--enable-http-updates":
            enableHttpUpdates = true;
            break;
        default:
            switch (store.Read(args, ref i))
            {
                case ArgumentRead.Read:
                    break;
                case ArgumentRead.NotMine:
                    Console.Error.WriteLine($"Error: Unknown option '{args[i]}'.");
                    Console.Error.WriteLine("Use --help for usage information.");
                    return 1;
                case ArgumentRead.LooksLikeSparql:
                    Console.Error.WriteLine($"Error: {store.Error}");
                    Console.Error.WriteLine("mercury-mcp is an MCP server, not a query CLI.");
                    Console.Error.WriteLine("Use 'mercury-sparql --query \"...\"' for one-shot queries.");
                    return 1;
                default:
                    Console.Error.WriteLine($"Error: {store.Error}");
                    return 1;
            }
            break;
    }
}

if (showVersion)
{
    Console.WriteLine(ToolVersion.Line("mercury-mcp", typeof(Program).Assembly));
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
          --service-connect-timeout <seconds>
                                  How long a SERVICE call waits to connect to its endpoint (default: 5)

        Examples:
          mercury-mcp                          # Persistent per-user store (see below)
          mercury-mcp ./mydata                 # Custom store path
          mercury-mcp -p 3031 --enable-http-updates

        Default store:
          macOS     ~/Library/SkyOmega/stores/mcp/
          Linux     $XDG_DATA_HOME/SkyOmega/stores/mcp/ (unset: ~/.local/share/SkyOmega/stores/mcp/)
          Windows   %LOCALAPPDATA%\SkyOmega\stores\mcp\

        The MCP server exposes:
          - MCP protocol on stdin/stdout (for Claude)
          - SPARQL HTTP endpoint at http://localhost:{port}/sparql
          - Named pipe 'mercury-mcp' for CLI attachment

        SERVICE:
          A query's SERVICE makes mercury-mcp send it to any address this machine can
          reach, services that listen only on localhost included; a failure says at most
          4 KiB of what such a service answers.

        Log:
          mercury.log in the store's directory: each run's start and stop, each store
          opened, and every warning and error a store reported. Read it after a failure.
        """);
    return 0;
}

// A SERVICE call's connect timeout is one for the process, set before the store opens (ADR-066 D5)
if (store.ServiceConnectTimeout is { } serviceConnectTimeout)
    SparqlEngine.ServiceConnectTimeout = serviceConnectTimeout;

// Default store path - persistent per-user location
var storePath = store.ResolvePath("mcp");
int httpPort = store.Port ?? MercuryPorts.Mcp;
var toolVersion = ToolVersion.Of(typeof(Program).Assembly);

Console.Error.WriteLine("Mercury MCP Server starting...");
Console.Error.WriteLine($"  Store: {Path.GetFullPath(storePath)}");
Console.Error.WriteLine($"  Log:   {StoreSession.LogPathFor(storePath)}");

StoreSession session;
try
{
    session = StoreSession.Open(storePath, "mercury-mcp", toolVersion);
}
catch (StoreInUseException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine("Another mercury-mcp or mercury process is using this store.");
    Console.Error.WriteLine("Close the other process first, or use -d to specify a different store path.");
    return 1;
}
if (session.LogWarning is { } logWarning)
    Console.Error.WriteLine(logWarning);

Console.Error.WriteLine($"  Updates: {(enableHttpUpdates ? "enabled" : "disabled")}");

var builder = StoreHost.CreateBuilder(session);
builder.Services.AddSingleton(new StorePathHolder(session.Path));

// Register MCP server with stdio transport and tools
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "mercury-mcp",
            Version = toolVersion
        };
        // Sent to the client during the initialization handshake and surfaced as an LLM system message:
        // orient the agent to Mercury as persistent semantic memory and point to the canonical discipline
        // doc (the MCP-layer counterpart to CLAUDE.md -> MERCURY.md).
        options.ServerInstructions =
            """
            Mercury is Sky Omega's RDF triple store and your persistent semantic memory across sessions
            (SPARQL 1.1 SELECT/ASK/CONSTRUCT/DESCRIBE + UPDATE; for substring search use
            FILTER(text:match(?v, "term")), which is trigram-accelerated). At session start, check what is
            already in memory; then record observations, decisions, and findings as they happen —
            reflexively, not only when asked. What you write persists for future sessions and other agents.

            When, why, and how — EEE discipline, provenance conventions, the bootstrap procedure, and
            consolidation patterns: MERCURY.md in the Sky Omega repo —
            https://github.com/bemafred/sky-omega/blob/main/MERCURY.md
            """;
    })
    .WithStdioServerTransport()
    .WithTools<MercuryTools>();

builder.AddStoreEndpoints(new StoreEndpointOptions
{
    Hosts = store.Hosts,
    Port = httpPort,
    EnableUpdates = enableHttpUpdates,
    PipeName = MercuryPorts.McpPipeName,
    PipeWelcome = $"Connected to Mercury MCP (store: {session.Path})",
    PipePrompt = "mcp> ",
});

Console.Error.WriteLine();
Console.Error.WriteLine("Ready. Waiting for MCP messages on stdin...");
Console.Error.WriteLine();

// The MCP stdio transport stops the host when standard input ends; SIGINT and SIGTERM stop it too, and stay handled
// until the store is closed (ADR-058 Q7).
var exitCode = await StoreHost.RunAsync(builder.Build(), session, closing: () => Console.Error.WriteLine("MCP Server shutting down..."));
Console.Error.WriteLine("MCP Server stopped.");

return exitCode;
