// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Diagnostics;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Sparql.Protocol;
using SkyOmega.Mercury.Storage;

// Parse command line arguments
string? storePath = null;
string? storeName = null;
bool inMemory = false;
bool showHelp = false;
bool showVersion = false;
int httpPort = MercuryPorts.Cli;
bool enableHttp = true;
string? attachTarget = null;
string? loadFile = null;
string? bulkLoadFile = null;
string? convertInput = null;
string? convertOutput = null;
bool rebuildIndexes = false;
long? minFreeSpaceGB = null;
long? loadLimit = null;
string? metricsOutPath = null;
bool noRepl = false;
StoreProfile? requestedProfile = null;

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
        case "--store":
            if (i + 1 < args.Length)
                storeName = args[++i];
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
        case "--load":
            if (i + 1 < args.Length)
                loadFile = args[++i];
            break;
        case "--bulk-load":
            if (i + 1 < args.Length)
                bulkLoadFile = args[++i];
            break;
        case "--convert":
            if (i + 2 < args.Length)
            {
                convertInput = args[++i];
                convertOutput = args[++i];
            }
            break;
        case "--rebuild-indexes":
            rebuildIndexes = true;
            break;
        case "--min-free-space":
            if (i + 1 < args.Length && long.TryParse(args[++i], out var gb))
                minFreeSpaceGB = gb;
            break;
        case "--limit":
            if (i + 1 < args.Length && long.TryParse(args[++i], out var lim) && lim >= 0)
                loadLimit = lim;
            else
            {
                Console.Error.WriteLine("Error: --limit requires a non-negative integer.");
                return 1;
            }
            break;
        case "--metrics-out":
            if (i + 1 < args.Length)
                metricsOutPath = args[++i];
            break;
        case "--no-repl":
            noRepl = true;
            break;
        case "--profile":
            if (i + 1 < args.Length && System.Enum.TryParse<StoreProfile>(args[++i], ignoreCase: true, out var parsedProfile))
            {
                requestedProfile = parsedProfile;
            }
            else
            {
                Console.Error.WriteLine($"Error: --profile requires one of: {string.Join(", ", System.Enum.GetNames<StoreProfile>())}.");
                return 1;
            }
            break;
        default:
            if (args[i].StartsWith('-'))
            {
                Console.Error.WriteLine($"Error: Unknown option '{args[i]}'.");
                Console.Error.WriteLine("Use --help for usage information.");
                return 1;
            }
            if (storePath != null)
            {
                Console.Error.WriteLine($"Error: Unexpected argument '{args[i]}'. Store path already set to '{storePath}'.");
                Console.Error.WriteLine("Use --help for usage information.");
                return 1;
            }
            if (LooksLikeSparql(args[i]))
            {
                Console.Error.WriteLine($"Error: '{args[i]}' looks like a SPARQL query, not a store path.");
                Console.Error.WriteLine("The mercury CLI does not execute queries directly from arguments.");
                Console.Error.WriteLine("Use 'mercury-sparql --query \"...\"' for one-shot queries,");
                Console.Error.WriteLine("or start the REPL with 'mercury' and type queries interactively.");
                return 1;
            }
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
          -v, --version              Show version information
          -h, --help                 Show this help message
          -m, --memory               Use temporary in-memory store (deleted on exit)
          -d, --data <path>          Path to data directory
          --store <name>             Named store (e.g., wikidata, fhir)
          -p, --port <port>          HTTP port (default: 3031)
          --no-http                  Disable HTTP endpoint
          -a, --attach [target]      Attach to running instance (default: mcp)
          --load <file>              Load RDF file at startup, then enter REPL
          --bulk-load <file>         Bulk load (GSPO only, no fsync), then enter REPL
          --convert <in> <out>       Streaming format conversion (no store, exits after)
          --rebuild-indexes          Rebuild secondary indexes, then enter REPL
          --min-free-space <GB>      Minimum free disk space (default: 100 for bulk, 1 otherwise)
          --limit <N>                Cap triples added (--bulk-load/--load) or emitted (--convert) at N
          --metrics-out <file>       Append JSONL metrics records (one per progress tick) for convert/load/rebuild
          --no-repl                  Skip the REPL after --load/--bulk-load/--rebuild-indexes (for profilers, CI, or pipes that stay open)

        Examples:
          mercury                                # Default store (cli)
          mercury --store wikidata               # Named store (wikidata)
          mercury --store wikidata --bulk-load data.nt   # Bulk load into named store
          mercury --convert data.ttl data.nt     # Convert Turtle to N-Triples
          mercury --store wikidata --rebuild-indexes     # Build GPOS/GOSP/TGSP
          mercury -m                             # Temporary store (deleted on exit)

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

