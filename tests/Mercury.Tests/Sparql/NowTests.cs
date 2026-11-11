using SkyOmega.Mercury.Sparql.Execution.Expressions;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;
using ValueType = SkyOmega.Mercury.Sparql.Execution.Expressions.ValueType;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// NOW() is one value per query execution: "All calls to this function in any one query execution must return the
/// same value" (SPARQL 1.1 §17.4.5.1). Each evaluator read the clock itself, so one SELECT returned two values
/// (2026-11-10, 19:47:54.7365140 and .7368530). An update request is one execution: its operations read one value.
/// Each test reaches NOW() through a different place the engine evaluates an expression.
/// </summary>
[Collection("QuadStore")]
public class NowTests : PooledStoreTestBase
{
    public NowTests(QuadStorePoolFixture fixture) : base(fixture) { }

    // Enough rows that the clock moves while one execution evaluates NOW() for each of them
    private static readonly string ThousandRows = "VALUES ?i { " + string.Join(" ", Enumerable.Range(1, 1000)) + " }";

    [Fact]
    public void SelectExpressions_InOneRow_ReadOneValue()
    {
        var result = SparqlEngine.Query(Store, "SELECT (NOW() AS ?a) (NOW() AS ?b) WHERE { }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(result.Rows![0]["a"], result.Rows[0]["b"]);
    }

    [Fact]
    public void Bind_OnEveryRow_ReadsOneValue()
    {
        var result = SparqlEngine.Query(Store, $"SELECT ?i ?t WHERE {{ {ThousandRows} BIND(NOW() AS ?t) }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1000, result.Rows!.Count);
        Assert.Single(result.Rows.Select(row => row["t"]).Distinct());
    }

    [Fact]
    public void Filter_OnEveryRow_ReadsTheValueBindRead()
    {
        var result = SparqlEngine.Query(Store,
            $"SELECT ?i WHERE {{ {ThousandRows} BIND(NOW() AS ?t) FILTER(?t = NOW()) }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1000, result.Rows!.Count);
    }

    [Fact]
    public void FilterWithExists_OnEveryRow_ReadsTheValueBindRead()
    {
        var result = SparqlEngine.Query(Store,
            $"SELECT ?i WHERE {{ {ThousandRows} BIND(NOW() AS ?t) FILTER(EXISTS {{ BIND(1 AS ?one) }} && ?t = NOW()) }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1000, result.Rows!.Count);
    }

    [Fact]
    public void SubSelect_ReadsTheOuterQuerysValue()
    {
        var result = SparqlEngine.Query(Store,
            $"SELECT ?i ?t ?u WHERE {{ {{ SELECT ?i (NOW() AS ?u) WHERE {{ {ThousandRows} }} }} BIND(NOW() AS ?t) }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1000, result.Rows!.Count);
        Assert.Single(result.Rows.Select(row => row["t"]).Concat(result.Rows.Select(row => row["u"])).Distinct());
    }

    [Fact]
    public void SelectExpression_OnEveryGroup_ReadsOneValue()
    {
        var result = SparqlEngine.Query(Store, $"SELECT ?i (NOW() AS ?t) WHERE {{ {ThousandRows} }} GROUP BY ?i",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1000, result.Rows!.Count);
        Assert.Single(result.Rows.Select(row => row["t"]).Distinct());
    }

    [Fact]
    public void Aggregate_OverRows_ReadsOneValue()
    {
        var result = SparqlEngine.Query(Store,
            $"SELECT (MIN(NOW()) AS ?first) (MAX(NOW()) AS ?last) WHERE {{ {ThousandRows} }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(result.Rows![0]["first"], result.Rows[0]["last"]);
    }

    [Fact]
    public void Aggregate_OverStoredTriples_ReadsOneValue()
    {
        // A simple aggregate over a basic graph pattern is folded while the triples are scanned (TreeJoinExecutor)
        for (int i = 0; i < 1000; i++)
            Store.AddCurrent($"<http://ex/s{i}>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store,
            "SELECT (MIN(NOW()) AS ?first) (MAX(NOW()) AS ?last) WHERE { ?s <http://ex/p> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(result.Rows![0]["first"], result.Rows[0]["last"]);
    }

    [Fact]
    public void OrderBy_OnEveryRow_ReadsOneValue()
    {
        // With one value, DESC(NOW()) ties every row and ?i orders them; with a value per row, the rows whose
        // value was read later would sort first
        var byNow = SparqlEngine.Query(Store, $"SELECT ?i WHERE {{ {ThousandRows} }} ORDER BY DESC(NOW()) ?i",
            TestContext.Current.CancellationToken);
        var byI = SparqlEngine.Query(Store, $"SELECT ?i WHERE {{ {ThousandRows} }} ORDER BY ?i",
            TestContext.Current.CancellationToken);

        Assert.True(byNow.Success, byNow.ErrorMessage);
        Assert.True(byI.Success, byI.ErrorMessage);
        Assert.Equal(byI.Rows!.Select(row => row["i"]), byNow.Rows!.Select(row => row["i"]));
    }

    [Fact]
    public void Update_OnEveryRow_ReadsOneValue()
    {
        var update = SparqlEngine.Update(Store,
            $"INSERT {{ ?s <http://ex/at> ?t }} WHERE {{ {ThousandRows} BIND(IRI(CONCAT(\"http://ex/s\", STR(?i))) AS ?s) BIND(NOW() AS ?t) }}",
            TestContext.Current.CancellationToken);
        var result = SparqlEngine.Query(Store, "SELECT DISTINCT ?t WHERE { ?s <http://ex/at> ?t }",
            TestContext.Current.CancellationToken);

        Assert.True(update.Success, update.ErrorMessage);
        Assert.Equal(1000, update.AffectedCount);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Single(result.Rows!);
    }

    [Fact]
    public void UpdateRequest_EveryOperation_ReadsOneValue()
    {
        var update = SparqlEngine.Update(Store,
            "INSERT { <http://ex/s> <http://ex/first> ?t } WHERE { BIND(NOW() AS ?t) } ; " +
            $"INSERT {{ ?s <http://ex/n> ?i }} WHERE {{ {ThousandRows} BIND(IRI(CONCAT(\"http://ex/s\", STR(?i))) AS ?s) }} ; " +
            "INSERT { <http://ex/s> <http://ex/last> ?t } WHERE { BIND(NOW() AS ?t) }",
            TestContext.Current.CancellationToken);
        var result = SparqlEngine.Query(Store,
            "SELECT ?first ?last WHERE { <http://ex/s> <http://ex/first> ?first ; <http://ex/last> ?last }",
            TestContext.Current.CancellationToken);

        Assert.True(update.Success, update.ErrorMessage);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(result.Rows![0]["first"], result.Rows[0]["last"]);
    }

    [Fact]
    public void Evaluator_ReturnsTheExecutionsTime_InUtc()
    {
        var execution = new DateTimeOffset(2026, 11, 11, 10, 14, 19, TimeSpan.FromHours(1)).AddTicks(1234567);

        var value = new FilterEvaluator("NOW()".AsSpan(), execution).EvaluateToValue(
            ReadOnlySpan<Binding>.Empty, 0, ReadOnlySpan<char>.Empty, null, ReadOnlySpan<char>.Empty);

        Assert.Equal("\"2026-11-11T09:14:19.1234567Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>", value.Term());
    }

    [Fact]
    public void Evaluator_OutsideAnExecution_HasNoTime()
    {
        // A constant read for its stored form (LiteralForm) is part of no execution: NOW() there is an error
        var value = new FilterEvaluator("NOW()".AsSpan(), now: null).EvaluateToValue(
            ReadOnlySpan<Binding>.Empty, 0, ReadOnlySpan<char>.Empty, null, ReadOnlySpan<char>.Empty);

        Assert.Equal(ValueType.Unbound, value.Type);
    }

    [Fact]
    public void TwoExecutions_ReadTwoValues()
    {
        var first = SparqlEngine.Query(Store, "SELECT (NOW() AS ?t) WHERE { }", TestContext.Current.CancellationToken);
        var second = SparqlEngine.Query(Store, "SELECT (NOW() AS ?t) WHERE { }", TestContext.Current.CancellationToken);

        Assert.True(first.Success, first.ErrorMessage);
        Assert.True(second.Success, second.ErrorMessage);
        Assert.True(string.CompareOrdinal(second.Rows![0]["t"], first.Rows![0]["t"]) > 0,
            $"{second.Rows[0]["t"]} is not after {first.Rows[0]["t"]}");
    }
}
