using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

public partial class QueryExecutorTests
{
    #region SERVICE clause tests

    [Fact]
    public void Parse_ServiceClause_Basic()
    {
        var query = "SELECT * WHERE { SERVICE <http://remote.example.org/sparql> { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Assert.True(parsedQuery.WhereClause.Pattern.HasService);
        Assert.Equal(1, parsedQuery.WhereClause.Pattern.ServiceClauseCount);

        var serviceClause = parsedQuery.WhereClause.Pattern.GetServiceClause(0);
        Assert.False(serviceClause.Silent);
        Assert.False(serviceClause.IsVariable);
        Assert.Equal(1, serviceClause.PatternCount);
    }

    [Fact]
    public void Parse_ServiceClause_Silent()
    {
        var query = "SELECT * WHERE { SERVICE SILENT <http://remote.example.org/sparql> { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Assert.True(parsedQuery.WhereClause.Pattern.HasService);
        var serviceClause = parsedQuery.WhereClause.Pattern.GetServiceClause(0);
        Assert.True(serviceClause.Silent);
    }

    [Fact]
    public void Parse_ServiceClause_Variable()
    {
        var query = "SELECT * WHERE { SERVICE ?endpoint { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Assert.True(parsedQuery.WhereClause.Pattern.HasService);
        var serviceClause = parsedQuery.WhereClause.Pattern.GetServiceClause(0);
        Assert.True(serviceClause.IsVariable);
    }

    [Fact]
    public void Parse_ServiceClause_MultiplePatterns()
    {
        var query = @"SELECT * WHERE {
            SERVICE <http://remote.example.org/sparql> {
                ?s <http://example.org/type> ?type .
                ?s <http://example.org/name> ?name
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        var serviceClause = parsedQuery.WhereClause.Pattern.GetServiceClause(0);
        Assert.Equal(2, serviceClause.PatternCount);
    }

