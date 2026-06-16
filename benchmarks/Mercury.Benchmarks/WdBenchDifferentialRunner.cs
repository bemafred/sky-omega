using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// ADR-047 breadth gate — run every WDBench query through BOTH executors (the old default path
/// <see cref="SparqlEngine.Query"/> and the unified tree path <see cref="SparqlEngine.QueryViaTreeForDifferential"/>)
/// against the same store, and compare the solution bags. This is the metamorphic mirror gate of
/// <c>DefaultVsTreeDifferentialTests</c> applied at real-query breadth: ~2,658 hand-written-by-nobody Wikidata queries
/// (single BGPs, multi-BGPs, property paths, OPTIONALs, c2rpqs) exercise shapes the ~35 hand-built unit cases cannot.
/// The truthy Reference store is the WDBench-native dataset, so this also validates the tree on the Reference profile
/// (which the writable-profile equivalence test does not cover).
///
/// Bags are compared order-insensitively (SPARQL bag semantics): per-query (row count, Σ row-hash, ⊕ row-hash). A
/// query that returns more than the store's MaxResultRows guard trips on BOTH paths identically, so huge-result
/// queries classify as equivalent (both guard-tripped) without holding a giant bag. The verdicts that matter are
/// <c>result_divergent</c> (both completed, different bags — a correctness bug) and <c>error_asymmetric</c> (one
/// completed, the other errored — the tree crashes where the old path succeeds, or vice versa). <c>timeout_asymmetric</c>
/// is a perf signal, not a correctness one. Distinct runner (not a flag on <see cref="WdBenchRunner"/>) by design.
/// </summary>
public static class WdBenchDifferentialRunner
{
    private enum RunStatus { Completed, TimedOut, Failed }

    private readonly record struct Digest(RunStatus Status, long RowCount, ulong BagSum, ulong BagXor, string? Error);

