using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-047 — the tree executor must produce the same bag as the old path on EVERY storage profile, not just the
/// default. Parsing is profile-independent (the parser has no profile awareness); execution is not — the scan
/// delegates to profile-dispatched indexes. This runs a representative query set through both executors on each
/// WRITABLE profile (Minimal, Cognitive, Graph). The Reference profile loads via the sealed bulk path and is
/// validated separately (SparqlAgainstReferenceProfileTests + the scale run); it is also the only profile that
/// carries the 21.3B Wikidata load.
/// </summary>
public class ProfileEquivalenceDifferentialTests
{
    private static readonly string[] Queries =
    {
        "SELECT ?s ?o WHERE { ?s <urn:p> ?o }",
        "SELECT ?s ?x WHERE { ?s <urn:p> ?o . ?o <urn:next> ?x }",
        "SELECT ?s WHERE { ?s <urn:p> ?o FILTER(?o = <urn:v1>) }",
        "SELECT ?s ?x WHERE { ?s <urn:p> ?o OPTIONAL { ?o <urn:next> ?x } }",
        "SELECT (COUNT(?o) AS ?c) WHERE { ?s <urn:p> ?o }",
        "SELECT ?x WHERE { <urn:v1> <urn:next>* ?x }",
        "SELECT ?o WHERE { VALUES ?s { <urn:a> } ?s <urn:p> ?o }",
    };

    [Theory]
    [InlineData(StoreProfile.Minimal)]
    [InlineData(StoreProfile.Cognitive)]
    [InlineData(StoreProfile.Graph)]
    public void TheTreeMatchesTheOldPath_OnEveryWritableProfile(StoreProfile profile)
    {
        var path = TempPath.Test($"profile-{profile}");
        try
        {
            using var store = new QuadStore(path, null, null, new StorageOptions { Profile = profile });
            store.BeginBatch();
            store.AddCurrentBatched("<urn:a>", "<urn:p>", "<urn:v1>");
            store.AddCurrentBatched("<urn:b>", "<urn:p>", "<urn:v2>");
            store.AddCurrentBatched("<urn:v1>", "<urn:next>", "<urn:v2>");
            store.CommitBatch();

            foreach (var query in Queries)
            {
                var old = SparqlEngine.Query(store, query);
                var tree = SparqlEngine.QueryViaTreeForDifferential(store, query);

                Assert.True(old.Success, $"[{profile}] old failed: {query} — {old.ErrorMessage}");
                Assert.True(tree.Success, $"[{profile}] tree failed: {query} — {tree.ErrorMessage}");
                Assert.Equal(Canonicalize(old.Rows), Canonicalize(tree.Rows));
            }
        }
        finally
        {
            TempPath.SafeCleanup(path);
        }
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
