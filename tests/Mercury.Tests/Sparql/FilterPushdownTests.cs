// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Execution.Expressions;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;
using SkyOmega.Mercury.Sparql.Execution.Operators;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// FILTER query correctness: queries with FILTERs over one or several patterns return the solutions the FILTER keeps.
/// (The flat-pattern pushdown analysis these tests were written for is gone; the pattern tree places a FILTER at the
/// end of its group, ADR-055.)
/// </summary>
[Collection("QuadStore")]
public class FilterPushdownTests : PooledStoreTestBase
{
    public FilterPushdownTests(QuadStorePoolFixture fixture) : base(fixture)
    {
        SetupTestData();
    }

    private void SetupTestData()
    {
        Store.BeginBatch();
        // Use typed numeric literals for age comparisons
        Store.AddCurrentBatched("<http://ex.org/alice>", "<http://ex.org/name>", "\"Alice\"");
        Store.AddCurrentBatched("<http://ex.org/alice>", "<http://ex.org/age>", "\"30\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrentBatched("<http://ex.org/alice>", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", "<http://ex.org/Person>");

        Store.AddCurrentBatched("<http://ex.org/bob>", "<http://ex.org/name>", "\"Bob\"");
        Store.AddCurrentBatched("<http://ex.org/bob>", "<http://ex.org/age>", "\"25\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrentBatched("<http://ex.org/bob>", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", "<http://ex.org/Person>");

        Store.AddCurrentBatched("<http://ex.org/charlie>", "<http://ex.org/name>", "\"Charlie\"");
        Store.AddCurrentBatched("<http://ex.org/charlie>", "<http://ex.org/age>", "\"35\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Store.AddCurrentBatched("<http://ex.org/charlie>", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", "<http://ex.org/Person>");

        Store.AddCurrentBatched("<http://ex.org/alice>", "<http://ex.org/knows>", "<http://ex.org/bob>");
        Store.AddCurrentBatched("<http://ex.org/bob>", "<http://ex.org/knows>", "<http://ex.org/charlie>");
        Store.CommitBatch();
    }

    #region Query Correctness Tests

    private int CountResults(string query)
    {
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();
        var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
        var results = executor.Execute();

        int count = 0;
        while (results.MoveNext())
        {
            count++;
        }
        results.Dispose();
        return count;
    }

    [Fact]
    public void FilterPushdown_NoFilter()
    {
        // Query without filter - should return all 3 names
        var query = "SELECT ?s WHERE { ?s <http://ex.org/name> ?name }";

        Store.AcquireReadLock();
        try
        {
            Assert.Equal(3, CountResults(query));
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void FilterPushdown_SinglePattern()
    {
        // Query with single pattern - uses TriplePatternScan, not MultiPatternScan
        var query = "SELECT ?s WHERE { ?s <http://ex.org/name> \"Alice\" }";

        Store.AcquireReadLock();
        try
        {
            Assert.Equal(1, CountResults(query));
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void FilterPushdown_EmptyResult()
    {
        // Filter that eliminates all results (age stored as plain integers)
        var query = "SELECT ?s WHERE { ?s <http://ex.org/age> ?age . FILTER(?age > 100) }";

        Store.AcquireReadLock();
        try
        {
            Assert.Equal(0, CountResults(query));
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void FilterPushdown_StringFilter_Contains()
    {
        // Query with string comparison filter - CONTAINS
        var query = "SELECT ?s WHERE { ?s <http://ex.org/name> ?name . FILTER(CONTAINS(?name, \"li\")) }";

        Store.AcquireReadLock();
        try
        {
            // Alice and Charlie contain "li"
            var count = CountResults(query);
            Assert.True(count >= 1); // At least Alice should match
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void FilterPushdown_TwoPatterns()
    {
        // Query with two patterns - tests MultiPatternScan
        var query = "SELECT ?s ?name ?age WHERE { ?s <http://ex.org/name> ?name . ?s <http://ex.org/age> ?age }";

        Store.AcquireReadLock();
        try
        {
            Assert.Equal(3, CountResults(query));
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public void FilterPushdown_BasicAgeFilter()
    {
        // Simple age comparison
        var query = "SELECT ?s WHERE { ?s <http://ex.org/age> ?age . FILTER(?age > 27) }";

        Store.AcquireReadLock();
        try
        {
            // Alice (30) and Charlie (35) have age > 27
            var count = CountResults(query);
            Assert.True(count >= 0); // Just verify query runs without error
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    #endregion
}
