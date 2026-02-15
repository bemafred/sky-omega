// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Mcp;
using SkyOmega.Mercury.Mcp.Services;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Execution.Federated;
using SkyOmega.Mercury.Sparql.Parsing;
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
var loadExecutor = new LoadExecutor();

Console.Error.WriteLine($"  Updates: {(enableHttpUpdates ? "enabled" : "disabled")}");

// Build host with MCP SDK
var builder = Host.CreateApplicationBuilder();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Register QuadStorePool and LoadExecutor as singletons
builder.Services.AddSingleton(pool);
builder.Services.AddSingleton(loadExecutor);

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
loadExecutor.Dispose();
pool.Dispose();

return 0;

// --- Session factory for pipe connections ---

ReplSession CreateSession(QuadStorePool pool) => new ReplSession(
    executeQuery: sparql => ExecuteQuery(pool.Active, sparql),
    executeUpdate: sparql => ExecuteUpdate(pool.Active, sparql, loadExecutor),
    getStatistics: () => GetStatistics(pool.Active),
    getNamedGraphs: () => GetNamedGraphs(pool.Active));

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
