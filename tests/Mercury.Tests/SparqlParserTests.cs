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

    #region WHERE Clause Parsing

    [Fact]
    public void WhereClause_ParsesSingleTriplePattern()
    {
        var query = "SELECT * WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(1, result.WhereClause.Pattern.PatternCount);
        var pattern = result.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.Subject.IsVariable);
        Assert.True(pattern.Predicate.IsVariable);
        Assert.True(pattern.Object.IsVariable);
    }

    [Fact]
    public void WhereClause_ParsesMultipleTriplePatterns()
    {
        var query = "SELECT * WHERE { ?s ?p ?o . ?x ?y ?z }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(2, result.WhereClause.Pattern.PatternCount);
    }

    [Fact]
    public void WhereClause_ParsesIriSubject()
    {
        var query = "SELECT * WHERE { <http://example.org/Alice> ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        var pattern = result.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.Subject.IsIri);
        // IRI includes angle brackets for matching against stored IRIs
        Assert.Equal("<http://example.org/Alice>", query.AsSpan().Slice(pattern.Subject.Start, pattern.Subject.Length).ToString());
    }

    [Fact]
    public void WhereClause_ParsesPrefixedName()
    {
        var query = "SELECT * WHERE { ?s foaf:name ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        var pattern = result.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.Predicate.IsIri);
        Assert.Equal("foaf:name", query.AsSpan().Slice(pattern.Predicate.Start, pattern.Predicate.Length).ToString());
    }

    [Fact]
    public void WhereClause_ParsesStringLiteral()
    {
        var query = "SELECT * WHERE { ?s ?p \"hello\" }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        var pattern = result.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.Object.IsLiteral);
        Assert.Equal("\"hello\"", query.AsSpan().Slice(pattern.Object.Start, pattern.Object.Length).ToString());
    }

    [Fact]
    public void WhereClause_ParsesNumericLiteral()
    {
        var query = "SELECT * WHERE { ?s ?p 42 }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        var pattern = result.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.Object.IsLiteral);
        Assert.Equal("42", query.AsSpan().Slice(pattern.Object.Start, pattern.Object.Length).ToString());
    }

    [Fact]
    public void WhereClause_ParsesFilter()
    {
        var query = "SELECT * WHERE { ?s ?p ?o FILTER(?o > 10) }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(1, result.WhereClause.Pattern.PatternCount);
        Assert.Equal(1, result.WhereClause.Pattern.FilterCount);

        var filter = result.WhereClause.Pattern.GetFilter(0);
        Assert.Equal("?o > 10", query.AsSpan().Slice(filter.Start, filter.Length).ToString());
    }

    [Fact]
    public void WhereClause_ParsesMultipleFilters()
    {
        var query = "SELECT * WHERE { ?s ?p ?o FILTER(?o > 10) FILTER(?o < 100) }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(2, result.WhereClause.Pattern.FilterCount);
    }

    [Fact]
    public void WhereClause_ParsesTypeShorthand()
    {
        var query = "SELECT * WHERE { ?s a ?type }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        var pattern = result.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.Predicate.IsIri);
        Assert.Equal("a", query.AsSpan().Slice(pattern.Predicate.Start, pattern.Predicate.Length).ToString());
    }

    [Fact]
    public void WhereClause_ParsesBlankNode()
    {
        var query = "SELECT * WHERE { _:b1 ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        var pattern = result.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.Subject.IsBlankNode);
    }

    [Fact]
    public void WhereClause_ParsesLangTag()
    {
        var query = "SELECT * WHERE { ?s ?p \"hello\"@en }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        var pattern = result.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.Object.IsLiteral);
        Assert.Equal("\"hello\"@en", query.AsSpan().Slice(pattern.Object.Start, pattern.Object.Length).ToString());
    }

    [Fact]
    public void WhereClause_ParsesDatatype()
    {
        var query = "SELECT * WHERE { ?s ?p \"42\"^^<http://www.w3.org/2001/XMLSchema#integer> }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        var pattern = result.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.Object.IsLiteral);
    }

    [Fact]
    public void WhereClause_ParsesComplexQuery()
    {
        // Test without prefix to simplify
        var query = "SELECT * WHERE { ?person <http://foaf/name> ?name . ?person <http://foaf/age> ?age . FILTER(?age > 18) } LIMIT 10";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(QueryType.Select, result.Type);
        Assert.Equal(2, result.WhereClause.Pattern.PatternCount);
        Assert.Equal(1, result.WhereClause.Pattern.FilterCount);
        Assert.Equal(10, result.SolutionModifier.Limit);
    }

    #endregion

    #region Property Paths

    [Fact]
    public void PropertyPath_ParsesInversePath()
    {
        var query = "SELECT * WHERE { ?child ^<http://example/parent> ?parent }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        var pattern = result.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.HasPropertyPath);
        Assert.Equal(PathType.Inverse, pattern.Path.Type);
    }

    [Fact]
    public void PropertyPath_ParsesZeroOrMore()
    {
        var query = "SELECT * WHERE { ?s <http://example/knows>* ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        var pattern = result.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.HasPropertyPath);
        Assert.Equal(PathType.ZeroOrMore, pattern.Path.Type);
    }

    [Fact]
    public void PropertyPath_ParsesOneOrMore()
    {
        var query = "SELECT * WHERE { ?s <http://example/knows>+ ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        var pattern = result.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.HasPropertyPath);
        Assert.Equal(PathType.OneOrMore, pattern.Path.Type);
    }

    [Fact]
    public void PropertyPath_ParsesZeroOrOne()
    {
        var query = "SELECT * WHERE { ?s <http://example/knows>? ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        var pattern = result.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.HasPropertyPath);
        Assert.Equal(PathType.ZeroOrOne, pattern.Path.Type);
    }

    [Fact]
    public void PropertyPath_ParsesSequence()
    {
        var query = "SELECT * WHERE { ?s <http://example/a>/<http://example/b> ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        var pattern = result.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.HasPropertyPath);
        Assert.Equal(PathType.Sequence, pattern.Path.Type);
    }

    [Fact]
    public void PropertyPath_ParsesAlternative()
    {
        var query = "SELECT * WHERE { ?s <http://example/a>|<http://example/b> ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        var pattern = result.WhereClause.Pattern.GetPattern(0);
        Assert.True(pattern.HasPropertyPath);
        Assert.Equal(PathType.Alternative, pattern.Path.Type);
    }

    [Fact]
    public void PropertyPath_SimplePredicateHasNoPath()
    {
        var query = "SELECT * WHERE { ?s <http://example/p> ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        var pattern = result.WhereClause.Pattern.GetPattern(0);
        Assert.False(pattern.HasPropertyPath);
        Assert.Equal(PathType.None, pattern.Path.Type);
    }

    #endregion

    #region DESCRIBE Queries

    [Fact]
    public void Describe_ParsesDescribeAll()
    {
        var query = "DESCRIBE * WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(QueryType.Describe, result.Type);
        Assert.True(result.DescribeAll);
        Assert.Equal(1, result.WhereClause.Pattern.PatternCount);
    }

    [Fact]
    public void Describe_WithoutWhere()
    {
        var query = "DESCRIBE *";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(QueryType.Describe, result.Type);
        Assert.True(result.DescribeAll);
        Assert.Equal(0, result.WhereClause.Pattern.PatternCount);
    }

    [Fact]
    public void Describe_WithWhereClause()
    {
        var query = "DESCRIBE * WHERE { ?person <http://foaf/name> \"Alice\" }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(QueryType.Describe, result.Type);
        Assert.Equal(1, result.WhereClause.Pattern.PatternCount);
    }

    #endregion
}
