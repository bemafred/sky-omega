using System.Collections.Generic;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

public partial class QueryExecutorTests
{
    #region GRAPH Clause Execution

    [Fact]
    public void Execute_GraphClause_QueriesNamedGraph()
    {
        // Add data to a named graph
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/David>", "<http://xmlns.com/foaf/0.1/name>", "\"David\"", "<http://example.org/graph1>");
        Store.AddCurrentBatched("<http://example.org/David>", "<http://xmlns.com/foaf/0.1/age>", "\"40\"^^<http://www.w3.org/2001/XMLSchema#integer>", "<http://example.org/graph1>");
        Store.CommitBatch();

        var query = "SELECT * WHERE { GRAPH <http://example.org/graph1> { ?s ?p ?o } }";

        // Use buffer-based constructor to avoid storing large Query struct
        var buffer = ParseToBuffer(query);

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), buffer);
            // Use ExecuteGraphToMaterialized() to avoid stack overflow from large QueryResults struct
            var results = executor.ExecuteGraphToMaterialized();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // David has 2 triples in graph1
            Assert.Equal(2, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GraphClause_DoesNotQueryDefaultGraph()
    {
        // Add data to a named graph
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/Eve>", "<http://xmlns.com/foaf/0.1/name>", "\"Eve\"", "<http://example.org/graph2>");
        Store.CommitBatch();

        // Query default graph - should NOT find Eve
        var query = "SELECT * WHERE { <http://example.org/Eve> ?p ?o }";
        var buffer = ParseToBuffer(query);

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), buffer);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // Eve is in graph2, not default graph
            Assert.Equal(0, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GraphClause_BindsVariables()
    {
        // Add data to a named graph
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/Frank>", "<http://xmlns.com/foaf/0.1/name>", "\"Frank\"", "<http://example.org/graph3>");
        Store.CommitBatch();

        var query = "SELECT ?name WHERE { GRAPH <http://example.org/graph3> { <http://example.org/Frank> <http://xmlns.com/foaf/0.1/name> ?name } }";
        var buffer = ParseToBuffer(query);

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            var names = new List<string>();
            while (results.MoveNext())
            {
                var idx = results.Current.FindBinding("?name".AsSpan());
                if (idx >= 0)
                    names.Add(results.Current.GetString(idx).ToString());
            }
            results.Dispose();

            Assert.Single(names);
            Assert.Equal("\"Frank\"", names[0]);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GraphClause_MultiplePatterns()
    {
        // Add data to a named graph
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/Grace>", "<http://xmlns.com/foaf/0.1/name>", "\"Grace\"", "<http://example.org/graph4>");
        Store.AddCurrentBatched("<http://example.org/Grace>", "<http://xmlns.com/foaf/0.1/age>", "\"28\"^^<http://www.w3.org/2001/XMLSchema#integer>", "<http://example.org/graph4>");
        Store.CommitBatch();

        var query = "SELECT ?name ?age WHERE { GRAPH <http://example.org/graph4> { ?person <http://xmlns.com/foaf/0.1/name> ?name . ?person <http://xmlns.com/foaf/0.1/age> ?age } }";
        var buffer = ParseToBuffer(query);

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            int count = 0;
            while (results.MoveNext())
            {
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                var ageIdx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(nameIdx >= 0);
                Assert.True(ageIdx >= 0);
                Assert.Equal("\"Grace\"", results.Current.GetString(nameIdx).ToString());
                Assert.Equal("28", ExtractNumericValue(results.Current.GetString(ageIdx).ToString()));
                count++;
            }
            results.Dispose();

            Assert.Equal(1, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GraphClause_NonExistentGraph_ReturnsEmpty()
    {
        var query = "SELECT * WHERE { GRAPH <http://example.org/nonexistent> { ?s ?p ?o } }";
        var buffer = ParseToBuffer(query);

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            Assert.Equal(0, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_VariableGraph_IteratesAllNamedGraphs()
    {
        // Add data to multiple named graphs
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/Henry>", "<http://xmlns.com/foaf/0.1/name>", "\"Henry\"", "<http://example.org/graphA>");
        Store.AddCurrentBatched("<http://example.org/Irene>", "<http://xmlns.com/foaf/0.1/name>", "\"Irene\"", "<http://example.org/graphB>");
        Store.AddCurrentBatched("<http://example.org/Jack>", "<http://xmlns.com/foaf/0.1/name>", "\"Jack\"", "<http://example.org/graphC>");
        Store.CommitBatch();

        var query = "SELECT ?g ?s ?name WHERE { GRAPH ?g { ?s <http://xmlns.com/foaf/0.1/name> ?name } }";
        var buffer = ParseToBuffer(query);

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            var foundGraphs = new HashSet<string>();
            var foundNames = new HashSet<string>();
            while (results.MoveNext())
            {
                var gIdx = results.Current.FindBinding("?g".AsSpan());
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                if (gIdx >= 0)
                    foundGraphs.Add(results.Current.GetString(gIdx).ToString());
                if (nameIdx >= 0)
                    foundNames.Add(results.Current.GetString(nameIdx).ToString());
            }
            results.Dispose();

            // Should find all 3 graphs
            Assert.Equal(3, foundGraphs.Count);
            Assert.Contains("<http://example.org/graphA>", foundGraphs);
            Assert.Contains("<http://example.org/graphB>", foundGraphs);
            Assert.Contains("<http://example.org/graphC>", foundGraphs);

            // Should find all 3 names
            Assert.Equal(3, foundNames.Count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_VariableGraph_BindsGraphVariable()
    {
        // Add data to a named graph
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/Kate>", "<http://xmlns.com/foaf/0.1/name>", "\"Kate\"", "<http://example.org/graphK>");
        Store.CommitBatch();

        var query = "SELECT ?g WHERE { GRAPH ?g { ?s <http://xmlns.com/foaf/0.1/name> \"Kate\" } }";
        var buffer = ParseToBuffer(query);

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            var graphs = new List<string>();
            while (results.MoveNext())
            {
                var gIdx = results.Current.FindBinding("?g".AsSpan());
                if (gIdx >= 0)
                    graphs.Add(results.Current.GetString(gIdx).ToString());
            }
            results.Dispose();

            Assert.Single(graphs);
            Assert.Equal("<http://example.org/graphK>", graphs[0]);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_VariableGraph_ExcludesDefaultGraph()
    {
        // Add data to default graph and named graph
        Store.BeginBatch();
        // Default graph data is already there from constructor
        Store.AddCurrentBatched("<http://example.org/Leo>", "<http://xmlns.com/foaf/0.1/name>", "\"Leo\"", "<http://example.org/graphL>");
        Store.CommitBatch();

        var query = "SELECT ?g ?s WHERE { GRAPH ?g { ?s ?p ?o } }";
        var buffer = ParseToBuffer(query);

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            var foundGraphs = new HashSet<string>();
            while (results.MoveNext())
            {
                var gIdx = results.Current.FindBinding("?g".AsSpan());
                if (gIdx >= 0)
                    foundGraphs.Add(results.Current.GetString(gIdx).ToString());
            }
            results.Dispose();

            // Should NOT find default graph (empty), only named graphs
            Assert.DoesNotContain("", foundGraphs);
            // Should find the named graph
            Assert.Contains("<http://example.org/graphL>", foundGraphs);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_VariableGraph_MultiplePatterns()
    {
        // Add person with name and age to named graph
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/Mary>", "<http://xmlns.com/foaf/0.1/name>", "\"Mary\"", "<http://example.org/graphM>");
        Store.AddCurrentBatched("<http://example.org/Mary>", "<http://xmlns.com/foaf/0.1/age>", "\"32\"^^<http://www.w3.org/2001/XMLSchema#integer>", "<http://example.org/graphM>");
        Store.CommitBatch();

        var query = "SELECT ?g ?name ?age WHERE { GRAPH ?g { ?person <http://xmlns.com/foaf/0.1/name> ?name . ?person <http://xmlns.com/foaf/0.1/age> ?age } }";
        var buffer = ParseToBuffer(query);

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            int count = 0;
            while (results.MoveNext())
            {
                var gIdx = results.Current.FindBinding("?g".AsSpan());
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                var ageIdx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(gIdx >= 0);
                Assert.True(nameIdx >= 0);
                Assert.True(ageIdx >= 0);
                Assert.Equal("<http://example.org/graphM>", results.Current.GetString(gIdx).ToString());
                Assert.Equal("\"Mary\"", results.Current.GetString(nameIdx).ToString());
                Assert.Equal("32", ExtractNumericValue(results.Current.GetString(ageIdx).ToString()));
                count++;
            }
            results.Dispose();

            Assert.Equal(1, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MultipleGraphClauses_JoinsResults()
    {
        // Add data to two different named graphs with a shared subject
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/Person1>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"", "<http://example.org/namesGraph>");
        Store.AddCurrentBatched("<http://example.org/Person1>", "<http://xmlns.com/foaf/0.1/age>", "\"30\"^^<http://www.w3.org/2001/XMLSchema#integer>", "<http://example.org/agesGraph>");
        Store.AddCurrentBatched("<http://example.org/Person2>", "<http://xmlns.com/foaf/0.1/name>", "\"Bob\"", "<http://example.org/namesGraph>");
        // Person2 has no age in agesGraph - should not appear in join
        Store.CommitBatch();

        // Query with two GRAPH clauses - should join on ?person
        var query = @"SELECT ?name ?age WHERE {
            GRAPH <http://example.org/namesGraph> { ?person <http://xmlns.com/foaf/0.1/name> ?name }
            GRAPH <http://example.org/agesGraph> { ?person <http://xmlns.com/foaf/0.1/age> ?age }
        }";
        var buffer = ParseToBuffer(query);

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            var found = new List<(string name, string age)>();
            while (results.MoveNext())
            {
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                var ageIdx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(nameIdx >= 0);
                Assert.True(ageIdx >= 0);
                found.Add((results.Current.GetString(nameIdx).ToString(), results.Current.GetString(ageIdx).ToString()));
            }
            results.Dispose();

            // Only Person1 should appear (has both name and age)
            Assert.Single(found);
            Assert.Equal("\"Alice\"", found[0].name);
            Assert.Equal("30", ExtractNumericValue(found[0].age));
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MultipleGraphClauses_WithVariableGraph()
    {
        // Add data with variable graph pattern
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/Item1>", "<http://example.org/label>", "\"Widget\"", "<http://example.org/labelsGraph>");
        Store.AddCurrentBatched("<http://example.org/Item1>", "<http://example.org/price>", "100", "<http://example.org/pricesGraph>");
        Store.CommitBatch();

        // Mix of fixed and variable GRAPH clauses
        var query = @"SELECT ?g ?label ?price WHERE {
            GRAPH <http://example.org/labelsGraph> { ?item <http://example.org/label> ?label }
            GRAPH ?g { ?item <http://example.org/price> ?price }
        }";
        var buffer = ParseToBuffer(query);

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            var found = new List<(string g, string label, string price)>();
            while (results.MoveNext())
            {
                var gIdx = results.Current.FindBinding("?g".AsSpan());
                var labelIdx = results.Current.FindBinding("?label".AsSpan());
                var priceIdx = results.Current.FindBinding("?price".AsSpan());
                Assert.True(gIdx >= 0);
                Assert.True(labelIdx >= 0);
                Assert.True(priceIdx >= 0);
                found.Add((
                    results.Current.GetString(gIdx).ToString(),
                    results.Current.GetString(labelIdx).ToString(),
                    results.Current.GetString(priceIdx).ToString()));
            }
            results.Dispose();

            Assert.Single(found);
            Assert.Equal("<http://example.org/pricesGraph>", found[0].g);
            Assert.Equal("\"Widget\"", found[0].label);
            Assert.Equal("100", found[0].price);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MultipleGraphClauses_NoMatch_ReturnsEmpty()
    {
        // Add data with no shared subjects between graphs
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/A>", "<http://example.org/p>", "\"ValueA\"", "<http://example.org/graphA>");
        Store.AddCurrentBatched("<http://example.org/B>", "<http://example.org/q>", "\"ValueB\"", "<http://example.org/graphB>");
        Store.CommitBatch();

        // Query with two GRAPH clauses - join on ?s should return no results
        var query = @"SELECT * WHERE {
            GRAPH <http://example.org/graphA> { ?s <http://example.org/p> ?v1 }
            GRAPH <http://example.org/graphB> { ?s <http://example.org/q> ?v2 }
        }";
        var buffer = ParseToBuffer(query);

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            Assert.Equal(0, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    #endregion

    #region FROM / FROM NAMED Dataset Clauses

    [Fact]
    public void Execute_SingleFromClause_QueriesSpecifiedGraph()
    {
        // Add data to named graphs
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/Person1>", "<http://xmlns.com/foaf/0.1/name>", "\"Person1\"", "<http://example.org/fromGraph1>");
        Store.AddCurrentBatched("<http://example.org/Person2>", "<http://xmlns.com/foaf/0.1/name>", "\"Person2\"", "<http://example.org/fromGraph2>");
        Store.CommitBatch();

        // Query with FROM clause - should only get data from fromGraph1
        var query = "SELECT * FROM <http://example.org/fromGraph1> WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteFromToMaterialized();

            var subjects = new List<string>();
            while (results.MoveNext())
            {
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                if (sIdx >= 0)
                    subjects.Add(results.Current.GetString(sIdx).ToString());
            }
            results.Dispose();

            // Should only find Person1 from fromGraph1
            Assert.Single(subjects);
            Assert.Contains("<http://example.org/Person1>", subjects);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MultipleFromClauses_UnionsResults()
    {
        // Add data to multiple named graphs
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/PersonA>", "<http://xmlns.com/foaf/0.1/name>", "\"PersonA\"", "<http://example.org/unionGraph1>");
        Store.AddCurrentBatched("<http://example.org/PersonB>", "<http://xmlns.com/foaf/0.1/name>", "\"PersonB\"", "<http://example.org/unionGraph2>");
        Store.AddCurrentBatched("<http://example.org/PersonC>", "<http://xmlns.com/foaf/0.1/name>", "\"PersonC\"", "<http://example.org/unionGraph3>");
        Store.CommitBatch();

        // Query with multiple FROM clauses - should union graph1 and graph2
        var query = "SELECT * FROM <http://example.org/unionGraph1> FROM <http://example.org/unionGraph2> WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteFromToMaterialized();

            var subjects = new HashSet<string>();
            while (results.MoveNext())
            {
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                if (sIdx >= 0)
                    subjects.Add(results.Current.GetString(sIdx).ToString());
            }
            results.Dispose();

            // Should find PersonA and PersonB but not PersonC
            Assert.Equal(2, subjects.Count);
            Assert.Contains("<http://example.org/PersonA>", subjects);
            Assert.Contains("<http://example.org/PersonB>", subjects);
            Assert.DoesNotContain("<http://example.org/PersonC>", subjects);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    // Note: FROM NAMED with GRAPH ?g hits stack size limitations due to large ref struct
    // combinations. This test verifies parsing works, actual execution with GRAPH ?g is
    // tested separately in the GRAPH clause tests (without FROM NAMED).
    [Fact]
    public void Parse_FromNamedClause_ParsesCorrectly()
    {
        // Query with FROM NAMED
        var query = "SELECT ?g ?s FROM NAMED <http://example.org/namedGraph1> FROM NAMED <http://example.org/namedGraph2> WHERE { GRAPH ?g { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify datasets parsed correctly
        Assert.Equal(2, parsedQuery.Datasets.Length);
        Assert.True(parsedQuery.Datasets[0].IsNamed);
        Assert.True(parsedQuery.Datasets[1].IsNamed);

        var graph1 = query.AsSpan().Slice(parsedQuery.Datasets[0].GraphIri.Start, parsedQuery.Datasets[0].GraphIri.Length).ToString();
        var graph2 = query.AsSpan().Slice(parsedQuery.Datasets[1].GraphIri.Start, parsedQuery.Datasets[1].GraphIri.Length).ToString();
        Assert.Equal("<http://example.org/namedGraph1>", graph1);
        Assert.Equal("<http://example.org/namedGraph2>", graph2);
    }

    [Fact]
    public void Execute_FromNamedClause_RestrictsGraphVariable()
    {
        // Add data to multiple named graphs
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/Item1>", "<http://example.org/type>", "\"TypeA\"", "<http://example.org/restrictGraph1>");
        Store.AddCurrentBatched("<http://example.org/Item2>", "<http://example.org/type>", "\"TypeB\"", "<http://example.org/restrictGraph2>");
        Store.AddCurrentBatched("<http://example.org/Item3>", "<http://example.org/type>", "\"TypeC\"", "<http://example.org/restrictGraph3>");
        Store.CommitBatch();

        // Query with FROM NAMED - should only see restrictGraph1 and restrictGraph2
        var query = @"SELECT ?g ?s ?type
                      FROM NAMED <http://example.org/restrictGraph1>
                      FROM NAMED <http://example.org/restrictGraph2>
                      WHERE { GRAPH ?g { ?s <http://example.org/type> ?type } }";
        var buffer = ParseToBuffer(query);

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            var graphs = new HashSet<string>();
            var items = new HashSet<string>();
            while (results.MoveNext())
            {
                var gIdx = results.Current.FindBinding("?g".AsSpan());
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                if (gIdx >= 0) graphs.Add(results.Current.GetString(gIdx).ToString());
                if (sIdx >= 0) items.Add(results.Current.GetString(sIdx).ToString());
            }
            results.Dispose();

            // Should only find Item1 and Item2, not Item3
            Assert.Equal(2, items.Count);
            Assert.Contains("<http://example.org/Item1>", items);
            Assert.Contains("<http://example.org/Item2>", items);
            Assert.DoesNotContain("<http://example.org/Item3>", items);

            // Should only bind restrictGraph1 and restrictGraph2
            Assert.Equal(2, graphs.Count);
            Assert.Contains("<http://example.org/restrictGraph1>", graphs);
            Assert.Contains("<http://example.org/restrictGraph2>", graphs);
            Assert.DoesNotContain("<http://example.org/restrictGraph3>", graphs);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_NoDatasetClauses_QueriesDefaultGraphAndAllNamed()
    {
        // This test verifies default behavior without FROM/FROM NAMED
        // Data was already added in constructor for default graph

        // Query without FROM - should get default graph data
        var query = "SELECT * WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // Alice, Bob, Charlie from default graph
            Assert.Equal(3, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_FromWithFilter_AppliesFilterToUnionedResults()
    {
        // Add data with varying ages to graphs
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/FilterPerson1>", "<http://xmlns.com/foaf/0.1/age>", "\"20\"^^<http://www.w3.org/2001/XMLSchema#integer>", "<http://example.org/filterGraph1>");
        Store.AddCurrentBatched("<http://example.org/FilterPerson2>", "<http://xmlns.com/foaf/0.1/age>", "\"40\"^^<http://www.w3.org/2001/XMLSchema#integer>", "<http://example.org/filterGraph1>");
        Store.AddCurrentBatched("<http://example.org/FilterPerson3>", "<http://xmlns.com/foaf/0.1/age>", "\"15\"^^<http://www.w3.org/2001/XMLSchema#integer>", "<http://example.org/filterGraph2>");
        Store.AddCurrentBatched("<http://example.org/FilterPerson4>", "<http://xmlns.com/foaf/0.1/age>", "\"50\"^^<http://www.w3.org/2001/XMLSchema#integer>", "<http://example.org/filterGraph2>");
        Store.CommitBatch();

        // Query with FROM and FILTER
        var query = @"SELECT ?s ?age
                      FROM <http://example.org/filterGraph1>
                      FROM <http://example.org/filterGraph2>
                      WHERE { ?s <http://xmlns.com/foaf/0.1/age> ?age FILTER(?age > 25) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteFromToMaterialized();

            var subjects = new HashSet<string>();
            while (results.MoveNext())
            {
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                if (sIdx >= 0) subjects.Add(results.Current.GetString(sIdx).ToString());
            }
            results.Dispose();

            // Should find Person2 (age 40) and Person4 (age 50) but not Person1 (20) or Person3 (15)
            Assert.Equal(2, subjects.Count);
            Assert.Contains("<http://example.org/FilterPerson2>", subjects);
            Assert.Contains("<http://example.org/FilterPerson4>", subjects);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_FromWithJoin_JoinsAcrossGraphs()
    {
        // Add related data across graphs
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/JoinPerson>", "<http://xmlns.com/foaf/0.1/name>", "\"JoinPerson\"", "<http://example.org/joinGraph1>");
        Store.AddCurrentBatched("<http://example.org/JoinPerson>", "<http://xmlns.com/foaf/0.1/age>", "\"35\"^^<http://www.w3.org/2001/XMLSchema#integer>", "<http://example.org/joinGraph2>");
        Store.CommitBatch();

        // Query joining data from both graphs
        var query = @"SELECT ?name ?age
                      FROM <http://example.org/joinGraph1>
                      FROM <http://example.org/joinGraph2>
                      WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name . ?s <http://xmlns.com/foaf/0.1/age> ?age }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteFromToMaterialized();

            var foundJoin = false;
            while (results.MoveNext())
            {
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                var ageIdx = results.Current.FindBinding("?age".AsSpan());
                if (nameIdx >= 0 && ageIdx >= 0)
                {
                    var name = results.Current.GetString(nameIdx).ToString();
                    var age = ExtractNumericValue(results.Current.GetString(ageIdx).ToString());
                    if (name == "\"JoinPerson\"" && age == "35")
                        foundJoin = true;
                }
            }
            results.Dispose();

            // Should successfully join data across graphs
            Assert.True(foundJoin);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_GraphWithExistsFilter_FiltersCorrectly()
    {
        // Test case based on W3C exists03
        // Named graph has: :a :p :o1 and :b :p :o1, :o2
        // Query: GRAPH <g> { ?s ?p ex:o1 FILTER EXISTS { ?s ?p ex:o2 } }
        // Only :b should match (has both :o1 and :o2)
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://www.example.org/a>", "<http://www.example.org/p>", "<http://www.example.org/o1>", "<http://example.org/testgraph>");
        Store.AddCurrentBatched("<http://www.example.org/b>", "<http://www.example.org/p>", "<http://www.example.org/o1>", "<http://example.org/testgraph>");
        Store.AddCurrentBatched("<http://www.example.org/b>", "<http://www.example.org/p>", "<http://www.example.org/o2>", "<http://example.org/testgraph>");
        Store.CommitBatch();

        // First test: verify data is in named graph (no EXISTS filter)
        var simpleQuery = @"SELECT * WHERE {
    GRAPH <http://example.org/testgraph> {
        ?s ?p ?o
    }
}";
        var simpleParser = new SparqlParser(simpleQuery.AsSpan());
        var simpleParsedQuery = simpleParser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var simpleExecutor = new QueryExecutor(Store, simpleQuery.AsSpan(), simpleParsedQuery);
            var simpleResults = simpleExecutor.Execute();
            int simpleCount = 0;
            while (simpleResults.MoveNext())
            {
                simpleCount++;
            }
            simpleResults.Dispose();

            // Should have 3 triples in the graph
            Assert.Equal(3, simpleCount);
        }
        finally
        {
            Store.ReleaseReadLock();
        }

        // Second test: with EXISTS filter
        var query = @"PREFIX ex: <http://www.example.org/>
SELECT * WHERE {
    GRAPH <http://example.org/testgraph> {
        ?s ?p ex:o1
        FILTER EXISTS { ?s ?p ex:o2 }
    }
}";
        // Use the standard parsed query path which supports EXISTS
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify parser detected EXISTS
        Assert.True(parsedQuery.WhereClause.Pattern.ExistsFilterCount > 0,
            $"Parser should detect EXISTS filter, but ExistsFilterCount={parsedQuery.WhereClause.Pattern.ExistsFilterCount}");

        // Verify buffer is created with EXISTS count
        var buffer = SkyOmega.Mercury.Sparql.Patterns.QueryBufferAdapter.FromQuery(in parsedQuery, query.AsSpan());
        Assert.True(buffer.ExistsFilterCount > 0,
            $"Buffer should have ExistsFilterCount > 0, but got {buffer.ExistsFilterCount}, HasExists={buffer.HasExists}");
        // Also check the GRAPH execution path conditions
        Assert.True(buffer.HasGraph, $"Buffer should have HasGraph=true, but got {buffer.HasGraph}");
        Assert.Equal(0, buffer.TriplePatternCount); // No top-level triple patterns
        Assert.False(buffer.HasSubQueries, $"Buffer should NOT have HasSubQueries, but it does");
        buffer.Dispose();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);

            // Verify executor's buffer has EXISTS and correct path conditions
            Assert.True(executor.BufferHasExists,
                $"Executor buffer should have HasExists=true, ExistsFilterCount={executor.BufferExistsFilterCount}");

            // Verify GRAPH execution path conditions
            Assert.True(executor.BufferHasGraph, "Executor buffer should have HasGraph=true");
            Assert.Equal(0, executor.BufferTriplePatternCount);
            Assert.False(executor.BufferHasSubQueries, "Executor buffer should not have subqueries");

            var results = executor.Execute();

            // Verify EXISTS handling is enabled
            Assert.True(results.HasExists,
                $"Results should have HasExists=true, HasOrderBy={results.HasOrderBy}");

            var subjects = new System.Collections.Generic.List<string>();
            while (results.MoveNext())
            {
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                if (sIdx >= 0)
                    subjects.Add(results.Current.GetString(sIdx).ToString());
            }
            results.Dispose();

            // Only :b has both :o1 and :o2
            Assert.Single(subjects);
            Assert.Contains("<http://www.example.org/b>", subjects);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_ExistsWithGraphVariable_BindsFromOuter()
    {
        // Test case based on W3C exists-graph-variable
        // Data in default graph: :s1 :p <graph1> and :s2 :p :o2
        // Data in named graph <graph1>: :s2 :p :o2
        // Query: SELECT ?s WHERE { ?s :p ?g . FILTER EXISTS { GRAPH ?g { ?s2 :p ?o2 } } }
        // Expected: :s1 (because ?g = <graph1> and that graph has matching triples)

        Store.BeginBatch();
        // Default graph: s1 points to graph1, s2 points to o2
        Store.AddCurrentBatched("<http://www.example.org/s1>", "<http://www.example.org/p>", "<http://example.org/graph1>");
        Store.AddCurrentBatched("<http://www.example.org/s2>", "<http://www.example.org/p>", "<http://www.example.org/o2>");
        // Named graph graph1 has matching triples
        Store.AddCurrentBatched("<http://www.example.org/s2>", "<http://www.example.org/p>", "<http://www.example.org/o2>", "<http://example.org/graph1>");
        Store.CommitBatch();

        var query = @"PREFIX : <http://www.example.org/>
SELECT ?s WHERE {
    ?s :p ?g .
    FILTER EXISTS { GRAPH ?g { ?s2 :p ?o2 } }
}";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var subjects = new System.Collections.Generic.List<string>();
            while (results.MoveNext())
            {
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                if (sIdx >= 0)
                    subjects.Add(results.Current.GetString(sIdx).ToString());
            }
            results.Dispose();

            // Only :s1 should match because ?g = <graph1> which exists and has matching triples
            // :s2 has ?g = :o2 which is not a named graph, so EXISTS fails
            Assert.Single(subjects);
            Assert.Contains("<http://www.example.org/s1>", subjects);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MinusInsideGraph_DisjointVariables_DoesNotExclude()
    {
        // Test case based on W3C graph-minus
        // GRAPH ?g { ?a :p :o MINUS { ?b :p :o } }
        // Since ?a and ?b are different variables (disjoint domains),
        // MINUS should NOT exclude any results

        Store.BeginBatch();
        // Named graph has one triple
        Store.AddCurrentBatched("<http://example/subj>", "<http://example/pred>", "<http://example/obj>", "<http://example/graph1>");
        Store.CommitBatch();

        // First, verify the GRAPH query works without MINUS
        var queryNoMinus = @"SELECT ?a WHERE {
    GRAPH <http://example/graph1> {
        ?a <http://example/pred> <http://example/obj>
    }
}";
        var bufferNoMinus = ParseToBuffer(queryNoMinus);

        Store.AcquireReadLock();
        try
        {
            using var executorNoMinus = new QueryExecutor(Store, queryNoMinus.AsSpan(), bufferNoMinus);
            var resultsNoMinus = executorNoMinus.ExecuteGraphToMaterialized();

            int count = 0;
            while (resultsNoMinus.MoveNext())
            {
                count++;
            }
            resultsNoMinus.Dispose();

            Assert.Equal(1, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }

        // Now test with MINUS (disjoint variables)
        var query = @"SELECT ?a WHERE {
    GRAPH <http://example/graph1> {
        ?a <http://example/pred> <http://example/obj>
        MINUS {
            ?b <http://example/pred> <http://example/obj>
        }
    }
}";
        var buffer = ParseToBuffer(query);

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), buffer);
            var results = executor.ExecuteGraphToMaterialized();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            // ?a and ?b are disjoint (different variables), so MINUS should NOT exclude
            Assert.Equal(1, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    #endregion
}
