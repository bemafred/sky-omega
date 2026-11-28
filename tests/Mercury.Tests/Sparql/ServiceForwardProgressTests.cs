// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Sparql.Protocol;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// Forward progress when SERVICE crosses an execution boundary (ADR-065 Step 6, ADR-066): a query that waits on SERVICE
/// holds up only the writes to its store, which come after it, and through them no read, whichever store the endpoint
/// serves.
/// </summary>
/// <remarks>
/// <para>
/// A query holds its store's read lock from its first read to its last row, its SERVICE waits included: a SERVICE call
/// is part of its read, whose results are matched to the store. A write that arrives meanwhile waits for it. But the
/// lock queues a reader behind a waiting writer, so that write holds every later read of the store behind it; and when
/// the endpoint, or an endpoint it reaches, reads that store, the wait is a cycle that only the HTTP client's timeout
/// ends (100 s; measured on zbook, 2026-11-21).
/// </para>
/// <para>
/// The first four tests show the issue. The next three hold what the correction must keep: SILENT ignores the
/// endpoint's failure and nothing else, a query reads one state of its store, and a store is not cleared under a query.
/// </para>
/// <para>
/// The rest hold ADR-066's rule (2026-11-22): a write waits for every read in progress and holds no read behind it
/// (D1), for every way an exclusive section begins, a clear among them (D2); a close of a store, or of its pool, waits
/// for its query, and a read after it finds the store closed (D2, I5, I6); and cancellation at the call, leaving no
/// lock (D4).
/// </para>
/// <para>
/// A test that fails here leaves its query, its write and an endpoint's handler waiting until that timeout. Each test
/// opens its gates and waits for them in a <c>finally</c> before its stores are closed: a store closed under a thread
/// that later reads or writes it is not safe.
/// </para>
/// </remarks>
public sealed class ServiceForwardProgressTests : IDisposable
{
    /// <summary>How long an operation that does not wait on anything outside its store may take here.</summary>
    private static readonly TimeSpan Progress = TimeSpan.FromSeconds(10);

    /// <summary>Longer than the HTTP client's timeout, which ends every wait these tests can leave.</summary>
    private static readonly TimeSpan Unwound = TimeSpan.FromSeconds(150);

    private readonly TempPath _dir;
    private readonly List<IDisposable> _closeLast = [];

    public ServiceForwardProgressTests()
    {
        _dir = TempPath.Test("service-progress");
        _dir.MarkOwnership();
    }

    public void Dispose()
    {
        foreach (var disposable in Enumerable.Reverse(_closeLast))
            disposable.Dispose();
        _dir.Cleanup();
    }

