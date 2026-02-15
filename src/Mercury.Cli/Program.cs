// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Pruning;
using SkyOmega.Mercury.Pruning.Filters;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Execution.Federated;
using SkyOmega.Mercury.Sparql.Parsing;
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

// Create ReplSession with inline lambdas and run REPL
using var loadExecutor = new LoadExecutor();
using (pool)
using (httpServer)
using (var session = new ReplSession(
    executeQuery: sparql => ExecuteQuery(pool.Active, sparql),
    executeUpdate: sparql => ExecuteUpdate(pool.Active, sparql, loadExecutor),
    getStatistics: () => GetStatistics(pool.Active),
    getNamedGraphs: () => GetNamedGraphs(pool.Active),
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

// --- Execution helpers for ReplSession ---

static QueryResult ExecuteQuery(QuadStore store, string sparql)
{
    var sw = Stopwatch.StartNew();

    try
    {
        var parser = new SparqlParser(sparql.AsSpan());
        Query parsed;

        try
        {
            parsed = parser.ParseQuery();
        }
        catch (SparqlParseException ex)
        {
            return new QueryResult { Success = false, ErrorMessage = ex.Message, ParseTime = sw.Elapsed };
        }

        var parseTime = sw.Elapsed;
        sw.Restart();

        store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(store, sparql.AsSpan(), parsed);

            return parsed.Type switch
            {
                QueryType.Select => ExecuteSelect(executor, sparql, parseTime, sw),
                QueryType.Ask => new QueryResult
                {
                    Success = true,
                    Kind = ExecutionResultKind.Ask,
                    AskResult = executor.ExecuteAsk(),
                    ParseTime = parseTime,
                    ExecutionTime = sw.Elapsed
                },
                QueryType.Construct or QueryType.Describe => ExecuteTriples(executor, parsed.Type, parseTime, sw),
                _ => new QueryResult
                {
                    Success = false,
                    ErrorMessage = $"Unsupported query type: {parsed.Type}",
                    ParseTime = parseTime,
                    ExecutionTime = sw.Elapsed
                }
            };
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }
    catch (Exception ex)
    {
        return new QueryResult { Success = false, ErrorMessage = ex.Message, ParseTime = sw.Elapsed };
    }
}

static QueryResult ExecuteSelect(QueryExecutor executor, string sparql, TimeSpan parseTime, Stopwatch sw)
{
    var results = executor.Execute();
    var rows = new List<Dictionary<string, string>>();
    string[] varNames = [];

    try
    {
        if (results.MoveNext())
        {
            var bindings = results.Current;
            varNames = ExtractVariableNames(bindings, sparql);
            rows.Add(BindingsToRow(bindings, varNames));
        }

        while (results.MoveNext())
            rows.Add(BindingsToRow(results.Current, varNames));
    }
    finally
    {
        results.Dispose();
    }

    return new QueryResult
    {
        Success = true,
        Kind = ExecutionResultKind.Select,
        Variables = varNames,
        Rows = rows,
        ParseTime = parseTime,
        ExecutionTime = sw.Elapsed
    };
}

static Dictionary<string, string> BindingsToRow(BindingTable bindings, string[] varNames)
{
    var row = new Dictionary<string, string>();
    for (int i = 0; i < bindings.Count; i++)
        row[varNames.Length > i ? varNames[i] : $"?var{i}"] = bindings.GetString(i).ToString();
    return row;
}

static string[] ExtractVariableNames(BindingTable bindings, string source)
{
    var knownVars = new List<(string Name, int Hash)>();
    var span = source.AsSpan();

    for (int i = 0; i < span.Length - 1; i++)
    {
        if (span[i] == '?')
        {
            int end = i + 1;
            while (end < span.Length && (char.IsLetterOrDigit(span[end]) || span[end] == '_'))
                end++;

            if (end > i + 1)
            {
                var varName = span.Slice(i, end - i).ToString();
                uint hash = 2166136261;
                foreach (var ch in varName) { hash ^= ch; hash *= 16777619; }
                if (!knownVars.Exists(v => v.Hash == (int)hash))
                    knownVars.Add((varName, (int)hash));
            }
        }
    }

    var result = new string[bindings.Count];
    for (int i = 0; i < bindings.Count; i++)
    {
        var bindingHash = bindings.GetVariableHash(i);
        string? foundName = null;
        foreach (var (name, hash) in knownVars)
        {
            if (hash == bindingHash) { foundName = name; break; }
        }
        result[i] = foundName ?? $"?var{i}";
    }
    return result;
}

static QueryResult ExecuteTriples(QueryExecutor executor, QueryType type, TimeSpan parseTime, Stopwatch sw)
{
    var triples = new List<(string, string, string)>();
    var results = executor.Execute();

    try
    {
        while (results.MoveNext())
        {
            var b = results.Current;
            if (b.Count >= 3)
                triples.Add((b.GetString(0).ToString(), b.GetString(1).ToString(), b.GetString(2).ToString()));
        }
    }
    finally
    {
        results.Dispose();
    }

    return new QueryResult
    {
        Success = true,
        Kind = type == QueryType.Construct ? ExecutionResultKind.Construct : ExecutionResultKind.Describe,
        Triples = triples,
        ParseTime = parseTime,
        ExecutionTime = sw.Elapsed
    };
}

static UpdateResult ExecuteUpdate(QuadStore store, string sparql, LoadExecutor loadExecutor)
{
    var sw = Stopwatch.StartNew();

    try
    {
        var parser = new SparqlParser(sparql.AsSpan());
        UpdateOperation parsed;

        try
        {
            parsed = parser.ParseUpdate();
        }
        catch (SparqlParseException ex)
        {
            return new UpdateResult { Success = false, ErrorMessage = ex.Message, ParseTime = sw.Elapsed };
        }

        var parseTime = sw.Elapsed;
        sw.Restart();

        var executor = new UpdateExecutor(store, sparql.AsSpan(), parsed, loadExecutor);
        var result = executor.Execute();

        return new UpdateResult
        {
            Success = result.Success,
            AffectedCount = result.AffectedCount,
            ErrorMessage = result.ErrorMessage,
            ParseTime = parseTime,
            ExecutionTime = sw.Elapsed
        };
    }
    catch (Exception ex)
    {
        return new UpdateResult { Success = false, ErrorMessage = ex.Message, ParseTime = sw.Elapsed };
    }
}

static StoreStatistics GetStatistics(QuadStore store)
{
    var (quadCount, atomCount, totalBytes) = store.GetStatistics();
    var (walTxId, walCheckpoint, walSize) = store.GetWalStatistics();
    return new StoreStatistics
    {
        QuadCount = quadCount,
        AtomCount = atomCount,
        TotalBytes = totalBytes,
        WalTxId = walTxId,
        WalCheckpoint = walCheckpoint,
        WalSize = walSize
    };
}

static IEnumerable<string> GetNamedGraphs(QuadStore store)
{
    var graphs = new List<string>();
    store.AcquireReadLock();
    try
    {
        var enumerator = store.GetNamedGraphs();
        while (enumerator.MoveNext())
            graphs.Add(enumerator.Current.ToString());
    }
    finally
    {
        store.ReleaseReadLock();
    }
    return graphs;
}

static PruneResult ExecutePrune(QuadStorePool pool, string args)
{
    var sw = Stopwatch.StartNew();

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

    // Build transfer options
    IPruningFilter? filter = null;
    var filters = new List<IPruningFilter>();

    if (excludeGraphs.Count > 0)
        filters.Add(GraphFilter.Exclude(excludeGraphs.ToArray()));
    if (excludePredicates.Count > 0)
        filters.Add(PredicateFilter.Exclude(excludePredicates.ToArray()));

    if (filters.Count > 0)
        filter = CompositeFilter.All(filters.ToArray());

    var options = new TransferOptions
    {
        HistoryMode = historyMode,
        DryRun = dryRun
    };
    if (filter != null)
        options = new TransferOptions
        {
            HistoryMode = historyMode,
            DryRun = dryRun,
            Filter = filter
        };

    // Clear secondary, run transfer, switch
    pool.Clear("secondary");
    var transfer = new PruningTransfer(pool["primary"], pool["secondary"], options);
    var result = transfer.Execute();

    if (result.Success && !dryRun)
    {
        pool.Switch("primary", "secondary");
        pool.Clear("secondary");
    }

    return new PruneResult
    {
        Success = result.Success,
        ErrorMessage = result.ErrorMessage,
        QuadsScanned = result.TotalScanned,
        QuadsWritten = result.TotalWritten,
        BytesSaved = result.BytesSaved,
        Duration = sw.Elapsed,
        DryRun = dryRun
    };
}
