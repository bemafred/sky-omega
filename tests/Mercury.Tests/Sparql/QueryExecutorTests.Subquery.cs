using System.Collections.Generic;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

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
            Assert.Contains(resultList, r => r.person == "<http://example.org/Alice>" && ExtractNumericValue(r.age) == "30");
            Assert.Contains(resultList, r => r.person == "<http://example.org/Bob>" && ExtractNumericValue(r.age) == "25");
            Assert.Contains(resultList, r => r.person == "<http://example.org/Charlie>" && ExtractNumericValue(r.age) == "35");
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
            Assert.Equal("30", ExtractNumericValue(resultList[0].age));
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

    [Fact]
    public void SubQuery_UnionInNestedSubQuery_ReturnsCorrectResults()
    {
        // Test nested subquery with UNION - simplified version of W3C dawg-delete-insert-04
        // Add test data: three entities with different predicate positions
        Store.AddCurrent("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o1>");
        Store.AddCurrent("<http://ex.org/s2>", "<http://ex.org/o2>", "<http://ex.org/p>");
        Store.AddCurrent("<http://ex.org/o3>", "<http://ex.org/o4>", "<http://ex.org/s3>");

        // Query finds all terms that appear in ANY position (subject, predicate, or object)
        var query = @"
            SELECT ?term WHERE {
                { SELECT ?term WHERE {
                    { ?term ?p1 ?o1 } UNION
                    { ?s2 ?term ?o2 } UNION
                    { ?s3 ?p3 ?term }
                } }
            }
        ";

        Store.AcquireReadLock();
        try
        {
            var parser = new SparqlParser(query.AsSpan());
            var parsed = parser.ParseQuery();

            // Verify the subquery has UNION
            Assert.True(parsed.WhereClause.Pattern.HasSubQueries);
            var subSelect = parsed.WhereClause.Pattern.GetSubQuery(0);
            Assert.True(subSelect.HasUnion, "SubQuery should have UNION");
            Assert.Equal(3, subSelect.PatternCount);  // 3 UNION alternatives
            Assert.Equal(1, subSelect.UnionStartIndex);  // UNION starts after first pattern
            Assert.False(subSelect.Distinct, "Removed DISTINCT to avoid GetHashCode issue");

            using var executor = new QueryExecutor(Store, query.AsSpan(), parsed);
            var results = executor.ExecuteSubQueryToMaterialized();

            var terms = new HashSet<string>();
            while (results.MoveNext())
            {
                var termIdx = results.Current.FindBinding("?term".AsSpan());
                Assert.True(termIdx >= 0, "Should have ?term binding");
                var term = results.Current.GetString(termIdx);
                terms.Add(term.ToString());
            }
            results.Dispose();

            // Should find all 9 unique terms (s1, s2, s3, p, o1, o2, o3, o4, plus http://ex.org/p appears twice)
            Assert.True(terms.Count >= 8, $"Expected at least 8 unique terms, got {terms.Count}");

            // Verify we got the expected terms
            Assert.Contains("<http://ex.org/s1>", terms);
            Assert.Contains("<http://ex.org/s2>", terms);
            Assert.Contains("<http://ex.org/s3>", terms);
            Assert.Contains("<http://ex.org/p>", terms);
            Assert.Contains("<http://ex.org/o1>", terms);
            Assert.Contains("<http://ex.org/o2>", terms);
            Assert.Contains("<http://ex.org/o3>", terms);
            Assert.Contains("<http://ex.org/o4>", terms);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_Sq10_WithExists_ExecutesCorrectly()
    {
        // W3C sq10 test case: Subquery with FILTER EXISTS
        // The subquery returns ?x, ?y bindings, then EXISTS checks if ?x ex:q ?y exists
        // Data: in:a ex:p in:b, in:a ex:q in:c
        // Query binds ?x=in:a, ?y=in:b from subquery, then checks EXISTS {in:a ex:q in:b}
        // Since in:a ex:q in:b does NOT exist (only in:a ex:q in:c), result should be empty

        Store.AddCurrent("<http://www.example.org/instance#a>", "<http://www.example.org/schema#p>", "<http://www.example.org/instance#b>");
        Store.AddCurrent("<http://www.example.org/instance#a>", "<http://www.example.org/schema#q>", "<http://www.example.org/instance#c>");

        Store.AcquireReadLock();
        try
        {
            var query = """
                prefix ex: <http://www.example.org/schema#>
                prefix in: <http://www.example.org/instance#>

                select ?x where {
                    {select * where {?x ex:p ?y}}
                    filter(exists {?x ex:q ?y})
                }
                """;

            var parser = new SparqlParser(query.AsSpan());
            var parsed = parser.ParseQuery();

            // Verify EXISTS filter is parsed into ExistsFilter (not FilterExpr)
            var outerPattern = parsed.WhereClause.Pattern;
            Assert.True(outerPattern.HasExists, "EXISTS should be parsed into ExistsFilter");

            // Use ExecuteToMaterialized() to avoid stack overflow from ~22KB QueryResults struct
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsed);
            var results = executor.ExecuteToMaterialized();

            var xValues = new List<string>();
            while (results.MoveNext())
            {
                var row = results.Current;
                var idx = row.FindBinding("?x".AsSpan());
                if (idx >= 0)
                {
                    xValues.Add(row.GetString(idx).ToString());
                }
            }
            results.Dispose();

            // Expected: 0 results (because ?y=in:b from subquery, but in:a ex:q in:b doesn't exist)
            Assert.Empty(xValues);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_NestedSubqueries_ParsesCorrectly()
    {
        // Test parsing of sq09-style nested subqueries:
        // SELECT * WHERE { { SELECT * WHERE { { SELECT ?x WHERE { ?x ?p ?o } } } } ?x ?p2 ?y }
        var query = @"
            PREFIX ex: <http://ex.org/>
            SELECT * WHERE {
                { SELECT * WHERE {
                    { SELECT ?x WHERE { ?x ex:q ?t } }
                } }
                ?x ex:p ?y
            }
        ";

        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify outer query has a subquery
        Assert.True(parsedQuery.WhereClause.Pattern.HasSubQueries, "Outer query should have subquery");
        Assert.Equal(1, parsedQuery.WhereClause.Pattern.SubQueryCount);

        // Get the middle subquery
        var middleSq = parsedQuery.WhereClause.Pattern.GetSubQuery(0);
        Assert.True(middleSq.SelectAll, "Middle subquery should be SELECT *");

        // The middle subquery should have a nested subquery
        Assert.True(middleSq.HasSubQueries, "Middle subquery should have nested subquery");
        Assert.Equal(1, middleSq.SubQueryCount);

        // Get the innermost subquery
        var innerSq = middleSq.GetSubQuery(0);
        Assert.False(innerSq.SelectAll, "Inner subquery should NOT be SELECT *");
        Assert.Equal(1, innerSq.ProjectedVarCount);
        Assert.Equal(1, innerSq.PatternCount);
    }

    [Fact]
    public void SubQuery_NestedSubqueries_ExecutesCorrectly()
    {
        // Recreate sq09 test case
        // Data: in:a ex:p in:b ; in:a ex:q in:c ; in:d ex:p in:e
        Store.AddCurrent("<http://www.example.org/instance#a>", "<http://www.example.org/schema#p>", "<http://www.example.org/instance#b>");
        Store.AddCurrent("<http://www.example.org/instance#a>", "<http://www.example.org/schema#q>", "<http://www.example.org/instance#c>");
        Store.AddCurrent("<http://www.example.org/instance#d>", "<http://www.example.org/schema#p>", "<http://www.example.org/instance#e>");

        Store.AcquireReadLock();
        try
        {
            // Test doubly nested subquery (like sq09 but without outer join)
            var doubleNestedQuery = @"
                PREFIX ex: <http://www.example.org/schema#>
                SELECT * WHERE {
                    { SELECT * WHERE {
                        { SELECT ?x WHERE { ?x ex:q ?t } }
                    } }
                }
            ";

            var doubleParser = new SparqlParser(doubleNestedQuery.AsSpan());
            var doubleParsed = doubleParser.ParseQuery();

            // Verify parsing
            Assert.True(doubleParsed.WhereClause.Pattern.HasSubQueries, "Should have subquery");
            var middleSq = doubleParsed.WhereClause.Pattern.GetSubQuery(0);
            Assert.True(middleSq.HasSubQueries, "Middle should have nested subquery");
            Assert.Equal(0, middleSq.PatternCount);  // Middle has no direct patterns
            var innerSq = middleSq.GetSubQuery(0);
            Assert.Equal(1, innerSq.PatternCount);  // Inner has 1 pattern

            // First, verify direct execution works (it does - from diagnostic tests)
            PrefixMapping[]? prefixes = null;
            if (doubleParsed.Prologue.PrefixCount > 0)
            {
                prefixes = new PrefixMapping[doubleParsed.Prologue.PrefixCount];
                for (int i = 0; i < doubleParsed.Prologue.PrefixCount; i++)
                {
                    var (ps, pl, irs, irl) = doubleParsed.Prologue.GetPrefix(i);
                    prefixes[i] = new PrefixMapping
                    {
                        PrefixStart = ps,
                        PrefixLength = pl,
                        IriStart = irs,
                        IriLength = irl
                    };
                }
            }

            // Verify direct BoxedSubQueryExecutor works
            var directExecutor = new BoxedSubQueryExecutor(Store, doubleNestedQuery, middleSq, prefixes);
            var directResults = directExecutor.Execute();
            Assert.Single(directResults);

            // Now test through QueryExecutor
            using var doubleExecutor = new QueryExecutor(Store, doubleNestedQuery.AsSpan(), doubleParsed);
            var doubleResults = doubleExecutor.ExecuteSubQueryToMaterialized();
            int doubleCount = 0;
            while (doubleResults.MoveNext()) doubleCount++;
            doubleResults.Dispose();
            Assert.Equal(1, doubleCount);  // Should get 1 result for ?x=in:a
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_NestedSubqueriesWithJoin_ExecutesCorrectly()
    {
        // Full sq09 test case with outer join pattern
        Store.AddCurrent("<http://www.example.org/instance#a>", "<http://www.example.org/schema#p>", "<http://www.example.org/instance#b>");
        Store.AddCurrent("<http://www.example.org/instance#a>", "<http://www.example.org/schema#q>", "<http://www.example.org/instance#c>");
        Store.AddCurrent("<http://www.example.org/instance#d>", "<http://www.example.org/schema#p>", "<http://www.example.org/instance#e>");

        var fullQuery = @"
            PREFIX ex: <http://www.example.org/schema#>
            PREFIX in: <http://www.example.org/instance#>
            SELECT * WHERE {
                { SELECT * WHERE {
                    { SELECT ?x WHERE { ?x ex:q ?t } }
                } }
                ?x ex:p ?y
            }
        ";

        Store.AcquireReadLock();
        try
        {
            var fullParser = new SparqlParser(fullQuery.AsSpan());
            var fullParsed = fullParser.ParseQuery();

            // Use ExecuteSubQueryToMaterialized to avoid stack overflow
            using var fullExecutor = new QueryExecutor(Store, fullQuery.AsSpan(), fullParsed);
            var fullResults = fullExecutor.ExecuteSubQueryToMaterialized();

            var rows = new List<(string x, string y)>();
            while (fullResults.MoveNext())
            {
                var xIdx = fullResults.Current.FindBinding("?x".AsSpan());
                var yIdx = fullResults.Current.FindBinding("?y".AsSpan());
                var x = xIdx >= 0 ? fullResults.Current.GetString(xIdx).ToString() : "";
                var y = yIdx >= 0 ? fullResults.Current.GetString(yIdx).ToString() : "";
                rows.Add((x, y));
            }
            fullResults.Dispose();

            // Expected result: 1 row with x=in:a, y=in:b
            // Inner: ?x ex:q ?t returns ?x=in:a (only in:a has ex:q)
            // Middle: SELECT * passes through ?x=in:a
            // Outer: joins ?x=in:a with ?x ex:p ?y, yielding ?x=in:a, ?y=in:b
            Assert.Single(rows);
            Assert.Equal("<http://www.example.org/instance#a>", rows[0].x);
            Assert.Equal("<http://www.example.org/instance#b>", rows[0].y);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_SingleLevel_WithPrefix_ExecutesCorrectly()
    {
        // Test a single-level subquery with prefix to verify prefix expansion works
        Store.AddCurrent("<http://www.example.org/instance#a>", "<http://www.example.org/schema#q>", "<http://www.example.org/instance#c>");

        var singleQuery = @"
            PREFIX ex: <http://www.example.org/schema#>
            SELECT * WHERE {
                { SELECT ?x WHERE { ?x ex:q ?t } }
            }
        ";

        Store.AcquireReadLock();
        try
        {
            var parser = new SparqlParser(singleQuery.AsSpan());
            var parsed = parser.ParseQuery();

            // Verify parsing
            Assert.True(parsed.WhereClause.Pattern.HasSubQueries, "Should have subquery");
            var sq = parsed.WhereClause.Pattern.GetSubQuery(0);
            Assert.Equal(1, sq.PatternCount);
            Assert.False(sq.HasSubQueries, "Single level should not have nested subqueries");

            // Execute
            using var executor = new QueryExecutor(Store, singleQuery.AsSpan(), parsed);
            var results = executor.ExecuteSubQueryToMaterialized();
            int count = 0;
            while (results.MoveNext())
            {
                count++;
                var xIdx = results.Current.FindBinding("?x".AsSpan());
                Assert.True(xIdx >= 0, "Should have ?x binding");
                var xValue = results.Current.GetString(xIdx).ToString();
                Assert.Equal("<http://www.example.org/instance#a>", xValue);
            }
            results.Dispose();
            Assert.Equal(1, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_Diagnostic_InnerSubQueryDirectly()
    {
        // Test executing the innermost subquery directly (no nesting)
        Store.AddCurrent("<http://www.example.org/instance#a>", "<http://www.example.org/schema#q>", "<http://www.example.org/instance#c>");

        var directQuery = @"
            PREFIX ex: <http://www.example.org/schema#>
            SELECT ?x WHERE { ?x ex:q ?t }
        ";

        Store.AcquireReadLock();
        try
        {
            var parser = new SparqlParser(directQuery.AsSpan());
            var parsed = parser.ParseQuery();

            // Should not have subqueries - this is a direct query
            Assert.False(parsed.WhereClause.Pattern.HasSubQueries);
            Assert.Equal(1, parsed.WhereClause.Pattern.PatternCount);

            // Execute normally
            using var executor = new QueryExecutor(Store, directQuery.AsSpan(), parsed);
            var results = executor.Execute();
            int count = 0;
            while (results.MoveNext())
            {
                count++;
                var xIdx = results.Current.FindBinding("?x".AsSpan());
                Assert.True(xIdx >= 0, "Should have ?x binding");
                var xValue = results.Current.GetString(xIdx).ToString();
                Assert.Equal("<http://www.example.org/instance#a>", xValue);
            }
            results.Dispose();
            Assert.Equal(1, count);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_Diagnostic_BoxedSubSelectRetrieval()
    {
        // Verify that the inner SubSelect is correctly retrieved after boxing/unboxing
        var doubleNestedQuery = @"
            PREFIX ex: <http://www.example.org/schema#>
            SELECT * WHERE {
                { SELECT * WHERE {
                    { SELECT ?x WHERE { ?x ex:q ?t } }
                } }
            }
        ";

        var parser = new SparqlParser(doubleNestedQuery.AsSpan());
        var parsed = parser.ParseQuery();

        // Get middle SubSelect from outer GroupPattern
        var middleSq = parsed.WhereClause.Pattern.GetSubQuery(0);

        // Verify middle SubSelect state
        Assert.True(middleSq.SelectAll, "Middle SelectAll should be true");
        Assert.True(middleSq.HasSubQueries, "Middle HasSubQueries should be true");
        Assert.Equal(1, middleSq.SubQueryCount);
        Assert.Equal(0, middleSq.PatternCount);

        // Get inner SubSelect from middle SubSelect (this involves unboxing)
        var innerSq = middleSq.GetSubQuery(0);

        // Verify inner SubSelect state after unboxing
        Assert.False(innerSq.SelectAll, "Inner SelectAll should be false");
        Assert.False(innerSq.HasSubQueries, "Inner HasSubQueries should be false (no more nesting)");
        Assert.Equal(0, innerSq.SubQueryCount);
        Assert.Equal(1, innerSq.PatternCount);
        Assert.Equal(1, innerSq.ProjectedVarCount);

        // Now simulate what BoxedSubQueryExecutor does for middle SubSelect
        // It should detect HasSubQueries && PatternCount == 0 and call ExecuteNestedSubQueries
        Assert.True(middleSq.HasSubQueries && middleSq.PatternCount == 0,
            "Middle SubSelect should trigger ExecuteNestedSubQueries path");
    }

    [Fact]
    public void SubQuery_Diagnostic_BoxedSubQueryExecutorDirectly()
    {
        // Test executing BoxedSubQueryExecutor directly on the middle SubSelect
        Store.AddCurrent("<http://www.example.org/instance#a>", "<http://www.example.org/schema#q>", "<http://www.example.org/instance#c>");

        var doubleNestedQuery = @"
            PREFIX ex: <http://www.example.org/schema#>
            SELECT * WHERE {
                { SELECT * WHERE {
                    { SELECT ?x WHERE { ?x ex:q ?t } }
                } }
            }
        ";

        Store.AcquireReadLock();
        try
        {
            var parser = new SparqlParser(doubleNestedQuery.AsSpan());
            var parsed = parser.ParseQuery();

            // Get middle SubSelect
            var middleSq = parsed.WhereClause.Pattern.GetSubQuery(0);

            // Get prefix mappings from prologue
            PrefixMapping[]? prefixes = null;
            if (parsed.Prologue.PrefixCount > 0)
            {
                prefixes = new PrefixMapping[parsed.Prologue.PrefixCount];
                for (int i = 0; i < parsed.Prologue.PrefixCount; i++)
                {
                    var (ps, pl, irs, irl) = parsed.Prologue.GetPrefix(i);
                    prefixes[i] = new PrefixMapping
                    {
                        PrefixStart = ps,
                        PrefixLength = pl,
                        IriStart = irs,
                        IriLength = irl
                    };
                }
            }

            // Create BoxedSubQueryExecutor directly for the middle SubSelect
            var executor = new BoxedSubQueryExecutor(Store, doubleNestedQuery, middleSq, prefixes);
            var results = executor.Execute();

            // Check results
            Assert.NotNull(results);
            Assert.Single(results);

            // Verify the result has ?x binding
            var row = results[0];
            var xValue = row.GetValueByName("?x".AsSpan());
            Assert.False(xValue.IsEmpty, "Result should have ?x binding");
            Assert.Equal("<http://www.example.org/instance#a>", xValue.ToString());
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_Diagnostic_SubQueryScanDirectly()
    {
        // Test SubQueryScan execution directly
        Store.AddCurrent("<http://www.example.org/instance#a>", "<http://www.example.org/schema#q>", "<http://www.example.org/instance#c>");

        var doubleNestedQuery = @"
            PREFIX ex: <http://www.example.org/schema#>
            SELECT * WHERE {
                { SELECT * WHERE {
                    { SELECT ?x WHERE { ?x ex:q ?t } }
                } }
            }
        ";

        Store.AcquireReadLock();
        try
        {
            var parser = new SparqlParser(doubleNestedQuery.AsSpan());
            var parsed = parser.ParseQuery();

            // Get middle SubSelect
            var middleSq = parsed.WhereClause.Pattern.GetSubQuery(0);

            // Get prefix mappings from prologue
            PrefixMapping[]? prefixes = null;
            if (parsed.Prologue.PrefixCount > 0)
            {
                prefixes = new PrefixMapping[parsed.Prologue.PrefixCount];
                for (int i = 0; i < parsed.Prologue.PrefixCount; i++)
                {
                    var (ps, pl, irs, irl) = parsed.Prologue.GetPrefix(i);
                    prefixes[i] = new PrefixMapping
                    {
                        PrefixStart = ps,
                        PrefixLength = pl,
                        IriStart = irs,
                        IriLength = irl
                    };
                }
            }

            // Create SubQueryScan and iterate through it
            var bindings = new Binding[16];
            var stringBuffer = new char[1024];
            var bindingTable = new BindingTable(bindings, stringBuffer);

            var subQueryScan = new SubQueryScan(Store, doubleNestedQuery.AsSpan(), middleSq, prefixes);
            var results = new List<MaterializedRow>();
            try
            {
                while (subQueryScan.MoveNext(ref bindingTable))
                {
                    results.Add(new MaterializedRow(bindingTable));
                }
            }
            finally
            {
                subQueryScan.Dispose();
            }

            // Check results
            Assert.Single(results);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_Diagnostic_InnerSubSelectPatternRefs()
    {
        // Verify that the inner SubSelect's pattern references are valid when retrieved from middle SubSelect
        var doubleNestedQuery = @"
            PREFIX ex: <http://www.example.org/schema#>
            SELECT * WHERE {
                { SELECT * WHERE {
                    { SELECT ?x WHERE { ?x ex:q ?t } }
                } }
            }
        ";

        var parser = new SparqlParser(doubleNestedQuery.AsSpan());
        var parsed = parser.ParseQuery();

        // Get the outer GroupPattern's subquery (middle SubSelect)
        var middleSq = parsed.WhereClause.Pattern.GetSubQuery(0);
        Assert.True(middleSq.SelectAll, "Middle should be SELECT *");
        Assert.Equal(0, middleSq.PatternCount);
        Assert.True(middleSq.HasSubQueries, "Middle should have nested subquery");

        // Get the inner SubSelect from the middle SubSelect
        var innerSq = middleSq.GetSubQuery(0);
        Assert.False(innerSq.SelectAll, "Inner should NOT be SELECT *");
        Assert.Equal(1, innerSq.PatternCount);
        Assert.Equal(1, innerSq.ProjectedVarCount);

        // Verify the inner pattern's term references point to valid positions in source
        var tp = innerSq.GetPattern(0);
        var source = doubleNestedQuery.AsSpan();

        // Subject should be ?x (variable)
        Assert.Equal(TermType.Variable, tp.Subject.Type);
        Assert.True(tp.Subject.Start >= 0 && tp.Subject.Start + tp.Subject.Length <= source.Length,
            $"Subject offset invalid: Start={tp.Subject.Start}, Length={tp.Subject.Length}, SourceLength={source.Length}");
        var subjStr = source.Slice(tp.Subject.Start, tp.Subject.Length).ToString();
        Assert.Equal("?x", subjStr);

        // Predicate should be ex:q (prefixed name)
        Assert.True(tp.Predicate.Start >= 0 && tp.Predicate.Start + tp.Predicate.Length <= source.Length,
            $"Predicate offset invalid: Start={tp.Predicate.Start}, Length={tp.Predicate.Length}, SourceLength={source.Length}");
        var predStr = source.Slice(tp.Predicate.Start, tp.Predicate.Length).ToString();
        Assert.Equal("ex:q", predStr);

        // Object should be ?t (variable)
        Assert.Equal(TermType.Variable, tp.Object.Type);
        Assert.True(tp.Object.Start >= 0 && tp.Object.Start + tp.Object.Length <= source.Length,
            $"Object offset invalid: Start={tp.Object.Start}, Length={tp.Object.Length}, SourceLength={source.Length}");
        var objStr = source.Slice(tp.Object.Start, tp.Object.Length).ToString();
        Assert.Equal("?t", objStr);
    }

    [Fact]
    public void SubQuery_Sq11_LimitPerResource_ExecutesCorrectly()
    {
        // W3C sq11 test case: Subquery with ORDER BY and LIMIT that limits by resource count
        // The subquery gets the first 2 orders, outer query joins to get item labels

        // Simplified test data (no blank node property lists to avoid stack issues)
        Store.AddCurrent("<http://www.example.org/order1>", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", "<http://www.example.org/Order>");
        Store.AddCurrent("<http://www.example.org/order1>", "<http://www.example.org/hasItem>", "<http://www.example.org/item1>");
        Store.AddCurrent("<http://www.example.org/item1>", "<http://www.w3.org/2000/01/rdf-schema#label>", "\"Ice Cream\"");

        Store.AddCurrent("<http://www.example.org/order2>", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", "<http://www.example.org/Order>");
        Store.AddCurrent("<http://www.example.org/order2>", "<http://www.example.org/hasItem>", "<http://www.example.org/item2>");
        Store.AddCurrent("<http://www.example.org/item2>", "<http://www.w3.org/2000/01/rdf-schema#label>", "\"Pizza\"");

        Store.AddCurrent("<http://www.example.org/order3>", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", "<http://www.example.org/Order>");
        Store.AddCurrent("<http://www.example.org/order3>", "<http://www.example.org/hasItem>", "<http://www.example.org/item3>");
        Store.AddCurrent("<http://www.example.org/item3>", "<http://www.w3.org/2000/01/rdf-schema#label>", "\"Sandwich\"");

        Store.AcquireReadLock();
        try
        {
            // Test full query with subquery (without blank node property list syntax)
            var fullQuery = """
                PREFIX : <http://www.example.org/>
                PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>

                SELECT ?L
                WHERE {
                  ?O :hasItem ?item .
                  ?item rdfs:label ?L .
                  {
                    SELECT DISTINCT ?O
                    WHERE { ?O a :Order }
                    ORDER BY ?O
                    LIMIT 2
                  }
                } ORDER BY ?L
                """;

            // Use ExecuteToMaterialized() to avoid stack overflow from ~22KB QueryResults struct
            var fullParser = new SparqlParser(fullQuery.AsSpan());
            var fullParsed = fullParser.ParseQuery();
            using var fullExecutor = new QueryExecutor(Store, fullQuery.AsSpan(), fullParsed);
            var fullResults = fullExecutor.ExecuteToMaterialized();

            var labels = new List<string>();
            while (fullResults.MoveNext())
            {
                var row = fullResults.Current;
                var idx = row.FindBinding("?L".AsSpan());
                if (idx >= 0)
                {
                    labels.Add(row.GetString(idx).ToString());
                }
            }
            fullResults.Dispose();

            // Expected: 2 labels from 2 orders (order1 has Ice Cream, order2 has Pizza)
            Assert.Equal(2, labels.Count);
            Assert.Contains("\"Ice Cream\"", labels);
            Assert.Contains("\"Pizza\"", labels);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    #endregion

    #region W3C sq07: Subquery with GRAPH clause

    [Fact]
    public void SubQuery_Sq07_GraphWithoutSubquery_Works()
    {
        // First verify GRAPH works without subquery
        var graphIri = "<http://example.org/named-graph>";
        Store.AddCurrent("<http://www.example.org/instance#a>", "<http://www.example.org/schema#p>", "<http://www.example.org/instance#b>", graphIri);
        Store.AddCurrent("<http://www.example.org/instance#c>", "<http://www.example.org/schema#p>", "\"value\"", graphIri);

        Store.AcquireReadLock();
        try
        {
            // Direct GRAPH query (no subquery)
            var query = """
                select ?x where {
                    graph ?g {?x ?p ?y}
                }
                """;

            var parser = new SparqlParser(query.AsSpan());
            var parsed = parser.ParseQuery();
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsed);
            var results = executor.Execute();

            var xValues = new List<string>();
            while (results.MoveNext())
            {
                var row = results.Current;
                var idx = row.FindBinding("?x".AsSpan());
                if (idx >= 0)
                {
                    xValues.Add(row.GetString(idx).ToString());
                }
            }
            results.Dispose();

            // Expected: 2 results (in:a and in:c)
            Assert.Equal(2, xValues.Count);
            Assert.Contains("<http://www.example.org/instance#a>", xValues);
            Assert.Contains("<http://www.example.org/instance#c>", xValues);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void SubQuery_Sq07_WithGraphInSubquery_ExecutesCorrectly()
    {
        // W3C sq07 test case: Subquery with GRAPH clause inside
        // Data is loaded into a named graph
        // Query: select ?x where { {select * where {graph ?g {?x ?p ?y}}} }

        // Load data into a named graph (simulating qt:graphData)
        var graphIri = "<http://example.org/named-graph>";
        Store.AddCurrent("<http://www.example.org/instance#a>", "<http://www.example.org/schema#p>", "<http://www.example.org/instance#b>", graphIri);
        Store.AddCurrent("<http://www.example.org/instance#c>", "<http://www.example.org/schema#p>", "\"value\"", graphIri);

        Store.AcquireReadLock();
        try
        {
            var query = """
                prefix ex: <http://www.example.org/schema#>
                prefix in: <http://www.example.org/instance#>

                select ?x where {
                    {select * where {graph ?g {?x ?p ?y}}}
                }
                """;

            // Use ExecuteToMaterialized() to avoid stack overflow from ~22KB QueryResults struct
            var parser = new SparqlParser(query.AsSpan());
            var parsed = parser.ParseQuery();
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsed);
            var results = executor.ExecuteToMaterialized();

            var xValues = new List<string>();
            while (results.MoveNext())
            {
                var row = results.Current;
                var idx = row.FindBinding("?x".AsSpan());
                if (idx >= 0)
                {
                    xValues.Add(row.GetString(idx).ToString());
                }
            }
            results.Dispose();

            // Get distinct values (there may be duplicates from cross-product)
            var distinct = xValues.Distinct().ToList();

            // Expected: 2 distinct results (in:a and in:c)
            Assert.Equal(2, distinct.Count);
            Assert.Contains("<http://www.example.org/instance#a>", distinct);
            Assert.Contains("<http://www.example.org/instance#c>", distinct);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Execute_AggregateSubqueryInsideVariableGraph_ReturnsCountPerGraph()
    {
        // W3C test: agg-empty-group-count-graph
        // GRAPH ?g { SELECT (count(*) AS ?c) WHERE { ?s :p ?x } }
        // Should return one row per named graph, even if the graph has 0 matches

        // Create named graphs
        Store.BeginBatch();
        // Empty graph - no matching triples with predicate :p
        Store.AddCurrentBatched("<http://example/placeholder>", "<http://example/other>", "\"placeholder\"", "<http://example/empty>");
        // Singleton graph - one matching triple with predicate :p
        Store.AddCurrentBatched("<http://example/s>", "<http://example/p>", "<http://example/o>", "<http://example/singleton>");
        Store.CommitBatch();

        var query = @"PREFIX : <http://example/>
SELECT ?g ?c WHERE {
   GRAPH ?g {SELECT (count(*) AS ?c) WHERE { ?s :p ?x }}
}";
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        // Verify parsing - subquery inside GRAPH should be detected
        Assert.True(parsedQuery.WhereClause.Pattern.HasSubQueries || parsedQuery.WhereClause.Pattern.HasGraph,
            "Query should have subqueries or GRAPH patterns");

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.ExecuteToMaterialized();

            var rows = new List<(string g, string c)>();
            while (results.MoveNext())
            {
                var row = results.Current;
                var gIdx = row.FindBinding("?g".AsSpan());
                var cIdx = row.FindBinding("?c".AsSpan());

                string g = gIdx >= 0 ? row.GetString(gIdx).ToString() : "(unbound)";
                string c = cIdx >= 0 ? row.GetString(cIdx).ToString() : "(unbound)";
                rows.Add((g, c));
            }
            results.Dispose();

            // Should have 2 rows: one for empty graph (count=0), one for singleton (count=1)
            Assert.Equal(2, rows.Count);

            // Find results by graph
            var emptyResult = rows.FirstOrDefault(r => r.g.Contains("empty"));
            var singletonResult = rows.FirstOrDefault(r => r.g.Contains("singleton"));

            // Verify both graphs returned results
            Assert.True(emptyResult != default, "Should have result for empty graph");
            Assert.True(singletonResult != default, "Should have result for singleton graph");

            // Empty graph should have count=0
            Assert.Contains("0", emptyResult.c);

            // Singleton graph should have count=1
            Assert.Contains("1", singletonResult.c);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    #endregion
}
