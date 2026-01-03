using System.Collections.Generic;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests;

public partial class QueryExecutorTests
{
    #region Subquery Tests

    [Fact]
    public void SubQuery_BasicParsing_ParsesCorrectly()
    {
        // Test that we can parse a simple subquery
        var query = "SELECT ?person WHERE { { SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify we have a subquery
        Assert.True(parsedQuery.WhereClause.Pattern.HasSubQueries);
        Assert.Equal(1, parsedQuery.WhereClause.Pattern.SubQueryCount);

        // Verify the subquery has the correct pattern
        var subSelect = parsedQuery.WhereClause.Pattern.GetSubQuery(0);
        Assert.Equal(1, subSelect.PatternCount);

        var tp = subSelect.GetPattern(0);

        // Verify subject is a variable
        Assert.Equal(TermType.Variable, tp.Subject.Type);
        Assert.True(tp.Subject.Length > 0, "Subject variable should have non-zero length");
        var subjectVar = query.AsSpan().Slice(tp.Subject.Start, tp.Subject.Length).ToString();
        Assert.Equal("?person", subjectVar);

        // Verify predicate is an IRI
        Assert.Equal(TermType.Iri, tp.Predicate.Type);
        Assert.True(tp.Predicate.Length > 0, $"Predicate IRI should have non-zero length, got Start={tp.Predicate.Start}, Len={tp.Predicate.Length}");
        var predicateIri = query.AsSpan().Slice(tp.Predicate.Start, tp.Predicate.Length).ToString();
        Assert.Equal("<http://xmlns.com/foaf/0.1/name>", predicateIri);

        // Verify object is a variable
        Assert.Equal(TermType.Variable, tp.Object.Type);
        Assert.True(tp.Object.Length > 0, "Object variable should have non-zero length");
        var objectVar = query.AsSpan().Slice(tp.Object.Start, tp.Object.Length).ToString();
        Assert.Equal("?name", objectVar);
    }

    [Fact]
    public void SubQuery_SimpleExecution_ReturnsResults()
    {
        // Test basic subquery execution
        var query = "SELECT ?person WHERE { { SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var persons = new List<string>();
            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                if (personIdx >= 0)
                {
                    persons.Add(results.Current.GetString(personIdx).ToString());
                }
            }
            results.Dispose();

            // Should find Alice, Bob, Charlie (all have names)
            Assert.Equal(3, persons.Count);
            Assert.Contains("<http://example.org/Alice>", persons);
            Assert.Contains("<http://example.org/Bob>", persons);
            Assert.Contains("<http://example.org/Charlie>", persons);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_SelectAll_ReturnsAllInnerVariables()
    {
        // Test SELECT * in subquery
        var query = "SELECT ?person ?name WHERE { { SELECT * WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            int count = 0;
            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                Assert.True(personIdx >= 0, "Should have ?person binding");
                Assert.True(nameIdx >= 0, "Should have ?name binding");
                count++;
            }
            results.Dispose();

