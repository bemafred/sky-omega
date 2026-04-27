using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// Cold-baseline benchmark runner for external SPARQL query suites (WDBench, BSBM, SP2Bench, etc.).
/// Loads a directory of <c>.sparql</c> files, runs each against a configured Mercury store with
/// per-query timeout + cancellation discipline, captures elapsed time + result-row count, and
/// emits per-query JSONL records plus a summary distribution.
/// </summary>
/// <remarks>
/// <para>
/// Per ADR-035 Phase 7c roadmap: WDBench cold baseline is the externally-comparable
/// "where we are now" reference point against which subsequent perf rounds measure
/// improvement. The harness produces the JSONL artifact that gets sealed into
/// <c>docs/validations/</c> and referenced from each Phase 7c round's before/after.
/// </para>
/// <para>
/// Discipline: per-query hard timeout (default 5 min) with cancellation via the existing
/// <see cref="SkyOmega.Mercury.SparqlEngine.Query(QuadStore, string, CancellationToken)"/>
/// surface. Timeouts are recorded as a separate category from completed-but-slow queries —
/// downstream consumers can filter them out of percentile computation if desired.
/// </para>
/// </remarks>
public static class WdBenchRunner
{
    public static int Run(string[] args)
    {
        var opts = ParseArgs(args);
        if (opts is null) return 1;

        Console.WriteLine($"WdBench cold-baseline runner");
        Console.WriteLine($"  Store:        {opts.StorePath}");
        Console.WriteLine($"  Queries dir:  {opts.QueriesDir}");
        Console.WriteLine($"  Metrics out:  {opts.MetricsOutPath ?? "(none)"}");
        Console.WriteLine($"  Per-query timeout: {opts.PerQueryTimeout}");
        Console.WriteLine($"  Max queries:  {opts.MaxQueries?.ToString() ?? "(all)"}");
        Console.WriteLine();

        if (!Directory.Exists(opts.StorePath))
        {
            Console.Error.WriteLine($"Store directory does not exist: {opts.StorePath}");
            return 1;
        }
        if (!Directory.Exists(opts.QueriesDir))
        {
            Console.Error.WriteLine($"Queries directory does not exist: {opts.QueriesDir}");
            return 1;
        }

        var queryFiles = Directory.EnumerateFiles(opts.QueriesDir, "*.sparql", SearchOption.AllDirectories);
        var sortedFiles = new List<string>(queryFiles);
        sortedFiles.Sort(StringComparer.Ordinal);
        if (opts.MaxQueries.HasValue && sortedFiles.Count > opts.MaxQueries.Value)
            sortedFiles = sortedFiles.GetRange(0, opts.MaxQueries.Value);

        if (sortedFiles.Count == 0)
        {
            Console.Error.WriteLine("No .sparql files found in queries directory.");
            return 1;
        }

        Console.WriteLine($"Loaded {sortedFiles.Count} queries.");
        Console.WriteLine();

        // Open the store. WDBench is read-only; no bulk mode, no profile override.
        using var pool = new QuadStorePool(opts.StorePath);
        pool.EnsureActive("primary");

        StreamWriter? metricsWriter = null;
        if (opts.MetricsOutPath is not null)
        {
            metricsWriter = new StreamWriter(opts.MetricsOutPath, append: true) { AutoFlush = true };
        }

        var elapsedTimes = new List<long>();   // microseconds, for completed queries
        int completed = 0, timedOut = 0, failed = 0;
        var globalStopwatch = Stopwatch.StartNew();

        for (int i = 0; i < sortedFiles.Count; i++)
        {
            var file = sortedFiles[i];
            string sparql;
            try { sparql = File.ReadAllText(file); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [{i + 1}/{sortedFiles.Count}] {Path.GetFileName(file)}: read-failed — {ex.Message}");
                failed++;
                continue;
            }

            using var cts = new CancellationTokenSource(opts.PerQueryTimeout);
            var sw = Stopwatch.StartNew();
            string status;
            long rows = 0;
            string? errorMsg = null;

            try
            {
                var result = SparqlEngine.Query(pool.Active, sparql, cts.Token);
                sw.Stop();
                if (result.Success)
                {
                    rows = result.Rows?.Count ?? result.Triples?.Count ?? (result.AskResult == true ? 1 : 0);
                    elapsedTimes.Add((long)sw.Elapsed.TotalMicroseconds);
                    completed++;
                    status = "completed";
                }
                else if (cts.IsCancellationRequested)
                {
                    // SparqlEngine catches OperationCanceledException internally and returns
                    // a Success=false result with ErrorMessage="Query cancelled". The harness
                    // classifies this as a timeout — the cancellation came from our token,
                    // not a real query error.
                    timedOut++;
                    status = "timeout";
                }
                else
                {
                    failed++;
                    status = "failed";
                    errorMsg = result.ErrorMessage;
                }
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                timedOut++;
                status = "timeout";
            }
            catch (Exception ex)
            {
                sw.Stop();
                failed++;
                status = "failed";
                errorMsg = ex.Message;
            }

            // Per-query JSONL record
            if (metricsWriter is not null)
            {
                using var buffer = new MemoryStream();
                using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
                {
                    json.WriteStartObject();
                    json.WriteString("schema_version", "1");
                    json.WriteString("phase", "wdbench_query");
                    json.WriteString("record_kind", "event");
                    json.WriteString("ts", DateTimeOffset.UtcNow.ToString("o"));
                    json.WriteString("file", Path.GetFileName(file));
                    json.WriteString("status", status);
                    json.WriteNumber("elapsed_us", (long)sw.Elapsed.TotalMicroseconds);
                    json.WriteNumber("rows", rows);
                    if (errorMsg is not null) json.WriteString("error", Truncate(errorMsg, 500));
                    json.WriteEndObject();
                }
                metricsWriter.WriteLine(System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
            }

            // Periodic progress to console (every 100 queries).
            if ((i + 1) % 100 == 0 || i == sortedFiles.Count - 1)
            {
                Console.WriteLine($"  [{i + 1}/{sortedFiles.Count}] completed={completed}  timed_out={timedOut}  failed={failed}");
            }
        }

        globalStopwatch.Stop();

        // Compute summary distribution from completed queries.
        elapsedTimes.Sort();
        var summary = ComputeSummary(elapsedTimes);

        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        Console.WriteLine($"  Total elapsed:  {globalStopwatch.Elapsed}");
        Console.WriteLine($"  Queries:        {sortedFiles.Count}");
        Console.WriteLine($"  Completed:      {completed}");
        Console.WriteLine($"  Timed out:      {timedOut}");
        Console.WriteLine($"  Failed:         {failed}");
        Console.WriteLine();
        Console.WriteLine($"  Completed-query latency distribution (microseconds):");
        Console.WriteLine($"    median:  {summary.P50:N0}");
        Console.WriteLine($"    p95:     {summary.P95:N0}");
        Console.WriteLine($"    p99:     {summary.P99:N0}");
        Console.WriteLine($"    p999:    {summary.P999:N0}");
        Console.WriteLine($"    max:     {summary.Max:N0}");
        Console.WriteLine($"    min:     {summary.Min:N0}");

        // Summary record at the end of the JSONL artifact.
        if (metricsWriter is not null)
        {
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
            {
                json.WriteStartObject();
                json.WriteString("schema_version", "1");
                json.WriteString("phase", "wdbench_summary");
                json.WriteString("record_kind", "event");
                json.WriteString("ts", DateTimeOffset.UtcNow.ToString("o"));
                json.WriteString("store_path", opts.StorePath);
                json.WriteString("queries_dir", opts.QueriesDir);
                json.WriteNumber("total_elapsed_ms", globalStopwatch.Elapsed.TotalMilliseconds);
                json.WriteNumber("queries_attempted", sortedFiles.Count);
                json.WriteNumber("completed", completed);
                json.WriteNumber("timed_out", timedOut);
                json.WriteNumber("failed", failed);
                json.WriteNumber("p50_us", summary.P50);
                json.WriteNumber("p95_us", summary.P95);
                json.WriteNumber("p99_us", summary.P99);
                json.WriteNumber("p999_us", summary.P999);
                json.WriteNumber("max_us", summary.Max);
                json.WriteNumber("min_us", summary.Min);
                json.WriteEndObject();
            }
            metricsWriter.WriteLine(System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
            metricsWriter.Dispose();
        }

        return failed > 0 || timedOut > sortedFiles.Count / 10 ? 2 : 0;
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s.Substring(0, maxLen);

    private record class Summary(long P50, long P95, long P99, long P999, long Max, long Min);

    private static Summary ComputeSummary(List<long> sortedAscending)
    {
        if (sortedAscending.Count == 0) return new Summary(0, 0, 0, 0, 0, 0);
        return new Summary(
            P50:  sortedAscending[(int)(sortedAscending.Count * 0.50)],
            P95:  sortedAscending[(int)(sortedAscending.Count * 0.95)],
            P99:  sortedAscending[(int)(sortedAscending.Count * 0.99)],
            P999: sortedAscending[Math.Min(sortedAscending.Count - 1, (int)(sortedAscending.Count * 0.999))],
            Max:  sortedAscending[sortedAscending.Count - 1],
            Min:  sortedAscending[0]);
    }

    private record class Options(
        string StorePath,
        string QueriesDir,
        string? MetricsOutPath,
        TimeSpan PerQueryTimeout,
        int? MaxQueries);

    private static Options? ParseArgs(string[] args)
    {
        string? storePath = null;
        string? queriesDir = null;
        string? metricsOutPath = null;
        TimeSpan timeout = TimeSpan.FromMinutes(5);
        int? maxQueries = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--store":         storePath = args[++i]; break;
                case "--queries":       queriesDir = args[++i]; break;
                case "--metrics-out":   metricsOutPath = args[++i]; break;
                case "--timeout":
                    if (double.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                        timeout = TimeSpan.FromSeconds(seconds);
                    break;
                case "--max":
                    if (int.TryParse(args[++i], out var m)) maxQueries = m;
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    return null;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    PrintHelp();
                    return null;
            }
        }

        if (storePath is null || queriesDir is null)
        {
            Console.Error.WriteLine("--store and --queries are required.");
            PrintHelp();
            return null;
        }

        return new Options(storePath, queriesDir, metricsOutPath, timeout, maxQueries);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: wdbench --store <path> --queries <dir> [--metrics-out <file>] [--timeout <seconds>] [--max <n>]");
        Console.WriteLine();
        Console.WriteLine("  --store <path>        Path to a Mercury QuadStore directory");
        Console.WriteLine("  --queries <dir>       Directory containing .sparql query files");
        Console.WriteLine("  --metrics-out <file>  Append per-query + summary JSONL records");
        Console.WriteLine("  --timeout <seconds>   Per-query hard timeout (default: 300)");
        Console.WriteLine("  --max <n>             Run only the first n queries (useful for harness validation)");
    }
}
