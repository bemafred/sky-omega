using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using SkyOmega.Mercury.Compression;
using SkyOmega.Mercury.NTriples;
using SkyOmega.Mercury.Rdf.Turtle;
using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.Compression;

/// <summary>
/// Decomposes the convert path (Turtle → N-Triples) to identify which stage actually
/// dominates wall-clock. The 2.7M triples/sec figure in STATISTICS predates Phase 7a
/// metrics infrastructure; this measurement runs three configurations on the same
/// 50 MB / ~58-block fixture and reports per-stage attribution.
///
/// The decisive comparison is configuration 3 (pre-decompressed) vs 1 (single-threaded
/// bz2 → parser → NT). The difference between them is the bz2 decompression cost. If
/// pre-decompressed runs significantly faster than bz2 → parser → NT, bz2 is bottleneck
/// and parallel bz2 helps. If they're similar, parser/writer is the bottleneck and bz2
/// optimization is wasted.
/// </summary>
public class ConvertPathThroughputMeasurement
{
    private const string FixturePath = "/tmp/multiblock-large.txt.bz2";
    private readonly ITestOutputHelper _output;

    public ConvertPathThroughputMeasurement(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Measure_ConvertPath_ByConfiguration()
    {
        if (!File.Exists(FixturePath))
        {
            _output.WriteLine($"SKIP: fixture not present at {FixturePath} — generate via 'bzip2 -9' on a 50 MB Turtle-shaped file");
            return;
        }

        var bz2 = File.ReadAllBytes(FixturePath);

        // Pre-decompress once for configuration 3.
        byte[] uncompressed;
        {
            using var input = new MemoryStream(bz2);
            using var dec = new BZip2DecompressorStream(input);
            using var ms = new MemoryStream(capacity: bz2.Length * 10);
            var buf = new byte[64 * 1024];
            int n;
            while ((n = dec.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
            uncompressed = ms.ToArray();
        }

        _output.WriteLine($"Fixture: {FixturePath}");
        _output.WriteLine($"  compressed:   {bz2.Length:N0} bytes ({bz2.Length / (1024.0 * 1024):F2} MB)");
        _output.WriteLine($"  decompressed: {uncompressed.Length:N0} bytes ({uncompressed.Length / (1024.0 * 1024):F1} MB)");
        _output.WriteLine("");

        // Note: input is synthetic Wikidata-shaped (URIs + integer literals). Not valid
        // Turtle (no @prefix/@base, no proper IRI escapes), but the parser can still
        // exercise the lexer + writer paths. If parsing fails on the fixture, fall back
        // to counting bytes consumed.
        _output.WriteLine($"  Configuration                    | wall (s) | source MB/s | triples (M) | triples/sec");
        _output.WriteLine($"  ---------------------------------|----------|-------------|-------------|------------");

        // Warmup
        await RunConfiguration(bz2, useParallel: false, warmup: true);

        var configs = new (string Label, Func<Task<(TimeSpan, long)>> Run)[]
        {
            ("single-threaded bz2 → parser → NT", () => MeasureBz2(bz2, useParallel: false, parallelWorkers: 0)),
            ("parallel(4) bz2 → parser → NT     ", () => MeasureBz2(bz2, useParallel: true, parallelWorkers: 4)),
            ("parallel(14) bz2 → parser → NT    ", () => MeasureBz2(bz2, useParallel: true, parallelWorkers: 14)),
            ("pre-decompressed → parser → NT    ", () => MeasurePreDecompressed(uncompressed)),
        };

        foreach (var (label, run) in configs)
        {
            var (elapsed, triples) = await run();
            double seconds = elapsed.TotalSeconds;
            double sourceMBps = (uncompressed.Length / (1024.0 * 1024)) / seconds;  // decompressed bytes / time
            double triplesPerSec = triples / seconds;
            _output.WriteLine($"  {label} | {seconds,8:F2} | {sourceMBps,11:F1} | {triples / 1_000_000.0,11:F2} | {triplesPerSec,11:N0}");
        }
    }

    private static async Task<(TimeSpan, long)> MeasureBz2(byte[] bz2, bool useParallel, int parallelWorkers)
    {
        long triples = 0;
        var sw = Stopwatch.StartNew();
        using var input = new MemoryStream(bz2);
        Stream decompress = useParallel
            ? new ParallelBZip2DecompressorStream(input, workerCount: parallelWorkers)
            : new BZip2DecompressorStream(input);
        try
        {
            using var parser = new TurtleStreamParser(decompress);
            await using var writer = new NTriplesStreamWriter(TextWriter.Null);
            try
            {
                await parser.ParseAsync((s, p, o) =>
                {
                    triples++;
                    writer.WriteTriple(s, p, o);
                });
            }
            catch (Exception ex) when (ex.Message.Contains("expected") || ex.Message.Contains("PN_") || ex.Message.Contains("Unexpected"))
            {
                // Synthetic fixture isn't strict Turtle — accept partial parse, report what we got.
            }
        }
        finally { decompress.Dispose(); }
        sw.Stop();
        return (sw.Elapsed, triples);
    }

    private static async Task<(TimeSpan, long)> MeasurePreDecompressed(byte[] uncompressed)
    {
        long triples = 0;
        var sw = Stopwatch.StartNew();
        using var input = new MemoryStream(uncompressed);
        using var parser = new TurtleStreamParser(input);
        await using var writer = new NTriplesStreamWriter(TextWriter.Null);
        try
        {
            await parser.ParseAsync((s, p, o) =>
            {
                triples++;
                writer.WriteTriple(s, p, o);
            });
        }
        catch (Exception ex) when (ex.Message.Contains("expected") || ex.Message.Contains("PN_") || ex.Message.Contains("Unexpected"))
        {
            // partial parse OK
        }
        sw.Stop();
        return (sw.Elapsed, triples);
    }

    private static async Task RunConfiguration(byte[] bz2, bool useParallel, bool warmup)
    {
        await MeasureBz2(bz2, useParallel, parallelWorkers: 4);
    }
}
