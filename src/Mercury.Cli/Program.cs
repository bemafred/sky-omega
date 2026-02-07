// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Protocol;
using SkyOmega.Mercury.Storage;

// Parse command line arguments
string? storePath = null;
bool inMemory = false;
bool showHelp = false;
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

if (showHelp)
{
    Console.WriteLine("""
        Mercury SPARQL CLI

        Usage: mercury [options] [store-path]

        Options:
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

// Create or open store
string actualPath;
bool usingTempStore = false;

if (inMemory)
{
    // Explicit in-memory mode: use temp directory, cleaned up on exit
    actualPath = SkyOmega.Mercury.Runtime.TempPath.Cli("session");
    usingTempStore = true;
}
else if (storePath != null)
{
    actualPath = storePath;
}
else
{
    // Default: persistent per-user store
    actualPath = MercuryPaths.Store("cli");
}

var store = new QuadStore(actualPath);

// Start HTTP server if enabled
SparqlHttpServer? httpServer = null;
if (enableHttp)
{
    try
    {
        httpServer = new SparqlHttpServer(
            store,
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
try
{
    using (store)
    using (httpServer)
    using (var session = new ReplSession(
        executeQuery: sparql => ExecuteQuery(store, sparql),
        executeUpdate: sparql => ExecuteUpdate(store, sparql),
        getStatistics: () => GetStatistics(store),
        getNamedGraphs: () => GetNamedGraphs(store)))
    {
        session.RunInteractive();
    }
}
finally
{
    // Clean up temp store
    if (usingTempStore && Directory.Exists(actualPath))
    {
        try
        {
            Directory.Delete(actualPath, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
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

static UpdateResult ExecuteUpdate(QuadStore store, string sparql)
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

        var executor = new UpdateExecutor(store, sparql.AsSpan(), parsed);
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
