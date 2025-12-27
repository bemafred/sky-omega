using SkyOmega.Mercury.Sparql;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for SPARQL parser
/// </summary>
public class SparqlParserTests
{
    [Fact]
    public void BasicSelect_ParsesSelectAllQuery()
    {
        var query = "SELECT * WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(QueryType.Select, result.Type);
        Assert.True(result.SelectClause.SelectAll);
    }

    [Fact]
    public void SelectDistinct_SetsDistinctFlag()
    {
        var query = "SELECT DISTINCT ?x WHERE { ?x ?y ?z }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(QueryType.Select, result.Type);
        Assert.True(result.SelectClause.Distinct);
        Assert.False(result.SelectClause.SelectAll);
    }

    [Fact]
    public void PrefixDeclaration_ParsesWithPrefix()
    {
        var query = "PREFIX foaf: <http://xmlns.com/foaf/0.1/> SELECT * WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(QueryType.Select, result.Type);
    }

    [Fact]
    public void Construct_SetsQueryType()
    {
        var query = "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(QueryType.Construct, result.Type);
    }

    [Fact]
    public void Ask_SetsQueryType()
    {
        var query = "ASK { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(QueryType.Ask, result.Type);
    }
}
