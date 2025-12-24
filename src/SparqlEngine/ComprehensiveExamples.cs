using System;
using SparqlEngine;

namespace SparqlEngine.Examples;

/// <summary>
/// Comprehensive examples demonstrating all SPARQL 1.1 features
/// </summary>
public static class ComprehensiveExamples
{
    public static void RunAll()
    {
        Console.WriteLine("=== SPARQL 1.1 Comprehensive Examples ===");
        Console.WriteLine();

        Example_ConstructQuery();
        Example_DescribeQuery();
        Example_AskQuery();
        Example_OptionalPattern();
        Example_UnionPattern();
        Example_OrderByLimitOffset();
        Example_PropertyPaths();
        Example_Aggregates();
    }

    private static void Example_ConstructQuery()
    {
        Console.WriteLine("Example: CONSTRUCT Query");
        Console.WriteLine("------------------------");

        using var store = new StreamingTripleStore();
        
        // Add some data
        store.Add("<http://ex.org/person/1>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
        store.Add("<http://ex.org/person/1>", "<http://xmlns.com/foaf/0.1/mbox>", "\"alice@example.org\"");
        store.Add("<http://ex.org/person/2>", "<http://xmlns.com/foaf/0.1/name>", "\"Bob\"");

        // Parse CONSTRUCT query
        var queryStr = @"
            CONSTRUCT { ?person <http://ex.org/hasName> ?name }
            WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name }
        ";
        
        var parser = new SparqlParser(queryStr.AsSpan());
        var query = parser.ParseQuery();
        
        Console.WriteLine($"Query Type: {query.Type}");
        Console.WriteLine("✓ CONSTRUCT query parsed successfully");
        
        // Execute
        var executor = new ConstructQueryExecutor(store, query.ConstructTemplate, query.WhereClause);
        var results = executor.Execute();
        
        Console.WriteLine("\nConstructed triples:");
        int count = 0;
        while (results.MoveNext())
        {
            var triple = results.Current;
            Console.WriteLine($"  {triple.Subject} {triple.Predicate} {triple.Object}");
            count++;
        }
        Console.WriteLine($"✓ Constructed {count} triples\n");
    }

    private static void Example_DescribeQuery()
    {
        Console.WriteLine("Example: DESCRIBE Query");
        Console.WriteLine("-----------------------");

        using var store = new StreamingTripleStore();
        
        // Add data about a resource
        store.Add("<http://ex.org/alice>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
        store.Add("<http://ex.org/alice>", "<http://xmlns.com/foaf/0.1/age>", "\"30\"");
        store.Add("<http://ex.org/alice>", "<http://xmlns.com/foaf/0.1/knows>", "<http://ex.org/bob>");
        store.Add("<http://ex.org/bob>", "<http://xmlns.com/foaf/0.1/name>", "\"Bob\"");

        // Parse DESCRIBE query
        var queryStr = "DESCRIBE <http://ex.org/alice>";
        var parser = new SparqlParser(queryStr.AsSpan());
        var query = parser.ParseQuery();
        
        Console.WriteLine($"Query Type: {query.Type}");
        
        // Execute
        Span<ResourceDescriptor> resources = stackalloc ResourceDescriptor[1];
        resources[0] = new ResourceDescriptor
        {
            IsVariable = false,
            ResourceUri = "<http://ex.org/alice>"
        };
        
        var executor = new DescribeQueryExecutor(store, resources);
        var results = executor.Execute();
        
        Console.WriteLine("\nDescription of resource:");
        int count = 0;
        while (results.MoveNext())
        {
            var triple = results.Current;
            Console.WriteLine($"  {triple.Subject} {triple.Predicate} {triple.Object}");
            count++;
        }
        Console.WriteLine($"✓ Retrieved {count} triples\n");
    }

    private static void Example_AskQuery()
    {
        Console.WriteLine("Example: ASK Query");
        Console.WriteLine("------------------");

        using var store = new StreamingTripleStore();
        
        store.Add("<http://ex.org/alice>", "<http://xmlns.com/foaf/0.1/knows>", "<http://ex.org/bob>");

        // Parse ASK query
        var queryStr = "ASK WHERE { ?x <http://xmlns.com/foaf/0.1/knows> ?y }";
        var parser = new SparqlParser(queryStr.AsSpan());
        var query = parser.ParseQuery();
        
        Console.WriteLine($"Query Type: {query.Type}");
        
        // Execute
        var executor = new QueryExecutor(store);
        var results = executor.Execute(query);
        
        if (results.MoveNext())
        {
            var solution = results.Current;
            if (solution.IsAskResult)
            {
                Console.WriteLine($"Result: {solution.AskResult}");
                Console.WriteLine($"✓ ASK query returned {solution.AskResult}\n");
            }
        }
    }

    private static void Example_OptionalPattern()
    {
        Console.WriteLine("Example: OPTIONAL Pattern");
        Console.WriteLine("-------------------------");

        using var store = new StreamingTripleStore();
        
        store.Add("<http://ex.org/alice>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
        store.Add("<http://ex.org/alice>", "<http://xmlns.com/foaf/0.1/mbox>", "\"alice@example.org\"");
        store.Add("<http://ex.org/bob>", "<http://xmlns.com/foaf/0.1/name>", "\"Bob\"");
        // Bob has no email

        // Query with OPTIONAL
        var required = store.Query(
            ReadOnlySpan<char>.Empty,
            "<http://xmlns.com/foaf/0.1/name>",
            ReadOnlySpan<char>.Empty
        );
        
        var optionalPattern = new TriplePattern
        {
            Subject = new TermPattern { IsVariable = true },
            Predicate = new TermPattern { IsVariable = false },
            Object = new TermPattern { IsVariable = true }
        };
        
        var matcher = new OptionalMatcher(store, required, optionalPattern);
        
        Console.WriteLine("Results with optional email:");
        int count = 0;
        while (matcher.MoveNext())
        {
            var result = matcher.Current;
            Console.WriteLine($"  Name: {result.Required.Object}");
            if (result.HasOptional)
            {
                Console.WriteLine($"    Email: {result.Optional.Object}");
            }
            else
            {
                Console.WriteLine("    Email: (not provided)");
            }
            count++;
        }
        Console.WriteLine($"✓ Processed {count} results with OPTIONAL\n");
    }

    private static void Example_UnionPattern()
    {
        Console.WriteLine("Example: UNION Pattern");
        Console.WriteLine("----------------------");

        using var store = new StreamingTripleStore();
        
        store.Add("<http://ex.org/alice>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
        store.Add("<http://ex.org/bob>", "<http://xmlns.com/foaf/0.1/nick>", "\"Bobby\"");
        store.Add("<http://ex.org/charlie>", "<http://xmlns.com/foaf/0.1/name>", "\"Charlie\"");

        // UNION of name and nick
        var leftEnum = store.Query(
            ReadOnlySpan<char>.Empty,
            "<http://xmlns.com/foaf/0.1/name>",
            ReadOnlySpan<char>.Empty
        );
        
        var rightEnum = store.Query(
            ReadOnlySpan<char>.Empty,
            "<http://xmlns.com/foaf/0.1/nick>",
            ReadOnlySpan<char>.Empty
        );
        
        var union = new UnionMatcher(leftEnum, rightEnum);
        
        Console.WriteLine("Results from UNION:");
        int count = 0;
        while (union.MoveNext())
        {
            var triple = union.Current;
            Console.WriteLine($"  {triple.Subject} -> {triple.Object}");
            count++;
        }
        Console.WriteLine($"✓ Retrieved {count} results from UNION\n");
    }

    private static void Example_OrderByLimitOffset()
    {
        Console.WriteLine("Example: ORDER BY, LIMIT, OFFSET");
        Console.WriteLine("---------------------------------");

        using var store = new StreamingTripleStore();
        var executor = new QueryExecutor(store);
        
        // Add numbered data
        for (int i = 1; i <= 20; i++)
        {
            store.Add(
                $"<http://ex.org/item{i}>",
                "<http://ex.org/value>",
                $"\"{i}\""
            );
        }

        // Parse query with modifiers
        var queryStr = "SELECT * WHERE { ?item ?p ?value } ORDER BY DESC(?value) LIMIT 5 OFFSET 3";
        var parser = new SparqlParser(queryStr.AsSpan());
        var query = parser.ParseQuery();
        
        Console.WriteLine($"Query: {queryStr}");
        Console.WriteLine($"ORDER BY conditions: {query.SolutionModifier.OrderBy.Count}");
        Console.WriteLine($"LIMIT: {query.SolutionModifier.Limit}");
        Console.WriteLine($"OFFSET: {query.SolutionModifier.Offset}");
        
        // Execute
        var results = executor.Execute(query);
        var modifierExecutor = new SolutionModifierExecutor(query.SolutionModifier);
        var modifiedResults = modifierExecutor.Apply(results);
        
        Console.WriteLine("\nResults (with ORDER BY, LIMIT, OFFSET):");
        int count = 0;
        while (modifiedResults.MoveNext())
        {
            var solution = modifiedResults.Current;
            if (!solution.IsAskResult)
            {
                Console.WriteLine($"  {count + 1}. {solution.Triple.Subject} = {solution.Triple.Object}");
                count++;
            }
        }
        Console.WriteLine($"✓ Retrieved {count} results after modifiers\n");
    }

    private static void Example_PropertyPaths()
    {
        Console.WriteLine("Example: Property Paths");
        Console.WriteLine("-----------------------");

        using var store = new StreamingTripleStore();
        
        // Create a chain of relationships
        store.Add("<http://ex.org/alice>", "<http://xmlns.com/foaf/0.1/knows>", "<http://ex.org/bob>");
        store.Add("<http://ex.org/bob>", "<http://xmlns.com/foaf/0.1/knows>", "<http://ex.org/charlie>");
        store.Add("<http://ex.org/charlie>", "<http://xmlns.com/foaf/0.1/knows>", "<http://ex.org/diana>");

        // Build property path: knows+
        var pathBuilder = PropertyPathBuilder.Create();
        var path = pathBuilder
            .Predicate("<http://xmlns.com/foaf/0.1/knows>")
            .OneOrMore()
            .Build();
        
        Console.WriteLine("Property Path: <http://xmlns.com/foaf/0.1/knows>+");
        
        // Evaluate path
        var evaluator = new PropertyPathEvaluator(
            store,
            path,
            "<http://ex.org/alice>",
            ReadOnlySpan<char>.Empty
        );
        
        var results = evaluator.Evaluate();
        
        Console.WriteLine("\nTransitive knows relationships from Alice:");
        int count = 0;
        while (results.MoveNext())
        {
            var result = results.Current;
            Console.WriteLine($"  {result.StartNode} -> {result.EndNode} (path length: {result.PathLength})");
            count++;
        }
        Console.WriteLine($"✓ Found {count} relationships via property path\n");
    }

    private static void Example_Aggregates()
    {
        Console.WriteLine("Example: Aggregate Functions");
        Console.WriteLine("----------------------------");

        using var store = new StreamingTripleStore();
        var executor = new QueryExecutor(store);
        
        // Add data for aggregation
        for (int i = 1; i <= 10; i++)
        {
            store.Add(
                $"<http://ex.org/person{i}>",
                "<http://ex.org/age>",
                $"\"{20 + i}\""
            );
        }

        // Execute base query
        var query = new Query
        {
            Type = QueryType.Select,
            SelectClause = new SelectClause { SelectAll = true }
        };
        
        var results = executor.Execute(query);
        
        // Apply COUNT aggregate
        var aggExecutor = new AggregateExecutor(results, AggregateFunction.Count);
        
        Console.WriteLine("Aggregate Function: COUNT");
        
        if (aggExecutor.MoveNext())
        {
            var result = aggExecutor.Current;
            Console.WriteLine($"  Total count: {result.IntegerValue}");
            Console.WriteLine($"✓ Aggregate computed successfully\n");
        }
    }
}
