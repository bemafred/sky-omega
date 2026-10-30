// GetRepresentationTests.cs
// The body of a Solid GET is UTF-8 with no byte order mark.

using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Solid.AccessControl;
using SkyOmega.Mercury.Solid.Http;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Solid.Tests;

public class GetRepresentationTests : IDisposable
{
    private readonly string _storeDir;
    private readonly QuadStore _store;

    public GetRepresentationTests()
    {
        var tempPath = TempPath.Test("solid-get");
        tempPath.MarkOwnership();
        _storeDir = tempPath;
        _store = new QuadStore(_storeDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        TempPath.SafeCleanup(_storeDir);
    }

    [Fact]
    public async Task ResourceHandler_GetNTriples_IsTheStatementsAlone()
    {
        // A resource's triples are in the graph named by its URI, as HandlePutAsync stores them.
        _store.AddCurrent("<http://pod.example/doc#it>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"",
            "http://pod.example/doc");
        var handler = new ResourceHandler(_store, AccessPolicyFactory.CreateAllowAll());

        var result = await handler.HandleGetAsync("http://pod.example/doc", null, "application/n-triples",
            TestContext.Current.CancellationToken);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("<http://pod.example/doc#it> <http://xmlns.com/foaf/0.1/name> \"Alice\" .\n"u8.ToArray(),
            result.Body);
    }

    [Fact]
    public async Task ContainerHandler_GetNTriples_StartsWithTheFirstStatement()
    {
        _store.AddCurrent("<http://pod.example/c/>", "<http://www.w3.org/ns/ldp#contains>",
            "<http://pod.example/c/doc>", "http://pod.example/c/");
        var handler = new ContainerHandler(_store, AccessPolicyFactory.CreateAllowAll());

        var result = await handler.HandleGetAsync("http://pod.example/c/", null, "application/n-triples",
            TestContext.Current.CancellationToken);

        // Latin-1 reads each byte as one character, so a byte order mark would be three characters here.
        Assert.Equal(200, result.StatusCode);
        Assert.StartsWith(
            "<http://pod.example/c/> <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://www.w3.org/ns/ldp#Container> .\n",
            System.Text.Encoding.Latin1.GetString(result.Body!));
    }
}