    public static int Run(string[] args)
    {
        var opts = ParseArgs(args);
        if (opts is null) return 1;

        Console.WriteLine("WDBench DIFFERENTIAL runner (old path ≡ tree path)");
        Console.WriteLine($"  Store:        {opts.StorePath}");
        Console.WriteLine($"  Queries dir:  {opts.QueriesDir}");
        Console.WriteLine($"  Report out:   {opts.ReportOutPath ?? "(none)"}");
        Console.WriteLine($"  Per-query timeout: {opts.PerQueryTimeout}");
        Console.WriteLine($"  Max queries:  {opts.MaxQueries?.ToString() ?? "(all)"}");
        Console.WriteLine($"  Tree reorder: {(opts.ReorderBgp ? "ON (planner selectivity — the cutover design)" : "off (source order)")}");
        Console.WriteLine();

        if (!Directory.Exists(opts.StorePath)) { Console.Error.WriteLine($"Store directory does not exist: {opts.StorePath}"); return 1; }
        if (!Directory.Exists(opts.QueriesDir)) { Console.Error.WriteLine($"Queries directory does not exist: {opts.QueriesDir}"); return 1; }

        var queryFiles = new List<string>(Directory.EnumerateFiles(opts.QueriesDir, "*.sparql", SearchOption.AllDirectories));
        queryFiles.Sort(StringComparer.Ordinal);
        if (opts.MaxQueries.HasValue && queryFiles.Count > opts.MaxQueries.Value)
            queryFiles = queryFiles.GetRange(0, opts.MaxQueries.Value);
        if (queryFiles.Count == 0) { Console.Error.WriteLine("No .sparql files found."); return 1; }

        Console.WriteLine($"Loaded {queryFiles.Count} queries.");
        Console.WriteLine();

        using var pool = new QuadStorePool(opts.StorePath);
        pool.EnsureActive("primary");

        StreamWriter? report = opts.ReportOutPath is not null ? new StreamWriter(opts.ReportOutPath, append: false) { AutoFlush = true } : null;

        var verdictCounts = new Dictionary<string, int>();
        var divergent = new List<(string File, string Verdict, Digest Old, Digest Tree)>();
        var keyBuffer = new List<string>(16); // reused across rows to bound hashing allocation
        // ADR-047 perf prerequisite: per-query latency, old vs tree, on the same Reference store (both-completed only,
        // so a timeout/error on either path does not skew the comparison). This is the Reference-scale planner check.
        var oldTimesMs = new List<double>();
        var treeTimesMs = new List<double>();
        var globalStopwatch = Stopwatch.StartNew();

        for (int i = 0; i < queryFiles.Count; i++)
        {
            var file = queryFiles[i];
            string sparql;
            try { sparql = File.ReadAllText(file); }
            catch (Exception ex) { Console.Error.WriteLine($"  read-failed {Path.GetFileName(file)}: {ex.Message}"); continue; }

            var swOld = Stopwatch.StartNew();
            var oldDigest = RunAndDigest(pool.Active, sparql, opts.PerQueryTimeout, tree: false, opts.ReorderBgp, keyBuffer);
            swOld.Stop();
            var swTree = Stopwatch.StartNew();
            var treeDigest = RunAndDigest(pool.Active, sparql, opts.PerQueryTimeout, tree: true, opts.ReorderBgp, keyBuffer);
            swTree.Stop();
            if (oldDigest.Status == RunStatus.Completed && treeDigest.Status == RunStatus.Completed)
            {
                oldTimesMs.Add(swOld.Elapsed.TotalMilliseconds);
                treeTimesMs.Add(swTree.Elapsed.TotalMilliseconds);
            }
            string verdict = Classify(oldDigest, treeDigest);

            verdictCounts.TryGetValue(verdict, out int c);
            verdictCounts[verdict] = c + 1;

            if (verdict is "result_divergent" or "error_asymmetric")
            {
                divergent.Add((file, verdict, oldDigest, treeDigest));
                Console.WriteLine($"  ✗ [{verdict}] {Category(file)}/{Path.GetFileName(file)}");
                Console.WriteLine($"      old:  {Describe(oldDigest)}");
                Console.WriteLine($"      tree: {Describe(treeDigest)}");
            }

            if (report is not null)
                report.WriteLine(QueryRecord(file, verdict, oldDigest, treeDigest));

            if ((i + 1) % 50 == 0 || i == queryFiles.Count - 1)
                Console.WriteLine($"  [{i + 1}/{queryFiles.Count}] divergent={divergent.Count}");
        }

        globalStopwatch.Stop();

        Console.WriteLine();
        Console.WriteLine("=== Differential summary ===");
        Console.WriteLine($"  Total elapsed: {globalStopwatch.Elapsed}");
        Console.WriteLine($"  Queries:       {queryFiles.Count}");
        foreach (var kv in verdictCounts)
            Console.WriteLine($"    {kv.Key,-20} {kv.Value}");
        Console.WriteLine();
        Console.WriteLine($"  CORRECTNESS divergences (result_divergent + error_asymmetric): {divergent.Count}");
        if (divergent.Count == 0)
            Console.WriteLine("  ✓ the tree path matches the old path on every compared query.");

        if (oldTimesMs.Count > 0)
        {
            oldTimesMs.Sort(); treeTimesMs.Sort();
            static double Med(List<double> xs) => xs[xs.Count / 2];
            static double P95(List<double> xs) => xs[Math.Min(xs.Count - 1, (int)(xs.Count * 0.95))];
            static double Tot(List<double> xs) { double t = 0; foreach (var x in xs) t += x; return t; }
            Console.WriteLine();
            Console.WriteLine($"  PERF (both paths completed: {oldTimesMs.Count} queries) — per-query ms on the Reference store:");
            Console.WriteLine($"    old   median={Med(oldTimesMs),8:F2}  p95={P95(oldTimesMs),9:F2}  total={Tot(oldTimesMs),10:F0}");
            Console.WriteLine($"    tree  median={Med(treeTimesMs),8:F2}  p95={P95(treeTimesMs),9:F2}  total={Tot(treeTimesMs),10:F0}");
            Console.WriteLine($"    tree/old total ratio: {Tot(treeTimesMs) / Tot(oldTimesMs):F2}x  (the Reference-scale planner check)");
        }

        if (report is not null)
        {
            report.WriteLine(SummaryRecord(opts, queryFiles.Count, verdictCounts, divergent.Count, globalStopwatch.Elapsed));
            report.Dispose();
        }

        // Non-zero exit only on a genuine correctness divergence — timeouts/perf asymmetry are not gate failures.
        return divergent.Count > 0 ? 2 : 0;
    }

