using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Rdf.Turtle;
using Xunit;
using Xunit.Abstractions;
using SkyOmega.Mercury.Sparql.Execution.Operators;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// Tests for subquery aggregate support.
/// </summary>
public class SubqueryAggregateTests
{
    private readonly ITestOutputHelper _output;

    public SubqueryAggregateTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void SubSelect_WithGroupConcat_ParsesAggregate()
    {
        // Test that parsing correctly extracts aggregate info
        var query = @"PREFIX : <http://www.example.org/>
SELECT ?g WHERE {
    {SELECT (GROUP_CONCAT(?o) AS ?g) WHERE { [] :p1 ?o }}
}";

        var parser = new SparqlParser(query);
        var parsed = parser.ParseQuery();

        _output.WriteLine($"SubQueryCount: {parsed.WhereClause.Pattern.SubQueryCount}");
        Assert.True(parsed.WhereClause.Pattern.SubQueryCount > 0, "Should have a subquery");

        var subSelect = parsed.WhereClause.Pattern.GetSubQuery(0);
        _output.WriteLine($"HasAggregates: {subSelect.HasAggregates}");
        _output.WriteLine($"AggregateCount: {subSelect.AggregateCount}");

        Assert.True(subSelect.HasAggregates, "Subquery should have aggregates");
        Assert.Equal(1, subSelect.AggregateCount);

        var agg = subSelect.GetAggregate(0);
        _output.WriteLine($"Function: {agg.Function}");
        _output.WriteLine($"AliasStart: {agg.AliasStart}, AliasLength: {agg.AliasLength}");
        _output.WriteLine($"VariableStart: {agg.VariableStart}, VariableLength: {agg.VariableLength}");

        Assert.Equal(AggregateFunction.GroupConcat, agg.Function);
        Assert.True(agg.AliasLength > 0, "Should have alias");

        var alias = query.Substring(agg.AliasStart, agg.AliasLength);
        _output.WriteLine($"Alias: {alias}");
        Assert.Equal("?g", alias);

        var varName = query.Substring(agg.VariableStart, agg.VariableLength);
        _output.WriteLine($"Variable: {varName}");
        Assert.Equal("?o", varName);
    }

}
