using System;
using Xunit;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// Prefixed names with hyphens (and internal dots) in FILTER expressions. The SPARQL PN_LOCAL grammar allows '-'
/// (PN_CHARS) and an internal '.' in a local name, but the expression tokenizer stopped at '-', reading
/// <c>p:a-b-c</c> as the arithmetic <c>p:a - b - c</c> — so <c>FILTER(?v = ck:obs-graph-limit-pushdown)</c> matched
/// nothing while the very same prefixed name worked as a BGP term. Position-independent, and (since the FILTER
/// evaluator is shared) the same on the default and GRAPH paths.
/// </summary>
public class FilterPrefixedNameTests : IDisposable
{
    private readonly QuadStore _store;
    private readonly TempPath _testPath;

    public FilterPrefixedNameTests()
    {
        var tempPath = TempPath.Test("filter-pname");
        tempPath.MarkOwnership();
        _testPath = tempPath;
        _store = new QuadStore(_testPath);

        _store.BeginBatch();
        _store.AddCurrentBatched("<urn:x>", "<urn:p>", "<http://ex/a-b-c>");            // hyphenated local part
        _store.AddCurrentBatched("<urn:y>", "<urn:p>", "<http://ex/abc>");              // plain local part
        _store.AddCurrentBatched("<urn:z>", "<urn:p>", "<http://ex/a.b>");              // internal dot
        _store.AddCurrentBatched("<urn:x>", "<urn:p>", "<http://ex/a-b-c>", "<urn:g>"); // also in a named graph
        _store.CommitBatch();
    }

    public void Dispose()
    {
        _store.Dispose();
        TempPath.SafeCleanup(_testPath);
    }

    [Theory]
    [InlineData("hyphenated PNAME", "PREFIX ex: <http://ex/> SELECT ?x WHERE { ?x <urn:p> ?o FILTER(?o = ex:a-b-c) }", "<urn:x>")]
    [InlineData("internal-dot PNAME", "PREFIX ex: <http://ex/> SELECT ?x WHERE { ?x <urn:p> ?o FILTER(?o = ex:a.b) }", "<urn:z>")]
    [InlineData("plain PNAME (control)", "PREFIX ex: <http://ex/> SELECT ?x WHERE { ?x <urn:p> ?o FILTER(?o = ex:abc) }", "<urn:y>")]
    [InlineData("hyphenated PNAME inside GRAPH", "PREFIX ex: <http://ex/> SELECT ?x WHERE { GRAPH <urn:g> { ?x <urn:p> ?o FILTER(?o = ex:a-b-c) } }", "<urn:x>")]
    public void FilterComparand_PrefixedName_ResolvesWithHyphensAndDots(string name, string query, string expected)
    {
        var result = SparqlEngine.Query(_store, query);
        Assert.True(result.Success, $"[{name}] {result.ErrorMessage}");
        Assert.Equal(expected, Assert.Single(result.Rows!)["x"]);
    }

    [Fact]
    public void Subtraction_WithSurroundingSpaces_IsUnaffected()
    {
        // A genuine subtraction keeps whitespace around the operator; the prefixed-name tokenizer only consumes a
        // hyphen with no surrounding whitespace, so arithmetic is unaffected by the PN_LOCAL fix.
        var result = SparqlEngine.Query(_store, "SELECT ?x WHERE { ?x <urn:p> ?o FILTER(10 - 3 = 7) }");
        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEmpty(result.Rows!);
    }
}