    private static Digest RunAndDigest(QuadStore store, string sparql, TimeSpan timeout, bool tree, bool reorderBgp, List<string> keyBuffer)
    {
        using var cts = new CancellationTokenSource(timeout);
        QueryResult result;
        try
        {
            result = tree
                ? SparqlEngine.QueryViaTreeForDifferential(store, sparql, reorderBgp: reorderBgp, ct: cts.Token)
                : SparqlEngine.Query(store, sparql, cts.Token);
        }
        catch (OperationCanceledException) { return new Digest(RunStatus.TimedOut, 0, 0, 0, null); }
        catch (Exception ex) { return new Digest(RunStatus.Failed, 0, 0, 0, ex.Message); }

        if (!result.Success)
        {
            bool cancelled = cts.IsCancellationRequested
                || (result.ErrorMessage?.Contains("cancel", StringComparison.OrdinalIgnoreCase) ?? false);
            return new Digest(cancelled ? RunStatus.TimedOut : RunStatus.Failed, 0, 0, 0, result.ErrorMessage);
        }

        long count = 0; ulong sum = 0, xor = 0;
        if (result.Rows is { } rows)
        {
            foreach (var row in rows)
            {
                ulong h = HashRow(row, keyBuffer);
                sum += h; xor ^= h; count++;
            }
        }
        else if (result.Triples is { } triples)
        {
            foreach (var (s, p, o) in triples)
            {
                ulong h = 1469598103934665603UL;
                h = Fnv(h, s); h = Fnv(h, " "); h = Fnv(h, p); h = Fnv(h, " "); h = Fnv(h, o);
                sum += h; xor ^= h; count++;
            }
        }
        else if (result.AskResult is { } ask)
        {
            count = ask ? 1 : 0; sum = ask ? 1UL : 0UL;
        }
        return new Digest(RunStatus.Completed, count, sum, xor, null);
    }

    /// <summary>Order-insensitive FNV-1a over the row's sorted key=value pairs (stable within a process run).</summary>
    private static ulong HashRow(Dictionary<string, string> row, List<string> keyBuffer)
    {
        keyBuffer.Clear();
        foreach (var k in row.Keys) keyBuffer.Add(k);
        keyBuffer.Sort(StringComparer.Ordinal);
        ulong h = 1469598103934665603UL;
        foreach (var k in keyBuffer)
        {
            h = Fnv(h, k); h = Fnv(h, "="); h = Fnv(h, row[k]); h = Fnv(h, "");
        }
        return h;
    }

    private static ulong Fnv(ulong h, string s)
    {
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }

    private static string Classify(Digest old, Digest tree)
    {
        if (old.Status == RunStatus.Completed && tree.Status == RunStatus.Completed)
            return old.RowCount == tree.RowCount && old.BagSum == tree.BagSum && old.BagXor == tree.BagXor
                ? "equivalent" : "result_divergent";
        if (old.Status == tree.Status)
            return old.Status == RunStatus.TimedOut ? "both_timeout" : "both_failed";
        if (old.Status == RunStatus.TimedOut || tree.Status == RunStatus.TimedOut)
            return "timeout_asymmetric";
        return "error_asymmetric";
    }

