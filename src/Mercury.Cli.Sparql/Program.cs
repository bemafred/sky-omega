using System;
using System.Runtime;
using SkyOmega.Mercury.Sparql;

namespace SkyOmega.Mercury.Cli.Sparql;

internal static class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("SPARQL Zero-GC Streaming Query Engine");
        Console.WriteLine("======================================");
        Console.WriteLine($".NET Version: {Environment.Version}");
        Console.WriteLine($"GC Mode: {(GCSettings.IsServerGC ? "Server" : "Workstation")}");
        Console.WriteLine();

        // Configure for zero-GC operation
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;

        // Run basic SPARQL parser example
        RunParserExample();
    }

    private static void RunParserExample()
    {
        Console.WriteLine("=== SPARQL Parser Example ===");
        Console.WriteLine();

        Console.WriteLine("Example: SPARQL Query Parsing");
        Console.WriteLine("-----------------------------");

        var queries = new[]
        {
            "SELECT * WHERE { ?s ?p ?o }",
            "SELECT DISTINCT ?name WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name }",
            "PREFIX foaf: <http://xmlns.com/foaf/0.1/> SELECT ?x WHERE { ?x foaf:knows ?y }",
            "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }",
            "ASK { ?s ?p ?o }"
        };

        foreach (var queryStr in queries)
        {
            Console.WriteLine($"\nQuery: {queryStr}");
            var parser = new SparqlParser(queryStr.AsSpan());

            try
            {
                var parsed = parser.ParseQuery();
                Console.WriteLine($"  Type: {parsed.Type}");
                if (parsed.Type == QueryType.Select)
                {
                    Console.WriteLine($"  Select All: {parsed.SelectClause.SelectAll}");
                    Console.WriteLine($"  Distinct: {parsed.SelectClause.Distinct}");
                }
                Console.WriteLine("  ✓ Parsed successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ Parse error: {ex.Message}");
            }
        }

        Console.WriteLine();
    }
}
