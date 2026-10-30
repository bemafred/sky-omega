using BenchmarkDotNet.Attributes;
using SkyOmega.Mercury.Compression;
using SkyOmega.Mercury.NTriples;
using SkyOmega.Mercury.Rdf.Turtle;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// The convert path, bzip2 → Turtle parser → N-Triples writer, on the generated 50 MB Wikidata-shaped document
/// (<see cref="BZip2LargeFixture"/>), against the same parse and write of the decompressed text. The difference is
/// what decompression costs on this path, and whether the parallel stream pays for itself here (ADR-036).
/// </summary>
/// <remarks>
/// This was <c>ConvertPathThroughputMeasurement</c> in the test suite. It read a file in <c>/tmp</c> that existed
/// only where it was made, and otherwise returned and reported passed; and its input was not valid Turtle, so it
/// caught the parse error and measured up to it. The generated document is valid N-Triples, which is Turtle.
/// </remarks>
[MemoryDiagnoser]
public class ConvertPathBenchmarks
{
    private byte[] _compressed = null!;
    private byte[] _text = null!;

    [GlobalSetup]
    public void Setup()
    {
        _compressed = BZip2LargeFixture.Compressed();
        _text = BZip2LargeFixture.Text();
    }

    [Benchmark(Baseline = true)]
    public Task<long> Decompressed() => ConvertAsync(new MemoryStream(_text));

    [Benchmark]
    public Task<long> SingleThreadedBZip2() => ConvertAsync(new BZip2DecompressorStream(new MemoryStream(_compressed)));

    [Benchmark]
    [Arguments(4)]
    [Arguments(14)]
    public Task<long> ParallelBZip2(int workers)
        => ConvertAsync(new ParallelBZip2DecompressorStream(new MemoryStream(_compressed), workerCount: workers));

    /// <summary>Parses <paramref name="source"/> as Turtle and writes each triple as N-Triples; returns the count.</summary>
    private static async Task<long> ConvertAsync(Stream source)
    {
        long triples = 0;
        await using (source)
        {
            using var parser = new TurtleStreamParser(source);
            await using var writer = new NTriplesStreamWriter(TextWriter.Null);
            await parser.ParseAsync((s, p, o) =>
            {
                triples++;
                writer.WriteTriple(s, p, o);
            });
        }
        return triples;
    }
}
