using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// End-to-end SPARQL SELECT / ASK / COUNT against a Reference-profile QuadStore.
/// ADR-029 Phase 2d: QueryCurrent routes through the Reference index family via a
/// TemporalResultEnumerator in Reference mode. Temporal fields in rows are synthesized
/// (MinValue / MaxValue) — SPARQL doesn't observe them for non-temporal queries.
/// </summary>
public class SparqlAgainstReferenceProfileTests : IDisposable
{
    private readonly string _testDir;

    public SparqlAgainstReferenceProfileTests()
    {
        var tempPath = TempPath.Test("sparqlref");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    private static async Task<QuadStore> BulkLoadNTriplesAsync(string storeDir, string nt)
    {
        var store = new QuadStore(storeDir, null, null, new StorageOptions { Profile = StoreProfile.Reference });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(nt));
        await RdfEngine.LoadStreamingAsync(store, stream, RdfFormat.NTriples).ConfigureAwait(false);
        // ADR-030 Decision 5: Reference bulk-load writes only GSPO. Rebuild populates
        // GPOS and trigram — required before predicate-bound SPARQL queries (which
        // route through GPOS) can return correct results.
        store.RebuildSecondaryIndexes();
        return store;
    }

    private const string SampleData = """
        <http://ex.org/alice> <http://ex.org/name> "Alice" .
        <http://ex.org/alice> <http://ex.org/age> "30" .
        <http://ex.org/bob> <http://ex.org/name> "Bob" .
        <http://ex.org/bob> <http://ex.org/age> "25" .
        <http://ex.org/carol> <http://ex.org/name> "Carol" .
        <http://ex.org/alice> <http://ex.org/knows> <http://ex.org/bob> .
        <http://ex.org/alice> <http://ex.org/knows> <http://ex.org/carol> .
        """;

    [Fact]
    public async Task Select_PredicateBound_UsesGposAndReturnsAllBindings()
    {
        var dir = Path.Combine(_testDir, "sel_pred");
        Directory.CreateDirectory(dir);
        using var store = await BulkLoadNTriplesAsync(dir, SampleData);

        var result = SparqlEngine.Query(store,
            "SELECT ?s ?name WHERE { ?s <http://ex.org/name> ?name }");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(ExecutionResultKind.Select, result.Kind);
        Assert.NotNull(result.Rows);
        Assert.Equal(3, result.Rows!.Count);
    }

    [Fact]
    public async Task Select_SubjectBound_UsesGspoAndReturnsAllBindings()
    {
        var dir = Path.Combine(_testDir, "sel_subj");
        Directory.CreateDirectory(dir);
        using var store = await BulkLoadNTriplesAsync(dir, SampleData);

        var result = SparqlEngine.Query(store,
            "SELECT ?p ?o WHERE { <http://ex.org/alice> ?p ?o }");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Rows);
        // alice has: name, age, knows-bob, knows-carol = 4 rows.
        Assert.Equal(4, result.Rows!.Count);
    }

    [Fact]
    public async Task Select_CountAll_MatchesBulkLoadTotal()
    {
        var dir = Path.Combine(_testDir, "sel_count");
        Directory.CreateDirectory(dir);
        using var store = await BulkLoadNTriplesAsync(dir, SampleData);

        var result = SparqlEngine.Query(store,
            "SELECT (COUNT(*) AS ?n) WHERE { ?s ?p ?o }");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Rows);
        Assert.Single(result.Rows!);
        // Seven triples in the sample data, all should count.
        var countValue = result.Rows![0].Values.First();
        Assert.Contains("7", countValue);
    }

    [Fact]
    public async Task Ask_MatchingPattern_ReturnsTrue()
    {
        var dir = Path.Combine(_testDir, "ask_ok");
        Directory.CreateDirectory(dir);
        using var store = await BulkLoadNTriplesAsync(dir, SampleData);

        var result = SparqlEngine.Query(store,
            "ASK { <http://ex.org/alice> <http://ex.org/knows> <http://ex.org/bob> }");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(ExecutionResultKind.Ask, result.Kind);
        Assert.True(result.AskResult);
    }

    [Fact]
    public async Task Ask_MissingPattern_ReturnsFalse()
    {
        var dir = Path.Combine(_testDir, "ask_missing");
        Directory.CreateDirectory(dir);
        using var store = await BulkLoadNTriplesAsync(dir, SampleData);

        var result = SparqlEngine.Query(store,
            "ASK { <http://ex.org/dave> <http://ex.org/name> ?name }");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(result.AskResult);
    }

    [Fact]
    public async Task Select_Join_TwoPatternsOnSubject_ReturnsCorrectRows()
    {
        var dir = Path.Combine(_testDir, "sel_join");
        Directory.CreateDirectory(dir);
        using var store = await BulkLoadNTriplesAsync(dir, SampleData);

        var result = SparqlEngine.Query(store,
            "SELECT ?s ?name ?age WHERE { ?s <http://ex.org/name> ?name . ?s <http://ex.org/age> ?age }");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Rows);
        // alice and bob have both name and age; carol has only name. Join => 2 rows.
        Assert.Equal(2, result.Rows!.Count);
    }

    [Fact]
    public async Task Select_AcrossReopen_PersistsCorrectly()
    {
        var dir = Path.Combine(_testDir, "sel_reopen");
        Directory.CreateDirectory(dir);

        using (var store = await BulkLoadNTriplesAsync(dir, SampleData))
        {
            Assert.Equal(7, store.GetStatistics().QuadCount);
        }

        using (var reopened = new QuadStore(dir))
        {
            Assert.Equal(StoreProfile.Reference, reopened.Schema.Profile);
            var result = SparqlEngine.Query(reopened,
                "SELECT (COUNT(*) AS ?n) WHERE { ?s ?p ?o }");
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Single(result.Rows!);
            Assert.Contains("7", result.Rows![0].Values.First());
        }
    }
}
