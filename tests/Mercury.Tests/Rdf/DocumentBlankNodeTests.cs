using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Rdf;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// RDF scopes a blank-node label to its document (RDF 1.1 Concepts §3.4): two documents that both write <c>_:n</c> mean two
/// nodes. Through 1.8.28 every load into a store merged them: the N-Triples and N-Quads parsers keep labels as written,
/// and the other parsers number blank nodes from <c>_:b0</c> per document. A load now gives each document a
/// <see cref="BlankNodeScope"/>.
/// </summary>
[Collection("QuadStore")]
public class DocumentBlankNodeTests : PooledStoreTestBase
{
    public DocumentBlankNodeTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Fact]
    public async Task LoadAsync_TwoNTriplesDocumentsWithTheSameLabel_AreTwoNodes()
    {
        await RdfEngine.LoadAsync(Store, new MemoryStream(Encoding.UTF8.GetBytes("<http://ex/a> <http://ex/p> _:n .\n")), RdfFormat.NTriples);
        await RdfEngine.LoadAsync(Store, new MemoryStream(Encoding.UTF8.GetBytes("<http://ex/b> <http://ex/p> _:n .\n")), RdfFormat.NTriples);

        var result = SparqlEngine.Query(Store, "SELECT DISTINCT ?o WHERE { ?s <http://ex/p> ?o }", TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Rows!.Count);
    }

    [Fact]
    public async Task LoadAsync_TwoTurtleDocumentsWithPropertyLists_AreTwoNodes()
    {
        // Each parse numbers its nodes from _:b0; both lists were one node holding :q 1 and :q 2
        await RdfEngine.LoadAsync(Store, new MemoryStream(Encoding.UTF8.GetBytes("@prefix : <http://ex/> . :c :p [ :q 1 ] .")), RdfFormat.Turtle);
        await RdfEngine.LoadAsync(Store, new MemoryStream(Encoding.UTF8.GetBytes("@prefix : <http://ex/> . :d :p [ :q 2 ] .")), RdfFormat.Turtle);

        var result = SparqlEngine.Query(Store, "SELECT ?s (COUNT(?v) AS ?n) WHERE { ?s <http://ex/p> ?b . ?b <http://ex/q> ?v } GROUP BY ?s",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.Rows!.Count);
        Assert.All(result.Rows, r => Assert.StartsWith("\"1\"", r["n"]));
    }

    [Fact]
    public async Task LoadAsync_ALabelUsedTwiceInOneDocument_IsOneNode()
    {
        await RdfEngine.LoadAsync(Store, new MemoryStream(Encoding.UTF8.GetBytes(
            "<http://ex/a> <http://ex/p> _:n .\n<http://ex/b> <http://ex/p> _:n .\n_:n <http://ex/q> \"v\" .\n")), RdfFormat.NTriples);

        var result = SparqlEngine.Query(Store, "SELECT ?s WHERE { ?s <http://ex/p> ?n . ?n <http://ex/q> \"v\" }",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Rows!.Count);
    }

    [Fact]
    public async Task LoadFileAsync_TwoNQuadsFiles_KeepTheirNodesAndGraphsApart()
    {
        var dir = Directory.CreateTempSubdirectory("bnode-docs");
        try
        {
            var first = Path.Combine(dir.FullName, "first.nq");
            var second = Path.Combine(dir.FullName, "second.nq");
            await File.WriteAllTextAsync(first, "<http://ex/a> <http://ex/p> _:n _:g .\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(second, "<http://ex/b> <http://ex/p> _:n _:g .\n", TestContext.Current.CancellationToken);

            await RdfEngine.LoadFileAsync(Store, first);
            await RdfEngine.LoadFileAsync(Store, second);
            var nodes = SparqlEngine.Query(Store, "SELECT DISTINCT ?o WHERE { GRAPH ?g { ?s <http://ex/p> ?o } }",
                TestContext.Current.CancellationToken);
            var graphs = SparqlEngine.Query(Store, "SELECT DISTINCT ?g WHERE { GRAPH ?g { ?s ?p ?o } }",
                TestContext.Current.CancellationToken);

            Assert.Equal(2, nodes.Rows!.Count);
            Assert.Equal(2, graphs.Rows!.Count);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SparqlLoad_TwoDocuments_AreTwoNodes()
    {
        var dir = Directory.CreateTempSubdirectory("bnode-load");
        try
        {
            var first = Path.Combine(dir.FullName, "first.ttl");
            var second = Path.Combine(dir.FullName, "second.ttl");
            await File.WriteAllTextAsync(first, "<http://ex/a> <http://ex/p> _:n .", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(second, "<http://ex/b> <http://ex/p> _:n .", TestContext.Current.CancellationToken);

            var load = SparqlEngine.Update(Store, $"LOAD <file://{first}> ; LOAD <file://{second}>", TestContext.Current.CancellationToken);
            var result = SparqlEngine.Query(Store, "SELECT DISTINCT ?o WHERE { ?s <http://ex/p> ?o }", TestContext.Current.CancellationToken);

            Assert.True(load.Success, load.ErrorMessage);
            Assert.Equal(2, result.Rows!.Count);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Token_Is22LabelCharacters_AndSortsByTheGuidsTime()
    {
        var earlier = BlankNodeScope.Token(Guid.CreateVersion7(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero)));
        var later = BlankNodeScope.Token(Guid.CreateVersion7(new DateTimeOffset(2026, 10, 28, 9, 0, 1, TimeSpan.Zero)));

        Assert.Equal(22, earlier.Length);
        Assert.All(earlier, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }

    [Fact]
    public void Scope_ALabel_IsTheScopesNode_AndOtherTermsAreUnchanged()
    {
        var scope = new BlankNodeScope();

        var node = scope.Subject("_:n").ToString();

        Assert.StartsWith("_:g", node);
        Assert.EndsWith("n", node);
        Assert.Equal(node, scope.Object("_:n").ToString());
        Assert.NotEqual(node, new BlankNodeScope().Subject("_:n").ToString());
        Assert.Equal("<http://ex/a>", scope.Subject("<http://ex/a>").ToString());
        Assert.Equal("\"_:n\"", scope.Object("\"_:n\"").ToString());
    }
}