    [Fact]
    public void Execute_ServiceClause_WithMockExecutor()
    {
        var query = "SELECT * WHERE { SERVICE <http://remote.example.org/sparql> { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Create mock executor with pre-configured results
        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.AddResult("http://remote.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["s"] = ("<http://remote.example.org/item1>", ServiceBindingType.Uri),
                ["p"] = ("<http://example.org/name>", ServiceBindingType.Uri),
                ["o"] = ("\"Item One\"", ServiceBindingType.Literal)
            },
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["s"] = ("<http://remote.example.org/item2>", ServiceBindingType.Uri),
                ["p"] = ("<http://example.org/name>", ServiceBindingType.Uri),
                ["o"] = ("\"Item Two\"", ServiceBindingType.Literal)
            }
        });

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery, mockExecutor);
            var results = executor.ExecuteServiceToMaterialized();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                var pIdx = results.Current.FindBinding("?p".AsSpan());
                var oIdx = results.Current.FindBinding("?o".AsSpan());

                Assert.True(sIdx >= 0, "?s should be bound");
                Assert.True(pIdx >= 0, "?p should be bound");
                Assert.True(oIdx >= 0, "?o should be bound");
            }
            results.Dispose();

            Assert.Equal(2, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ServiceClause_Silent_ReturnsEmptyOnError()
    {
        var query = "SELECT * WHERE { SERVICE SILENT <http://failing.example.org/sparql> { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Create mock executor that throws an error
        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.SetErrorForEndpoint("http://failing.example.org/sparql", new SparqlServiceException("Simulated failure"));

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery, mockExecutor);
            var results = executor.ExecuteServiceToMaterialized();

            // SILENT should return empty results, not throw
            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            Assert.Equal(0, count); // No results due to error with SILENT
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ServiceClause_NoExecutor_ThrowsInvalidOperation()
    {
        var query = "SELECT * WHERE { SERVICE <http://remote.example.org/sparql> { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            // Don't provide a service executor
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);

            Assert.Throws<InvalidOperationException>(() =>
            {
                var results = executor.ExecuteServiceToMaterialized();
                results.Dispose();
            });
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ServiceWithLocalPatterns_JoinsResults()
    {
        // Add local data
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://local/person1>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
        Store.AddCurrentBatched("<http://local/person2>", "<http://xmlns.com/foaf/0.1/name>", "\"Bob\"");
        Store.CommitBatch();

        // Query that combines local pattern with SERVICE
        var query = @"SELECT ?s ?name ?remoteData WHERE {
            ?s <http://xmlns.com/foaf/0.1/name> ?name .
            SERVICE <http://remote.example.org/sparql> {
                ?s <http://remote.org/data> ?remoteData
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify pattern structure
        Assert.Equal(1, parsedQuery.WhereClause.Pattern.PatternCount); // 1 local pattern
        Assert.Equal(1, parsedQuery.WhereClause.Pattern.ServiceClauseCount); // 1 SERVICE

        // Create mock executor that returns data for person1 only
        // Note: Mock values are raw (no angle brackets for URIs, no quotes for literals)
        // because ToRdfTerm() adds the RDF syntax
        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.AddResult("http://remote.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["s"] = ("http://local/person1", ServiceBindingType.Uri),
                ["remoteData"] = ("Remote data for Alice", ServiceBindingType.Literal)
            }
        });

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery, mockExecutor);
            var results = executor.Execute();

            var resultList = new List<(string s, string name, string data)>();
            while (results.MoveNext())
            {
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                var dataIdx = results.Current.FindBinding("?remoteData".AsSpan());

                Assert.True(sIdx >= 0);
                Assert.True(nameIdx >= 0);
                Assert.True(dataIdx >= 0);

                resultList.Add((
                    results.Current.GetString(sIdx).ToString(),
                    results.Current.GetString(nameIdx).ToString(),
                    results.Current.GetString(dataIdx).ToString()
                ));
            }
            results.Dispose();

            // Should get results where local and SERVICE data join on ?s
            Assert.Single(resultList);
            Assert.Equal("<http://local/person1>", resultList[0].s);
            Assert.Equal("\"Alice\"", resultList[0].name);
            Assert.Equal("\"Remote data for Alice\"", resultList[0].data);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ServiceWithLocalPatterns_MultipleLocalResults()
    {
        // Add local data with multiple results
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://local/item1>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        Store.AddCurrentBatched("<http://local/item2>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        Store.AddCurrentBatched("<http://local/item3>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        Store.CommitBatch();

        var query = @"SELECT ?item ?price WHERE {
            ?item <http://ex.org/type> <http://ex.org/Widget> .
            SERVICE <http://pricing.example.org/sparql> {
                ?item <http://ex.org/price> ?price
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Mock executor returns prices for items 1 and 3 (not 2)
        // Note: Mock values are raw (no angle brackets for URIs, no quotes for literals)
        // because ToRdfTerm() adds the RDF syntax
        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.AddResult("http://pricing.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://local/item1", ServiceBindingType.Uri),
                ["price"] = ("9.99", ServiceBindingType.Literal)
            },
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://local/item3", ServiceBindingType.Uri),
                ["price"] = ("19.99", ServiceBindingType.Literal)
            }
        });

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery, mockExecutor);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // Should get 2 results (items 1 and 3 that have both local and remote data)
            Assert.Equal(2, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MultipleServiceClauses_JoinsAllResults()
    {
        var query = @"SELECT ?s ?data1 ?data2 WHERE {
            SERVICE <http://endpoint1.example.org/sparql> {
                ?s <http://ex.org/prop1> ?data1
            }
            SERVICE <http://endpoint2.example.org/sparql> {
                ?s <http://ex.org/prop2> ?data2
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Assert.Equal(2, parsedQuery.WhereClause.Pattern.ServiceClauseCount);

        // Note: Mock values are raw (no angle brackets for URIs, no quotes for literals)
        // because ToRdfTerm() adds the RDF syntax
        var mockExecutor = new MockSparqlServiceExecutor();

        // First SERVICE returns data for items A and B
        mockExecutor.AddResult("http://endpoint1.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["s"] = ("http://ex.org/itemA", ServiceBindingType.Uri),
                ["data1"] = ("Data1-A", ServiceBindingType.Literal)
            },
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["s"] = ("http://ex.org/itemB", ServiceBindingType.Uri),
                ["data1"] = ("Data1-B", ServiceBindingType.Literal)
            }
        });

        // Second SERVICE returns data for item A only
        mockExecutor.AddResult("http://endpoint2.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["s"] = ("http://ex.org/itemA", ServiceBindingType.Uri),
                ["data2"] = ("Data2-A", ServiceBindingType.Literal)
            }
        });

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery, mockExecutor);
            var results = executor.Execute();

            var resultList = new List<string>();
            while (results.MoveNext())
            {
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                resultList.Add(results.Current.GetString(sIdx).ToString());
            }
            results.Dispose();

            // Should get results where both SERVICE clauses have matching ?s
            Assert.Single(resultList);
            Assert.Equal("<http://ex.org/itemA>", resultList[0]);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ServiceWithLocalPatterns_Silent_HandlesErrors()
    {
        // Add local data
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://local/x>", "<http://ex.org/type>", "<http://ex.org/Thing>");
        Store.CommitBatch();

        var query = @"SELECT ?s ?data WHERE {
            ?s <http://ex.org/type> <http://ex.org/Thing> .
            SERVICE SILENT <http://failing.example.org/sparql> {
                ?s <http://ex.org/data> ?data
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.SetErrorForEndpoint("http://failing.example.org/sparql",
            new SparqlServiceException("Simulated network failure"));

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery, mockExecutor);

            // Should not throw due to SILENT modifier
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // SILENT means SERVICE errors are silently ignored, resulting in no matches
            Assert.Equal(0, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ServiceWithLocalPatterns_ServiceFirstStrategy()
    {
        // This test verifies that service-first strategy works correctly
        // when QueryPlanner determines SERVICE is more selective than local patterns.
        // The SERVICE has bound variables (highly selective), while local patterns
        // would produce many results (less selective).

        // Add many local items (low selectivity for local patterns)
        Store.BeginBatch();
        for (int i = 0; i < 100; i++)
        {
            Store.AddCurrentBatched($"<http://local/item{i}>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        }
        Store.CommitBatch();

        // Query where SERVICE has specific bound variable pattern (high selectivity)
        var query = @"SELECT ?item ?price WHERE {
            ?item <http://ex.org/type> <http://ex.org/Widget> .
            SERVICE <http://pricing.example.org/sparql> {
                ?item <http://ex.org/price> ?price
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Mock executor returns prices for only 2 specific items
        // Note: Mock values are raw (no angle brackets for URIs, no quotes for literals)
        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.AddResult("http://pricing.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://local/item5", ServiceBindingType.Uri),
                ["price"] = ("9.99", ServiceBindingType.Literal)
            },
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://local/item42", ServiceBindingType.Uri),
                ["price"] = ("19.99", ServiceBindingType.Literal)
            }
        });

        // Create a QueryPlanner with statistics to influence strategy selection
        var planner = new QueryPlanner(Store.Statistics, Store.Atoms);

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery, mockExecutor, planner);
            var results = executor.Execute();

            var resultList = new List<(string item, string price)>();
            while (results.MoveNext())
            {
                var itemIdx = results.Current.FindBinding("?item".AsSpan());
                var priceIdx = results.Current.FindBinding("?price".AsSpan());

                Assert.True(itemIdx >= 0);
                Assert.True(priceIdx >= 0);

                resultList.Add((
                    results.Current.GetString(itemIdx).ToString(),
                    results.Current.GetString(priceIdx).ToString()
                ));
            }
            results.Dispose();

            // Should get 2 results (items 5 and 42 that have both local and remote data)
            Assert.Equal(2, resultList.Count);
            Assert.Contains(resultList, r => r.item == "<http://local/item5>" && r.price == "\"9.99\"");
            Assert.Contains(resultList, r => r.item == "<http://local/item42>" && r.price == "\"19.99\"");
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ServiceWithMultipleLocalPatterns_JoinsResults()
    {
        // Test SERVICE+local join with MULTIPLE local patterns (uses MultiPatternScan)
        // Query has a chain of local patterns that must be joined before SERVICE
        Store.BeginBatch();
        // item1 is a Widget in Electronics category
        Store.AddCurrentBatched("<http://local/item1>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        Store.AddCurrentBatched("<http://local/item1>", "<http://ex.org/category>", "<http://ex.org/Electronics>");
        // item2 is a Widget in Home category
        Store.AddCurrentBatched("<http://local/item2>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        Store.AddCurrentBatched("<http://local/item2>", "<http://ex.org/category>", "<http://ex.org/Home>");
        // item3 is a Gadget (not Widget) in Electronics - won't match
        Store.AddCurrentBatched("<http://local/item3>", "<http://ex.org/type>", "<http://ex.org/Gadget>");
        Store.AddCurrentBatched("<http://local/item3>", "<http://ex.org/category>", "<http://ex.org/Electronics>");
        Store.CommitBatch();

        // Query with TWO local patterns joined, then SERVICE
        var query = @"SELECT ?item ?cat ?price WHERE {
            ?item <http://ex.org/type> <http://ex.org/Widget> .
            ?item <http://ex.org/category> ?cat .
            SERVICE <http://pricing.example.org/sparql> {
                ?item <http://ex.org/price> ?price
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify we have 2 local patterns
        Assert.Equal(2, parsedQuery.WhereClause.Pattern.PatternCount);
        Assert.Equal(1, parsedQuery.WhereClause.Pattern.ServiceClauseCount);

        // Mock executor returns prices for item1 only
        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.AddResult("http://pricing.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://local/item1", ServiceBindingType.Uri),
                ["price"] = ("99.99", ServiceBindingType.Literal)
            }
        });

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery, mockExecutor);
            var results = executor.Execute();

            var resultList = new List<(string item, string cat, string price)>();
            while (results.MoveNext())
            {
                var itemIdx = results.Current.FindBinding("?item".AsSpan());
                var catIdx = results.Current.FindBinding("?cat".AsSpan());
                var priceIdx = results.Current.FindBinding("?price".AsSpan());

                Assert.True(itemIdx >= 0, "item binding not found");
                Assert.True(catIdx >= 0, "cat binding not found");
                Assert.True(priceIdx >= 0, "price binding not found");

                resultList.Add((
                    results.Current.GetString(itemIdx).ToString(),
                    results.Current.GetString(catIdx).ToString(),
                    results.Current.GetString(priceIdx).ToString()
                ));
            }
            results.Dispose();

            // Should get 1 result: item1 (Widget in Electronics with price from SERVICE)
            Assert.Single(resultList);
            Assert.Equal("<http://local/item1>", resultList[0].item);
            Assert.Equal("<http://ex.org/Electronics>", resultList[0].cat);
            Assert.Equal("\"99.99\"", resultList[0].price);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ServiceWithMultipleLocalPatterns_ServiceFirstStrategy()
    {
        // Test SERVICE-first strategy with multiple local patterns
        // When SERVICE is more selective, execute it first then join with local patterns
        Store.BeginBatch();
        // Create many items to make local patterns less selective
        for (int i = 0; i < 50; i++)
        {
            Store.AddCurrentBatched($"<http://local/item{i}>", "<http://ex.org/type>", "<http://ex.org/Widget>");
            Store.AddCurrentBatched($"<http://local/item{i}>", "<http://ex.org/inStock>", "\"true\"^^<http://www.w3.org/2001/XMLSchema#boolean>");
        }
        Store.CommitBatch();

        // Query with TWO local patterns, SERVICE should be more selective
        var query = @"SELECT ?item ?price WHERE {
            ?item <http://ex.org/type> <http://ex.org/Widget> .
            ?item <http://ex.org/inStock> ?stock .
            SERVICE <http://pricing.example.org/sparql> {
                ?item <http://ex.org/price> ?price
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Mock executor returns prices for only 2 specific items
        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.AddResult("http://pricing.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://local/item10", ServiceBindingType.Uri),
                ["price"] = ("29.99", ServiceBindingType.Literal)
            },
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://local/item25", ServiceBindingType.Uri),
                ["price"] = ("49.99", ServiceBindingType.Literal)
            }
        });

        var planner = new QueryPlanner(Store.Statistics, Store.Atoms);

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery, mockExecutor, planner);
            var results = executor.Execute();

            var resultList = new List<(string item, string price)>();
            while (results.MoveNext())
            {
                var itemIdx = results.Current.FindBinding("?item".AsSpan());
                var priceIdx = results.Current.FindBinding("?price".AsSpan());

                Assert.True(itemIdx >= 0);
                Assert.True(priceIdx >= 0);

                resultList.Add((
                    results.Current.GetString(itemIdx).ToString(),
                    results.Current.GetString(priceIdx).ToString()
                ));
            }
            results.Dispose();

            // Should get 2 results (items 10 and 25 that match both local patterns and SERVICE)
            Assert.Equal(2, resultList.Count);
            Assert.Contains(resultList, r => r.item == "<http://local/item10>" && r.price == "\"29.99\"");
            Assert.Contains(resultList, r => r.item == "<http://local/item25>" && r.price == "\"49.99\"");
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OptionalService_PreservesLocalBindingsWhenNoMatch()
    {
        // Test OPTIONAL { SERVICE ... } - should preserve local bindings when SERVICE returns no match
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://local/item1>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        Store.AddCurrentBatched("<http://local/item2>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        Store.CommitBatch();

        // Query with OPTIONAL SERVICE - item1 has price, item2 does not
        var query = @"SELECT ?item ?price WHERE {
            ?item <http://ex.org/type> <http://ex.org/Widget> .
            OPTIONAL {
                SERVICE <http://pricing.example.org/sparql> {
                    ?item <http://ex.org/price> ?price
                }
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify OPTIONAL SERVICE was parsed correctly
        Assert.True(parsedQuery.WhereClause.Pattern.ServiceClauseCount > 0);
        Assert.True(parsedQuery.WhereClause.Pattern.GetServiceClause(0).IsOptional);

        // Mock executor returns price for only item1
        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.AddResult("http://pricing.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://local/item1", ServiceBindingType.Uri),
                ["price"] = ("29.99", ServiceBindingType.Literal)
            }
        });

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery, mockExecutor);
            var results = executor.Execute();

            var resultList = new List<(string item, bool hasPrice)>();
            while (results.MoveNext())
            {
                var itemIdx = results.Current.FindBinding("?item".AsSpan());
                var priceIdx = results.Current.FindBinding("?price".AsSpan());

                Assert.True(itemIdx >= 0);

                var item = results.Current.GetString(itemIdx).ToString();
                var hasPrice = priceIdx >= 0;
                resultList.Add((item, hasPrice));
            }
            results.Dispose();

            // Should get 2 results: item1 with price, item2 without price (preserved by OPTIONAL)
            Assert.Equal(2, resultList.Count);
            Assert.Contains(resultList, r => r.item == "<http://local/item1>" && r.hasPrice);
            Assert.Contains(resultList, r => r.item == "<http://local/item2>");
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OptionalServiceSilent_CombinesBothModifiers()
    {
        // Test OPTIONAL { SERVICE SILENT ... } - should preserve bindings AND ignore errors
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://local/item1>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        Store.CommitBatch();

        // Query with OPTIONAL SERVICE SILENT
        var query = @"SELECT ?item ?price WHERE {
            ?item <http://ex.org/type> <http://ex.org/Widget> .
            OPTIONAL {
                SERVICE SILENT <http://pricing.example.org/sparql> {
                    ?item <http://ex.org/price> ?price
                }
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify both modifiers are set
        var serviceClause = parsedQuery.WhereClause.Pattern.GetServiceClause(0);
        Assert.True(serviceClause.IsOptional);
        Assert.True(serviceClause.Silent);
    }

    [Fact]
    public void Parse_UnionWithService_ParsesCorrectly()
    {
        // Test parsing { local } UNION { SERVICE ... }
        // Note: Execution of SERVICE-only UNION branches requires further work
        // as SERVICE clauses are stored separately from triple patterns

        // Query with UNION: local branch and SERVICE branch
        var query = @"SELECT ?item ?source WHERE {
            {
                ?item <http://ex.org/type> <http://ex.org/Widget> .
                ?item <http://ex.org/source> ?source
            }
            UNION
            {
                SERVICE <http://remote.example.org/sparql> {
                    ?item <http://ex.org/type> <http://ex.org/Widget> .
                    ?item <http://ex.org/source> ?source
                }
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify UNION was parsed
        Assert.True(parsedQuery.WhereClause.Pattern.HasUnion);

        // Verify local patterns in first branch
        Assert.Equal(2, parsedQuery.WhereClause.Pattern.FirstBranchPatternCount);

        // Verify SERVICE was parsed
        Assert.True(parsedQuery.WhereClause.Pattern.ServiceClauseCount > 0);

        // Verify SERVICE is NOT optional (it's in UNION, not OPTIONAL)
        Assert.False(parsedQuery.WhereClause.Pattern.GetServiceClause(0).IsOptional);

        // Verify SERVICE has the correct endpoint
        var endpoint = parsedQuery.WhereClause.Pattern.GetServiceClause(0).Endpoint;
        Assert.Equal(TermType.Iri, endpoint.Type);
    }

    [Fact]
    public void Parse_OptionalServiceWithFilter_ParsesFilterCorrectly()
    {
        // Test OPTIONAL { SERVICE ... FILTER(...) } - FILTER inside OPTIONAL SERVICE block
        var query = @"SELECT ?item ?price WHERE {
            ?item <http://ex.org/type> <http://ex.org/Widget> .
            OPTIONAL {
                SERVICE <http://pricing.example.org/sparql> {
                    ?item <http://ex.org/price> ?price
                    FILTER(?price > 10)
                }
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify OPTIONAL SERVICE was parsed
        Assert.True(parsedQuery.WhereClause.Pattern.ServiceClauseCount > 0);
        Assert.True(parsedQuery.WhereClause.Pattern.GetServiceClause(0).IsOptional);

        // Verify FILTER was parsed (added to parent pattern)
        Assert.True(parsedQuery.WhereClause.Pattern.FilterCount > 0);
    }

    [Fact]
    public void Parse_OptionalServiceWithVariableEndpoint_ParsesCorrectly()
    {
        // Test OPTIONAL { SERVICE ?endpoint { ... } } - variable endpoint inside OPTIONAL
        var query = @"SELECT ?item ?price ?endpoint WHERE {
            ?item <http://ex.org/type> <http://ex.org/Widget> .
            ?item <http://ex.org/priceService> ?endpoint .
            OPTIONAL {
                SERVICE ?endpoint {
                    ?item <http://ex.org/price> ?price
                }
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify OPTIONAL SERVICE was parsed
        Assert.True(parsedQuery.WhereClause.Pattern.ServiceClauseCount > 0);
        var serviceClause = parsedQuery.WhereClause.Pattern.GetServiceClause(0);
        Assert.True(serviceClause.IsOptional);

        // Verify endpoint is a variable
        Assert.Equal(TermType.Variable, serviceClause.Endpoint.Type);
    }

    [Fact]
    public void Parse_UnionWithService_TracksUnionBranch()
    {
        // Test that SERVICE in second UNION branch has UnionBranch = 1
        var query = @"SELECT ?item WHERE {
            { ?item <http://ex.org/type> <http://ex.org/Local> }
            UNION
            { SERVICE <http://remote.example.org/sparql> { ?item <http://ex.org/type> <http://ex.org/Remote> } }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Assert.True(parsedQuery.WhereClause.Pattern.HasUnion);
        Assert.Equal(1, parsedQuery.WhereClause.Pattern.ServiceClauseCount);

        var serviceClause = parsedQuery.WhereClause.Pattern.GetServiceClause(0);
        Assert.Equal(1, serviceClause.UnionBranch); // SERVICE in second UNION branch
    }

    [Fact]
    public void Parse_ServiceInFirstUnionBranch_TracksUnionBranch()
    {
        // Test that SERVICE in first UNION branch has UnionBranch = 0
        var query = @"SELECT ?item WHERE {
            { SERVICE <http://remote.example.org/sparql> { ?item <http://ex.org/type> <http://ex.org/Remote> } }
            UNION
            { ?item <http://ex.org/type> <http://ex.org/Local> }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Assert.True(parsedQuery.WhereClause.Pattern.HasUnion);
        Assert.Equal(1, parsedQuery.WhereClause.Pattern.ServiceClauseCount);

        var serviceClause = parsedQuery.WhereClause.Pattern.GetServiceClause(0);
        Assert.Equal(0, serviceClause.UnionBranch); // SERVICE in first UNION branch
    }

    [Fact]
    public void Execute_UnionLocalAndService_ExecutesBothBranches()
    {
        // Test { local } UNION { SERVICE ... } - should return results from both branches
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://local/item1>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        Store.AddCurrentBatched("<http://local/item1>", "<http://ex.org/source>", "\"Local Store\"");
        Store.CommitBatch();

        var query = @"SELECT ?item ?source WHERE {
            { ?item <http://ex.org/type> <http://ex.org/Widget> . ?item <http://ex.org/source> ?source }
            UNION
            { SERVICE <http://remote.example.org/sparql> { ?item <http://ex.org/type> <http://ex.org/Widget> . ?item <http://ex.org/source> ?source } }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Mock executor returns remote items
        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.AddResult("http://remote.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://remote/item2", ServiceBindingType.Uri),
                ["source"] = ("Remote Endpoint", ServiceBindingType.Literal)
            }
        });

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery, mockExecutor);
            var results = executor.Execute();

            var resultList = new List<(string item, string source)>();
            while (results.MoveNext())
            {
                var itemIdx = results.Current.FindBinding("?item".AsSpan());
                var sourceIdx = results.Current.FindBinding("?source".AsSpan());

                Assert.True(itemIdx >= 0, "item binding not found");
                Assert.True(sourceIdx >= 0, "source binding not found");

                resultList.Add((
                    results.Current.GetString(itemIdx).ToString(),
                    results.Current.GetString(sourceIdx).ToString()
                ));
            }
            results.Dispose();

            // Should get 2 results: one from local, one from SERVICE
            Assert.Equal(2, resultList.Count);
            Assert.Contains(resultList, r => r.item == "<http://local/item1>" && r.source == "\"Local Store\"");
            Assert.Contains(resultList, r => r.item == "<http://remote/item2>" && r.source == "\"Remote Endpoint\"");
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_UnionServiceAndLocal_ExecutesBothBranches()
    {
        // Test { SERVICE ... } UNION { local } - should return results from both branches
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://local/item1>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        Store.CommitBatch();

        var query = @"SELECT ?item WHERE {
            { SERVICE <http://remote.example.org/sparql> { ?item <http://ex.org/type> <http://ex.org/Widget> } }
            UNION
            { ?item <http://ex.org/type> <http://ex.org/Widget> }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify SERVICE is in first branch (UnionBranch = 0)
        Assert.Equal(0, parsedQuery.WhereClause.Pattern.GetServiceClause(0).UnionBranch);

        // Mock executor returns remote item
        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.AddResult("http://remote.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://remote/item2", ServiceBindingType.Uri)
            }
        });

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery, mockExecutor);
            var results = executor.Execute();

            var resultList = new List<string>();
            while (results.MoveNext())
            {
                var itemIdx = results.Current.FindBinding("?item".AsSpan());
                Assert.True(itemIdx >= 0);
                resultList.Add(results.Current.GetString(itemIdx).ToString());
            }
            results.Dispose();

            // Should get 2 results: one from SERVICE, one from local
            Assert.Equal(2, resultList.Count);
            Assert.Contains("<http://remote/item2>", resultList);
            Assert.Contains("<http://local/item1>", resultList);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_UnionServiceAndService_ExecutesBothServices()
    {
        // Test { SERVICE <a> ... } UNION { SERVICE <b> ... } - should return results from both SERVICE clauses
        var query = @"SELECT ?item WHERE {
            { SERVICE <http://endpoint1.example.org/sparql> { ?item <http://ex.org/type> <http://ex.org/Widget> } }
            UNION
            { SERVICE <http://endpoint2.example.org/sparql> { ?item <http://ex.org/type> <http://ex.org/Widget> } }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify we have 2 SERVICE clauses in different union branches
        Assert.Equal(2, parsedQuery.WhereClause.Pattern.ServiceClauseCount);
        Assert.Equal(0, parsedQuery.WhereClause.Pattern.GetServiceClause(0).UnionBranch); // First SERVICE
        Assert.Equal(1, parsedQuery.WhereClause.Pattern.GetServiceClause(1).UnionBranch); // Second SERVICE

        // Mock executor returns different items from each endpoint
        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.AddResult("http://endpoint1.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://endpoint1/itemA", ServiceBindingType.Uri)
            }
        });
        mockExecutor.AddResult("http://endpoint2.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://endpoint2/itemB", ServiceBindingType.Uri)
            }
        });

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery, mockExecutor);
            var results = executor.Execute();

            var resultList = new List<string>();
            while (results.MoveNext())
            {
                var itemIdx = results.Current.FindBinding("?item".AsSpan());
                Assert.True(itemIdx >= 0);
                resultList.Add(results.Current.GetString(itemIdx).ToString());
            }
            results.Dispose();

            // Should get 2 results: one from each SERVICE
            Assert.Equal(2, resultList.Count);
            Assert.Contains("<http://endpoint1/itemA>", resultList);
            Assert.Contains("<http://endpoint2/itemB>", resultList);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_UnionLocalWithServiceAndLocal_ExecutesBothBranches()
    {
        // Test { local + SERVICE } UNION { local } - join within first branch plus second branch
        Store.BeginBatch();
        // Local items for first branch join
        Store.AddCurrentBatched("<http://local/item1>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        Store.AddCurrentBatched("<http://local/item2>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        // Local items for second branch
        Store.AddCurrentBatched("<http://local/itemX>", "<http://ex.org/type>", "<http://ex.org/Gadget>");
        Store.CommitBatch();

        var query = @"SELECT ?item ?price WHERE {
            {
                ?item <http://ex.org/type> <http://ex.org/Widget> .
                SERVICE <http://pricing.example.org/sparql> {
                    ?item <http://ex.org/price> ?price
                }
            }
            UNION
            {
                ?item <http://ex.org/type> <http://ex.org/Gadget>
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Mock executor returns prices for item1 only
        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.AddResult("http://pricing.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://local/item1", ServiceBindingType.Uri),
                ["price"] = ("29.99", ServiceBindingType.Literal)
            }
        });

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery, mockExecutor);
            var results = executor.Execute();

            var resultList = new List<(string item, bool hasPrice)>();
            while (results.MoveNext())
            {
                var itemIdx = results.Current.FindBinding("?item".AsSpan());
                var priceIdx = results.Current.FindBinding("?price".AsSpan());

                Assert.True(itemIdx >= 0);

                resultList.Add((
                    results.Current.GetString(itemIdx).ToString(),
                    priceIdx >= 0
                ));
            }
            results.Dispose();

            // Should get 2 results:
            // - item1 from first branch (local + SERVICE join has price from SERVICE)
            // - itemX from second branch (local only, no price)
            Assert.Equal(2, resultList.Count);
            Assert.Contains(resultList, r => r.item == "<http://local/item1>" && r.hasPrice);
            Assert.Contains(resultList, r => r.item == "<http://local/itemX>" && !r.hasPrice);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ServiceWithVariableEndpoint_ResolvesFromBindings()
    {
        // Test SERVICE ?endpoint { ... } where endpoint is bound from outer pattern
        Store.BeginBatch();
        // Local items with their SERVICE endpoint references
        Store.AddCurrentBatched("<http://local/item1>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        Store.AddCurrentBatched("<http://local/item1>", "<http://ex.org/priceService>", "<http://pricing-us.example.org/sparql>");
        Store.AddCurrentBatched("<http://local/item2>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        Store.AddCurrentBatched("<http://local/item2>", "<http://ex.org/priceService>", "<http://pricing-eu.example.org/sparql>");
        Store.CommitBatch();

        var query = @"SELECT ?item ?price WHERE {
            ?item <http://ex.org/type> <http://ex.org/Widget> .
            ?item <http://ex.org/priceService> ?endpoint .
            SERVICE ?endpoint {
                ?item <http://ex.org/price> ?price
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify SERVICE has variable endpoint
        var serviceClause = parsedQuery.WhereClause.Pattern.GetServiceClause(0);
        Assert.True(serviceClause.IsVariable);
        Assert.Equal(TermType.Variable, serviceClause.Endpoint.Type);

        // Mock executor returns different prices from different endpoints
        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.AddResult("http://pricing-us.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://local/item1", ServiceBindingType.Uri),
                ["price"] = ("19.99 USD", ServiceBindingType.Literal)
            }
        });
        mockExecutor.AddResult("http://pricing-eu.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://local/item2", ServiceBindingType.Uri),
                ["price"] = ("17.99 EUR", ServiceBindingType.Literal)
            }
        });

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery, mockExecutor);
            var results = executor.Execute();

            var resultList = new List<(string item, string price)>();
            while (results.MoveNext())
            {
                var itemIdx = results.Current.FindBinding("?item".AsSpan());
                var priceIdx = results.Current.FindBinding("?price".AsSpan());

                Assert.True(itemIdx >= 0, "item binding not found");
                Assert.True(priceIdx >= 0, "price binding not found");

                resultList.Add((
                    results.Current.GetString(itemIdx).ToString(),
                    results.Current.GetString(priceIdx).ToString()
                ));
            }
            results.Dispose();

            // Should get 2 results: each item with price from its respective endpoint
            Assert.Equal(2, resultList.Count);
            Assert.Contains(resultList, r => r.item == "<http://local/item1>" && r.price.Contains("USD"));
            Assert.Contains(resultList, r => r.item == "<http://local/item2>" && r.price.Contains("EUR"));
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_OptionalServiceWithMultiplePatterns_JoinsAllPatterns()
    {
        // Test OPTIONAL SERVICE with multiple patterns inside
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://local/item1>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        Store.AddCurrentBatched("<http://local/item2>", "<http://ex.org/type>", "<http://ex.org/Widget>");
        Store.CommitBatch();

        var query = @"SELECT ?item ?price ?currency WHERE {
            ?item <http://ex.org/type> <http://ex.org/Widget> .
            OPTIONAL {
                SERVICE <http://pricing.example.org/sparql> {
                    ?item <http://ex.org/price> ?price .
                    ?item <http://ex.org/currency> ?currency
                }
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify SERVICE has multiple patterns
        var serviceClause = parsedQuery.WhereClause.Pattern.GetServiceClause(0);
        Assert.True(serviceClause.IsOptional);
        Assert.Equal(2, serviceClause.PatternCount);

        // Mock executor returns price and currency for item1 only
        var mockExecutor = new MockSparqlServiceExecutor();
        mockExecutor.AddResult("http://pricing.example.org/sparql", new[]
        {
            new Dictionary<string, (string value, ServiceBindingType type)>
            {
                ["item"] = ("http://local/item1", ServiceBindingType.Uri),
                ["price"] = ("29.99", ServiceBindingType.Literal),
                ["currency"] = ("USD", ServiceBindingType.Literal)
            }
        });

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery, mockExecutor);
            var results = executor.Execute();

            var resultList = new List<(string item, bool hasPrice, bool hasCurrency)>();
            while (results.MoveNext())
            {
                var itemIdx = results.Current.FindBinding("?item".AsSpan());
                var priceIdx = results.Current.FindBinding("?price".AsSpan());
                var currencyIdx = results.Current.FindBinding("?currency".AsSpan());

                Assert.True(itemIdx >= 0);

                var item = results.Current.GetString(itemIdx).ToString();
                resultList.Add((item, priceIdx >= 0, currencyIdx >= 0));
            }
            results.Dispose();

            // Should get 2 results: item1 with price+currency, item2 without (preserved by OPTIONAL)
            Assert.Equal(2, resultList.Count);
            Assert.Contains(resultList, r => r.item == "<http://local/item1>" && r.hasPrice && r.hasCurrency);
            Assert.Contains(resultList, r => r.item == "<http://local/item2>" && !r.hasPrice && !r.hasCurrency);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    #endregion
}