// Open metrics output stream once if requested — flushed/disposed on process exit.
StreamWriter? metricsWriter = metricsOutPath != null
    ? new StreamWriter(new FileStream(metricsOutPath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true }
    : null;

var metricsJsonOptions = new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

void WriteMetric(object record)
{
    if (metricsWriter == null) return;
    metricsWriter.WriteLine(JsonSerializer.Serialize(record, metricsJsonOptions));
}

// Handle --convert (no store needed, exits after completion)
if (convertInput != null && convertOutput != null)
{
    Console.WriteLine($"Converting {convertInput} -> {convertOutput}...");
    if (loadLimit.HasValue)
        Console.WriteLine($"Limit:           {loadLimit.Value:N0} triples");
    var convertStart = DateTimeOffset.UtcNow;
    var count = await RdfEngine.ConvertAsync(convertInput, convertOutput, p =>
    {
        var rate = p.Elapsed.TotalSeconds > 0 ? p.TriplesLoaded / p.Elapsed.TotalSeconds : 0;
        Console.Error.Write($"\r  {p.TriplesLoaded:N0} triples  {rate:N0}/sec  {p.Elapsed.TotalSeconds:F1}s");

        WriteMetric(new
        {
            ts = DateTimeOffset.UtcNow.ToString("o"),
            phase = "convert",
            input = convertInput,
            output = convertOutput,
            triples = p.TriplesLoaded,
            triples_per_sec = rate,
            elapsed_sec = p.Elapsed.TotalSeconds,
        });
    }, limit: loadLimit);
    Console.Error.WriteLine();
    Console.WriteLine($"Converted {count:N0} triples.");

    var convertElapsed = DateTimeOffset.UtcNow - convertStart;
    WriteMetric(new
    {
        ts = DateTimeOffset.UtcNow.ToString("o"),
        phase = "convert.summary",
        input = convertInput,
        output = convertOutput,
        triples = count,
        elapsed_sec = convertElapsed.TotalSeconds,
        avg_triples_per_sec = convertElapsed.TotalSeconds > 0 ? count / convertElapsed.TotalSeconds : 0,
    });
    metricsWriter?.Dispose();
    return 0;
}

// Create or open pool
QuadStorePool pool;
string resolvedStorePath;
bool isBulkLoad = bulkLoadFile != null;

// Determine minimum free disk space: explicit flag > bulk default (100 GB) > normal default (1 GB)
var minFreeSpace = minFreeSpaceGB.HasValue
    ? minFreeSpaceGB.Value * 1024L * 1024L * 1024L
    : isBulkLoad ? 100L * 1024L * 1024L * 1024L
    : StorageOptions.DefaultMinimumFreeDiskSpace;

if (inMemory)
{
    pool = QuadStorePool.CreateTemp("cli-session");
    resolvedStorePath = pool.BasePath!;
}
else
{
    resolvedStorePath = storePath ?? (storeName != null ? MercuryPaths.Store(storeName) : MercuryPaths.Store("cli"));
    try
    {
        // ADR-028 Stage 2 validation knob: when set, honor this exactly as the AtomStore
        // initial hash capacity (bypasses the bulk-mode 256M floor) so rehash-on-grow
        // is exercised during bulk load. Unset in normal operation.
        long? atomHashOverride = null;
        if (Environment.GetEnvironmentVariable("MERCURY_ATOM_HASH_INITIAL_CAPACITY") is string capVar
            && long.TryParse(capVar, out var parsedCap) && parsedCap > 0)
        {
            atomHashOverride = parsedCap;
            Console.WriteLine($"Override:        MERCURY_ATOM_HASH_INITIAL_CAPACITY={parsedCap:N0} buckets (forces rehash-on-grow)");
        }

        var storeOpts = new StorageOptions
        {
            BulkMode = isBulkLoad,
            MinimumFreeDiskSpace = minFreeSpace,
            AtomHashTableInitialCapacity = atomHashOverride ?? new StorageOptions().AtomHashTableInitialCapacity,
            ForceAtomHashCapacity = atomHashOverride.HasValue,
            // --profile only takes effect at store creation; existing stores honor their
            // persisted store-schema.json regardless of what the caller passed.
            Profile = requestedProfile ?? StoreProfile.Cognitive,
        };
        pool = new QuadStorePool(resolvedStorePath, new QuadStorePoolOptions { StorageOptions = storeOpts });
    }
    catch (StoreInUseException ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Console.Error.WriteLine("Use -m for a temporary in-memory session, or -d to specify a different store path.");
        return 1;
    }
}

// Ensure a primary store exists so pool.Active works immediately
// (new pools and temp pools start with no stores; persistent pools
// may have been created by an older version without pool metadata)
pool.EnsureActive("primary");

// ADR-030 Phase 1: when --metrics-out is set, install the JSONL listener on the
// active store so query and rebuild records land in the same file as load progress.
JsonlMetricsListener? jsonlListener = null;
if (metricsOutPath != null)
{
    jsonlListener = new JsonlMetricsListener(metricsOutPath);
    pool.Active.QueryMetricsListener = jsonlListener;
    pool.Active.RebuildMetricsListener = jsonlListener;
}

// Print startup diagnostics when loading or rebuilding
if (loadFile != null || bulkLoadFile != null || rebuildIndexes)
{
    var freeSpace = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(resolvedStorePath))!).AvailableFreeSpace;
    Console.WriteLine($"Store:           {resolvedStorePath}");
    Console.WriteLine($"Profile:         {pool.Active.Schema.Profile}");
    Console.WriteLine($"Index state:     {pool.Active.IndexState}");
    if (isBulkLoad)
        Console.WriteLine($"Mode:            bulk (GSPO only, no fsync)");
    if (freeSpace >= 0)
        Console.WriteLine($"Free disk space: {freeSpace / (1024.0 * 1024 * 1024):F1} GB");
    Console.WriteLine($"Min free space:  {minFreeSpace / (1024.0 * 1024 * 1024):F0} GB");
    if (loadLimit.HasValue)
        Console.WriteLine($"Limit:           {loadLimit.Value:N0} triples");
    Console.WriteLine();
}

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

