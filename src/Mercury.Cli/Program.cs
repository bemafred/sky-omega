// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Sparql.Protocol;
using SkyOmega.Mercury.Storage;

// Parse command line arguments
string? storePath = null;
bool inMemory = false;
bool showHelp = false;
bool showVersion = false;
int httpPort = MercuryPorts.Cli;
bool enableHttp = true;
string? attachTarget = null;

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
        case "-m":
        case "--memory":
            inMemory = true;
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
        case "--no-http":
            enableHttp = false;
            break;
        case "-a":
        case "--attach":
            attachTarget = i + 1 < args.Length && !args[i + 1].StartsWith('-')
                ? args[++i]
                : "mcp";
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
    Console.WriteLine($"mercury {version}");
    return 0;
}

if (showHelp)
{
    Console.WriteLine("""
        Mercury SPARQL CLI

        Usage: mercury [options] [store-path]

        Options:
          -v, --version      Show version information
          -h, --help         Show this help message
          -m, --memory       Use temporary in-memory store (deleted on exit)
          -d, --data <path>  Path to data directory
          -p, --port <port>  HTTP port (default: 3031)
          --no-http          Disable HTTP endpoint
          -a, --attach [target]  Attach to running instance (default: mcp)

        Examples:
          mercury                     # Persistent store at ~/Library/SkyOmega/stores/cli/
          mercury ./mydata            # Custom store path
          mercury -m                  # Temporary store (deleted on exit)
          mercury -a mcp              # Attach to MCP instance via pipe
          mercury -a                  # Same as above

        Inside the REPL:
          :help              Show REPL commands
          :quit              Exit the REPL

        HTTP Endpoint:
          When running, SPARQL is available at http://localhost:{port}/sparql
          Use SERVICE <http://localhost:3030/sparql> to query MCP instance
        """);
    return 0;
}

// Handle attach mode
if (attachTarget != null)
{
    return await RunAttachMode(attachTarget);
}

// Create or open pool
QuadStorePool pool;

if (inMemory)
{
    pool = QuadStorePool.CreateTemp("cli-session");
}
else
{
    var actualPath = storePath ?? MercuryPaths.Store("cli");
    pool = new QuadStorePool(actualPath);
}

// Ensure a primary store exists so pool.Active works immediately
// (new pools and temp pools start with no stores; persistent pools
// may have been created by an older version without pool metadata)
_ = pool["primary"];

// Start HTTP server if enabled
SparqlHttpServer? httpServer = null;
if (enableHttp)
{
    try
    {
        httpServer = new SparqlHttpServer(
            () => pool.Active,
            $"http://localhost:{httpPort}/",
            new SparqlHttpServerOptions { EnableUpdates = true });

        httpServer.Start();
        Console.WriteLine($"HTTP endpoint: http://localhost:{httpPort}/sparql");

        // Check if MCP is running
        if (await IsEndpointAlive($"http://localhost:{MercuryPorts.Mcp}/sparql"))
        {
            Console.WriteLine($"MCP instance detected - SERVICE <http://localhost:{MercuryPorts.Mcp}/sparql>");
        }
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning: Could not start HTTP server on port {httpPort}: {ex.Message}");
        Console.WriteLine("Continuing without HTTP endpoint.");
        Console.WriteLine();
        httpServer = null;
    }
}

// Create ReplSession with facade calls and run REPL
using (pool)
using (httpServer)
using (var session = new ReplSession(
    executeQuery: sparql => SparqlEngine.Query(pool.Active, sparql),
    executeUpdate: sparql => SparqlEngine.Update(pool.Active, sparql),
    getStatistics: () => SparqlEngine.GetStatistics(pool.Active),
    getNamedGraphs: () => SparqlEngine.GetNamedGraphs(pool.Active),
    executePrune: pruneArgs => ExecutePrune(pool, pruneArgs)))
{
    session.RunInteractive();
}

return 0;

// --- Helper functions ---

static async Task<bool> IsEndpointAlive(string url)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var response = await client.GetAsync(url);
        return response.IsSuccessStatusCode;
    }
    catch
    {
        return false;
    }
}

static async Task<int> RunAttachMode(string target)
{
    var pipeName = target.ToLowerInvariant() switch
    {
        "mcp" => MercuryPorts.McpPipeName,
        "cli" => MercuryPorts.CliPipeName,
        _ => target // Assume it's a custom pipe name
    };

    Console.WriteLine($"Attaching to {target} via pipe '{pipeName}'...");

    try
    {
        using var client = new PipeClient(pipeName);
        await client.ConnectAsync(timeoutMs: 5000);
        await client.RunInteractiveAsync();
        return 0;
    }
    catch (TimeoutException)
    {
        Console.WriteLine($"Error: Could not connect to {target}. Is it running?");
        return 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

// --- Execution helper for ReplSession ---

static PruneResult ExecutePrune(QuadStorePool pool, string args)
{
    // Parse prune args
    bool dryRun = false;
    var historyMode = HistoryMode.FlattenToCurrent;
    var excludeGraphs = new List<string>();
    var excludePredicates = new List<string>();

    var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    for (int i = 0; i < parts.Length; i++)
    {
        switch (parts[i])
        {
            case "--dry-run":
                dryRun = true;
                break;
            case "--history" when i + 1 < parts.Length:
                historyMode = parts[++i].ToLowerInvariant() switch
                {
                    "preserve" => HistoryMode.PreserveVersions,
                    "all" => HistoryMode.PreserveAll,
                    _ => HistoryMode.FlattenToCurrent
                };
                break;
            case "--exclude-graph" when i + 1 < parts.Length:
                excludeGraphs.Add(parts[++i]);
                break;
            case "--exclude-predicate" when i + 1 < parts.Length:
                excludePredicates.Add(parts[++i]);
                break;
        }
    }

    var options = new PruneOptions
    {
        DryRun = dryRun,
        HistoryMode = historyMode,
        ExcludeGraphs = excludeGraphs.Count > 0 ? excludeGraphs.ToArray() : null,
        ExcludePredicates = excludePredicates.Count > 0 ? excludePredicates.ToArray() : null
    };

    return PruneEngine.Execute(pool, options);
}
