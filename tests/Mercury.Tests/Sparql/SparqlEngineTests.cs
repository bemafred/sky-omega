using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

[Collection("QuadStore")]
public class SparqlEngineTests : PooledStoreTestBase
{
    public SparqlEngineTests(QuadStorePoolFixture fixture) : base(fixture) { }

    private void PopulateTestData()
    {
        Store.AddCurrent("<http://ex/alice>", "<http://ex/name>", "\"Alice\"");
        Store.AddCurrent("<http://ex/alice>", "<http://ex/age>", "\"30\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://ex/bob>", "<http://ex/name>", "\"Bob\"");
        Store.AddCurrent("<http://ex/bob>", "<http://ex/age>", "\"25\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://ex/carol>", "<http://ex/name>", "\"Carol\"");
    }

    [Fact]
    public void Query_Select_ReturnsRows()
    {
        PopulateTestData();

        var result = SparqlEngine.Query(Store, "SELECT ?s ?name WHERE { ?s <http://ex/name> ?name }");

        Assert.True(result.Success);
        Assert.Equal(ExecutionResultKind.Select, result.Kind);
        Assert.NotNull(result.Variables);
        Assert.Contains("s", result.Variables);
        Assert.Contains("name", result.Variables);
        Assert.NotNull(result.Rows);
        Assert.Equal(3, result.Rows.Count);
    }

    [Fact]
    public void Query_SelectStar_ReturnsRows()
    {
        PopulateTestData();

        var result = SparqlEngine.Query(Store, "SELECT * WHERE { ?s <http://ex/name> ?name }");

        Assert.True(result.Success);
        Assert.Equal(ExecutionResultKind.Select, result.Kind);
        Assert.NotNull(result.Variables);
        Assert.NotNull(result.Rows);
        Assert.Equal(3, result.Rows.Count);
        // Variable names should be extracted via FNV-1a hash matching
        Assert.True(result.Variables.Length >= 2);
    }

    [Fact]
    public void Query_Ask_ReturnsTrue()
    {
        PopulateTestData();

        var result = SparqlEngine.Query(Store, "ASK { <http://ex/alice> <http://ex/name> ?name }");

        Assert.True(result.Success);
        Assert.Equal(ExecutionResultKind.Ask, result.Kind);
        Assert.True(result.AskResult);
    }

    [Fact]
    public void Query_Ask_ReturnsFalse()
    {
        PopulateTestData();

        var result = SparqlEngine.Query(Store, "ASK { <http://ex/nobody> <http://ex/name> ?name }");

        Assert.True(result.Success);
        Assert.Equal(ExecutionResultKind.Ask, result.Kind);
        Assert.False(result.AskResult);
    }

    [Fact]
    public void Query_Construct_ReturnsTriples()
    {
        PopulateTestData();

        var result = SparqlEngine.Query(Store, "CONSTRUCT { ?s <http://ex/label> ?name } WHERE { ?s <http://ex/name> ?name }");

        Assert.True(result.Success);
        Assert.Equal(ExecutionResultKind.Construct, result.Kind);
        Assert.NotNull(result.Triples);
        Assert.Equal(3, result.Triples.Count);
        Assert.All(result.Triples, t => Assert.Equal("<http://ex/label>", t.Predicate));
    }

    [Fact]
    public void Query_Describe_ReturnsResult()
    {
        PopulateTestData();

        var result = SparqlEngine.Query(Store, "DESCRIBE ?s WHERE { ?s <http://ex/name> ?name }");

        Assert.True(result.Success, $"DESCRIBE failed: {result.ErrorMessage}");
        Assert.Equal(ExecutionResultKind.Describe, result.Kind);
        Assert.NotNull(result.Triples);
    }

    [Fact]
    public void Query_ParseError_ReturnsError()
    {
        var result = SparqlEngine.Query(Store, "SELECT * WHERE { ?s ?p }");

        Assert.False(result.Success);
        Assert.Equal(ExecutionResultKind.Error, result.Kind);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void Query_Cancellation_ReturnsError()
    {
        PopulateTestData();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = SparqlEngine.Query(Store, "SELECT * WHERE { ?s ?p ?o }", cts.Token);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void Query_HasTimingInfo()
    {
        PopulateTestData();

        var result = SparqlEngine.Query(Store, "SELECT * WHERE { ?s <http://ex/name> ?name }");

        Assert.True(result.Success);
        Assert.True(result.ParseTime >= TimeSpan.Zero);
        Assert.True(result.ExecutionTime >= TimeSpan.Zero);
    }

    [Fact]
    public void Update_InsertData_Succeeds()
    {
        var result = SparqlEngine.Update(Store, "INSERT DATA { <http://ex/dave> <http://ex/name> \"Dave\" }");

        Assert.True(result.Success);
        Assert.True(result.AffectedCount >= 1);

        // Verify the data was inserted
        var query = SparqlEngine.Query(Store, "ASK { <http://ex/dave> <http://ex/name> \"Dave\" }");
        Assert.True(query.AskResult);
    }

    [Fact]
    public void Update_DeleteData_Succeeds()
    {
        Store.AddCurrent("<http://ex/temp>", "<http://ex/name>", "\"Temp\"");

        var result = SparqlEngine.Update(Store, "DELETE DATA { <http://ex/temp> <http://ex/name> \"Temp\" }");

        Assert.True(result.Success);
    }

    [Fact]
    public void Update_ParseError_ReturnsError()
    {
        var result = SparqlEngine.Update(Store, "INSERT DATAA { <http://ex/s> <http://ex/p> \"o\" }");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void Explain_WithoutStore_ReturnsText()
    {
        var plan = SparqlEngine.Explain("SELECT ?s ?p ?o WHERE { ?s ?p ?o }");

        Assert.NotNull(plan);
        Assert.Contains("QUERY PLAN", plan);
    }

    [Fact]
    public void Explain_WithStore_ReturnsAnalyzePlan()
    {
        PopulateTestData();

        var plan = SparqlEngine.Explain("SELECT ?s WHERE { ?s <http://ex/name> ?name }", Store);

        Assert.NotNull(plan);
        Assert.Contains("QUERY PLAN", plan);
    }

    [Fact]
    public void GetNamedGraphs_Empty_ReturnsEmpty()
    {
        var graphs = SparqlEngine.GetNamedGraphs(Store);
        Assert.NotNull(graphs);
        Assert.Empty(graphs);
    }

    [Fact]
    public void GetNamedGraphs_WithGraphs_ReturnsList()
    {
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "\"o\"", "<http://ex/graph1>");
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "\"o\"", "<http://ex/graph2>");

        var graphs = SparqlEngine.GetNamedGraphs(Store);

        Assert.NotNull(graphs);
        Assert.Equal(2, graphs.Count);
        Assert.Contains("<http://ex/graph1>", graphs);
        Assert.Contains("<http://ex/graph2>", graphs);
    }

    [Fact]
    public void GetStatistics_ReturnsStats()
    {
        PopulateTestData();

        var stats = SparqlEngine.GetStatistics(Store);

        Assert.True(stats.QuadCount >= 5);
        Assert.True(stats.AtomCount > 0);
        Assert.True(stats.TotalBytes > 0);
    }

    [Fact]
    public void Query_SelectWithAggregates_Works()
    {
        PopulateTestData();

        var result = SparqlEngine.Query(Store, "SELECT (COUNT(?s) AS ?count) WHERE { ?s <http://ex/name> ?name }");

        Assert.True(result.Success);
        Assert.Equal(ExecutionResultKind.Select, result.Kind);
        Assert.NotNull(result.Rows);
        Assert.Single(result.Rows);
    }
}
