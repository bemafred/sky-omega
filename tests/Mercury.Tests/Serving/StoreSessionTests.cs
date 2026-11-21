// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Text;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Serving;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Serving;

/// <summary>
/// Opening and closing a store as the Mercury tools do (ADR-065 D11): the pool, the log's first and last lines, and
/// a second process refused without a line in the log of the first.
/// </summary>
public sealed class StoreSessionTests : IDisposable
{
    private readonly TempPath _dir;

    public StoreSessionTests()
    {
        _dir = TempPath.Test("store-session");
        _dir.MarkOwnership();
    }

    public void Dispose() => _dir.Cleanup();

    private string StorePath => Path.Combine(_dir.FullPath, "store");

    private StoreSession Open() => StoreSession.Open(StorePath, "mercury-test", "9.9.9", StorageOptions.ForTesting);

    // The log's lines as a reader beside its writer reads them. While the store is open its logger holds the file open
    // for writing, and Windows refuses a reader that does not share writing (File.ReadAllLines does not; CI run
    // 44810648562).
    private string[] LogLines()
    {
        using var reader = new StreamReader(new FileStream(StoreSession.LogPathFor(StorePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines.ToArray();
    }

    [Fact]
    public void Open_TheLogStartsWithTheToolItsVersionAndItsProcess()
    {
        using var session = Open();

        Assert.NotNull(session.Log);
        Assert.Equal(StoreSession.LogPathFor(StorePath), session.Log.Path);
        Assert.EndsWith($" INF mercury-test 9.9.9 starts, process {Environment.ProcessId}", LogLines()[0]);
        Assert.Null(session.LogWarning);
    }

    [Fact]
    public void Dispose_ClosesTheStoreAndWritesTheStop_Once()
    {
        var session = Open();
        session.Dispose();
        session.Dispose();

        var stops = LogLines().Where(l => l.Contains(" stops, ", StringComparison.Ordinal)).ToArray();
        Assert.Single(stops);
        Assert.EndsWith($" INF mercury-test stops, process {Environment.ProcessId}", stops[0]);
        Assert.EndsWith(" stops, process " + Environment.ProcessId, LogLines()[^1]);
        // Closed: the store can be opened again.
        using var again = Open();
    }

    [Fact]
    public void Open_StoreInUse_IsRefused_AndTheLogOfTheHolderIsLeftAlone()
    {
        using var first = Open();
        var before = LogLines();

        Assert.Throws<StoreInUseException>(() => Open());

        Assert.Equal(before, LogLines());
    }

    [Fact]
    public void Open_TheStoreIsActive_AndQueriedThroughThePool()
    {
        using var session = Open();

        var ct = TestContext.Current.CancellationToken;
        SparqlEngine.Update(session.Pool.Active, "INSERT DATA { <urn:s> <urn:p> \"o\" }", ct);
        var result = SparqlEngine.Query(session.Pool.Active, "SELECT ?o WHERE { <urn:s> <urn:p> ?o }", ct);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Single(result.Rows!);
    }

    [Fact]
    public void Open_ARelativePath_IsHeldAsAFullPath()
    {
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, StorePath);
        using var session = StoreSession.Open(relative, "mercury-test", "9.9.9", StorageOptions.ForTesting);

        Assert.Equal(Path.GetFullPath(StorePath), session.Path);
    }

    [Fact]
    public void Open_TheProfileGiven_IsTheProfileOfAStoreMadeThere()
    {
        var testing = StorageOptions.ForTesting;
        using var session = StoreSession.Open(StorePath, "mercury-test", "9.9.9", new StorageOptions
        {
            IndexInitialSizeBytes = testing.IndexInitialSizeBytes,
            AtomDataInitialSizeBytes = testing.AtomDataInitialSizeBytes,
            AtomOffsetInitialCapacity = testing.AtomOffsetInitialCapacity,
            AtomHashTableInitialCapacity = testing.AtomHashTableInitialCapacity,
            MinimumFreeDiskSpace = testing.MinimumFreeDiskSpace,
            Profile = StoreProfile.Graph,
        });

        Assert.Equal(StoreProfile.Graph, session.Pool.Active.Schema.Profile);
    }

    [Fact]
    public void OpenTemporary_HasNoLog_AndIsGoneWhenClosed()
    {
        string path;
        using (var session = StoreSession.OpenTemporary("store-session-test", "mercury-test"))
        {
            path = session.Path;
            Assert.Null(session.Log);
            Assert.Null(session.LogWarning);
            Assert.True(Directory.Exists(path));
            Assert.NotNull(session.Pool.Active);
        }

        Assert.False(Directory.Exists(path));
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.OK)]
    public async Task CreateHttpServer_ServesTheActiveStore_AndTakesUpdatesOnlyWhenTold(bool enableUpdates, HttpStatusCode updateStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        using var session = Open();
        SparqlEngine.Update(session.Pool.Active, "INSERT DATA { <urn:s> <urn:p> \"from the session\" }", ct);
        int port = FreePort();
        using var server = session.CreateHttpServer(["localhost"], port, enableUpdates);
        server.Start();
        try
        {
            using var http = new HttpClient();
            var query = Uri.EscapeDataString("SELECT ?o WHERE { <urn:s> <urn:p> ?o }");
            var answer = await http.GetStringAsync($"http://localhost:{port}/sparql?query={query}", ct);
            Assert.Contains("from the session", answer);

            var update = new StringContent("INSERT DATA { <urn:s> <urn:p> \"over http\" }", Encoding.UTF8, "application/sparql-update");
            var response = await http.PostAsync($"http://localhost:{port}/sparql/update", update, ct);
            Assert.Equal(updateStatus, response.StatusCode);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