    [Fact]
    public async Task WhileAQueryWaitsOnService_AWriteToItsStoreWaitsForIt_AndAReadAfterTheWriteDoesNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore("l");
        store.AddCurrent("<urn:s>", "<urn:p>", "\"before\"");
        using var gate = new GatedSparqlEndpoint();
        var query = Task.Run(() => SparqlEngine.Query(store, $"SELECT * WHERE {{ SERVICE <{gate.Url}> {{ ?gate ?p ?o }} }}"), ct);
        Task<UpdateResult>? write = null;
        Task<QueryResult>? read = null;
        try
        {
            await gate.Asked.WaitAsync(Progress, ct);

            // The write arrived after the read in progress: it comes after it.
            write = Task.Run(() => SparqlEngine.Update(store, "INSERT DATA { <urn:s> <urn:p> \"written\" }"), ct);
            await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(1), ct));
            Assert.False(write.IsCompleted);

            // A read that arrives while the write waits is not held behind it, and reads the store as it is: the write
            // has not happened.
            read = Task.Run(() => SparqlEngine.Query(store, "SELECT ?o WHERE { <urn:s> <urn:p> ?o }"), ct);
            var result = await read.WaitAsync(Progress, ct);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("\"before\"", Assert.Single(result.Rows!)["o"]);
            Assert.False(write.IsCompleted);

            gate.Open();
            await query.WaitAsync(Progress, ct);
            Assert.True((await write.WaitAsync(Progress, ct)).Success);
        }
        finally
        {
            gate.Open();
            await Unwind(query, write, read);
        }
    }

    [Fact]
    public async Task AStoresServiceToItself_CompletesWithAWriteQueued()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore("a");
        store.AddCurrent("<urn:s>", "<urn:p>", "\"held\"");
        var endpoint = Serve(store);
        using var gate = new GatedSparqlEndpoint();
        var query = Task.Run(() => SparqlEngine.Query(store,
            $"SELECT * WHERE {{ SERVICE <{gate.Url}> {{ ?gate ?gp ?go }} SERVICE <{endpoint}> {{ ?s ?p ?o }} }}"), ct);
        Task<UpdateResult>? write = null;
        try
        {
            await gate.Asked.WaitAsync(Progress, ct);
            write = Task.Run(() => SparqlEngine.Update(store, "INSERT DATA { <urn:s> <urn:p> \"written\" }"), ct);
            await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(1), ct)); // queued behind the query, or done
            gate.Open();

            var result = await query.WaitAsync(Progress, ct);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotEmpty(result.Rows!);
        }
        finally
        {
            gate.Open();
            await Unwind(query, write);
        }
    }

    [Fact]
    public async Task TwoStoresQueryingEachOther_CompleteWithWritesQueuedOnBoth()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = NewStore("a");
        var b = NewStore("b");
        a.AddCurrent("<urn:s>", "<urn:in>", "\"a\"");
        b.AddCurrent("<urn:s>", "<urn:in>", "\"b\"");
        var endpointA = Serve(a);
        var endpointB = Serve(b);
        using var gateA = new GatedSparqlEndpoint();
        using var gateB = new GatedSparqlEndpoint();
        var queryA = Task.Run(() => SparqlEngine.Query(a,
            $"SELECT * WHERE {{ SERVICE <{gateA.Url}> {{ ?gate ?gp ?go }} SERVICE <{endpointB}> {{ ?s ?p ?o }} }}"), ct);
        var queryB = Task.Run(() => SparqlEngine.Query(b,
            $"SELECT * WHERE {{ SERVICE <{gateB.Url}> {{ ?gate ?gp ?go }} SERVICE <{endpointA}> {{ ?s ?p ?o }} }}"), ct);
        Task<UpdateResult>? writeA = null, writeB = null;
        try
        {
            await Task.WhenAll(gateA.Asked, gateB.Asked).WaitAsync(Progress, ct);
            writeA = Task.Run(() => SparqlEngine.Update(a, "INSERT DATA { <urn:s> <urn:w> \"a\" }"), ct);
            writeB = Task.Run(() => SparqlEngine.Update(b, "INSERT DATA { <urn:s> <urn:w> \"b\" }"), ct);
            await Task.WhenAny(Task.WhenAll(writeA, writeB), Task.Delay(TimeSpan.FromSeconds(1), ct));
            gateA.Open();
            gateB.Open();

            var results = await Task.WhenAll(queryA, queryB).WaitAsync(Progress, ct);

            Assert.All(results, result => Assert.True(result.Success, result.ErrorMessage));
        }
        finally
        {
            gateA.Open();
            gateB.Open();
            await Unwind(queryA, queryB, writeA, writeB);
        }
    }

    [Fact]
    public async Task ACancelledQuery_StopsWaitingOnItsService()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore("c");
        using var gate = new GatedSparqlEndpoint();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var query = Task.Run(() => SparqlEngine.Query(store,
            $"SELECT * WHERE {{ SERVICE <{gate.Url}> {{ ?gate ?p ?o }} }}", cancel.Token), ct);
        try
        {
            await gate.Asked.WaitAsync(Progress, ct);

            await cancel.CancelAsync();

            var result = await query.WaitAsync(Progress, ct);
            Assert.False(result.Success);
            Assert.Equal("Query cancelled", result.ErrorMessage);
        }
        finally
        {
            gate.Open();
            await Unwind(query);
        }
    }

    [Fact]
    public async Task AWriteQueuedDuringTheWait_IsNotAFailureSilentIgnores()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore("s");
        using var gate = new GatedSparqlEndpoint();
        var query = Task.Run(() => SparqlEngine.Query(store,
            $"SELECT ?gate WHERE {{ SERVICE SILENT <{gate.Url}> {{ ?gate ?p ?o }} }}"), ct);
        Task<UpdateResult>? write = null;
        try
        {
            await gate.Asked.WaitAsync(Progress, ct);
            write = Task.Run(() => SparqlEngine.Update(store, "INSERT DATA { <urn:s> <urn:p> \"written\" }"), ct);
            await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(1), ct));

            gate.Open();

            var result = await query.WaitAsync(Unwound, ct);
            Assert.True(result.Success, result.ErrorMessage);
            var row = Assert.Single(result.Rows!);
            Assert.Equal("\"opened\"", row["gate"]);
        }
        finally
        {
            gate.Open();
            await Unwind(query, write);
        }
    }

    [Fact]
    public async Task AQueryReadsOneStateOfItsStore_AcrossItsServiceWait()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore("o");
        store.AddCurrent("<urn:s>", "<urn:before>", "\"1\"");
        store.AddCurrent("<urn:s>", "<urn:after>", "\"1\"");
        using var gate = new GatedSparqlEndpoint();
        // The pattern before the SERVICE is read before the wait, the one after it after the wait.
        var query = Task.Run(() => SparqlEngine.Query(store,
            $"SELECT ?before ?after WHERE {{ <urn:s> <urn:before> ?before . SERVICE <{gate.Url}> {{ ?gate ?p ?o }} <urn:s> <urn:after> ?after }}"), ct);
        Task? write = null;
        try
        {
            await gate.Asked.WaitAsync(Progress, ct);
            // Both facts change in one exclusive section: one state has both at "1", the next both at "2".
            write = Task.Run(() =>
            {
                store.BeginBatch();
                store.DeleteCurrentBatched("<urn:s>", "<urn:before>", "\"1\"");
                store.DeleteCurrentBatched("<urn:s>", "<urn:after>", "\"1\"");
                store.AddCurrentBatched("<urn:s>", "<urn:before>", "\"2\"");
                store.AddCurrentBatched("<urn:s>", "<urn:after>", "\"2\"");
                store.CommitBatch();
            }, ct);
            await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(1), ct));

            gate.Open();

            var result = await query.WaitAsync(Unwound, ct);
            Assert.True(result.Success, result.ErrorMessage);
            var row = Assert.Single(result.Rows!);
            Assert.Equal(row["before"], row["after"]);
        }
        finally
        {
            gate.Open();
            await Unwind(query, write);
        }
    }

    [Fact]
    public async Task AStoreIsNotClearedUnderAQuery_WaitingOnService()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore("p");
        store.AddCurrent("<urn:s>", "<urn:p>", "\"kept\"");
        using var gate = new GatedSparqlEndpoint();
        var query = Task.Run(() => SparqlEngine.Query(store,
            $"SELECT ?o WHERE {{ SERVICE <{gate.Url}> {{ ?gate ?gp ?go }} <urn:s> <urn:p> ?o }}"), ct);
        Task? clear = null;
        try
        {
            await gate.Asked.WaitAsync(Progress, ct);
            // The clear waits for the query in progress (D2).
            clear = Task.Run(store.Clear, ct);
            await Task.WhenAny(clear, Task.Delay(TimeSpan.FromSeconds(1), ct));

            gate.Open();

            var result = await query.WaitAsync(Unwound, ct);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("\"kept\"", Assert.Single(result.Rows!)["o"]);
        }
        finally
        {
            gate.Open();
            await Unwind(query, clear);
        }
    }

    // ── A clear is a write (D2): it waits for the read in progress, and holds no read behind it ─────────────────

    [Fact]
    public async Task AClearOfAStoreWhoseQueryWaitsOnServiceToIt_EndsWithoutADeadlock()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore("k");
        store.AddCurrent("<urn:s>", "<urn:p>", "\"held\"");
        var endpoint = Serve(store);
        using var gate = new GatedSparqlEndpoint();
        var query = Task.Run(() => SparqlEngine.Query(store,
            $"SELECT * WHERE {{ SERVICE <{gate.Url}> {{ ?gate ?gp ?go }} SERVICE <{endpoint}> {{ ?s ?p ?o }} }}"), ct);
        Task? clear = null;
        try
        {
            await gate.Asked.WaitAsync(Progress, ct);
            // The clear waits for the query in progress; the query's second SERVICE comes back to this store, whose
            // endpoint's read must not wait behind the clear that waits for the query.
            clear = Task.Run(store.Clear, ct);
            await Task.WhenAny(clear, Task.Delay(TimeSpan.FromSeconds(1), ct));
            gate.Open();

            await Task.WhenAll(query, clear).WaitAsync(Progress, ct);
        }
        finally
        {
            gate.Open();
            await Unwind(query, clear);
        }
    }

    [Fact]
    public async Task AReadOfAStoreWhoseClearWaits_IsAnsweredAtOnce_FromTheStoreAsItIs()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore("r");
        store.AddCurrent("<urn:s>", "<urn:p>", "\"held\"");
        using var gate = new GatedSparqlEndpoint();
        var waiting = Task.Run(() => SparqlEngine.Query(store,
            $"SELECT * WHERE {{ SERVICE <{gate.Url}> {{ ?gate ?gp ?go }} }}"), ct);
        Task? clear = null;
        Task<QueryResult>? read = null;
        try
        {
            await gate.Asked.WaitAsync(Progress, ct);
            clear = Task.Run(store.Clear, ct);
            await Task.WhenAny(clear, Task.Delay(TimeSpan.FromSeconds(1), ct));

            // A read that comes while the clear waits for the one in progress does not wait for the clear.
            read = Task.Run(() => SparqlEngine.Query(store, "SELECT ?o WHERE { <urn:s> <urn:p> ?o }"), ct);
            var result = await read.WaitAsync(Progress, ct);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("\"held\"", Assert.Single(result.Rows!)["o"]);
            Assert.False(clear.IsCompleted);
        }
        finally
        {
            gate.Open();
            await Unwind(waiting, clear, read);
        }
    }

    // ── A store closed while its query waits on SERVICE (D2, I5, I6) ────────────────────────────────────────────

    [Fact]
    public async Task AStoreClosedWhileItsQueryWaitsOnService_ClosesAfterTheQuery_AndAReadAfterFindsItClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore("d");
        store.AddCurrent("<urn:s>", "<urn:p>", "\"held\"");
        using var gate = new GatedSparqlEndpoint();
        var query = Task.Run(() => SparqlEngine.Query(store,
            $"SELECT ?o ?gate WHERE {{ <urn:s> <urn:p> ?o SERVICE <{gate.Url}> {{ ?gate ?gp ?go }} }}"), ct);
        Task? close = null;
        try
        {
            await gate.Asked.WaitAsync(Progress, ct);

            // The close is a write: it waits for the query, outside the lock.
            close = Task.Run(store.Dispose, ct);
            Assert.True(SpinWait.SpinUntil(() => store.WritesWaiting == 1, Progress));

            // The query answers from the store, which is open, and then the store closes.
            gate.Open();
            var result = await query.WaitAsync(Progress, ct);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("\"held\"", Assert.Single(result.Rows!)["o"]);
            await close.WaitAsync(Progress, ct);

            var after = SparqlEngine.Query(store, "SELECT ?o WHERE { <urn:s> <urn:p> ?o }", ct);
            Assert.False(after.Success);
            Assert.Equal("The store is closed.", after.ErrorMessage);
        }
        finally
        {
            gate.Open();
            await Unwind(query, close);
        }
    }

    /// <remarks>The pool closes its stores after it has let go of <c>_namedLock</c>, which the endpoint's
    /// <c>Active</c> takes (I5). This test does not fail if the pool closed them holding it: <c>Active</c> refuses a
    /// pool whose close has begun before it takes the lock, so the SERVICE that comes after the close began finds the
    /// pool closed. Only a request between that check and the lock would wait, which no test reaches.</remarks>
    [Fact]
    public async Task APoolClosedWhileAQueryOnItsStoreWaitsOnService_ToThePoolsOwnEndpoint_Ends()
    {
        var ct = TestContext.Current.CancellationToken;
        var pool = new QuadStorePool(Path.Combine(_dir.FullPath, "pool"), QuadStorePoolOptions.ForTesting);
        _closeLast.Add(pool);
        var store = pool.EnsureActive("primary");
        store.AddCurrent("<urn:s>", "<urn:p>", "\"held\"");
        var server = FreePort.Start(port => new SparqlHttpServer(() => pool.Active, $"http://localhost:{port}/"),
            server => server.Start(), out var port);
        _closeLast.Add(server);
        using var gate = new GatedSparqlEndpoint();
        var query = Task.Run(() => SparqlEngine.Query(store,
            $"SELECT * WHERE {{ SERVICE <{gate.Url}> {{ ?gate ?gp ?go }} " +
            $"SERVICE <http://localhost:{port}/sparql> {{ ?s ?p ?o }} }}"), ct);
        Task? close = null;
        try
        {
            await gate.Asked.WaitAsync(Progress, ct);

            // The pool's close waits for the query, through its store's close.
            close = Task.Run(pool.Dispose, ct);
            Assert.True(SpinWait.SpinUntil(() => store.WritesWaiting == 1, Progress));

            // The query's SERVICE to the pool's endpoint finds the pool closed, the query fails, and the pool closes.
            gate.Open();
            var result = await query.WaitAsync(Progress, ct);
            Assert.False(result.Success);
            await close.WaitAsync(Progress, ct);
        }
        finally
        {
            gate.Open();
            await Unwind(query, close);
        }
    }

    [Fact]
    public async Task AnEndpointStoppedWhileItsQueryWaitsOnService_AnswersIt_AndRefusesItsServiceBackToItself()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore("h");
        store.AddCurrent("<urn:s>", "<urn:p>", "\"held\"");
        var server = FreePort.Start(port => new SparqlHttpServer(() => store, $"http://localhost:{port}/"),
            server => server.Start(), out var port);
        var self = $"http://localhost:{port}/sparql";
        _closeLast.Add(server);
        using var gate = new GatedSparqlEndpoint();
        using var http = new HttpClient();
        var query = $"SELECT * WHERE {{ SERVICE <{gate.Url}> {{ ?gate ?gp ?go }} SERVICE <{self}> {{ ?s ?p ?o }} }}";
        var asked = http.GetAsync($"{self}?query={Uri.EscapeDataString(query)}", ct);
        Task? stop = null;
        try
        {
            await gate.Asked.WaitAsync(Progress, ct);
            // ADR-065 D3: the stop waits for the requests in progress, and answers 503 to one that comes meanwhile.
            stop = server.StopAsync();
            await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(1), ct));
            Assert.False(stop.IsCompleted);
            gate.Open();

            using var response = await asked.WaitAsync(Progress, ct);
            await stop.WaitAsync(Progress, ct);

            // The query's SERVICE back to its stopping endpoint was refused, so the query failed and said why.
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("503", await response.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            gate.Open();
            await Unwind(asked, stop);
        }
    }

    // ── Every exclusive section waits for the read in progress, and holds no read behind it (D1, D2) ────────────

    /// <summary>Each way an exclusive section of a store begins, by which a store's content or structure can
    /// change.</summary>
    public static TheoryData<string> ExclusiveSections =>
    [
        "AddCurrent", "DeleteCurrent", "a batch committed", "a batch rolled back", "INSERT DATA", "DELETE WHERE",
        "LOAD", "Checkpoint", "RebuildSecondaryIndexes", "RepairDeletes", "Clear",
    ];

    [Theory]
    [MemberData(nameof(ExclusiveSections))]
    public async Task AnExclusiveSectionDuringAServiceWait_WaitsForTheQuery_AndHoldsNoReadBehindIt(string section)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore("v");
        store.AddCurrent("<urn:s>", "<urn:p>", "\"1\"");
        var document = Path.Combine(_dir.FullPath, "load.nt");
        File.WriteAllText(document, "<urn:l> <urn:p> \"loaded\" .\n");
        using var gate = new GatedSparqlEndpoint();
        var query = Task.Run(() => SparqlEngine.Query(store,
            $"SELECT ?gate WHERE {{ SERVICE <{gate.Url}> {{ ?gate ?p ?o }} }}"), ct);
        Task? exclusive = null;
        Task<QueryResult>? read = null;
        try
        {
            await gate.Asked.WaitAsync(Progress, ct);
            // A batch begins and ends on one thread.
            exclusive = Task.Factory.StartNew(() =>
            {
                switch (section)
                {
                    case "AddCurrent": store.AddCurrent("<urn:s>", "<urn:p>", "\"2\""); break;
                    case "DeleteCurrent": store.DeleteCurrent("<urn:s>", "<urn:p>", "\"1\""); break;
                    case "a batch committed":
                        store.BeginBatch();
                        store.AddCurrentBatched("<urn:s>", "<urn:p>", "\"2\"");
                        store.CommitBatch();
                        break;
                    case "a batch rolled back":
                        store.BeginBatch();
                        store.AddCurrentBatched("<urn:s>", "<urn:p>", "\"2\"");
                        store.RollbackBatch();
                        break;
                    case "INSERT DATA": SparqlEngine.Update(store, "INSERT DATA { <urn:s> <urn:p> \"2\" }"); break;
                    case "DELETE WHERE": SparqlEngine.Update(store, "DELETE WHERE { <urn:s> <urn:p> ?o }"); break;
                    case "LOAD": SparqlEngine.Update(store, $"LOAD <file://{document}>"); break;
                    case "Checkpoint": store.Checkpoint(); break;
                    case "RebuildSecondaryIndexes": store.RebuildSecondaryIndexes(); break;
                    case "RepairDeletes": store.RepairDeletes([], dryRun: false, TextWriter.Null); break;
                    case "Clear": store.Clear(); break;
                    default: throw new ArgumentOutOfRangeException(nameof(section), section, null);
                }
            }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await Task.WhenAny(exclusive, Task.Delay(TimeSpan.FromSeconds(1), ct));
            Assert.False(exclusive.IsCompleted);

            read = Task.Run(() => SparqlEngine.Query(store, "SELECT * WHERE { ?s ?p ?o }"), ct);
            var result = await read.WaitAsync(Progress, ct);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(exclusive.IsCompleted);

            gate.Open();
            await query.WaitAsync(Progress, ct);
            await exclusive.WaitAsync(Progress, ct);
        }
        finally
        {
            gate.Open();
            await Unwind(query, exclusive, read);
        }
    }

    // ── Cancellation (D4): at the call ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACancelledQuery_LeavesNoLock()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore("c2");
        using var gate = new GatedSparqlEndpoint();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var query = Task.Run(() => SparqlEngine.Query(store,
            $"SELECT * WHERE {{ SERVICE <{gate.Url}> {{ ?gate ?p ?o }} }}", cancel.Token), ct);
        Task? write = null, clear = null;
        try
        {
            await gate.Asked.WaitAsync(Progress, ct);
            await cancel.CancelAsync();

            var result = await query.WaitAsync(Progress, ct);
            Assert.Equal("Query cancelled", result.ErrorMessage);

            // The query let go of its store: a write and a clear wait for nothing of it.
            write = Task.Run(() => store.AddCurrent("<urn:s>", "<urn:p>", "\"after\""), ct);
            await write.WaitAsync(Progress, ct);
            clear = Task.Run(store.Clear, ct);
            await clear.WaitAsync(Progress, ct);
        }
        finally
        {
            gate.Open();
            await Unwind(query, write, clear);
        }
    }

    [Fact]
    public async Task ServiceSilent_DoesNotHideTheQuerysCancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore("c4");
        using var gate = new GatedSparqlEndpoint();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var query = Task.Run(() => SparqlEngine.Query(store,
            $"SELECT * WHERE {{ SERVICE SILENT <{gate.Url}> {{ ?gate ?p ?o }} }}", cancel.Token), ct);
        try
        {
            await gate.Asked.WaitAsync(Progress, ct);
            await cancel.CancelAsync();

            var result = await query.WaitAsync(Progress, ct);
            Assert.False(result.Success);
            Assert.Equal("Query cancelled", result.ErrorMessage);
        }
        finally
        {
            gate.Open();
            await Unwind(query);
        }
    }

    private QuadStore NewStore(string name)
    {
        var store = new QuadStore(Path.Combine(_dir.FullPath, name), null, null, StorageOptions.ForTesting);
        _closeLast.Add(store);
        return store;
    }

    /// <summary>Serves <paramref name="store"/> over HTTP until the test ends; its SPARQL endpoint.</summary>
    private string Serve(QuadStore store)
    {
        var server = FreePort.Start(port => new SparqlHttpServer(() => store, $"http://localhost:{port}/"),
            server => server.Start(), out var port);
        _closeLast.Add(server);
        return $"http://localhost:{port}/sparql";
    }

    /// <summary>Waits for what a test started to end, however it ended, so that no thread is in a store that is
    /// closed.</summary>
    private static async Task Unwind(params Task?[] started)
    {
        var all = Task.WhenAll(started.Where(task => task is not null).Select(task => (Task)task!));
        await Task.WhenAny(all, Task.Delay(Unwound));
    }
}
