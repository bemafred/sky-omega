// Program.cs
// Example usage of zero-GC streaming Turtle parser

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SkyOmega.Rdf.Turtle;

internal static class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== Sky Omega Zero-GC Turtle Parser ===\n");

        // Example 1: Parse from string
        await ParseFromStringExample();

        Console.WriteLine();

        // Example 2: Object lists and blank node property lists
        await ParseObjectListsExample();

        Console.WriteLine();

        // Example 3: Parse from file (streaming)
        await ParseFromFileExample();

        Console.WriteLine();

        // Example 4: Performance test
        await PerformanceTest();
    }
    
    static async Task ParseFromStringExample()
    {
        Console.WriteLine("Example 1: Parse Turtle from string\n");
        
        var turtle = """
            @prefix rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> .
            @prefix foaf: <http://xmlns.com/foaf/0.1/> .
            @prefix ex: <http://example.org/> .
            
            ex:alice foaf:name "Alice" ;
                     foaf:knows ex:bob ;
                     foaf:age 30 .
            
            ex:bob foaf:name "Bob" ;
                   foaf:knows ex:alice .
            """;
        
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(turtle));
        using var parser = new TurtleStreamParser(stream);
        
        var count = 0;
        await foreach (var triple in parser.ParseAsync())
        {
            count++;
            Console.WriteLine($"  Triple #{count}: {triple}");
        }
        
        Console.WriteLine($"\nParsed {count} triples");
    }

    static async Task ParseObjectListsExample()
    {
        Console.WriteLine("Example 2: Object lists and blank node property lists\n");

        // Test: Object lists (comma-separated objects) should yield multiple triples
        var turtle = """
            @prefix ex: <http://example.org/> .
            @prefix foaf: <http://xmlns.com/foaf/0.1/> .

            # Object list: should produce 3 triples
            ex:alice foaf:knows ex:bob, ex:charlie, ex:david .

            # Multiple predicate-object pairs with object lists
            ex:bob foaf:name "Bob" ;
                   foaf:knows ex:alice, ex:charlie .
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(turtle));
        using var parser = new TurtleStreamParser(stream);

        var count = 0;
        await foreach (var triple in parser.ParseAsync())
        {
            count++;
            Console.WriteLine($"  Triple #{count}: {triple}");
        }

        // Expected: 6 triples total
        // 1. alice knows bob
        // 2. alice knows charlie
        // 3. alice knows david
        // 4. bob name "Bob"
        // 5. bob knows alice
        // 6. bob knows charlie
        var expected = 6;
        var status = count == expected ? "PASS" : "FAIL";
        Console.WriteLine($"\nParsed {count} triples (expected {expected}): {status}");
    }

    static async Task ParseFromFileExample()
    {
        Console.WriteLine("Example 3: Parse Turtle from file (streaming)\n");
        
        // Create a sample Turtle file
        var filename = "sample.ttl";
        await File.WriteAllTextAsync(filename, """
            VERSION "1.2"
            PREFIX : <http://example.org/>
            PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>
            
            :subject1 :predicate1 :object1 .
            :subject2 :predicate2 "literal value" .
            :subject3 :predicate3 123 .
            :subject4 :predicate4 true .
            :subject5 :predicate5 "hello"@en .
            :subject6 :predicate6 "3.14"^^xsd:decimal .
            """);
        
        try
        {
            using var fileStream = File.OpenRead(filename);
            using var parser = new TurtleStreamParser(fileStream, bufferSize: 4096);
            
            var count = 0;
            await foreach (var triple in parser.ParseAsync())
            {
                count++;
                Console.WriteLine($"  {triple.ToNTriples()}");
            }
            
            Console.WriteLine($"\nParsed {count} triples from file");
        }
        finally
        {
            if (File.Exists(filename))
                File.Delete(filename);
        }
    }
    
    static async Task PerformanceTest()
    {
        Console.WriteLine("Example 4: Performance Test (Zero-GC)\n");
        
        // Generate large Turtle document
        var triplesCount = 10000;
        var turtle = GenerateLargeTurtleDocument(triplesCount);
        
        Console.WriteLine($"Generated {triplesCount} triples ({turtle.Length / 1024}KB)");
        Console.WriteLine("Parsing with zero-GC streaming...\n");
        
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(turtle));
        using var parser = new TurtleStreamParser(stream, bufferSize: 16384);
        
        // Measure GC before
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var memBefore = GC.GetTotalMemory(true);
        
        var sw = Stopwatch.StartNew();
        var parsedCount = 0;
        
        await foreach (var triple in parser.ParseAsync())
        {
            parsedCount++;
            // Process triple (in real usage, would store in Lucy RDF DB)
        }
        
        sw.Stop();
        
        // Measure GC after
        var gen0After = GC.CollectionCount(0);
        var gen1After = GC.CollectionCount(1);
        var gen2After = GC.CollectionCount(2);
        var memAfter = GC.GetTotalMemory(false);
        
        Console.WriteLine($"Parsed:       {parsedCount} triples");
        Console.WriteLine($"Time:         {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"Throughput:   {parsedCount / sw.Elapsed.TotalSeconds:F0} triples/sec");
        Console.WriteLine($"Throughput:   {turtle.Length / 1024.0 / sw.Elapsed.TotalSeconds:F2} KB/sec");
        Console.WriteLine();
        Console.WriteLine("GC Collections:");
        Console.WriteLine($"  Gen 0:      {gen0After - gen0Before}");
        Console.WriteLine($"  Gen 1:      {gen1After - gen1Before}");
        Console.WriteLine($"  Gen 2:      {gen2After - gen2Before}");
        Console.WriteLine($"  Memory Δ:   {(memAfter - memBefore) / 1024.0:F2} KB");
        
        if (gen0After - gen0Before == 0 && gen1After - gen1Before == 0 && gen2After - gen2Before == 0)
        {
            Console.WriteLine("\n✓ Zero GC achieved!");
        }
    }
    
    static string GenerateLargeTurtleDocument(int tripleCount)
    {
        var sb = new System.Text.StringBuilder();
        
        sb.AppendLine("@prefix : <http://example.org/> .");
        sb.AppendLine("@prefix foaf: <http://xmlns.com/foaf/0.1/> .");
        sb.AppendLine();
        
        for (int i = 0; i < tripleCount; i++)
        {
            sb.AppendLine($":subject{i} foaf:name \"Person {i}\" .");
        }
        
        return sb.ToString();
    }
}

// Example Lucy RDF Store integration (stub)
public interface ILucyRdfStore
{
    ValueTask StoreTripleAsync(RdfTriple triple);
    IAsyncEnumerable<RdfTriple> QueryAsync(string sparql);
}

// Example usage with Lucy
public class LucyTurtleImporter
{
    private readonly ILucyRdfStore _store;
    
    public LucyTurtleImporter(ILucyRdfStore store)
    {
        _store = store;
    }
    
    public async Task<long> ImportTurtleFileAsync(
        string filePath, 
        int bufferSize = 32768)
    {
        using var fileStream = File.OpenRead(filePath);
        using var parser = new TurtleStreamParser(fileStream, bufferSize);
        
        var count = 0L;
        
        await foreach (var triple in parser.ParseAsync())
        {
            await _store.StoreTripleAsync(triple);
            count++;
            
            if (count % 10000 == 0)
            {
                Console.WriteLine($"Imported {count} triples...");
            }
        }
        
        return count;
    }
}
