using System.IO;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for SPARQL Update execution
/// </summary>
[Collection("QuadStore")]
public class UpdateExecutorTests : PooledStoreTestBase
{
    public UpdateExecutorTests(QuadStorePoolFixture fixture) : base(fixture)
    {
    }

    private int CountTriples(string? graphIri = null)
    {
        int count = 0;
        Store.AcquireReadLock();
        try
        {
            var graphSpan = graphIri != null ? graphIri.AsSpan() : System.ReadOnlySpan<char>.Empty;
            var results = Store.QueryCurrent(
                System.ReadOnlySpan<char>.Empty,
                System.ReadOnlySpan<char>.Empty,
                System.ReadOnlySpan<char>.Empty,
                graphSpan);

            while (results.MoveNext())
            {
                // Only count triples from the specified graph
                if (graphIri == null)
                {
                    if (results.Current.Graph.IsEmpty)
                        count++;
                }
                else
                {
                    if (results.Current.Graph.Equals(graphIri.AsSpan(), System.StringComparison.Ordinal))
                        count++;
                }
            }
            results.Dispose();
        }
        finally
        {
            Store.ReleaseReadLock();
        }
        return count;
    }

    #region INSERT DATA Tests

    [Fact]
    public void InsertData_SingleTriple_InsertsSuccessfully()
    {
        var update = "INSERT DATA { <http://ex.org/s> <http://ex.org/p> <http://ex.org/o> }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(1, result.AffectedCount);
        Assert.Equal(1, CountTriples());
    }