// Handle startup actions (before REPL)
var fileToLoad = bulkLoadFile ?? loadFile;
if (fileToLoad != null)
{
    var mode = bulkLoadFile != null ? "bulk" : "standard";
    Console.WriteLine($"Loading {fileToLoad} ({mode})...");
    Console.WriteLine();

    var lastDisplayTime = DateTimeOffset.MinValue;
    var loadStartTime = DateTimeOffset.UtcNow;

    var count = await RdfEngine.LoadFileAsync(pool.Active, fileToLoad, onProgress: p =>
    {
        // Metrics file gets every progress callback (denser than terminal display)
        WriteMetric(new
        {
            ts = DateTimeOffset.UtcNow.ToString("o"),
            phase = "load",
            mode,
            file = fileToLoad,
            triples = p.TriplesLoaded,
            triples_per_sec_avg = p.TriplesPerSecond,
            triples_per_sec_recent = p.RecentTriplesPerSecond,
            gc_heap_bytes = p.GcHeapBytes,
            working_set_bytes = p.WorkingSetBytes,
            elapsed_sec = p.Elapsed.TotalSeconds,
        });

        // Throttle terminal display to every 10 seconds
        var now = DateTimeOffset.UtcNow;
        if ((now - lastDisplayTime).TotalSeconds < 10) return;
        lastDisplayTime = now;

        var elapsed = p.Elapsed;
        var gcMB = p.GcHeapBytes / (1024.0 * 1024);
        var wsMB = p.WorkingSetBytes / (1024.0 * 1024);

        Console.Error.WriteLine(
            $"  {elapsed:hh\\:mm\\:ss}  " +
            $"{p.TriplesLoaded:N0} triples  " +
            $"avg {p.TriplesPerSecond:N0}/sec  " +
            $"recent {p.RecentTriplesPerSecond:N0}/sec  " +
            $"GC {gcMB:N0} MB  " +
            $"RSS {wsMB:N0} MB");
    }, limit: loadLimit);

    Console.Error.WriteLine();

    // Summary
    var totalElapsed = DateTimeOffset.UtcNow - loadStartTime;
    var avgRate = totalElapsed.TotalSeconds > 0 ? count / totalElapsed.TotalSeconds : 0;
    var finalGcMB = GC.GetTotalMemory(false) / (1024.0 * 1024);
    var finalWsMB = Environment.WorkingSet / (1024.0 * 1024);
    var freeAfter = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(resolvedStorePath))!).AvailableFreeSpace;

    Console.WriteLine($"--- Load complete ---");
    Console.WriteLine($"  Triples:       {count:N0}");
    Console.WriteLine($"  Elapsed:       {totalElapsed:hh\\:mm\\:ss}");
    Console.WriteLine($"  Avg rate:      {avgRate:N0} triples/sec");
    Console.WriteLine($"  GC heap:       {finalGcMB:N0} MB");
    Console.WriteLine($"  Working set:   {finalWsMB:N0} MB");
    Console.WriteLine($"  Free disk:     {freeAfter / (1024.0 * 1024 * 1024):F1} GB");
    Console.WriteLine();

    WriteMetric(new
    {
        ts = DateTimeOffset.UtcNow.ToString("o"),
        phase = "load.summary",
        mode,
        file = fileToLoad,
        triples = count,
        elapsed_sec = totalElapsed.TotalSeconds,
        avg_triples_per_sec = avgRate,
        gc_heap_bytes = (long)(finalGcMB * 1024 * 1024),
        working_set_bytes = (long)(finalWsMB * 1024 * 1024),
        free_disk_bytes = freeAfter,
    });
}

