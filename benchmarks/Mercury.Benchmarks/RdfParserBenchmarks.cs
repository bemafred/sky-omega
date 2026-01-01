using System.Text;
using BenchmarkDotNet.Attributes;
using SkyOmega.Mercury.NTriples;
using SkyOmega.Mercury.Rdf.Turtle;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// Benchmarks for N-Triples parser throughput.
/// Compares zero-GC handler API vs allocating IAsyncEnumerable API.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class NTriplesParserBenchmarks
{
    private MemoryStream _smallData = null!;
    private MemoryStream _mediumData = null!;
    private MemoryStream _largeData = null!;

    private const int SmallTripleCount = 1_000;
    private const int MediumTripleCount = 10_000;
    private const int LargeTripleCount = 100_000;

    [GlobalSetup]
    public void Setup()
    {
        _smallData = GenerateNTriples(SmallTripleCount);
        _mediumData = GenerateNTriples(MediumTripleCount);
        _largeData = GenerateNTriples(LargeTripleCount);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _smallData.Dispose();
        _mediumData.Dispose();
        _largeData.Dispose();
    }

    private static MemoryStream GenerateNTriples(int count)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            sb.AppendLine($"<http://example.org/person{i}> <http://xmlns.com/foaf/0.1/name> \"Person {i}\" .");
            sb.AppendLine($"<http://example.org/person{i}> <http://xmlns.com/foaf/0.1/age> \"{20 + (i % 60)}\"^^<http://www.w3.org/2001/XMLSchema#integer> .");
            sb.AppendLine($"<http://example.org/person{i}> <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://xmlns.com/foaf/0.1/Person> .");
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return new MemoryStream(bytes);
    }

    [Benchmark(Description = "N-Triples 1K (zero-GC handler)")]
    public async Task<long> NTriples_Small_ZeroGC()
    {
        _smallData.Position = 0;
        long count = 0;

        var parser = new NTriplesStreamParser(_smallData);
        await parser.ParseAsync((s, p, o) => count++);

        return count;
    }

    [Benchmark(Description = "N-Triples 1K (IAsyncEnumerable)")]
    public async Task<long> NTriples_Small_Allocating()
    {
        _smallData.Position = 0;
        long count = 0;

        var parser = new NTriplesStreamParser(_smallData);
        await foreach (var triple in parser.ParseAsync())
        {
            count++;
            _ = triple.Subject.Length;
        }

        return count;
    }

    [Benchmark(Description = "N-Triples 10K (zero-GC handler)")]
    public async Task<long> NTriples_Medium_ZeroGC()
    {
        _mediumData.Position = 0;
        long count = 0;

        var parser = new NTriplesStreamParser(_mediumData);
        await parser.ParseAsync((s, p, o) => count++);

        return count;
    }

    [Benchmark(Description = "N-Triples 10K (IAsyncEnumerable)")]
    public async Task<long> NTriples_Medium_Allocating()
    {
        _mediumData.Position = 0;
        long count = 0;

        var parser = new NTriplesStreamParser(_mediumData);
        await foreach (var triple in parser.ParseAsync())
        {
            count++;
            _ = triple.Subject.Length;
        }

        return count;
    }

    [Benchmark(Description = "N-Triples 100K (zero-GC handler)")]
    public async Task<long> NTriples_Large_ZeroGC()
    {
        _largeData.Position = 0;
        long count = 0;

        var parser = new NTriplesStreamParser(_largeData);
        await parser.ParseAsync((s, p, o) => count++);

        return count;
    }

    [Benchmark(Description = "N-Triples 100K (IAsyncEnumerable)")]
    public async Task<long> NTriples_Large_Allocating()
    {
        _largeData.Position = 0;
        long count = 0;

        var parser = new NTriplesStreamParser(_largeData);
        await foreach (var triple in parser.ParseAsync())
        {
            count++;
            _ = triple.Subject.Length;
        }

        return count;
    }
}

