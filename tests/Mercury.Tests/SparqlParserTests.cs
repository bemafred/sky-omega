using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Parsing;
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

    #region GRAPH Clause

    [Fact]
    public void Graph_ParsesGraphWithIri()
    {
        var query = "SELECT * WHERE { GRAPH <http://example.org/graph1> { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(1, result.WhereClause.Pattern.GraphClauseCount);
        var graphClause = result.WhereClause.Pattern.GetGraphClause(0);
        Assert.True(graphClause.Graph.IsIri);
        Assert.Equal("<http://example.org/graph1>", query.AsSpan().Slice(graphClause.Graph.Start, graphClause.Graph.Length).ToString());
        Assert.Equal(1, graphClause.PatternCount);
    }

    [Fact]
    public void Graph_ParsesGraphWithVariable()
    {
        var query = "SELECT * WHERE { GRAPH ?g { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(1, result.WhereClause.Pattern.GraphClauseCount);
        var graphClause = result.WhereClause.Pattern.GetGraphClause(0);
        Assert.True(graphClause.Graph.IsVariable);
        Assert.Equal("?g", query.AsSpan().Slice(graphClause.Graph.Start, graphClause.Graph.Length).ToString());
    }

    [Fact]
    public void Graph_ParsesMultiplePatterns()
    {
        var query = "SELECT * WHERE { GRAPH <http://example.org/g> { ?s ?p ?o . ?x ?y ?z } }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(1, result.WhereClause.Pattern.GraphClauseCount);
        var graphClause = result.WhereClause.Pattern.GetGraphClause(0);
        Assert.Equal(2, graphClause.PatternCount);
    }

    [Fact]
    public void Graph_ParsesMultipleGraphClauses()
    {
        var query = "SELECT * WHERE { GRAPH <http://ex.org/g1> { ?s ?p ?o } GRAPH <http://ex.org/g2> { ?x ?y ?z } }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(2, result.WhereClause.Pattern.GraphClauseCount);

        var g1 = result.WhereClause.Pattern.GetGraphClause(0);
        Assert.Equal("<http://ex.org/g1>", query.AsSpan().Slice(g1.Graph.Start, g1.Graph.Length).ToString());

        var g2 = result.WhereClause.Pattern.GetGraphClause(1);
        Assert.Equal("<http://ex.org/g2>", query.AsSpan().Slice(g2.Graph.Start, g2.Graph.Length).ToString());
    }

    [Fact]
    public void Graph_MixedWithDefaultGraphPatterns()
    {
        var query = "SELECT * WHERE { ?s ?p ?o . GRAPH <http://ex.org/g> { ?x ?y ?z } }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        // One pattern in default graph
        Assert.Equal(1, result.WhereClause.Pattern.PatternCount);

        // One GRAPH clause
        Assert.Equal(1, result.WhereClause.Pattern.GraphClauseCount);
        var graphClause = result.WhereClause.Pattern.GetGraphClause(0);
        Assert.Equal(1, graphClause.PatternCount);
    }

    [Fact]
    public void Graph_HasGraphProperty()
    {
        var query = "SELECT * WHERE { GRAPH <http://ex.org/g> { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.True(result.WhereClause.Pattern.HasGraph);
    }

    [Fact]
    public void Graph_NoGraphClause_HasGraphFalse()
    {
        var query = "SELECT * WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.False(result.WhereClause.Pattern.HasGraph);
        Assert.Equal(0, result.WhereClause.Pattern.GraphClauseCount);
    }

    #endregion

    #region FROM / FROM NAMED Clauses

    [Fact]
    public void From_ParsesSingleFromClause()
    {
        var query = "SELECT * FROM <http://example.org/graph1> WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(1, result.Datasets.Length);
        Assert.False(result.Datasets[0].IsNamed);
        Assert.Equal("<http://example.org/graph1>", query.AsSpan().Slice(result.Datasets[0].GraphIri.Start, result.Datasets[0].GraphIri.Length).ToString());
    }

    [Fact]
    public void From_ParsesMultipleFromClauses()
    {
        var query = "SELECT * FROM <http://example.org/g1> FROM <http://example.org/g2> WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(2, result.Datasets.Length);
        Assert.False(result.Datasets[0].IsNamed);
        Assert.False(result.Datasets[1].IsNamed);
        Assert.Equal("<http://example.org/g1>", query.AsSpan().Slice(result.Datasets[0].GraphIri.Start, result.Datasets[0].GraphIri.Length).ToString());
        Assert.Equal("<http://example.org/g2>", query.AsSpan().Slice(result.Datasets[1].GraphIri.Start, result.Datasets[1].GraphIri.Length).ToString());
    }

    [Fact]
    public void FromNamed_ParsesSingleFromNamedClause()
    {
        var query = "SELECT * FROM NAMED <http://example.org/named1> WHERE { GRAPH ?g { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(1, result.Datasets.Length);
        Assert.True(result.Datasets[0].IsNamed);
        Assert.Equal("<http://example.org/named1>", query.AsSpan().Slice(result.Datasets[0].GraphIri.Start, result.Datasets[0].GraphIri.Length).ToString());
    }

    [Fact]
    public void FromNamed_ParsesMultipleFromNamedClauses()
    {
        var query = "SELECT * FROM NAMED <http://ex.org/n1> FROM NAMED <http://ex.org/n2> WHERE { GRAPH ?g { ?s ?p ?o } }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(2, result.Datasets.Length);
        Assert.True(result.Datasets[0].IsNamed);
        Assert.True(result.Datasets[1].IsNamed);
    }

    [Fact]
    public void From_ParsesMixedFromAndFromNamed()
    {
        var query = "SELECT * FROM <http://ex.org/default> FROM NAMED <http://ex.org/named1> FROM NAMED <http://ex.org/named2> WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(3, result.Datasets.Length);
        Assert.False(result.Datasets[0].IsNamed);  // FROM
        Assert.True(result.Datasets[1].IsNamed);   // FROM NAMED
        Assert.True(result.Datasets[2].IsNamed);   // FROM NAMED
    }

    [Fact]
    public void From_NoFromClause_ReturnsEmptyDatasets()
    {
        var query = "SELECT * WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Empty(result.Datasets);
    }

    [Fact]
    public void From_WorksWithConstruct()
    {
        var query = "CONSTRUCT { ?s ?p ?o } FROM <http://ex.org/g> WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(QueryType.Construct, result.Type);
        Assert.Equal(1, result.Datasets.Length);
        Assert.False(result.Datasets[0].IsNamed);
    }

    [Fact]
    public void From_WorksWithAsk()
    {
        var query = "ASK FROM <http://ex.org/g> { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(QueryType.Ask, result.Type);
        Assert.Equal(1, result.Datasets.Length);
    }

    [Fact]
    public void From_WorksWithDescribe()
    {
        var query = "DESCRIBE * FROM <http://ex.org/g> WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(QueryType.Describe, result.Type);
        Assert.Equal(1, result.Datasets.Length);
    }

    #endregion

    #region SPARQL Update Parsing

    [Fact]
    public void InsertData_ParsesBasicInsert()
    {
        var update = "INSERT DATA { <http://ex.org/s> <http://ex.org/p> <http://ex.org/o> }";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.InsertData, result.Type);
        Assert.NotNull(result.InsertData);
        Assert.Single(result.InsertData);
    }

    [Fact]
    public void InsertData_ParsesMultipleTriples()
    {
        var update = @"INSERT DATA {
            <http://ex.org/s1> <http://ex.org/p> <http://ex.org/o1> .
            <http://ex.org/s2> <http://ex.org/p> <http://ex.org/o2>
        }";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.InsertData, result.Type);
        Assert.Equal(2, result.InsertData.Length);
    }

    [Fact]
    public void InsertData_ParsesWithGraph()
    {
        var update = @"INSERT DATA {
            GRAPH <http://ex.org/g1> {
                <http://ex.org/s> <http://ex.org/p> <http://ex.org/o>
            }
        }";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.InsertData, result.Type);
        Assert.Single(result.InsertData);
        Assert.True(result.InsertData[0].GraphLength > 0);
    }

    [Fact]
    public void DeleteData_ParsesBasicDelete()
    {
        var update = "DELETE DATA { <http://ex.org/s> <http://ex.org/p> <http://ex.org/o> }";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.DeleteData, result.Type);
        Assert.NotNull(result.DeleteData);
        Assert.Single(result.DeleteData);
    }

    [Fact]
    public void DeleteWhere_ParsesPattern()
    {
        var update = "DELETE WHERE { ?s <http://ex.org/p> ?o }";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.DeleteWhere, result.Type);
        Assert.True(result.DeleteTemplate.PatternCount > 0);
        Assert.True(result.WhereClause.Pattern.PatternCount > 0);
    }

    [Fact]
    public void Modify_ParsesDeleteInsertWhere()
    {
        var update = @"DELETE { ?s <http://ex.org/old> ?o }
                       INSERT { ?s <http://ex.org/new> ?o }
                       WHERE { ?s <http://ex.org/old> ?o }";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.Modify, result.Type);
        Assert.True(result.DeleteTemplate.PatternCount > 0);
        Assert.True(result.InsertTemplate.PatternCount > 0);
        Assert.True(result.WhereClause.Pattern.PatternCount > 0);
    }

    [Fact]
    public void Modify_ParsesWithGraph()
    {
        var update = @"WITH <http://ex.org/g1>
                       DELETE { ?s <http://ex.org/old> ?o }
                       INSERT { ?s <http://ex.org/new> ?o }
                       WHERE { ?s <http://ex.org/old> ?o }";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.Modify, result.Type);
        Assert.Equal(GraphTargetType.Graph, result.SourceGraph.Type);
    }

    [Fact]
    public void Load_ParsesBasicLoad()
    {
        var update = "LOAD <http://ex.org/data.ttl>";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.Load, result.Type);
        Assert.True(result.SourceUriLength > 0);
        Assert.False(result.Silent);
    }

    [Fact]
    public void Load_ParsesSilentIntoGraph()
    {
        var update = "LOAD SILENT <http://ex.org/data.ttl> INTO GRAPH <http://ex.org/g1>";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.Load, result.Type);
        Assert.True(result.Silent);
        Assert.Equal(GraphTargetType.Graph, result.DestinationGraph.Type);
    }

    [Fact]
    public void Clear_ParsesDefault()
    {
        var update = "CLEAR DEFAULT";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.Clear, result.Type);
        Assert.Equal(GraphTargetType.Default, result.DestinationGraph.Type);
    }

    [Fact]
    public void Clear_ParsesNamedGraph()
    {
        var update = "CLEAR GRAPH <http://ex.org/g1>";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.Clear, result.Type);
        Assert.Equal(GraphTargetType.Graph, result.DestinationGraph.Type);
    }

    [Fact]
    public void Clear_ParsesAll()
    {
        var update = "CLEAR ALL";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.Clear, result.Type);
        Assert.Equal(GraphTargetType.All, result.DestinationGraph.Type);
    }

    [Fact]
    public void Clear_ParsesSilent()
    {
        var update = "CLEAR SILENT NAMED";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.Clear, result.Type);
        Assert.True(result.Silent);
        Assert.Equal(GraphTargetType.Named, result.DestinationGraph.Type);
    }

    [Fact]
    public void Create_ParsesGraph()
    {
        var update = "CREATE GRAPH <http://ex.org/newgraph>";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.Create, result.Type);
        Assert.Equal(GraphTargetType.Graph, result.DestinationGraph.Type);
    }

    [Fact]
    public void Create_ParsesSilent()
    {
        var update = "CREATE SILENT GRAPH <http://ex.org/newgraph>";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.Create, result.Type);
        Assert.True(result.Silent);
    }

    [Fact]
    public void Drop_ParsesGraph()
    {
        var update = "DROP GRAPH <http://ex.org/oldgraph>";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.Drop, result.Type);
        Assert.Equal(GraphTargetType.Graph, result.DestinationGraph.Type);
    }

    [Fact]
    public void Drop_ParsesAll()
    {
        var update = "DROP ALL";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.Drop, result.Type);
        Assert.Equal(GraphTargetType.All, result.DestinationGraph.Type);
    }

    [Fact]
    public void Copy_ParsesSourceToDestination()
    {
        var update = "COPY <http://ex.org/src> TO <http://ex.org/dst>";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.Copy, result.Type);
        Assert.Equal(GraphTargetType.Graph, result.SourceGraph.Type);
        Assert.Equal(GraphTargetType.Graph, result.DestinationGraph.Type);
    }

    [Fact]
    public void Copy_ParsesDefaultToGraph()
    {
        var update = "COPY DEFAULT TO <http://ex.org/dst>";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.Copy, result.Type);
        Assert.Equal(GraphTargetType.Default, result.SourceGraph.Type);
        Assert.Equal(GraphTargetType.Graph, result.DestinationGraph.Type);
    }

    [Fact]
    public void Move_ParsesGraphToDefault()
    {
        var update = "MOVE <http://ex.org/src> TO DEFAULT";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.Move, result.Type);
        Assert.Equal(GraphTargetType.Graph, result.SourceGraph.Type);
        Assert.Equal(GraphTargetType.Default, result.DestinationGraph.Type);
    }

    [Fact]
    public void Move_ParsesSilent()
    {
        var update = "MOVE SILENT <http://ex.org/src> TO <http://ex.org/dst>";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.Move, result.Type);
        Assert.True(result.Silent);
    }

    [Fact]
    public void Add_ParsesSourceToDestination()
    {
        var update = "ADD <http://ex.org/src> TO <http://ex.org/dst>";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.Add, result.Type);
        Assert.Equal(GraphTargetType.Graph, result.SourceGraph.Type);
        Assert.Equal(GraphTargetType.Graph, result.DestinationGraph.Type);
    }

    [Fact]
    public void InsertData_ParsesWithPrefixes()
    {
        var update = @"PREFIX ex: <http://ex.org/>
                       INSERT DATA { ex:s ex:p ex:o }";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.InsertData, result.Type);
        Assert.Single(result.InsertData);
    }

    [Fact]
    public void InsertData_ParsesLiterals()
    {
        var update = @"INSERT DATA { <http://ex.org/s> <http://ex.org/name> ""John"" }";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.InsertData, result.Type);
        Assert.Single(result.InsertData);
        Assert.Equal(TermType.Literal, result.InsertData[0].ObjectType);
    }

    [Fact]
    public void InsertData_ParsesBlankNodes()
    {
        var update = @"INSERT DATA { _:b1 <http://ex.org/p> <http://ex.org/o> }";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.InsertData, result.Type);
        Assert.Single(result.InsertData);
        Assert.Equal(TermType.BlankNode, result.InsertData[0].SubjectType);
    }

    #endregion
}
