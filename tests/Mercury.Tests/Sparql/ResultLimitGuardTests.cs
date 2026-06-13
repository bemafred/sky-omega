using System;
using Xunit;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// The unbounded-result guard (docs/limits/unbounded-result-materialization.md). A query that would materialize more
/// than <see cref="StorageOptions.MaxResultRows"/> rows fails FAST with a clear error instead of OOMing — the field's
/// row-cap answer (Virtuoso's <c>ResultSetMaxRows</c>), chosen as the ship-gate for the global tool. The cap is
/// checked at every row-accumulation point, because ORDER BY / GROUP BY / the tree materialize the whole set BEFORE
/// the result drain ever runs: a drain-only check would already have OOMed. This pins all four sites.
/// </summary>
public class ResultLimitGuardTests : IDisposable
{
    private readonly QuadStore _store;          // cap = 5, holds 20 rows
    private readonly TempPath _testPath;

    public ResultLimitGuardTests()
    {
        var tempPath = TempPath.Test("result-limit-guard");
        tempPath.MarkOwnership();
        _testPath = tempPath;
        _store = new QuadStore(_testPath, null, null, new StorageOptions { MaxResultRows = 5 });

        _store.BeginBatch();
        for (int i = 0; i < 20; i++)
            _store.AddCurrentBatched($"<urn:s{i:D2}>", "<urn:p>", $"<urn:o{i:D2}>");
        _store.CommitBatch();
    }

    public void Dispose()
    {
        _store.Dispose();
        TempPath.SafeCleanup(_testPath);
    }

    [Theory]
    // Each shape accumulates 20 rows where the cap is 5 — a different accumulation site for each.
    [InlineData("plain SELECT (the result drain)", "SELECT ?s WHERE { ?s <urn:p> ?o }")]
    [InlineData("ORDER BY (collect-before-sort)", "SELECT ?o WHERE { ?s <urn:p> ?o } ORDER BY ?o")]
    [InlineData("GROUP BY (group explosion)", "SELECT ?s (COUNT(?o) AS ?c) WHERE { ?s <urn:p> ?o } GROUP BY ?s")]
    public void OverCap_FailsFastWithAClearError(string shape, string query)
    {
        var result = SparqlEngine.Query(_store, query);

        Assert.False(result.Success, $"[{shape}] expected the guard to trip");
        Assert.Contains("exceeded the maximum of 5 rows", result.ErrorMessage);
    }

    [Fact]
    public void OverCap_OnTheTreePath_AlsoFailsFast()
    {
        // The tree materializes its BGP intermediate before the drain too (the GRAPH path, and the cutover target).
        var result = SparqlEngine.QueryViaTreeForDifferential(_store, "SELECT ?s WHERE { ?s <urn:p> ?o }");

        Assert.False(result.Success);
        Assert.Contains("exceeded the maximum of 5 rows", result.ErrorMessage);
    }

    [Fact]
    public void UnderCap_Succeeds()
    {
        // LIMIT 3 keeps the result at or below the cap of 5 — no trip.
        var result = SparqlEngine.Query(_store, "SELECT ?s WHERE { ?s <urn:p> ?o } LIMIT 3");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, result.Rows!.Count);
    }

    [Fact]
    public void Unbounded_OptOut_DoesNotTrip()
    {
        // MaxResultRows = 0 is the explicit opt-out — the same 20-row query returns all 20.
        var path = TempPath.Test("result-limit-unbounded");
        try
        {
            using var store = new QuadStore(path, null, null, new StorageOptions { MaxResultRows = 0 });
            store.BeginBatch();
            for (int i = 0; i < 20; i++)
                store.AddCurrentBatched($"<urn:s{i:D2}>", "<urn:p>", $"<urn:o{i:D2}>");
            store.CommitBatch();

            var result = SparqlEngine.Query(store, "SELECT ?s WHERE { ?s <urn:p> ?o }");
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(20, result.Rows!.Count);
        }
        finally
        {
            TempPath.SafeCleanup(path);
        }
    }

    [Fact]
    public void ThrowIfExceeded_TripsOnlyWhenCountExceedsAPositiveCap()
    {
        ResultLimitExceededException.ThrowIfExceeded(0, 1_000_000);   // 0 = unbounded — never throws
        ResultLimitExceededException.ThrowIfExceeded(5, 5);           // count == cap is allowed
        var ex = Assert.Throws<ResultLimitExceededException>(() => ResultLimitExceededException.ThrowIfExceeded(5, 6));
        Assert.Equal(5, ex.MaxResultRows);
        Assert.Equal(6, ex.RowsProduced);
    }
}
