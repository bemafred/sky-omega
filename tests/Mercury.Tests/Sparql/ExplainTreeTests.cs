using System;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-055 D6: EXPLAIN describes the pattern tree that executes, one operator per tree node kind. The flat parse it
/// read before drew every plan with LIMIT 0, put an OPTIONAL's FILTER at the outer level, left out FILTER EXISTS, drew
/// GRAPH, MINUS and sub-SELECT without their contents, and drew a property path as a join with "?" for its middle term.
/// Each test explains one query (no store) and reads the text plan; nesting is checked by indentation.
/// </summary>
public class ExplainTreeTests
{
    [Fact]
    public void Triple_IsAScan_PathIsOnePathScan()
    {
        var plan = SparqlEngine.Explain("SELECT * WHERE { ?s <http://ex/a> ?x . ?x <http://ex/b>/<http://ex/c>* ?o }");

        Assert.Contains("⋈ Join 2 patterns", plan);
        Assert.Contains("⊳ ?s <http://ex/a> ?x", plan);
        Assert.Contains("⇝ ?x <http://ex/b>/<http://ex/c>* ?o", plan);
        Assert.DoesNotContain(" ? ", plan);
    }

    [Fact]
    public void Optional_HoldsItsGroup_FilterInsideIt()
    {
        var plan = SparqlEngine.Explain("SELECT * WHERE { ?s <http://ex/a> ?o OPTIONAL { ?o <http://ex/b> ?x FILTER(?x > 1) } }");
        var lines = plan.Split('\n');

        int optional = Array.FindIndex(lines, l => l.Contains("⟕ OPTIONAL"));
        int filter = Array.FindIndex(lines, l => l.Contains("σ FILTER(?x > 1)"));
        Assert.True(optional >= 0 && filter > optional, plan);
        Assert.True(lines[filter].IndexOf('σ') > lines[optional].IndexOf('⟕'), plan);
    }

    [Fact]
    public void Union_HasOneChildPerBranch_IncludingABindOnlyBranch()
    {
        var plan = SparqlEngine.Explain("SELECT * WHERE { { ?s <http://ex/a> ?o } UNION { BIND(1 AS ?x) } }");

        Assert.Contains("∪ Alternative patterns", plan);
        Assert.Contains("⊳ ?s <http://ex/a> ?o", plan);
        Assert.Contains("← BIND(1) AS ?x", plan);
    }

    [Fact]
    public void Minus_AndExists_AndNotExists_ShowTheirGroups()
    {
        var plan = SparqlEngine.Explain(
            "SELECT * WHERE { ?s <http://ex/a> ?o MINUS { ?s <http://ex/m> ?o } FILTER EXISTS { ?o <http://ex/e> ?y } FILTER NOT EXISTS { ?o <http://ex/n> ?z } }");

        Assert.Contains("− Anti-join (MINUS)", plan);
        Assert.Contains("⊳ ?s <http://ex/m> ?o", plan);
        Assert.Contains("∃ FILTER EXISTS", plan);
        Assert.Contains("⊳ ?o <http://ex/e> ?y", plan);
        Assert.Contains("∄ FILTER NOT EXISTS", plan);
        Assert.Contains("⊳ ?o <http://ex/n> ?z", plan);
    }

    [Theory]
    [InlineData("SELECT * WHERE { GRAPH ?g { ?s <http://ex/a> ?o } }", "▢ GRAPH ?g")]
    [InlineData("SELECT * WHERE { GRAPH <http://ex/g> { ?s <http://ex/a> ?o } }", "▣ GRAPH <http://ex/g>")]
    public void Graph_ShowsItsTermAndItsPatterns(string query, string header)
    {
        var plan = SparqlEngine.Explain(query);
        var lines = plan.Split('\n');

        int graph = Array.FindIndex(lines, l => l.Contains(header));
        int scan = Array.FindIndex(lines, l => l.Contains("⊳ ?s <http://ex/a> ?o"));
        Assert.True(graph >= 0 && scan > graph && lines[scan].IndexOf('⊳') > lines[graph].IndexOf(header[0]), plan);
    }

    [Fact]
    public void Filter_IsDrawnLastInItsGroup()
    {
        var plan = SparqlEngine.Explain("SELECT * WHERE { FILTER(?o != 1) ?s <http://ex/a> ?o . ?o <http://ex/b> ?x }");
        var lines = plan.Split('\n');

        int filter = Array.FindIndex(lines, l => l.Contains("σ FILTER(?o != 1)"));
        int lastScan = Array.FindLastIndex(lines, l => l.Contains("⊳ "));
        Assert.True(filter > lastScan, plan);
    }

    [Fact]
    public void Values_AndBind_AreSteps()
    {
        var plan = SparqlEngine.Explain("SELECT * WHERE { VALUES ?s { <http://ex/x> } ?s <http://ex/a> ?o BIND(STR(?o) AS ?t) }");

        Assert.Contains("≡ Inline data: VALUES ?s { <http://ex/x> }", plan);
        Assert.Contains("← BIND(STR(?o)) AS ?t", plan);
        Assert.Contains("→ binds: ?t", plan);
    }

    [Fact]
    public void SubSelect_ShowsItsOwnModifiersAndWhere()
    {
        var plan = SparqlEngine.Explain("SELECT * WHERE { ?s <http://ex/a> ?o { SELECT ?o WHERE { ?o <http://ex/b> ?x } ORDER BY ?x LIMIT 1 } }");
        var lines = plan.Split('\n');

        int sub = Array.FindIndex(lines, l => l.Contains("⊂ Nested SELECT"));
        int slice = Array.FindIndex(lines, l => l.Contains("⌊ LIMIT 1"));
        int sort = Array.FindIndex(lines, l => l.Contains("↑ Sort by 1 expression(s)"));
        int scan = Array.FindIndex(lines, l => l.Contains("⊳ ?o <http://ex/b> ?x"));
        Assert.True(sub >= 0 && slice > sub && sort > slice && scan > sort, plan);
    }

    [Fact]
    public void Service_ShowsItsEndpoint()
    {
        var plan = SparqlEngine.Explain("SELECT * WHERE { SERVICE SILENT <http://ex/sparql> { ?s ?p ?o } }");

        Assert.Contains("⇄ SERVICE SILENT <http://ex/sparql>", plan);
    }

    [Fact]
    public void Text_KeepsTheLinesOfTheLevelsAbove()
    {
        // The OPTIONAL is not the last child of the join, so the lines under it carry the join's '│'.
        var plan = SparqlEngine.Explain("SELECT * WHERE { ?s <http://ex/a> ?o OPTIONAL { ?o <http://ex/b> ?x } ?o <http://ex/c> ?y }");
        var lines = plan.Split('\n');

        int inner = Array.FindIndex(lines, l => l.Contains("⊳ ?o <http://ex/b> ?x"));
        Assert.True(inner >= 0, plan);
        Assert.StartsWith("     │  ", lines[inner]);
    }

    [Fact]
    public void Json_NamesTheNewOperators()
    {
        var query = "SELECT * WHERE { ?s <http://ex/a>* ?o FILTER EXISTS { ?s ?p ?o } FILTER NOT EXISTS { ?o ?p ?s } }";
        var parsed = new SparqlParser(query.AsSpan()).ParseQuery();

        var json = parsed.Explain(query.AsSpan()).Format(ExplainFormat.Json);

        Assert.Contains("\"operator\": \"PathScan\"", json);
        Assert.Contains("\"operator\": \"Exists\"", json);
        Assert.Contains("\"operator\": \"NotExists\"", json);
    }
}