    [Fact]
    public void InsertData_MultipleTriples_InsertsAll()
    {
        var update = @"INSERT DATA {
            <http://ex.org/s1> <http://ex.org/p> <http://ex.org/o1> .
            <http://ex.org/s2> <http://ex.org/p> <http://ex.org/o2> .
            <http://ex.org/s3> <http://ex.org/p> <http://ex.org/o3>
        }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(3, result.AffectedCount);
        Assert.Equal(3, CountTriples());
    }

    [Fact]
    public void InsertData_WithNamedGraph_InsertsIntoGraph()
    {
        var update = @"INSERT DATA {
            GRAPH <http://ex.org/g1> {
                <http://ex.org/s> <http://ex.org/p> <http://ex.org/o>
            }
        }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(1, result.AffectedCount);
        Assert.Equal(0, CountTriples()); // Default graph should be empty
        Assert.Equal(1, CountTriples("<http://ex.org/g1>"));
    }

    [Fact]
    public void InsertData_WithLiteral_InsertsSuccessfully()
    {
        var update = @"INSERT DATA { <http://ex.org/person1> <http://ex.org/name> ""Alice"" }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(1, result.AffectedCount);
    }

    #endregion

    #region DELETE DATA Tests

    [Fact]
    public void DeleteData_ExistingTriple_DeletesSuccessfully()
    {
        // First insert a triple
        Store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        Assert.Equal(1, CountTriples());

        var update = "DELETE DATA { <http://ex.org/s> <http://ex.org/p> <http://ex.org/o> }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(1, result.AffectedCount);
        Assert.Equal(0, CountTriples());
    }

    [Fact]
    public void DeleteData_NonExistentTriple_ReturnsZeroAffected()
    {
        var update = "DELETE DATA { <http://ex.org/notexist> <http://ex.org/p> <http://ex.org/o> }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(0, result.AffectedCount);
    }

    [Fact]
    public void DeleteData_FromNamedGraph_DeletesFromCorrectGraph()
    {
        // Insert into named graph
        Store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", "<http://ex.org/g1>");
        Assert.Equal(1, CountTriples("<http://ex.org/g1>"));

        var update = @"DELETE DATA {
            GRAPH <http://ex.org/g1> {
                <http://ex.org/s> <http://ex.org/p> <http://ex.org/o>
            }
        }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(1, result.AffectedCount);
        Assert.Equal(0, CountTriples("<http://ex.org/g1>"));
    }

    #endregion

    #region CLEAR Tests

    [Fact]
    public void Clear_Default_ClearsDefaultGraph()
    {
        // Add triples to default and named graphs
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o1>");
        Store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o2>");
        Store.AddCurrentBatched("<http://ex.org/s3>", "<http://ex.org/p>", "<http://ex.org/o3>", "<http://ex.org/g1>");
        Store.CommitBatch();

        Assert.Equal(2, CountTriples());
        Assert.Equal(1, CountTriples("<http://ex.org/g1>"));

        var update = "CLEAR DEFAULT";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(2, result.AffectedCount);
        Assert.Equal(0, CountTriples());
        Assert.Equal(1, CountTriples("<http://ex.org/g1>")); // Named graph unaffected
    }

    [Fact]
    public void Clear_SpecificGraph_ClearsOnlyThatGraph()
    {
        // Add triples to default and named graphs
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o1>");
        Store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o2>", "<http://ex.org/g1>");
        Store.AddCurrentBatched("<http://ex.org/s3>", "<http://ex.org/p>", "<http://ex.org/o3>", "<http://ex.org/g1>");
        Store.CommitBatch();

        Assert.Equal(1, CountTriples());
        Assert.Equal(2, CountTriples("<http://ex.org/g1>"));

        var update = "CLEAR GRAPH <http://ex.org/g1>";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(2, result.AffectedCount);
        Assert.Equal(1, CountTriples()); // Default graph unaffected
        Assert.Equal(0, CountTriples("<http://ex.org/g1>"));
    }

    [Fact]
    public void Clear_All_ClearsEverything()
    {
        // Add triples to default and named graphs
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o1>");
        Store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o2>", "<http://ex.org/g1>");
        Store.AddCurrentBatched("<http://ex.org/s3>", "<http://ex.org/p>", "<http://ex.org/o3>", "<http://ex.org/g2>");
        Store.CommitBatch();

        var update = "CLEAR ALL";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(3, result.AffectedCount);
        Assert.Equal(0, CountTriples());
        Assert.Equal(0, CountTriples("<http://ex.org/g1>"));
        Assert.Equal(0, CountTriples("<http://ex.org/g2>"));
    }

    #endregion

    #region DROP Tests

    [Fact]
    public void Drop_Graph_SameAsClear()
    {
        Store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", "<http://ex.org/g1>");
        Assert.Equal(1, CountTriples("<http://ex.org/g1>"));

        var update = "DROP GRAPH <http://ex.org/g1>";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(0, CountTriples("<http://ex.org/g1>"));
    }

    #endregion

    #region CREATE Tests

    [Fact]
    public void Create_Graph_IsNoOp()
    {
        var update = "CREATE GRAPH <http://ex.org/newgraph>";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(0, result.AffectedCount);
    }

    #endregion

    #region COPY Tests

    [Fact]
    public void Copy_GraphToGraph_CopiesTriples()
    {
        // Add triples to source graph
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o1>", "<http://ex.org/src>");
        Store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o2>", "<http://ex.org/src>");
        Store.CommitBatch();

        var update = "COPY <http://ex.org/src> TO <http://ex.org/dst>";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(2, result.AffectedCount);
        Assert.Equal(2, CountTriples("<http://ex.org/src>")); // Source unchanged
        Assert.Equal(2, CountTriples("<http://ex.org/dst>")); // Destination has copies
    }

    [Fact]
    public void Copy_DefaultToGraph_CopiesFromDefaultGraph()
    {
        // Add triples to default graph
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o1>");
        Store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o2>");
        Store.CommitBatch();

        var update = "COPY DEFAULT TO <http://ex.org/dst>";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(2, result.AffectedCount);
        Assert.Equal(2, CountTriples()); // Default unchanged
        Assert.Equal(2, CountTriples("<http://ex.org/dst>")); // Destination has copies
    }

    #endregion

    #region MOVE Tests

    [Fact]
    public void Move_GraphToGraph_MovesTriples()
    {
        // Add triples to source graph
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o1>", "<http://ex.org/src>");
        Store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o2>", "<http://ex.org/src>");
        Store.CommitBatch();

        var update = "MOVE <http://ex.org/src> TO <http://ex.org/dst>";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(4, result.AffectedCount); // 2 copied + 2 deleted
        Assert.Equal(0, CountTriples("<http://ex.org/src>")); // Source now empty
        Assert.Equal(2, CountTriples("<http://ex.org/dst>")); // Destination has data
    }

    #endregion

    #region ADD Tests

    [Fact]
    public void Add_GraphToGraph_AddsWithoutClearing()
    {
        // Add triples to both graphs
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o1>", "<http://ex.org/src>");
        Store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o2>", "<http://ex.org/dst>");
        Store.CommitBatch();

        var update = "ADD <http://ex.org/src> TO <http://ex.org/dst>";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(1, result.AffectedCount);
        Assert.Equal(1, CountTriples("<http://ex.org/src>")); // Source unchanged
        Assert.Equal(2, CountTriples("<http://ex.org/dst>")); // Destination has original + new
    }

    #endregion

    #region DELETE WHERE Tests

    [Fact]
    public void DeleteWhere_PatternParsedCorrectly()
    {
        // Verify DELETE WHERE patterns are parsed correctly
        var update = @"DELETE WHERE { ?s <http://ex.org/type> <http://ex.org/Person> }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        Assert.Equal(QueryType.DeleteWhere, operation.Type);
        Assert.Equal(1, operation.WhereClause.Pattern.PatternCount);

        var tp = operation.WhereClause.Pattern.GetPattern(0);
        var subject = update.AsSpan(tp.Subject.Start, tp.Subject.Length);
        var predicate = update.AsSpan(tp.Predicate.Start, tp.Predicate.Length);
        var obj = update.AsSpan(tp.Object.Start, tp.Object.Length);

        Assert.Equal(TermType.Variable, tp.Subject.Type);
        Assert.True(subject.SequenceEqual("?s".AsSpan()));
        Assert.Equal(TermType.Iri, tp.Predicate.Type);
        Assert.True(predicate.SequenceEqual("<http://ex.org/type>".AsSpan()));
    }

    [Fact]
    public void DeleteWhere_SingleVariable_DeletesAllMatching()
    {
        // Add triples with same predicate but different subjects/objects
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/type>", "<http://ex.org/Person>");
        Store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/type>", "<http://ex.org/Person>");
        Store.AddCurrentBatched("<http://ex.org/s3>", "<http://ex.org/type>", "<http://ex.org/Animal>");
        Store.CommitBatch();

        Assert.Equal(3, CountTriples());

        // DELETE WHERE matches only Person types
        var update = @"DELETE WHERE { ?s <http://ex.org/type> <http://ex.org/Person> }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(2, result.AffectedCount);
        Assert.Equal(1, CountTriples()); // Only Animal remains
    }

    [Fact]
    public void DeleteWhere_MultipleVariables_DeletesMatching()
    {
        // Add triples with various values
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex.org/Alice>", "<http://ex.org/knows>", "<http://ex.org/Bob>");
        Store.AddCurrentBatched("<http://ex.org/Alice>", "<http://ex.org/knows>", "<http://ex.org/Charlie>");
        Store.AddCurrentBatched("<http://ex.org/Bob>", "<http://ex.org/knows>", "<http://ex.org/Alice>");
        Store.CommitBatch();

        Assert.Equal(3, CountTriples());

        // DELETE WHERE all "knows" relationships
        var update = @"DELETE WHERE { ?s <http://ex.org/knows> ?o }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(3, result.AffectedCount);
        Assert.Equal(0, CountTriples());
    }

    [Fact]
    public void DeleteWhere_NoMatches_ReturnsZeroAffected()
    {
        // Add some triples
        Store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        Assert.Equal(1, CountTriples());

        // DELETE WHERE with no matches
        var update = @"DELETE WHERE { ?s <http://ex.org/notexist> ?o }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(0, result.AffectedCount);
        Assert.Equal(1, CountTriples()); // Original triple unchanged
    }

    [Fact]
    public void DeleteWhere_AllVariables_DeletesEverything()
    {
        // Add multiple triples
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/p1>", "<http://ex.org/o1>");
        Store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/p2>", "<http://ex.org/o2>");
        Store.AddCurrentBatched("<http://ex.org/s3>", "<http://ex.org/p3>", "<http://ex.org/o3>");
        Store.CommitBatch();

        Assert.Equal(3, CountTriples());

        // DELETE WHERE with all variables matches everything
        var update = @"DELETE WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(3, result.AffectedCount);
        Assert.Equal(0, CountTriples());
    }

    #endregion

    #region DELETE/INSERT WHERE (Modify) Tests

    [Fact]
    public void Modify_DeleteAndInsert_ModifiesTriples()
    {
        // Add a triple to be modified
        Store.AddCurrent("<http://ex.org/person1>", "<http://ex.org/status>", "\"active\"");
        Assert.Equal(1, CountTriples());

        // Modify: change status from "active" to "inactive"
        var update = @"DELETE { ?p <http://ex.org/status> ""active"" }
                       INSERT { ?p <http://ex.org/status> ""inactive"" }
                       WHERE { ?p <http://ex.org/status> ""active"" }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(2, result.AffectedCount); // 1 deleted + 1 inserted
        Assert.Equal(1, CountTriples()); // Still have one triple (with new value)
    }

    [Fact]
    public void Modify_InsertOnly_InsertsNewTriples()
    {
        // Add existing triples
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/name>", "\"Alice\"");
        Store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/name>", "\"Bob\"");
        Store.CommitBatch();

        Assert.Equal(2, CountTriples());

        // INSERT WHERE: Add a type triple for each person with a name
        var update = @"INSERT { ?s <http://ex.org/type> <http://ex.org/Person> }
                       WHERE { ?s <http://ex.org/name> ?name }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(2, result.AffectedCount); // 2 inserted
        Assert.Equal(4, CountTriples()); // 2 original + 2 new
    }

    [Fact]
    public void Modify_DeleteOnly_DeletesMatchingTriples()
    {
        // Add triples
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/temp>", "\"value1\"");
        Store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/temp>", "\"value2\"");
        Store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/perm>", "\"keep\"");
        Store.CommitBatch();

        Assert.Equal(3, CountTriples());

        // DELETE WHERE: Remove all temp predicates
        var update = @"DELETE { ?s <http://ex.org/temp> ?o }
                       WHERE { ?s <http://ex.org/temp> ?o }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(2, result.AffectedCount); // 2 deleted
        Assert.Equal(1, CountTriples()); // Only perm triple remains
    }

    #endregion

    #region WITH Clause Tests

    [Fact]
    public void With_DeleteInsert_ScopesToSpecifiedGraph()
    {
        // Add data to named graph
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/status>", "\"active\"", "<http://ex.org/g1>");
        Store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/status>", "\"active\"", "<http://ex.org/g1>");
        // Also add same pattern to default graph - should NOT be affected
        Store.AddCurrentBatched("<http://ex.org/s3>", "<http://ex.org/status>", "\"active\"");
        Store.CommitBatch();

        Assert.Equal(2, CountTriples("<http://ex.org/g1>"));
        Assert.Equal(1, CountTriples());

        // WITH clause scopes the update to the named graph
        var update = @"WITH <http://ex.org/g1>
                       DELETE { ?s <http://ex.org/status> ""active"" }
                       INSERT { ?s <http://ex.org/status> ""inactive"" }
                       WHERE { ?s <http://ex.org/status> ""active"" }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(4, result.AffectedCount); // 2 deleted + 2 inserted

        // Named graph should have new values
        Assert.Equal(2, CountTriples("<http://ex.org/g1>"));

        // Default graph should be unchanged
        Assert.Equal(1, CountTriples());

        // Verify the content in the named graph was changed
        Store.AcquireReadLock();
        try
        {
            var results = Store.QueryCurrent(
                System.ReadOnlySpan<char>.Empty,
                "<http://ex.org/status>".AsSpan(),
                "\"inactive\"".AsSpan(),
                "<http://ex.org/g1>".AsSpan());
            int count = 0;
            while (results.MoveNext()) count++;
            results.Dispose();
            Assert.Equal(2, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void With_InsertOnly_InsertsIntoSpecifiedGraph()
    {
        // Add data to named graph
        Store.AddCurrent("<http://ex.org/alice>", "<http://ex.org/name>", "\"Alice\"", "<http://ex.org/g1>");

        Assert.Equal(1, CountTriples("<http://ex.org/g1>"));
        Assert.Equal(0, CountTriples());

        // WITH clause scopes insert to the named graph
        var update = @"WITH <http://ex.org/g1>
                       INSERT { ?s <http://ex.org/type> <http://ex.org/Person> }
                       WHERE { ?s <http://ex.org/name> ?name }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(1, result.AffectedCount);

        // New triple should be in named graph
        Assert.Equal(2, CountTriples("<http://ex.org/g1>"));
        // Default graph should still be empty
        Assert.Equal(0, CountTriples());
    }

    [Fact]
    public void With_DeleteOnly_DeletesFromSpecifiedGraph()
    {
        // Add data to named graph
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/temp>", "\"value\"", "<http://ex.org/g1>");
        Store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/perm>", "\"keep\"", "<http://ex.org/g1>");
        // Also add same pattern to default graph
        Store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/temp>", "\"value\"");
        Store.CommitBatch();

        Assert.Equal(2, CountTriples("<http://ex.org/g1>"));
        Assert.Equal(1, CountTriples());

        // WITH clause scopes delete to the named graph
        var update = @"WITH <http://ex.org/g1>
                       DELETE { ?s <http://ex.org/temp> ?o }
                       WHERE { ?s <http://ex.org/temp> ?o }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(1, result.AffectedCount);

        // Named graph should only have the perm triple
        Assert.Equal(1, CountTriples("<http://ex.org/g1>"));
        // Default graph should be unchanged
        Assert.Equal(1, CountTriples());
    }

    [Fact]
    public void With_ExplicitGraphInTemplate_OverridesWithGraph()
    {
        // Add data to named graph g1
        Store.AddCurrent("<http://ex.org/alice>", "<http://ex.org/name>", "\"Alice\"", "<http://ex.org/g1>");

        Assert.Equal(1, CountTriples("<http://ex.org/g1>"));
        Assert.Equal(0, CountTriples("<http://ex.org/g2>"));

        // WITH specifies g1 for WHERE, but INSERT uses explicit GRAPH g2
        var update = @"WITH <http://ex.org/g1>
                       INSERT { GRAPH <http://ex.org/g2> { ?s <http://ex.org/type> <http://ex.org/Person> } }
                       WHERE { ?s <http://ex.org/name> ?name }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(1, result.AffectedCount);

        // New triple should be in g2, not g1
        Assert.Equal(1, CountTriples("<http://ex.org/g1>")); // Original unchanged
        Assert.Equal(1, CountTriples("<http://ex.org/g2>")); // New triple here
    }

    [Fact]
    public void With_MixedTemplates_UsesCorrectGraphs()
    {
        // Add data to named graph g1
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex.org/alice>", "<http://ex.org/status>", "\"active\"", "<http://ex.org/g1>");
        Store.AddCurrentBatched("<http://ex.org/bob>", "<http://ex.org/status>", "\"active\"", "<http://ex.org/g1>");
        Store.CommitBatch();

        Assert.Equal(2, CountTriples("<http://ex.org/g1>"));

        // WITH g1, but insert to g2 (explicit) and delete from g1 (implicit via WITH)
        var update = @"WITH <http://ex.org/g1>
                       DELETE { ?s <http://ex.org/status> ""active"" }
                       INSERT { GRAPH <http://ex.org/g2> { ?s <http://ex.org/status> ""archived"" } }
                       WHERE { ?s <http://ex.org/status> ""active"" }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(4, result.AffectedCount); // 2 deleted + 2 inserted

        // g1 should be empty (deleted)
        Assert.Equal(0, CountTriples("<http://ex.org/g1>"));
        // g2 should have the archived statuses
        Assert.Equal(2, CountTriples("<http://ex.org/g2>"));
    }

    [Fact]
    public void With_NoMatchingData_ReturnsZeroAffected()
    {
        // Add data to default graph only
        Store.AddCurrent("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o>");

        Assert.Equal(1, CountTriples());
        Assert.Equal(0, CountTriples("<http://ex.org/g1>"));

        // WITH scopes to g1 which is empty
        var update = @"WITH <http://ex.org/g1>
                       DELETE { ?s ?p ?o }
                       WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(0, result.AffectedCount);

        // Default graph should be unchanged
        Assert.Equal(1, CountTriples());
    }

    [Fact]
    public void With_ParsedCorrectly()
    {
        var update = @"WITH <http://example.org/graph1>
                       DELETE { ?s ?p ?o }
                       INSERT { ?s <http://ex.org/new> ?o }
                       WHERE { ?s <http://ex.org/old> ?o }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        Assert.Equal(QueryType.Modify, operation.Type);
        Assert.True(operation.WithGraphLength > 0);

        var withGraph = update.AsSpan(operation.WithGraphStart, operation.WithGraphLength);
        Assert.True(withGraph.SequenceEqual("<http://example.org/graph1>".AsSpan()));
    }

    #endregion

    #region DELETE WHERE with GRAPH Clause Tests

    [Fact]
    public void DeleteWhere_WithGraphClause_DeletesFromSpecifiedGraph()
    {
        // Add data to named graph
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o>", "<http://ex.org/g1>");
        Store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>", "<http://ex.org/g1>");
        // Also in default graph
        Store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o>");
        Store.CommitBatch();

        Assert.Equal(2, CountTriples("<http://ex.org/g1>"));
        Assert.Equal(1, CountTriples());

        // DELETE WHERE with explicit GRAPH clause
        var update = @"DELETE WHERE { GRAPH <http://ex.org/g1> { ?s ?p ?o } }";
        var parser = new SparqlParser(update.AsSpan());
        var operation = parser.ParseUpdate();

        var executor = new UpdateExecutor(Store, update.AsSpan(), operation);
        var result = executor.Execute();

        Assert.True(result.Success);
        Assert.Equal(2, result.AffectedCount);

        // Named graph should be empty
        Assert.Equal(0, CountTriples("<http://ex.org/g1>"));
        // Default graph should be unchanged
        Assert.Equal(1, CountTriples());
    }

    #endregion
}