/// <summary>
/// Benchmarks for Turtle parser throughput.
/// Compares zero-GC handler API vs allocating IAsyncEnumerable API.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class TurtleParserBenchmarks
{
    private MemoryStream _smallData = null!;
    private MemoryStream _mediumData = null!;
    private MemoryStream _largeData = null!;
    private MemoryStream _prefixedData = null!;

    private const int SmallTripleCount = 1_000;
    private const int MediumTripleCount = 10_000;
    private const int LargeTripleCount = 100_000;

    [GlobalSetup]
    public void Setup()
    {
        _smallData = GenerateTurtle(SmallTripleCount, usePrefixes: false);
        _mediumData = GenerateTurtle(MediumTripleCount, usePrefixes: false);
        _largeData = GenerateTurtle(LargeTripleCount, usePrefixes: false);
        _prefixedData = GenerateTurtle(MediumTripleCount, usePrefixes: true);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _smallData.Dispose();
        _mediumData.Dispose();
        _largeData.Dispose();
        _prefixedData.Dispose();
    }

    private static MemoryStream GenerateTurtle(int count, bool usePrefixes)
    {
        var sb = new StringBuilder();

        if (usePrefixes)
        {
            sb.AppendLine("@prefix ex: <http://example.org/> .");
            sb.AppendLine("@prefix foaf: <http://xmlns.com/foaf/0.1/> .");
            sb.AppendLine("@prefix xsd: <http://www.w3.org/2001/XMLSchema#> .");
            sb.AppendLine();

            for (int i = 0; i < count; i++)
            {
                // Use Turtle's subject grouping with semicolons
                sb.AppendLine($"ex:person{i}");
                sb.AppendLine($"    foaf:name \"Person {i}\" ;");
                sb.AppendLine($"    foaf:age \"{20 + (i % 60)}\"^^xsd:integer ;");
                sb.AppendLine($"    a foaf:Person .");
                sb.AppendLine();
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                // Full IRIs without prefixes
                sb.AppendLine($"<http://example.org/person{i}>");
                sb.AppendLine($"    <http://xmlns.com/foaf/0.1/name> \"Person {i}\" ;");
                sb.AppendLine($"    <http://xmlns.com/foaf/0.1/age> \"{20 + (i % 60)}\"^^<http://www.w3.org/2001/XMLSchema#integer> ;");
                sb.AppendLine($"    <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://xmlns.com/foaf/0.1/Person> .");
                sb.AppendLine();
            }
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return new MemoryStream(bytes);
    }

    [Benchmark(Description = "Turtle 1K (zero-GC handler)")]
    public async Task<long> Turtle_Small_ZeroGC()
    {
        _smallData.Position = 0;
        long count = 0;

        var parser = new TurtleStreamParser(_smallData);
        await parser.ParseAsync((s, p, o) => count++);

        return count;
    }

    [Benchmark(Description = "Turtle 1K (IAsyncEnumerable)")]
    public async Task<long> Turtle_Small_Allocating()
    {
        _smallData.Position = 0;
        long count = 0;

        var parser = new TurtleStreamParser(_smallData);
        await foreach (var triple in parser.ParseAsync())
        {
            count++;
            _ = triple.Subject.Length;
        }

        return count;
    }

    [Benchmark(Description = "Turtle 10K (zero-GC handler)")]
    public async Task<long> Turtle_Medium_ZeroGC()
    {
        _mediumData.Position = 0;
        long count = 0;

        var parser = new TurtleStreamParser(_mediumData);
        await parser.ParseAsync((s, p, o) => count++);

        return count;
    }

    [Benchmark(Description = "Turtle 10K (IAsyncEnumerable)")]
    public async Task<long> Turtle_Medium_Allocating()
    {
        _mediumData.Position = 0;
        long count = 0;

        var parser = new TurtleStreamParser(_mediumData);
        await foreach (var triple in parser.ParseAsync())
        {
            count++;
            _ = triple.Subject.Length;
        }

        return count;
    }

    [Benchmark(Description = "Turtle 100K (zero-GC handler)")]
    public async Task<long> Turtle_Large_ZeroGC()
    {
        _largeData.Position = 0;
        long count = 0;

        var parser = new TurtleStreamParser(_largeData);
        await parser.ParseAsync((s, p, o) => count++);

        return count;
    }

    [Benchmark(Description = "Turtle 100K (IAsyncEnumerable)")]
    public async Task<long> Turtle_Large_Allocating()
    {
        _largeData.Position = 0;
        long count = 0;

        var parser = new TurtleStreamParser(_largeData);
        await foreach (var triple in parser.ParseAsync())
        {
            count++;
            _ = triple.Subject.Length;
        }

        return count;
    }

    [Benchmark(Description = "Turtle 10K prefixed (zero-GC)")]
    public async Task<long> Turtle_Prefixed_ZeroGC()
    {
        _prefixedData.Position = 0;
        long count = 0;

        var parser = new TurtleStreamParser(_prefixedData);
        await parser.ParseAsync((s, p, o) => count++);

        return count;
    }

    [Benchmark(Description = "Turtle 10K prefixed (IAsyncEnumerable)")]
    public async Task<long> Turtle_Prefixed_Allocating()
    {
        _prefixedData.Position = 0;
        long count = 0;

        var parser = new TurtleStreamParser(_prefixedData);
        await foreach (var triple in parser.ParseAsync())
        {
            count++;
            _ = triple.Subject.Length;
        }

        return count;
    }
}

/// <summary>
/// Comparative benchmarks between N-Triples and Turtle parsing.
/// Uses same logical data to compare format overhead.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class RdfFormatComparisonBenchmarks
{
    private MemoryStream _ntriplesData = null!;
    private MemoryStream _turtleFullIri = null!;
    private MemoryStream _turtlePrefixed = null!;

    private const int TripleCount = 10_000;

    [GlobalSetup]
    public void Setup()
    {
        // Generate same logical triples in different formats
        _ntriplesData = GenerateNTriples(TripleCount);
        _turtleFullIri = GenerateTurtleFullIri(TripleCount);
        _turtlePrefixed = GenerateTurtlePrefixed(TripleCount);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _ntriplesData.Dispose();
        _turtleFullIri.Dispose();
        _turtlePrefixed.Dispose();
    }

    private static MemoryStream GenerateNTriples(int count)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            sb.AppendLine($"<http://example.org/person{i}> <http://xmlns.com/foaf/0.1/name> \"Person {i}\" .");
            sb.AppendLine($"<http://example.org/person{i}> <http://xmlns.com/foaf/0.1/age> \"{20 + (i % 60)}\"^^<http://www.w3.org/2001/XMLSchema#integer> .");
            sb.AppendLine($"<http://example.org/person{i}> <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://xmlns.com/foaf/0.1/Person> .");
        }
        return new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static MemoryStream GenerateTurtleFullIri(int count)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            sb.AppendLine($"<http://example.org/person{i}>");
            sb.AppendLine($"    <http://xmlns.com/foaf/0.1/name> \"Person {i}\" ;");
            sb.AppendLine($"    <http://xmlns.com/foaf/0.1/age> \"{20 + (i % 60)}\"^^<http://www.w3.org/2001/XMLSchema#integer> ;");
            sb.AppendLine($"    <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://xmlns.com/foaf/0.1/Person> .");
        }
        return new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static MemoryStream GenerateTurtlePrefixed(int count)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@prefix ex: <http://example.org/> .");
        sb.AppendLine("@prefix foaf: <http://xmlns.com/foaf/0.1/> .");
        sb.AppendLine("@prefix xsd: <http://www.w3.org/2001/XMLSchema#> .");
        sb.AppendLine();

        for (int i = 0; i < count; i++)
        {
            sb.AppendLine($"ex:person{i} foaf:name \"Person {i}\" ; foaf:age \"{20 + (i % 60)}\"^^xsd:integer ; a foaf:Person .");
        }
        return new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    [Benchmark(Baseline = true, Description = "N-Triples 10K")]
    public async Task<long> NTriples()
    {
        _ntriplesData.Position = 0;
        long count = 0;

        var parser = new NTriplesStreamParser(_ntriplesData);
        await parser.ParseAsync((s, p, o) => count++);

        return count;
    }

    [Benchmark(Description = "Turtle full IRIs 10K")]
    public async Task<long> TurtleFullIri()
    {
        _turtleFullIri.Position = 0;
        long count = 0;

        var parser = new TurtleStreamParser(_turtleFullIri);
        await parser.ParseAsync((s, p, o) => count++);

        return count;
    }

    [Benchmark(Description = "Turtle prefixed 10K")]
    public async Task<long> TurtlePrefixed()
    {
        _turtlePrefixed.Position = 0;
        long count = 0;

        var parser = new TurtleStreamParser(_turtlePrefixed);
        await parser.ParseAsync((s, p, o) => count++);

        return count;
    }
}
