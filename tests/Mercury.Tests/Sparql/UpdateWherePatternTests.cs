using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-052: an Update's WHERE is a GroupGraphPattern, parsed and evaluated exactly like a query's.
/// Every test drives the facade production uses (<c>SparqlEngine.Update</c> then <c>SparqlEngine.Query</c>) and
/// asserts the triples present afterwards, not only the affected count. Objects are IRIs where a value is
/// compared, so numeric-literal typing (the ADR-053 axis) cannot confound the result.
/// </summary>
[Collection("QuadStore")]
public class UpdateWherePatternTests : PooledStoreTestBase
{
    public UpdateWherePatternTests(QuadStorePoolFixture fixture) : base(fixture) { }

    // ── The six defects in ADR-052's table ────────────────────────────────────────────────────────────

    [Fact]
    public void Insert_WhereIsOnlyValues_InsertsOneTriplePerValue()
    {
        var result = SparqlEngine.Update(Store,
            "INSERT { <http://ex/s> <http://ex/p> ?o } WHERE { VALUES ?o { \"a\" \"b\" } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.AffectedCount);
        Assert.True(SparqlEngine.Query(Store, "ASK { <http://ex/s> <http://ex/p> \"a\" }", TestContext.Current.CancellationToken).AskResult);
        Assert.True(SparqlEngine.Query(Store, "ASK { <http://ex/s> <http://ex/p> \"b\" }", TestContext.Current.CancellationToken).AskResult);
    }

    [Fact]
    public void Insert_WhereIsOnlyBind_InsertsTheBoundValue()
    {
        var result = SparqlEngine.Update(Store,
            "INSERT { <http://ex/s> <http://ex/p> ?x } WHERE { BIND(\"b\" AS ?x) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.AffectedCount);
        Assert.True(SparqlEngine.Query(Store, "ASK { <http://ex/s> <http://ex/p> \"b\" }", TestContext.Current.CancellationToken).AskResult);
    }

    [Fact]
    public void Insert_WhereHasOptionalGraph_ParsesAndInserts()
    {
        Store.AddCurrent("<http://ex/s>", "<http://ex/q>", "<http://ex/v>");

        var result = SparqlEngine.Update(Store,
            "INSERT { <http://ex/s> <http://ex/r> ?v } WHERE { <http://ex/s> <http://ex/q> ?v OPTIONAL { GRAPH <http://ex/g> { <http://ex/s> <http://ex/z> ?w } } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.AffectedCount);
        Assert.True(SparqlEngine.Query(Store, "ASK { <http://ex/s> <http://ex/r> <http://ex/v> }", TestContext.Current.CancellationToken).AskResult);
    }

    [Fact]
    public void DeleteWhere_GraphWithTwoPatterns_DeletesOnlyTheJoinedTriples()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/va>", "<http://ex/g>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/k>", "<http://ex/one>", "<http://ex/g>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/vb>", "<http://ex/g>");

        var result = SparqlEngine.Update(Store,
            "DELETE WHERE { GRAPH <http://ex/g> { ?s <http://ex/p> ?v . ?s <http://ex/k> <http://ex/one> } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.AffectedCount);
        Assert.False(SparqlEngine.Query(Store, "ASK { GRAPH <http://ex/g> { <http://ex/a> <http://ex/p> <http://ex/va> } }", TestContext.Current.CancellationToken).AskResult);
        Assert.False(SparqlEngine.Query(Store, "ASK { GRAPH <http://ex/g> { <http://ex/a> <http://ex/k> <http://ex/one> } }", TestContext.Current.CancellationToken).AskResult);
        // <b> has no <k> <one>, so the request does not match it and it must survive.
        Assert.True(SparqlEngine.Query(Store, "ASK { GRAPH <http://ex/g> { <http://ex/b> <http://ex/p> <http://ex/vb> } }", TestContext.Current.CancellationToken).AskResult);
    }

    [Fact]
    public void With_SingleTripleAndFilter_DeletesOnlyTheFilteredTriple()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/one>", "<http://ex/g>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/two>", "<http://ex/g>");

        var result = SparqlEngine.Update(Store,
            "WITH <http://ex/g> DELETE { ?s <http://ex/p> ?v } WHERE { ?s <http://ex/p> ?v FILTER(?v = <http://ex/one>) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.AffectedCount);
        Assert.False(SparqlEngine.Query(Store, "ASK { GRAPH <http://ex/g> { <http://ex/a> <http://ex/p> <http://ex/one> } }", TestContext.Current.CancellationToken).AskResult);
        Assert.True(SparqlEngine.Query(Store, "ASK { GRAPH <http://ex/g> { <http://ex/b> <http://ex/p> <http://ex/two> } }", TestContext.Current.CancellationToken).AskResult);
    }

    [Fact]
    public void With_SeveralTriples_GraphTemplate_InsertsIntoTheTemplateGraph()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/va>", "<http://ex/g>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/k>", "<http://ex/one>", "<http://ex/g>");

        var result = SparqlEngine.Update(Store,
            "WITH <http://ex/g> INSERT { GRAPH <http://ex/h> { ?s <http://ex/c> ?v } } WHERE { ?s <http://ex/p> ?v . ?s <http://ex/k> <http://ex/one> }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.AffectedCount);
        Assert.True(SparqlEngine.Query(Store, "ASK { GRAPH <http://ex/h> { <http://ex/a> <http://ex/c> <http://ex/va> } }", TestContext.Current.CancellationToken).AskResult);
        Assert.False(SparqlEngine.Query(Store, "ASK { GRAPH <http://ex/g> { <http://ex/a> <http://ex/c> <http://ex/va> } }", TestContext.Current.CancellationToken).AskResult);
    }

    // ── Group shapes a query supports, now in an Update WHERE ─────────────────────────────────────────

    [Fact]
    public void Insert_EmptyWhere_InstantiatesTheTemplateOnce()
    {
        var result = SparqlEngine.Update(Store,
            "INSERT { <http://ex/s> <http://ex/p> <http://ex/o> } WHERE { }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.AffectedCount);
        Assert.True(SparqlEngine.Query(Store, "ASK { <http://ex/s> <http://ex/p> <http://ex/o> }", TestContext.Current.CancellationToken).AskResult);
    }

    [Fact]
    public void Delete_WhereHasMinus_KeepsTheExcludedTriple()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/va>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/vb>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/k>", "<http://ex/one>");

        var result = SparqlEngine.Update(Store,
            "DELETE { ?s <http://ex/p> ?v } WHERE { ?s <http://ex/p> ?v MINUS { ?s <http://ex/k> <http://ex/one> } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.AffectedCount);
        Assert.False(SparqlEngine.Query(Store, "ASK { <http://ex/a> <http://ex/p> <http://ex/va> }", TestContext.Current.CancellationToken).AskResult);
        Assert.True(SparqlEngine.Query(Store, "ASK { <http://ex/b> <http://ex/p> <http://ex/vb> }", TestContext.Current.CancellationToken).AskResult);
    }

    [Fact]
    public void Insert_WhereHasUnion_InsertsForEachBranch()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/x>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/q>", "<http://ex/y>");

        var result = SparqlEngine.Update(Store,
            "INSERT { ?s <http://ex/seen> <http://ex/yes> } WHERE { { ?s <http://ex/p> ?o } UNION { ?s <http://ex/q> ?o } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.AffectedCount);
        Assert.True(SparqlEngine.Query(Store, "ASK { <http://ex/a> <http://ex/seen> <http://ex/yes> }", TestContext.Current.CancellationToken).AskResult);
        Assert.True(SparqlEngine.Query(Store, "ASK { <http://ex/b> <http://ex/seen> <http://ex/yes> }", TestContext.Current.CancellationToken).AskResult);
    }

    [Fact]
    public void Insert_WhereHasNotExists_InsertsOnlyWhereAbsent()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/type>", "<http://ex/T>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/type>", "<http://ex/T>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/status>", "<http://ex/done>");

        var result = SparqlEngine.Update(Store,
            "INSERT { ?s <http://ex/status> <http://ex/open> } WHERE { ?s <http://ex/type> <http://ex/T> FILTER NOT EXISTS { ?s <http://ex/status> ?any } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.AffectedCount);
        Assert.True(SparqlEngine.Query(Store, "ASK { <http://ex/a> <http://ex/status> <http://ex/open> }", TestContext.Current.CancellationToken).AskResult);
        Assert.False(SparqlEngine.Query(Store, "ASK { <http://ex/b> <http://ex/status> <http://ex/open> }", TestContext.Current.CancellationToken).AskResult);
    }

    [Fact]
    public void Insert_WhereHasSubSelect_InsertsTheProjectedRows()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/x>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/y>");

        var result = SparqlEngine.Update(Store,
            "INSERT { ?s <http://ex/first> <http://ex/yes> } WHERE { { SELECT ?s WHERE { ?s <http://ex/p> ?o } ORDER BY ?s LIMIT 1 } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.AffectedCount);
        Assert.True(SparqlEngine.Query(Store, "ASK { <http://ex/a> <http://ex/first> <http://ex/yes> }", TestContext.Current.CancellationToken).AskResult);
        Assert.False(SparqlEngine.Query(Store, "ASK { <http://ex/b> <http://ex/first> <http://ex/yes> }", TestContext.Current.CancellationToken).AskResult);
    }

    [Fact]
    public void Insert_WhereHasPropertyPath_InsertsTheTransitiveClosure()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/next>", "<http://ex/b>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/next>", "<http://ex/c>");

        var result = SparqlEngine.Update(Store,
            "INSERT { <http://ex/a> <http://ex/reaches> ?o } WHERE { <http://ex/a> <http://ex/next>+ ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.AffectedCount);
        Assert.True(SparqlEngine.Query(Store, "ASK { <http://ex/a> <http://ex/reaches> <http://ex/b> }", TestContext.Current.CancellationToken).AskResult);
        Assert.True(SparqlEngine.Query(Store, "ASK { <http://ex/a> <http://ex/reaches> <http://ex/c> }", TestContext.Current.CancellationToken).AskResult);
    }

    [Fact]
    public void Insert_WhereHasOptionalAndBind_InstantiatesWithAndWithoutTheOptionalBinding()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/type>", "<http://ex/T>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/label>", "\"A\"");
        Store.AddCurrent("<http://ex/b>", "<http://ex/type>", "<http://ex/T>");

        var result = SparqlEngine.Update(Store,
            "INSERT { ?s <http://ex/name> ?n } WHERE { ?s <http://ex/type> <http://ex/T> OPTIONAL { ?s <http://ex/label> ?l } BIND(COALESCE(?l, \"none\") AS ?n) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.AffectedCount);
        Assert.True(SparqlEngine.Query(Store, "ASK { <http://ex/a> <http://ex/name> \"A\" }", TestContext.Current.CancellationToken).AskResult);
        Assert.True(SparqlEngine.Query(Store, "ASK { <http://ex/b> <http://ex/name> \"none\" }", TestContext.Current.CancellationToken).AskResult);
    }

    [Fact]
    public void Insert_WhereHasObjectListThenAnotherTriple_EvaluatesTheWholeGroup()
    {
        // The tree parser once stopped at ',' and dropped the rest of the group; only a closed-brace check exposed it.
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/x>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/y>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/q>", "<http://ex/z>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/x>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/y>");

        var result = SparqlEngine.Update(Store,
            "INSERT { ?s <http://ex/both> ?c } WHERE { ?s <http://ex/p> <http://ex/x>, <http://ex/y> . ?s <http://ex/q> ?c }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.AffectedCount);
        Assert.True(SparqlEngine.Query(Store, "ASK { <http://ex/a> <http://ex/both> <http://ex/z> }", TestContext.Current.CancellationToken).AskResult);
    }

    [Fact]
    public void Select_ObjectListThenAnotherTriple_JoinsTheWholeGroup()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/x>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/y>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/q>", "<http://ex/z>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/x>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/y>");

        var result = SparqlEngine.Query(Store,
            "SELECT ?s ?c WHERE { ?s <http://ex/p> <http://ex/x>, <http://ex/y> . ?s <http://ex/q> ?c }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("<http://ex/a>", row["s"]);
        Assert.Equal("<http://ex/z>", row["c"]);
    }

    // ── Dataset: WITH, USING, USING NAMED; template graphs ────────────────────────────────────────────

    [Fact]
    public void Using_MatchesOnlyTheUsingGraph_AndWritesTheTemplateToTheDefaultGraph()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/x>", "<http://ex/g1>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/y>", "<http://ex/g2>");

        var result = SparqlEngine.Update(Store,
            "INSERT { ?s <http://ex/found> <http://ex/yes> } USING <http://ex/g1> WHERE { ?s <http://ex/p> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.AffectedCount);
        Assert.True(SparqlEngine.Query(Store, "ASK { <http://ex/a> <http://ex/found> <http://ex/yes> }", TestContext.Current.CancellationToken).AskResult);
        Assert.False(SparqlEngine.Query(Store, "ASK { <http://ex/b> <http://ex/found> <http://ex/yes> }", TestContext.Current.CancellationToken).AskResult);
    }

    [Fact]
    public void UsingOverridesWith_ForTheWhere_ButWithStillNamesTheTemplateGraph()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/x>", "<http://ex/src>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/y>", "<http://ex/dst>");

        var result = SparqlEngine.Update(Store,
            "WITH <http://ex/dst> INSERT { ?s <http://ex/copied> ?o } USING <http://ex/src> WHERE { ?s <http://ex/p> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.AffectedCount);
        Assert.True(SparqlEngine.Query(Store, "ASK { GRAPH <http://ex/dst> { <http://ex/a> <http://ex/copied> <http://ex/x> } }", TestContext.Current.CancellationToken).AskResult);
        Assert.False(SparqlEngine.Query(Store, "ASK { GRAPH <http://ex/dst> { <http://ex/b> <http://ex/copied> <http://ex/y> } }", TestContext.Current.CancellationToken).AskResult);
    }

    [Fact]
    public void UsingNamed_RestrictsGraphVariable_ToTheNamedGraphs()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/x>", "<http://ex/g1>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/y>", "<http://ex/g2>");

        var result = SparqlEngine.Update(Store,
            "INSERT { ?s <http://ex/in> ?g } USING NAMED <http://ex/g2> WHERE { GRAPH ?g { ?s <http://ex/p> ?o } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.AffectedCount);
        Assert.True(SparqlEngine.Query(Store, "ASK { <http://ex/b> <http://ex/in> <http://ex/g2> }", TestContext.Current.CancellationToken).AskResult);
        Assert.False(SparqlEngine.Query(Store, "ASK { <http://ex/a> <http://ex/in> <http://ex/g1> }", TestContext.Current.CancellationToken).AskResult);
    }

    [Fact]
    public void Insert_GraphVariableTemplate_WritesEachRowIntoItsOwnGraph()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/x>", "<http://ex/g1>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/y>", "<http://ex/g2>");

        var result = SparqlEngine.Update(Store,
            "INSERT { GRAPH ?g { ?s <http://ex/mark> <http://ex/yes> } } WHERE { GRAPH ?g { ?s <http://ex/p> ?o } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.AffectedCount);
        Assert.True(SparqlEngine.Query(Store, "ASK { GRAPH <http://ex/g1> { <http://ex/a> <http://ex/mark> <http://ex/yes> } }", TestContext.Current.CancellationToken).AskResult);
        Assert.True(SparqlEngine.Query(Store, "ASK { GRAPH <http://ex/g2> { <http://ex/b> <http://ex/mark> <http://ex/yes> } }", TestContext.Current.CancellationToken).AskResult);
        Assert.False(SparqlEngine.Query(Store, "ASK { GRAPH <http://ex/g1> { <http://ex/b> <http://ex/mark> <http://ex/yes> } }", TestContext.Current.CancellationToken).AskResult);
    }

    // ── Multi-operation requests: each operation evaluates its own WHERE ──────────────────────────────

    [Fact]
    public void Sequence_TemplateBeforeABindOnlyWhere_UsesThisOperationsWhere()
    {
        // The brace guess this ADR retires took the first '{' for a WHERE without triples — here, an earlier
        // operation's INSERT DATA block. The parser's recorded offset names the right group.
        var result = SparqlEngine.Update(Store, """
            INSERT DATA { <http://ex/a> <http://ex/p> <http://ex/x> } ;
            INSERT { <http://ex/s> <http://ex/p> ?v } WHERE { BIND(<http://ex/bound> AS ?v) }
            """, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.AffectedCount);
        Assert.True(SparqlEngine.Query(Store, "ASK { <http://ex/s> <http://ex/p> <http://ex/bound> }", TestContext.Current.CancellationToken).AskResult);
    }

    [Fact]
    public void Sequence_DeleteInsertWithOptionalGraph_RewritesStatusInOneRequest()
    {
        // ADR-052 validation 5: the shape of the Mercury memory-status rewrite that failed on 2026-10-27.
        Store.AddCurrent("<http://ex/bug>", "<http://ex/status>", "\"open\"", "<http://ex/mem>");
        Store.AddCurrent("<http://ex/bug>", "<http://ex/label>", "\"a bug\"", "<http://ex/mem>");

        var result = SparqlEngine.Update(Store, """
            PREFIX ex: <http://ex/>
            DELETE { GRAPH ex:mem { ex:bug ex:status ?old } }
            INSERT { GRAPH ex:mem { ex:bug ex:status "fixed" } }
            WHERE { GRAPH ex:mem { ex:bug ex:status ?old } OPTIONAL { GRAPH ex:mem { ex:bug ex:note ?n } } }
            """, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.AffectedCount);
        Assert.False(SparqlEngine.Query(Store, "ASK { GRAPH <http://ex/mem> { <http://ex/bug> <http://ex/status> \"open\" } }", TestContext.Current.CancellationToken).AskResult);
        Assert.True(SparqlEngine.Query(Store, "ASK { GRAPH <http://ex/mem> { <http://ex/bug> <http://ex/status> \"fixed\" } }", TestContext.Current.CancellationToken).AskResult);
        Assert.True(SparqlEngine.Query(Store, "ASK { GRAPH <http://ex/mem> { <http://ex/bug> <http://ex/label> \"a bug\" } }", TestContext.Current.CancellationToken).AskResult);
    }

    // ── Syntax: one grammar, so a malformed WHERE is rejected and nothing runs ────────────────────────

    [Fact]
    public void DeleteWhere_WithFilter_IsASyntaxError_AndDeletesNothing()
    {
        // [41] DeleteWhere ::= 'DELETE WHERE' QuadPattern — triples and GRAPH blocks only.
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/one>");

        var result = SparqlEngine.Update(Store,
            "DELETE WHERE { ?s <http://ex/p> ?v FILTER(?v = <http://ex/one>) }",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(SparqlEngine.Query(Store, "ASK { <http://ex/a> <http://ex/p> <http://ex/one> }", TestContext.Current.CancellationToken).AskResult);
    }

    [Fact]
    public void Modify_WhereMissingClosingBrace_IsASyntaxError_AndRunsNoOperation()
    {
        var result = SparqlEngine.Update(Store,
            "INSERT DATA { <http://ex/a> <http://ex/p> <http://ex/x> } ; INSERT { <http://ex/s> <http://ex/p> ?o } WHERE { OPTIONAL { ?s <http://ex/p> ?o }",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(SparqlEngine.Query(Store, "ASK { <http://ex/a> <http://ex/p> <http://ex/x> }", TestContext.Current.CancellationToken).AskResult);
    }

    // ── Equivalence: the solutions an Update instantiates are the solutions SELECT * returns ───────────

    [Theory]
    [InlineData("{ ?s <http://ex/p> ?o }")]
    [InlineData("{ ?s <http://ex/p> ?o FILTER(?o != <http://ex/x>) }")]
    [InlineData("{ ?s <http://ex/p> ?o OPTIONAL { ?s <http://ex/k> ?k } }")]
    [InlineData("{ ?s <http://ex/p> ?o MINUS { ?s <http://ex/k> <http://ex/one> } }")]
    [InlineData("{ { ?s <http://ex/p> ?o } UNION { ?s <http://ex/k> ?o } }")]
    [InlineData("{ GRAPH <http://ex/g> { ?s <http://ex/p> ?o } }")]
    [InlineData("{ ?s <http://ex/p> ?o FILTER EXISTS { GRAPH <http://ex/g> { ?s ?any ?thing } } }")]
    [InlineData("{ VALUES ?s { <http://ex/a> <http://ex/c> } ?s <http://ex/p> ?o }")]
    public void Insert_RowCount_EqualsSelectStarRowCount(string where)
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/x>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/y>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/k>", "<http://ex/one>");
        Store.AddCurrent("<http://ex/c>", "<http://ex/p>", "<http://ex/z>");
        Store.AddCurrent("<http://ex/c>", "<http://ex/p>", "<http://ex/w>", "<http://ex/g>");

        var select = SparqlEngine.Query(Store, "SELECT * WHERE " + where, TestContext.Current.CancellationToken);
        Assert.True(select.Success, select.ErrorMessage);

        // One fresh triple per solution: the template's subject is a distinct IRI per (?s, ?o) pair, written to a
        // graph no WHERE above reads, so the inserted triples are exactly the solutions.
        var result = SparqlEngine.Update(Store,
            "INSERT { GRAPH <http://ex/out> { ?s <http://ex/solution> ?o } } WHERE " + where,
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.ErrorMessage);

        var written = SparqlEngine.Query(Store, "SELECT * WHERE { GRAPH <http://ex/out> { ?s <http://ex/solution> ?o } }", TestContext.Current.CancellationToken);
        Assert.Equal(select.Rows!.Count, written.Rows!.Count);
    }
}
