using System;
using System.Linq;
using Xunit;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// A WHERE clause that is JUST a VALUES block (inline data, no triple patterns) produces the VALUES rows — including
/// MULTI-variable rows. Through the facade this returned nothing: the TriplePatternCount==0 dispatch handled only
/// aggregate / BIND / FILTER / EXISTS expressions, not VALUES, so it fell through to Empty(). Now routed through the
/// unified tree executor, which materializes the rows (and threads ORDER BY / LIMIT / DISTINCT) correctly.
/// </summary>
public class ValuesOnlyTests : IDisposable
{
    private readonly QuadStore _store;
    private readonly TempPath _testPath;

    public ValuesOnlyTests()
    {
        var tempPath = TempPath.Test("values-only");
        tempPath.MarkOwnership();
        _testPath = tempPath;
        _store = new QuadStore(_testPath); // empty — VALUES is inline data, independent of the store
    }

    public void Dispose()
    {
        _store.Dispose();
        TempPath.SafeCleanup(_testPath);
    }

    [Fact]
    public void SingleVariable_ReturnsEachRow()
    {
        var result = SparqlEngine.Query(_store, "SELECT ?a WHERE { VALUES ?a { <urn:1> <urn:2> <urn:3> } }");
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "<urn:1>", "<urn:2>", "<urn:3>" },
            result.Rows!.Select(r => r["a"]).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void MultiVariable_ReturnsEachRowWithAllVariables()
    {
        var result = SparqlEngine.Query(_store, "SELECT * WHERE { VALUES (?a ?b) { (<urn:1> <urn:x>) (<urn:2> <urn:y>) } }");
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.Rows!.Count);
        var byA = result.Rows!.ToDictionary(r => r["a"], r => r["b"]);
        Assert.Equal("<urn:x>", byA["<urn:1>"]);
        Assert.Equal("<urn:y>", byA["<urn:2>"]);
    }

    [Fact]
    public void MultiVariable_WithUndef_StillReturnsTheRows()
    {
        // UNDEF leaves that variable unbound in its row, but the row is still produced.
        var result = SparqlEngine.Query(_store, "SELECT * WHERE { VALUES (?a ?b) { (<urn:1> UNDEF) (<urn:2> <urn:y>) } }");
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.Rows!.Count);
    }

    [Fact]
    public void WithOrderByAndLimit_AppliesModifiers()
    {
        var result = SparqlEngine.Query(_store, "SELECT ?a WHERE { VALUES ?a { <urn:3> <urn:1> <urn:2> } } ORDER BY ?a LIMIT 2");
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "<urn:1>", "<urn:2>" }, result.Rows!.Select(r => r["a"]));
    }
}
