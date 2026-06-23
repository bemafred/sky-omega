using System;
using Xunit;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// W3C SPARQL 1.1 §4.1.3: a prefixed name whose prefix is not declared is a STATIC ERROR. Mercury
/// previously expanded an undefined prefix to the unchanged token, which matched no atom and returned an
/// empty (silently-wrong) result. The converged <c>PrefixExpander</c> now throws; <c>SparqlEngine</c>
/// surfaces it as a failed query with a clear message. Pinned across term positions so no resolver path
/// regresses back to the silent-empty behaviour.
/// </summary>
public class UndefinedPrefixTests : IDisposable
{
    private readonly QuadStore _store;
    private readonly TempPath _testPath;

    public UndefinedPrefixTests()
    {
        var tempPath = TempPath.Test("undefined-prefix");
        tempPath.MarkOwnership();
        _testPath = tempPath;
        _store = new QuadStore(_testPath);
        _store.BeginBatch();
        _store.AddCurrentBatched("<urn:s>", "<urn:p>", "<urn:o>");
        _store.CommitBatch();
    }

    public void Dispose()
    {
        _store.Dispose();
        TempPath.SafeCleanup(_testPath);
    }

    [Theory]
    [InlineData("predicate", "SELECT ?s WHERE { ?s zzz:undef ?o }")]
    [InlineData("subject", "SELECT ?o WHERE { zzz:undef <urn:p> ?o }")]
    [InlineData("object", "SELECT ?s WHERE { ?s <urn:p> zzz:undef }")]
    public void UndefinedPrefix_FailsWithClearError(string position, string query)
    {
        var result = SparqlEngine.Query(_store, query);
        Assert.False(result.Success, $"[{position}] an undefined prefix must error, not return empty");
        Assert.Contains("Undefined prefix", result.ErrorMessage ?? "");
    }

    [Fact]
    public void DeclaredPrefix_StillResolves()
    {
        var result = SparqlEngine.Query(_store, "PREFIX p: <urn:> SELECT ?o WHERE { p:s p:p ?o }");
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Single(result.Rows!);
    }
}