if (rebuildIndexes)
{
    Console.WriteLine("Rebuilding secondary indexes...");
    var rebuildStart = DateTimeOffset.UtcNow;
    pool.Active.RebuildSecondaryIndexes((name, entries) =>
    {
        Console.Error.WriteLine($"  {name}: {entries:N0} entries");
        WriteMetric(new
        {
            ts = DateTimeOffset.UtcNow.ToString("o"),
            phase = "rebuild",
            index = name,
            entries,
        });
    });
    var rebuildElapsed = DateTimeOffset.UtcNow - rebuildStart;
    WriteMetric(new
    {
        ts = DateTimeOffset.UtcNow.ToString("o"),
        phase = "rebuild.summary",
        elapsed_sec = rebuildElapsed.TotalSeconds,
    });
    Console.WriteLine("Secondary indexes rebuilt.");
}

// Flush/close metrics file before entering REPL
metricsWriter?.Dispose();

// Create ReplSession with facade calls and run REPL
using (pool)
using (httpServer)
using (var session = new ReplSession(
    executeQuery: sparql => SparqlEngine.Query(pool.Active, sparql),
    executeUpdate: sparql => SparqlEngine.Update(pool.Active, sparql),
    getStatistics: () => SparqlEngine.GetStatistics(pool.Active),
    getNamedGraphs: () => SparqlEngine.GetNamedGraphs(pool.Active),
    executePrune: pruneArgs => ExecutePrune(pool, pruneArgs),
    getStorePath: () => resolvedStorePath,
    executeAttach: target => RunAttachSession(target),
    executeLoad: async (path, bulk, progress) =>
    {
        Action<LoadProgress>? onProgress = progress != null
            ? p => progress(p.TriplesLoaded, p.Elapsed)
            : null;
        return await RdfEngine.LoadFileAsync(pool.Active, path, onProgress: onProgress);
    },
    executeConvert: async (input, output, progress) =>
    {
        Action<LoadProgress>? onProgress = progress != null
            ? p => progress(p.TriplesLoaded, p.Elapsed)
            : null;
        return await RdfEngine.ConvertAsync(input, output, onProgress);
    },
    executeRebuildIndexes: progress =>
    {
        pool.Active.RebuildSecondaryIndexes(progress);
        return Task.CompletedTask;
    }))
{
    // --no-repl is the explicit opt-out for profilers, CI, and Rider run configs
    // that hold stdin open with no data (which would hang the REPL in read()).
    // Piped stdin with EOF (echo/cat/heredoc) works fine without the flag — the
    // REPL reads lines until EOF and exits.
    if (!noRepl)
    {
        session.RunInteractive();
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

static async Task RunAttachSession(string target)
{
    var pipeName = target.ToLowerInvariant() switch
    {
        "mcp" => MercuryPorts.McpPipeName,
        "cli" => MercuryPorts.CliPipeName,
        _ => target
    };

    Console.WriteLine($"Attaching to {target} via pipe '{pipeName}'...");

    using var client = new PipeClient(pipeName);
    await client.ConnectAsync(timeoutMs: 5000);
    await client.RunInteractiveAsync();

    Console.WriteLine($"Detached from {target}.");
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

static bool LooksLikeSparql(string arg)
{
    // Detect SPARQL keywords and syntax that should never appear in a file path
    var upper = arg.ToUpperInvariant();
    return upper.Contains("SELECT ") || upper.Contains("CONSTRUCT ") ||
           upper.Contains("DESCRIBE ") || upper.Contains("ASK ") ||
           upper.Contains("INSERT ") || upper.Contains("DELETE ") ||
           upper.Contains("WHERE") || arg.Contains('{') || arg.Contains('}') ||
           arg.Contains("?s ") || arg.Contains("?p ") || arg.Contains("?o ");
}
