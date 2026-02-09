using System;
using System.IO;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;
using SkyOmega.Mercury.Sparql.Execution.Operators;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// Tests for SPARQL EXPLAIN functionality.
/// </summary>
[Collection("QuadStore")]
public class SparqlExplainTests : PooledStoreTestBase
{
    public SparqlExplainTests(QuadStorePoolFixture fixture) : base(fixture)
    {
        // Add some test data
        Store.AddCurrent("<http://ex.org/Alice>", "<http://ex.org/knows>", "<http://ex.org/Bob>");
        Store.AddCurrent("<http://ex.org/Alice>", "<http://ex.org/age>", "\"30\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrent("<http://ex.org/Bob>", "<http://ex.org/knows>", "<http://ex.org/Carol>");
    }

    #region Basic Plan Generation Tests

    [Fact]
    public void Explain_SimpleSelect_GeneratesPlan()
    {
        var query = "SELECT * WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        var explainer = new SparqlExplainer(query.AsSpan(), in parsed);
        var plan = explainer.Explain();

        Assert.NotNull(plan);
        Assert.Equal(query, plan.Query);
        Assert.False(plan.IsAnalyzed);
        Assert.NotNull(plan.Root);
        Assert.Equal(ExplainOperatorType.Query, plan.Root.OperatorType);
    }

    [Fact]
    public void Explain_SelectDistinct_ShowsDistinct()
    {
        var query = "SELECT DISTINCT ?s WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        var plan = parsed.Explain(query.AsSpan());

        Assert.Contains("DISTINCT", plan.Root.Description);
    }

    [Fact]
    public void Explain_SinglePattern_ShowsTriplePatternScan()
    {
        var query = "SELECT * WHERE { ?s <http://ex.org/knows> ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        var plan = parsed.Explain(query.AsSpan());
        var textOutput = plan.Format(ExplainFormat.Text);

        Assert.Contains("⊳", textOutput); // TriplePatternScan symbol
        Assert.Contains("?s", textOutput);
        Assert.Contains("?o", textOutput);
    }

    [Fact]
    public void Explain_MultiplePatterns_ShowsNestedLoopJoin()
    {
        var query = "SELECT * WHERE { ?s <http://ex.org/knows> ?o . ?o <http://ex.org/age> ?age }";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        var plan = parsed.Explain(query.AsSpan());
        var textOutput = plan.Format(ExplainFormat.Text);

        Assert.Contains("⋈", textOutput); // NestedLoopJoin symbol
        Assert.Contains("Join", textOutput);
    }

    #endregion

    #region Solution Modifier Tests

    [Fact]
    public void Explain_LimitOffset_ShowsSlice()
    {
        var query = "SELECT * WHERE { ?s ?p ?o } LIMIT 10 OFFSET 5";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        var plan = parsed.Explain(query.AsSpan());
        var textOutput = plan.Format(ExplainFormat.Text);

        Assert.Contains("⌊", textOutput); // Slice symbol
        Assert.Contains("LIMIT 10", textOutput);
        Assert.Contains("OFFSET 5", textOutput);
    }

    [Fact]
    public void Explain_OrderBy_ShowsSort()
    {
        var query = "SELECT * WHERE { ?s ?p ?o } ORDER BY ?s";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        var plan = parsed.Explain(query.AsSpan());
        var textOutput = plan.Format(ExplainFormat.Text);

        Assert.Contains("↑", textOutput); // Sort symbol
        Assert.Contains("Sort", textOutput);
    }

    [Fact]
    public void Explain_GroupBy_ShowsAggregation()
    {
        var query = "SELECT ?s (COUNT(?o) AS ?count) WHERE { ?s ?p ?o } GROUP BY ?s";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        var plan = parsed.Explain(query.AsSpan());
        var textOutput = plan.Format(ExplainFormat.Text);

        Assert.Contains("γ", textOutput); // GroupBy symbol
        Assert.Contains("aggregate", textOutput.ToLower());
    }

    #endregion

    #region Optional and Union Tests

    [Fact]
    public void Explain_Optional_ShowsLeftOuterJoin()
    {
        var query = "SELECT * WHERE { ?s ?p ?o OPTIONAL { ?o <http://ex.org/age> ?age } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        var plan = parsed.Explain(query.AsSpan());
        var textOutput = plan.Format(ExplainFormat.Text);

        Assert.Contains("⟕", textOutput); // LeftOuterJoin symbol
        Assert.Contains("OPTIONAL", textOutput);
    }

    [Fact]
    public void Explain_Union_ShowsUnionOperator()
    {
        var query = "SELECT * WHERE { { ?s <http://ex.org/knows> ?o } UNION { ?s <http://ex.org/likes> ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        var plan = parsed.Explain(query.AsSpan());
        var textOutput = plan.Format(ExplainFormat.Text);

        Assert.Contains("∪", textOutput); // Union symbol
        Assert.Contains("Alternative", textOutput);
    }

    #endregion

    #region Filter Tests

    [Fact]
    public void Explain_Filter_ShowsFilterOperator()
    {
        var query = "SELECT * WHERE { ?s ?p ?o FILTER(?o > 25) }";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        var plan = parsed.Explain(query.AsSpan());
        var textOutput = plan.Format(ExplainFormat.Text);

        Assert.Contains("σ", textOutput); // Filter symbol
        Assert.Contains("FILTER", textOutput);
    }

    #endregion

    #region ASK Query Tests

    [Fact]
    public void Explain_AskQuery_ShowsAsk()
    {
        var query = "ASK WHERE { ?s <http://ex.org/knows> ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        var plan = parsed.Explain(query.AsSpan());

        Assert.Contains("ASK", plan.Root.Description);
    }

    #endregion

    #region EXPLAIN ANALYZE Tests

    [Fact]
    public void ExplainAnalyze_ReturnsExecutionStats()
    {
        var query = "SELECT * WHERE { ?s <http://ex.org/knows> ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        var plan = parsed.ExplainAnalyze(query.AsSpan(), Store);

        Assert.True(plan.IsAnalyzed);
        Assert.NotNull(plan.TotalExecutionTimeMs);
        Assert.True(plan.TotalExecutionTimeMs >= 0);
        Assert.NotNull(plan.TotalRows);
        Assert.True(plan.TotalRows >= 0);
    }

    [Fact]
    public void ExplainAnalyze_CountsCorrectRows()
    {
        var query = "SELECT * WHERE { ?s <http://ex.org/knows> ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        var plan = parsed.ExplainAnalyze(query.AsSpan(), Store);

        // We added 2 "knows" triples in setup
        Assert.Equal(2, plan.TotalRows);
    }

    [Fact]
    public void ExplainAnalyze_AskQuery_ReturnsOneOrZero()
    {
        var query = "ASK WHERE { ?s <http://ex.org/knows> ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        var plan = parsed.ExplainAnalyze(query.AsSpan(), Store);

        Assert.True(plan.IsAnalyzed);
        Assert.Equal(1, plan.TotalRows); // ASK returns true (1 row)
    }

    #endregion

    #region Output Format Tests

    [Fact]
    public void Format_Text_ProducesReadableOutput()
    {
        var query = "SELECT * WHERE { ?s ?p ?o } LIMIT 10";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        var plan = parsed.Explain(query.AsSpan());
        var text = plan.Format(ExplainFormat.Text);

        Assert.Contains("QUERY PLAN", text);
        Assert.Contains("─", text); // Box drawing character
        Assert.Contains("SELECT", text);
    }

    [Fact]
    public void Format_Json_ProducesValidStructure()
    {
        var query = "SELECT * WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        var plan = parsed.Explain(query.AsSpan());
        var json = plan.Format(ExplainFormat.Json);

        Assert.Contains("\"query\":", json);
        Assert.Contains("\"analyzed\":", json);
        Assert.Contains("\"plan\":", json);
        Assert.Contains("\"operator\":", json);
        Assert.Contains("\"children\":", json);
    }

    [Fact]
    public void Format_Json_EscapesSpecialCharacters()
    {
        var query = "SELECT * WHERE { ?s ?p \"test\\nvalue\" }";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        var plan = parsed.Explain(query.AsSpan());
        var json = plan.Format(ExplainFormat.Json);

        // Should escape backslash-n as \\n in JSON
        Assert.Contains("\\\\n", json);
    }

    #endregion

    #region Output Variables Tests

    [Fact]
    public void Explain_TriplePattern_ListsBoundVariables()
    {
        var query = "SELECT * WHERE { ?subject <http://ex.org/knows> ?object }";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        var plan = parsed.Explain(query.AsSpan());
        var text = plan.Format(ExplainFormat.Text);

        Assert.Contains("?subject", text);
        Assert.Contains("?object", text);
        Assert.Contains("binds:", text);
    }

    #endregion

    #region Extension Method Tests

    [Fact]
    public void Query_ExplainExtension_Works()
    {
        var query = "SELECT * WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        // Use extension method
        var plan = parsed.Explain(query.AsSpan());

        Assert.NotNull(plan);
        Assert.NotNull(plan.Root);
    }

    [Fact]
    public void Query_ExplainAnalyzeExtension_Works()
    {
        var query = "SELECT * WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        // Use extension method
        var plan = parsed.ExplainAnalyze(query.AsSpan(), Store);

        Assert.True(plan.IsAnalyzed);
    }

    #endregion
}
