// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using SkyOmega.Mercury.Mcp;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Protocol;
using SkyOmega.Mercury.Storage;
using ReplUpdateResult = SkyOmega.Mercury.Runtime.IO.UpdateResult;

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
ReplSession CreateSession() => new ReplSession(
    executeQuery: sparql => ExecuteQuery(store, sparql),
    executeUpdate: sparql => ExecuteUpdate(store, sparql),
    getStatistics: () => GetStatistics(store),
    getNamedGraphs: () => GetNamedGraphs(store));

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

static ReplUpdateResult ExecuteUpdate(QuadStore store, string sparql)
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
            return new ReplUpdateResult { Success = false, ErrorMessage = ex.Message, ParseTime = sw.Elapsed };
        }

        var parseTime = sw.Elapsed;
        sw.Restart();

        var executor = new UpdateExecutor(store, sparql.AsSpan(), parsed);
        var result = executor.Execute();

        return new ReplUpdateResult
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
        return new ReplUpdateResult { Success = false, ErrorMessage = ex.Message, ParseTime = sw.Elapsed };
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
