using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Parsing;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

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

    #endregion

    #region Property Paths

    #endregion

    #region DESCRIBE Queries

    #endregion

    #region GRAPH Clause

    #endregion

    #region FROM / FROM NAMED Clauses

    [Fact]
    public void From_ParsesSingleFromClause()
    {
        var query = "SELECT * FROM <http://example.org/graph1> WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Single(result.Datasets);
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

        Assert.Single(result.Datasets);
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
        Assert.Single(result.Datasets);
        Assert.False(result.Datasets[0].IsNamed);
    }

    [Fact]
    public void From_WorksWithAsk()
    {
        var query = "ASK FROM <http://ex.org/g> { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(QueryType.Ask, result.Type);
        Assert.Single(result.Datasets);
    }

    [Fact]
    public void From_WorksWithDescribe()
    {
        var query = "DESCRIBE * FROM <http://ex.org/g> WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        Assert.Equal(QueryType.Describe, result.Type);
        Assert.Single(result.Datasets);
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
        Assert.True(result.InsertData[0].InGraphBlock);
    }

    // Regression: prefixed-datatype literal followed by ';' in a PropertyListNotEmpty
    // chain inside INSERT DATA. Legal SPARQL 1.1 Update grammar (productions [37, 47,
    // 50, 51, 52, 129, 136, 137]), but not exercised by the W3C sparql11-update
    // conformance suite, and the parser mishandles it (discovered 2026-04-17 while
    // storing session observations in Mercury). Full-IRI datatype works — only the
    // PrefixedName form in the '^^' iri slot triggers the bug.

    [Fact]
    public void InsertData_PrefixedDatatypeFollowedBySemicolon_DefaultGraph()
    {
        var update = @"PREFIX ex: <http://example.org/>
                       PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>
                       INSERT DATA {
                         ex:s ex:date ""2026-04-17""^^xsd:date ;
                              ex:topic ""first"" .
                       }";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.InsertData, result.Type);
        Assert.Equal(2, result.InsertData.Length);
    }

    [Fact]
    public void InsertData_PrefixedDatatypeFollowedBySemicolon_InGraph()
    {
        var update = @"PREFIX ex: <http://example.org/>
                       PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>
                       INSERT DATA {
                         GRAPH <urn:test:g> {
                           ex:s ex:date ""2026-04-17""^^xsd:date ;
                                ex:topic ""first"" .
                         }
                       }";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.InsertData, result.Type);
        Assert.Equal(2, result.InsertData.Length);
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
        Assert.True(result.DeleteTemplate.Length > 0);
        Assert.Equal(update.IndexOf('{'), result.WhereGroupStart);
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
        Assert.True(result.DeleteTemplate.Length > 0);
        Assert.True(result.InsertTemplate.Length > 0);
        Assert.Equal(update.LastIndexOf('{'), result.WhereGroupStart);
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
        Assert.True(result.WithGraphLength > 0);
        var withGraph = update.AsSpan(result.WithGraphStart, result.WithGraphLength);
        Assert.True(withGraph.SequenceEqual("<http://ex.org/g1>".AsSpan()));
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
        Assert.Equal(TermType.Literal, result.InsertData[0].Object.Type);
    }

    [Fact]
    public void InsertData_ParsesBlankNodes()
    {
        var update = @"INSERT DATA { _:b1 <http://ex.org/p> <http://ex.org/o> }";
        var parser = new SparqlParser(update.AsSpan());
        var result = parser.ParseUpdate();

        Assert.Equal(QueryType.InsertData, result.Type);
        Assert.Single(result.InsertData);
        Assert.Equal(TermType.BlankNode, result.InsertData[0].Subject.Type);
    }

    #endregion

    #region Embedded Aggregate Extraction

    [Fact]
    public void EmbeddedAggregates_ExtractsMinMaxFromExpression()
    {
        // This tests that embedded aggregates like MIN(?p) and MAX(?p) inside an expression
        // are extracted and tracked separately for computation
        var query = @"PREFIX : <http://example.com/data/#>
SELECT ?g (AVG(?p) AS ?avg) ((MIN(?p) + MAX(?p)) / 2 AS ?c)
WHERE { ?g :p ?p }
GROUP BY ?g";

        var parser = new SparqlParser(query.AsSpan());
        var result = parser.ParseQuery();

        // Should have 4 aggregates:
        // 1. AVG(?p) AS ?avg
        // 2. (MIN(?p) + MAX(?p)) / 2 AS ?c (the expression itself, Function=None)
        // 3. MIN(?p) - extracted hidden aggregate
        // 4. MAX(?p) - extracted hidden aggregate
        Assert.True(result.SelectClause.AggregateCount >= 4,
            $"Expected at least 4 aggregates, got {result.SelectClause.AggregateCount}");

        // Verify we have AVG, MIN, MAX tracked
        bool hasAvg = false, hasMin = false, hasMax = false;
        for (int i = 0; i < result.SelectClause.AggregateCount; i++)
        {
            var agg = result.SelectClause.GetAggregate(i);
            if (agg.Function == AggregateFunction.Avg) hasAvg = true;
            if (agg.Function == AggregateFunction.Min) hasMin = true;
            if (agg.Function == AggregateFunction.Max) hasMax = true;
        }

        Assert.True(hasAvg, "Missing AVG aggregate");
        Assert.True(hasMin, "Missing MIN aggregate (should be extracted from expression)");
        Assert.True(hasMax, "Missing MAX aggregate (should be extracted from expression)");
    }

    #endregion
}
