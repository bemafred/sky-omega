// Licensed under the MIT License.

using Xunit;
using Xunit.Abstractions;
using SkyOmega.Mercury.Rdf.Turtle;

namespace SkyOmega.Mercury.Tests.W3C;

/// <summary>
/// Diagnostic tests for W3C manifest parsing.
/// </summary>
public class W3CManifestParserDiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public W3CManifestParserDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task DiagnoseNTriplesManifest()
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");

        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.NTriples11);
        _output.WriteLine($"Manifest path: {manifestPath}");
        _output.WriteLine($"Exists: {File.Exists(manifestPath)}");

        if (!File.Exists(manifestPath))
        {
            _output.WriteLine("Manifest file does not exist!");
            return;
        }

        _output.WriteLine("\n--- Parsing triples ---");
        var tripleCount = 0;
        var entriesCount = 0;

        try
        {
            await using var stream = File.OpenRead(manifestPath);
            using var parser = new TurtleStreamParser(stream);

            await parser.ParseAsync((subject, predicate, obj) =>
            {
                tripleCount++;
                var s = subject.ToString();
                var p = predicate.ToString();
                var o = obj.ToString();

                // Show first 20 triples
                if (tripleCount <= 20)
                {
                    _output.WriteLine($"  [{tripleCount}] {s}");
                    _output.WriteLine($"       {p}");
                    _output.WriteLine($"       {o}");
                }

                // Count mf:entries
                if (p.Contains("entries"))
                    entriesCount++;
            });

            _output.WriteLine($"\nTotal triples: {tripleCount}");
            _output.WriteLine($"Entries predicates: {entriesCount}");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"\nParsing error: {ex.GetType().Name}: {ex.Message}");
            _output.WriteLine($"Stack: {ex.StackTrace}");
        }

        // Now try the manifest parser
        _output.WriteLine("\n--- Using W3CManifestParser ---");
        var parser2 = new W3CManifestParser();
        var tests = await parser2.ParseAsync(manifestPath);
        _output.WriteLine($"Tests found: {tests.Count}");

        foreach (var test in tests.Take(5))
        {
            _output.WriteLine($"  - {test.Name} ({test.Type})");
            _output.WriteLine($"    Action: {test.ActionPath}");
        }
    }

    [SkippableFact]
    public async Task DiagnoseTurtleManifest()
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");

        var manifestPath = W3CTestContext.GetManifestPath(W3CTestSuite.Turtle11);
        _output.WriteLine($"Manifest path: {manifestPath}");
        _output.WriteLine($"Exists: {File.Exists(manifestPath)}");

        if (!File.Exists(manifestPath))
        {
            _output.WriteLine("Manifest file does not exist!");
            return;
        }

        _output.WriteLine("\n--- Parsing triples ---");
        var tripleCount = 0;
        var entriesCount = 0;

        try
        {
            await using var stream = File.OpenRead(manifestPath);
            using var parser = new TurtleStreamParser(stream);

            await parser.ParseAsync((subject, predicate, obj) =>
            {
                tripleCount++;
                var s = subject.ToString();
                var p = predicate.ToString();
                var o = obj.ToString();

                // Show first 30 triples
                if (tripleCount <= 30)
                {
                    _output.WriteLine($"  [{tripleCount}] {s}");
                    _output.WriteLine($"       {p}");
                    _output.WriteLine($"       {o}");
                }

                // Count mf:entries
                if (p.Contains("entries"))
                    entriesCount++;
            });

            _output.WriteLine($"\nTotal triples: {tripleCount}");
            _output.WriteLine($"Entries predicates: {entriesCount}");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"\nParsing error: {ex.GetType().Name}: {ex.Message}");
            _output.WriteLine($"Stack: {ex.StackTrace}");
        }

        // Now try the manifest parser
        _output.WriteLine("\n--- Using W3CManifestParser ---");
        var parser2 = new W3CManifestParser();
        var tests = await parser2.ParseAsync(manifestPath);
        _output.WriteLine($"Tests found: {tests.Count}");

        foreach (var test in tests.Take(5))
        {
            _output.WriteLine($"  - {test.Name} ({test.Type})");
            _output.WriteLine($"    Action: {test.ActionPath}");
        }
    }

    [SkippableFact]
    public async Task DiagnoseSparqlAggregatesManifest()
    {
        Skip.IfNot(W3CTestContext.IsAvailable, "W3C test suite not available");

        var manifestPath = Path.Combine(W3CTestContext.TestsRoot, "sparql", "sparql11", "aggregates", "manifest.ttl");
        _output.WriteLine($"Manifest path: {manifestPath}");
        _output.WriteLine($"Exists: {File.Exists(manifestPath)}");

        if (!File.Exists(manifestPath))
        {
            _output.WriteLine("Manifest file does not exist!");
            return;
        }

        _output.WriteLine("\n--- Parsing triples ---");
        var tripleCount = 0;
        var entriesCount = 0;
        var subjects = new Dictionary<string, int>();
        var predicates = new Dictionary<string, int>();
        var queryEvalCount = 0;

        try
        {
            await using var stream = File.OpenRead(manifestPath);
            using var parser = new TurtleStreamParser(stream, 131072);

            await parser.ParseAsync((subject, predicate, obj) =>
            {
                tripleCount++;
                var s = subject.ToString();
                var p = predicate.ToString();
                var o = obj.ToString();

                subjects[s] = subjects.GetValueOrDefault(s, 0) + 1;
                predicates[p] = predicates.GetValueOrDefault(p, 0) + 1;

                // Show first 60 triples
                if (tripleCount <= 60)
                {
                    _output.WriteLine($"  [{tripleCount}] {s}");
                    _output.WriteLine($"       {p}");
                    _output.WriteLine($"       {o}");
                }

                // Count mf:entries
                if (p.Contains("entries"))
                {
                    entriesCount++;
                    _output.WriteLine($"  ENTRIES TRIPLE: {s} -> {o}");
                }

                // Count QueryEvaluationTest
                if (o.Contains("QueryEvaluationTest"))
                    queryEvalCount++;
            });

            _output.WriteLine($"\nTotal triples: {tripleCount}");
            _output.WriteLine($"Entries predicates: {entriesCount}");
            _output.WriteLine($"QueryEvaluationTest types: {queryEvalCount}");
            _output.WriteLine($"\nTop predicates:");
            foreach (var p in predicates.OrderByDescending(x => x.Value).Take(10))
                _output.WriteLine($"  {p.Value}x {p.Key}");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"\nParsing error: {ex.GetType().Name}: {ex.Message}");
            _output.WriteLine($"Stack: {ex.StackTrace}");
        }

        // Now try the manifest parser
        _output.WriteLine("\n--- Using W3CManifestParser ---");
        var parser2 = new W3CManifestParser();
        var tests = await parser2.ParseAsync(manifestPath);
        _output.WriteLine($"Tests found: {tests.Count}");

        foreach (var test in tests.Take(10))
        {
            _output.WriteLine($"  - {test.Name} ({test.Type})");
            _output.WriteLine($"    Action: {test.ActionPath}");
        }
    }
}