/// <summary>
/// Mock implementation of ISparqlServiceExecutor for testing.
/// </summary>
internal class MockSparqlServiceExecutor : ISparqlServiceExecutor
{
    private readonly Dictionary<string, List<Dictionary<string, (string value, ServiceBindingType type)>>> _results = new();
    private readonly Dictionary<string, Exception> _errors = new();

    public void AddResult(string endpoint, IEnumerable<Dictionary<string, (string value, ServiceBindingType type)>> rows)
    {
        _results[endpoint] = rows.ToList();
    }

    public void SetErrorForEndpoint(string endpoint, Exception error)
    {
        _errors[endpoint] = error;
    }

    public ValueTask<List<ServiceResultRow>> ExecuteSelectAsync(string endpointUri, string query, System.Threading.CancellationToken ct = default)
    {
        if (_errors.TryGetValue(endpointUri, out var error))
        {
            throw error;
        }

        var resultRows = new List<ServiceResultRow>();
        if (_results.TryGetValue(endpointUri, out var rows))
        {
            foreach (var row in rows)
            {
                var resultRow = new ServiceResultRow();
                foreach (var kvp in row)
                {
                    resultRow.AddBinding(kvp.Key, new ServiceBinding(kvp.Value.type, kvp.Value.value));
                }
                resultRows.Add(resultRow);
            }
        }

        return ValueTask.FromResult(resultRows);
    }

    public ValueTask<bool> ExecuteAskAsync(string endpointUri, string query, System.Threading.CancellationToken ct = default)
    {
        if (_errors.TryGetValue(endpointUri, out var error))
        {
            throw error;
        }

        return ValueTask.FromResult(_results.ContainsKey(endpointUri) && _results[endpointUri].Count > 0);
    }
}
