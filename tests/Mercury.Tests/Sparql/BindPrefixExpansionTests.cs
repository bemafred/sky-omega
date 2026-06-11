using System;
using Xunit;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// BIND(prefixedName AS ?v) expands a prologue prefix to its full IRI. BindExpressionEvaluator previously handled
/// only the hardcoded xsd/rdf/rdfs prefixes — it was never passed the prologue PrefixMapping[], so a user prefix
/// bound null. Now the prefixes are threaded through every BIND-execution path (default, GRAPH, sub-SELECT,
/// multi-pattern), and together with the PN_LOCAL tokenizer fix a hyphenated local name resolves too.
/// </summary>
public class BindPrefixExpansionTests : IDisposable
{
    private readonly QuadStore _store;
    private readonly TempPath _testPath;

    public BindPrefixExpansionTests()
    {
        var tempPath = TempPath.Test("bind-prefix");
        tempPath.MarkOwnership();
        _testPath = tempPath;
        _store = new QuadStore(_testPath);

        _store.BeginBatch();
        _store.AddCurrentBatched("<urn:x>", "<urn:p>", "<urn:o>");
        _store.AddCurrentBatched("<urn:x>", "<urn:p>", "<urn:o>", "<urn:g>");
        _store.CommitBatch();
    }

    public void Dispose()
    {
        _store.Dispose();
        TempPath.SafeCleanup(_testPath);
    }

    [Theory]
    [InlineData("default path, plain local", "PREFIX ex: <urn:> SELECT ?b WHERE { ?x <urn:p> ?o BIND(ex:abc AS ?b) }", "<urn:abc>")]
    [InlineData("default path, hyphenated local", "PREFIX ex: <urn:> SELECT ?b WHERE { ?x <urn:p> ?o BIND(ex:a-b-c AS ?b) }", "<urn:a-b-c>")]
    [InlineData("GRAPH path, hyphenated local", "PREFIX ex: <urn:> SELECT ?b WHERE { GRAPH <urn:g> { ?x <urn:p> ?o } BIND(ex:a-b-c AS ?b) }", "<urn:a-b-c>")]
    [InlineData("empty prefix", "PREFIX : <urn:> SELECT ?b WHERE { ?x <urn:p> ?o BIND(:abc AS ?b) }", "<urn:abc>")]
    public void Bind_PrefixedNameConstant_ExpandsToIri(string name, string query, string expected)
    {
        var result = SparqlEngine.Query(_store, query);
        Assert.True(result.Success, $"[{name}] {result.ErrorMessage}");
        var row = Assert.Single(result.Rows!);
        Assert.Equal(expected, row["b"]);
    }
}