            Assert.Equal(3, count); // Alice, Bob, Charlie
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_WithLimit_RespectsLimit()
    {
        // Test LIMIT in subquery
        var query = "SELECT ?person WHERE { { SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } LIMIT 2 } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            Assert.Equal(2, count); // Limited to 2
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_WithOffset_SkipsResults()
    {
        // Test OFFSET in subquery
        var query = "SELECT ?person WHERE { { SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } OFFSET 1 } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            Assert.Equal(2, count); // 3 total, minus 1 offset = 2
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_VariableProjection_OnlyProjectsSelectedVariables()
    {
        // Test that only SELECT-ed variables are projected to outer query
        var query = "SELECT ?person ?name WHERE { { SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } } }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                Assert.True(personIdx >= 0, "Should have ?person binding");
                // ?name should NOT be visible - it was not projected from subquery
                Assert.True(nameIdx < 0, "Should NOT have ?name binding (not projected from subquery)");
            }
            results.Dispose();
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_MultiplePatterns_JoinsCorrectly()
    {
        // Test subquery with multiple triple patterns
        var query = @"SELECT ?person ?name WHERE {
            {
                SELECT ?person ?name WHERE {
                    ?person <http://xmlns.com/foaf/0.1/name> ?name .
                    ?person <http://xmlns.com/foaf/0.1/age> ?age
                }
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var names = new List<string>();
            while (results.MoveNext())
            {
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                if (nameIdx >= 0)
                {
                    names.Add(results.Current.GetString(nameIdx).ToString());
                }
            }
            results.Dispose();

            // Alice, Bob, Charlie all have name AND age
            Assert.Equal(3, names.Count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_WithFilter_FiltersInnerResults()
    {
        // Test FILTER in subquery
        var query = @"SELECT ?person WHERE {
            {
                SELECT ?person WHERE {
                    ?person <http://xmlns.com/foaf/0.1/age> ?age
                    FILTER(?age > 28)
                }
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var persons = new List<string>();
            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                if (personIdx >= 0)
                {
                    persons.Add(results.Current.GetString(personIdx).ToString());
                }
            }
            results.Dispose();

            // Alice (30) and Charlie (35) have age > 28, Bob (25) does not
            Assert.Equal(2, persons.Count);
            Assert.Contains("<http://example.org/Alice>", persons);
            Assert.Contains("<http://example.org/Charlie>", persons);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_WithDistinct_RemovesDuplicates()
    {
        // Add duplicate entries for this test
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/Alice>", "<http://xmlns.com/foaf/0.1/knows>", "<http://example.org/Charlie>");
        Store.CommitBatch();

        // Test DISTINCT in subquery - Alice knows both Bob and Charlie
        var query = @"SELECT ?knower WHERE {
            {
                SELECT DISTINCT ?knower WHERE {
                    ?knower <http://xmlns.com/foaf/0.1/knows> ?known
                }
            }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var knowers = new List<string>();
            while (results.MoveNext())
            {
                var knowerIdx = results.Current.FindBinding("?knower".AsSpan());
                if (knowerIdx >= 0)
                {
                    knowers.Add(results.Current.GetString(knowerIdx).ToString());
                }
            }
            results.Dispose();

            // Alice knows two people but DISTINCT should return her only once
            Assert.Single(knowers);
            Assert.Equal("<http://example.org/Alice>", knowers[0]);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_WithOuterPattern_JoinsCorrectly()
    {
        // Test subquery with outer pattern join
        // Subquery finds persons with names, outer pattern gets their ages
        var query = @"SELECT ?person ?age WHERE {
            ?person <http://xmlns.com/foaf/0.1/age> ?age .
            { SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var resultList = new List<(string person, string age)>();
            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                var ageIdx = results.Current.FindBinding("?age".AsSpan());
                Assert.True(personIdx >= 0, "Should have ?person binding");
                Assert.True(ageIdx >= 0, "Should have ?age binding");

                resultList.Add((
                    results.Current.GetString(personIdx).ToString(),
                    results.Current.GetString(ageIdx).ToString()
                ));
            }
            results.Dispose();

            // All three persons have both name and age
            Assert.Equal(3, resultList.Count);
            Assert.Contains(resultList, r => r.person == "<http://example.org/Alice>" && r.age == "30");
            Assert.Contains(resultList, r => r.person == "<http://example.org/Bob>" && r.age == "25");
            Assert.Contains(resultList, r => r.person == "<http://example.org/Charlie>" && r.age == "35");
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_WithOuterPatternFilter_FiltersAfterJoin()
    {
        // Test filter on outer pattern variables after join
        var query = @"SELECT ?person ?age WHERE {
            ?person <http://xmlns.com/foaf/0.1/age> ?age .
            { SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } }
            FILTER(?age > 28)
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var resultList = new List<(string person, string age)>();
            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                var ageIdx = results.Current.FindBinding("?age".AsSpan());

                resultList.Add((
                    results.Current.GetString(personIdx).ToString(),
                    results.Current.GetString(ageIdx).ToString()
                ));
            }
            results.Dispose();

            // Only Alice (30) and Charlie (35) have age > 28
            Assert.Equal(2, resultList.Count);
            Assert.Contains(resultList, r => r.person == "<http://example.org/Alice>");
            Assert.Contains(resultList, r => r.person == "<http://example.org/Charlie>");
            Assert.DoesNotContain(resultList, r => r.person == "<http://example.org/Bob>");
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_JoinWithSubqueryFilter_RespectsSubqueryFilter()
    {
        // Test that subquery filter is respected, then outer join is applied
        var query = @"SELECT ?person ?age WHERE {
            ?person <http://xmlns.com/foaf/0.1/age> ?age .
            { SELECT ?person WHERE {
                ?person <http://xmlns.com/foaf/0.1/name> ?name
                FILTER(CONTAINS(?name, ""ob""))
            } }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var resultList = new List<string>();
            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                resultList.Add(results.Current.GetString(personIdx).ToString());
            }
            results.Dispose();

            // Only Bob's name contains "ob"
            Assert.Single(resultList);
            Assert.Equal("<http://example.org/Bob>", resultList[0]);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_EmptySubquery_ReturnsEmpty()
    {
        // Test that empty subquery results in empty outer results
        var query = @"SELECT ?person ?age WHERE {
            ?person <http://xmlns.com/foaf/0.1/age> ?age .
            { SELECT ?person WHERE {
                ?person <http://xmlns.com/foaf/0.1/name> ""NonExistent""
            } }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();

            Assert.Equal(0, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_MultipleOuterPatterns_JoinsAll()
    {
        // Test with multiple outer patterns
        var query = @"SELECT ?person ?name ?age WHERE {
            ?person <http://xmlns.com/foaf/0.1/name> ?name .
            ?person <http://xmlns.com/foaf/0.1/age> ?age .
            { SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/knows> ?other } }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var resultList = new List<(string person, string name, string age)>();
            while (results.MoveNext())
            {
                var personIdx = results.Current.FindBinding("?person".AsSpan());
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                var ageIdx = results.Current.FindBinding("?age".AsSpan());

                resultList.Add((
                    results.Current.GetString(personIdx).ToString(),
                    results.Current.GetString(nameIdx).ToString(),
                    results.Current.GetString(ageIdx).ToString()
                ));
            }
            results.Dispose();

            // Only Alice knows someone
            Assert.Single(resultList);
            Assert.Equal("<http://example.org/Alice>", resultList[0].person);
            Assert.Equal("\"Alice\"", resultList[0].name);
            Assert.Equal("30", resultList[0].age);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MultipleSubqueries_JoinsResults()
    {
        // Add data where two subqueries will join on a shared variable
        // Use unique predicates to avoid conflicts with test fixture data
        Store.BeginBatch();
        // First subquery: employees with nicknames
        Store.AddCurrentBatched("<http://example.org/emp100>", "<http://example.org/nickname>", "\"EmpAlice\"");
        Store.AddCurrentBatched("<http://example.org/emp200>", "<http://example.org/nickname>", "\"EmpBob\"");
        // Second subquery: employees with salaries (only emp100)
        Store.AddCurrentBatched("<http://example.org/emp100>", "<http://example.org/salary>", "50000");
        Store.CommitBatch();

        // Query with two subqueries - should join on ?employee
        var query = @"SELECT ?employee ?nick ?sal WHERE {
            { SELECT ?employee ?nick WHERE { ?employee <http://example.org/nickname> ?nick } }
            { SELECT ?employee ?sal WHERE { ?employee <http://example.org/salary> ?sal } }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var found = new List<(string employee, string nick, string sal)>();
            while (results.MoveNext())
            {
                var empIdx = results.Current.FindBinding("?employee".AsSpan());
                var nickIdx = results.Current.FindBinding("?nick".AsSpan());
                var salIdx = results.Current.FindBinding("?sal".AsSpan());
                if (empIdx < 0 || nickIdx < 0 || salIdx < 0)
                {
                    var subQueryCount = parsedQuery.WhereClause.Pattern.SubQueryCount;
                    var requiredPatternCount = parsedQuery.WhereClause.Pattern.RequiredPatternCount;
                    Assert.Fail($"Missing binding: empIdx={empIdx}, nickIdx={nickIdx}, salIdx={salIdx}. SubQueryCount={subQueryCount}, RequiredPatternCount={requiredPatternCount}");
                }
                found.Add((
                    results.Current.GetString(empIdx).ToString(),
                    results.Current.GetString(nickIdx).ToString(),
                    results.Current.GetString(salIdx).ToString()));
            }
            results.Dispose();

            // Only emp100 should appear (has both nickname and salary)
            Assert.Single(found);
            Assert.Equal("<http://example.org/emp100>", found[0].employee);
            Assert.Equal("\"EmpAlice\"", found[0].nick);
            Assert.Equal("50000", found[0].sal);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MultipleSubqueries_WithOuterPattern()
    {
        // Add data for subqueries plus outer pattern
        Store.BeginBatch();
        // Subquery 1: employees with departments
        Store.AddCurrentBatched("<http://example.org/emp1>", "<http://example.org/dept>", "<http://example.org/engineering>");
        Store.AddCurrentBatched("<http://example.org/emp2>", "<http://example.org/dept>", "<http://example.org/sales>");
        // Subquery 2: employees with names
        Store.AddCurrentBatched("<http://example.org/emp1>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
        Store.AddCurrentBatched("<http://example.org/emp2>", "<http://xmlns.com/foaf/0.1/name>", "\"Bob\"");
        // Outer pattern: department labels (only engineering has label)
        Store.AddCurrentBatched("<http://example.org/engineering>", "<http://www.w3.org/2000/01/rdf-schema#label>", "\"Engineering Dept\"");
        Store.CommitBatch();

        // Query with two subqueries and an outer pattern
        var query = @"SELECT ?emp ?name ?deptLabel WHERE {
            { SELECT ?emp ?dept WHERE { ?emp <http://example.org/dept> ?dept } }
            { SELECT ?emp ?name WHERE { ?emp <http://xmlns.com/foaf/0.1/name> ?name } }
            ?dept <http://www.w3.org/2000/01/rdf-schema#label> ?deptLabel
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            var found = new List<(string emp, string name, string label)>();
            while (results.MoveNext())
            {
                var empIdx = results.Current.FindBinding("?emp".AsSpan());
                var nameIdx = results.Current.FindBinding("?name".AsSpan());
                var labelIdx = results.Current.FindBinding("?deptLabel".AsSpan());
                Assert.True(empIdx >= 0);
                Assert.True(nameIdx >= 0);
                Assert.True(labelIdx >= 0);
                found.Add((
                    results.Current.GetString(empIdx).ToString(),
                    results.Current.GetString(nameIdx).ToString(),
                    results.Current.GetString(labelIdx).ToString()));
            }
            results.Dispose();

            // Only emp1 has department with a label
            Assert.Single(found);
            Assert.Equal("<http://example.org/emp1>", found[0].emp);
            Assert.Equal("\"Alice\"", found[0].name);
            Assert.Equal("\"Engineering Dept\"", found[0].label);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_MultipleSubqueries_NoMatch_ReturnsEmpty()
    {
        // Add data with no shared values between subqueries
        Store.BeginBatch();
        Store.AddCurrentBatched("<http://example.org/A>", "<http://example.org/typeA>", "\"Value1\"");
        Store.AddCurrentBatched("<http://example.org/B>", "<http://example.org/typeB>", "\"Value2\"");
        Store.CommitBatch();

        // Query with two subqueries that have same variable but different subjects
        var query = @"SELECT ?s ?v1 ?v2 WHERE {
            { SELECT ?s ?v1 WHERE { ?s <http://example.org/typeA> ?v1 } }
            { SELECT ?s ?v2 WHERE { ?s <http://example.org/typeB> ?v2 } }
        }";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Debug: Check how many subqueries were parsed
        var subQueryCount = parsedQuery.WhereClause.Pattern.SubQueryCount;

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteSubQueryToMaterialized();

            int count = 0;
            var bindingsInfo = new List<string>();
            while (results.MoveNext())
            {
                count++;
                // Collect all bindings for debug
                var bindingStrs = new List<string>();
                var sIdx = results.Current.FindBinding("?s".AsSpan());
                var v1Idx = results.Current.FindBinding("?v1".AsSpan());
                var v2Idx = results.Current.FindBinding("?v2".AsSpan());
                bindingStrs.Add($"?s idx={sIdx} val={(sIdx >= 0 ? results.Current.GetString(sIdx).ToString() : "N/A")}");
                bindingStrs.Add($"?v1 idx={v1Idx} val={(v1Idx >= 0 ? results.Current.GetString(v1Idx).ToString() : "N/A")}");
                bindingStrs.Add($"?v2 idx={v2Idx} val={(v2Idx >= 0 ? results.Current.GetString(v2Idx).ToString() : "N/A")}");
                bindingsInfo.Add(string.Join(", ", bindingStrs));
            }
            results.Dispose();

            // If count > 0, fail with debug info
            if (count > 0)
            {
                Assert.Fail($"Expected 0 results but got {count}. SubQueryCount={subQueryCount}. Bindings: [{string.Join("; ", bindingsInfo)}]");
            }
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    #endregion
}
