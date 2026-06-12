using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-047 — temporal SPARQL (AS OF / DURING / ALL VERSIONS) through the unified tree executor. The tree threads the
/// temporal mode + time bounds into TriplePatternScan, but the default-equiv-tree differential gate only exercised
/// the Current mode. The cutover routes temporal queries through the tree too, so they must produce the same bag as
/// the old path. Bitemporal data; queries at points across the valid-time history.
/// </summary>
public class TemporalDifferentialTests : IDisposable
{
    private readonly QuadStore _store;
    private readonly TempPath _testPath;

    public TemporalDifferentialTests()
    {
        var tempPath = TempPath.Test("temporal-diff");
        tempPath.MarkOwnership();
        _testPath = tempPath;
        _store = new QuadStore(_testPath);

        // Alice@Acme 2020-01-01..2023-06-30; Alice@Anthropic 2023-07-01..∞; Bob@Acme 2019-01-01..2022-12-31.
        _store.Add("<http://ex/alice>", "<http://ex/worksFor>", "<http://ex/Acme>",
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2023, 6, 30, 0, 0, 0, TimeSpan.Zero));
        _store.Add("<http://ex/alice>", "<http://ex/worksFor>", "<http://ex/Anthropic>",
            new DateTimeOffset(2023, 7, 1, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.MaxValue);
        _store.Add("<http://ex/bob>", "<http://ex/worksFor>", "<http://ex/Acme>",
            new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2022, 12, 31, 0, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        _store.Dispose();
        TempPath.SafeCleanup(_testPath);
    }

    [Theory]
    // expectedRows confirms the temporal filter is real (not all-or-nothing); the gate is old ≡ tree.
    [InlineData("AS OF 2021 — Alice@Acme + Bob@Acme", "SELECT ?p ?c WHERE { ?p <http://ex/worksFor> ?c } AS OF \"2021-06-15\"^^xsd:date", 2)]
    [InlineData("AS OF 2023-08 — Alice@Anthropic", "SELECT ?p ?c WHERE { ?p <http://ex/worksFor> ?c } AS OF \"2023-08-01\"^^xsd:date", 1)]
    [InlineData("AS OF 2025 — Alice@Anthropic", "SELECT ?p ?c WHERE { ?p <http://ex/worksFor> ?c } AS OF \"2025-01-01\"^^xsd:date", 1)]
    [InlineData("DURING 2020..2024 — all four facts", "SELECT ?p ?c WHERE { ?p <http://ex/worksFor> ?c } DURING [\"2020-01-01\"^^xsd:date, \"2024-01-01\"^^xsd:date]", -1)]
    [InlineData("ALL VERSIONS — every version", "SELECT ?p ?c WHERE { ?p <http://ex/worksFor> ?c } ALL VERSIONS", -1)]
    public void Temporal_DefaultEquivTree(string name, string query, int expectedRows)
    {
        var old = SparqlEngine.Query(_store, query);
        var tree = SparqlEngine.QueryViaTreeForDifferential(_store, query);

        Assert.True(old.Success, $"[{name}] old: {old.ErrorMessage}");
        Assert.True(tree.Success, $"[{name}] tree: {tree.ErrorMessage}");
        Assert.Equal(Canonicalize(old.Rows), Canonicalize(tree.Rows)); // the cutover must not change temporal results
        if (expectedRows >= 0)
            Assert.Equal(expectedRows, tree.Rows!.Count);
    }

    private static List<string> Canonicalize(List<Dictionary<string, string>>? rows)
    {
        var canon = new List<string>();
        if (rows is null) return canon;
        foreach (var row in rows)
            canon.Add(string.Join("|", row.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value)));
        canon.Sort(StringComparer.Ordinal);
        return canon;
    }
}