    private static string Category(string file) => Path.GetFileName(Path.GetDirectoryName(file)) ?? "?";

    private static string Describe(in Digest d) =>
        d.Status == RunStatus.Completed
            ? $"completed rows={d.RowCount} sum={d.BagSum:x} xor={d.BagXor:x}"
            : d.Status == RunStatus.TimedOut ? "timeout" : $"failed: {Truncate(d.Error ?? "", 200)}";

    private static string QueryRecord(string file, string verdict, in Digest old, in Digest tree)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("phase", "wdbench_differential_query");
            json.WriteString("file", Path.GetFileName(file));
            json.WriteString("category", Category(file));
            json.WriteString("verdict", verdict);
            json.WriteString("old_status", old.Status.ToString());
            json.WriteString("tree_status", tree.Status.ToString());
            json.WriteNumber("old_rows", old.RowCount);
            json.WriteNumber("tree_rows", tree.RowCount);
            if (old.Error is not null) json.WriteString("old_error", Truncate(old.Error, 300));
            if (tree.Error is not null) json.WriteString("tree_error", Truncate(tree.Error, 300));
            json.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string SummaryRecord(Options opts, int total, Dictionary<string, int> verdicts, int divergent, TimeSpan elapsed)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("phase", "wdbench_differential_summary");
            json.WriteString("store_path", opts.StorePath);
            json.WriteString("queries_dir", opts.QueriesDir);
            json.WriteNumber("queries", total);
            json.WriteNumber("correctness_divergences", divergent);
            json.WriteNumber("total_elapsed_ms", elapsed.TotalMilliseconds);
            json.WriteStartObject("verdicts");
            foreach (var kv in verdicts) json.WriteNumber(kv.Key, kv.Value);
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s.Substring(0, maxLen);

    private record class Options(string StorePath, string QueriesDir, string? ReportOutPath, TimeSpan PerQueryTimeout, int? MaxQueries, bool ReorderBgp);

    private static Options? ParseArgs(string[] args)
    {
        string? storePath = null, queriesDir = null, reportOut = null;
        TimeSpan timeout = TimeSpan.FromSeconds(60);
        int? maxQueries = null;
        bool reorder = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--store": storePath = args[++i]; break;
                case "--queries": queriesDir = args[++i]; break;
                case "--report-out": reportOut = args[++i]; break;
                case "--reorder": reorder = true; break;
                case "--timeout":
                    if (double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s))
                        timeout = TimeSpan.FromSeconds(s);
                    break;
                case "--max": if (int.TryParse(args[++i], out var m)) maxQueries = m; break;
                case "-h": case "--help": PrintHelp(); return null;
                default: Console.Error.WriteLine($"Unknown argument: {args[i]}"); PrintHelp(); return null;
            }
        }
        if (storePath is null || queriesDir is null) { Console.Error.WriteLine("--store and --queries are required."); PrintHelp(); return null; }
        return new Options(storePath, queriesDir, reportOut, timeout, maxQueries, reorder);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: wdbench-diff --store <path> --queries <dir> [--report-out <file.jsonl>] [--timeout <seconds>] [--max <n>]");
        Console.WriteLine();
        Console.WriteLine("  Runs each query through BOTH executors and compares solution bags (old ≡ tree).");
        Console.WriteLine("  --store <path>        Path to a Mercury QuadStore directory");
        Console.WriteLine("  --queries <dir>       Directory of .sparql files (recursed)");
        Console.WriteLine("  --report-out <file>   Per-query + summary JSONL");
        Console.WriteLine("  --timeout <seconds>   Per-query, per-path hard timeout (default: 60)");
        Console.WriteLine("  --max <n>             Only the first n queries (harness validation)");
        Console.WriteLine("  --reorder             Tree uses the QueryPlanner selectivity reorder (the cutover design; apples-to-apples with the old path)");
    }
}
