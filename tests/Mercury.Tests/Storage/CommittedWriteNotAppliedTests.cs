using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Diagnostics;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Runtime.Buffers;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-061 M7: a store that could not apply a write its log has committed stops. It answers no read, takes no write
/// and makes no checkpoint, it closes without one, and opening it again applies the log. The error says that it is an
/// internal error of the store, names the cause, and says that the write is not lost.
///
/// The failure is made with a part the store is given at its construction: the buffer manager. The trigram index
/// rents a buffer from it when it indexes a literal longer than 512 bytes, and it does so after the four indexes have
/// received the record. Each test writes such a literal and makes the buffer manager fail at that moment, so a record
/// the log has committed is in the four indexes and not in the trigram index, and a record after it is in none.
/// </summary>
public class CommittedWriteNotAppliedTests : IDisposable
{
    private const string S = "<http://ex.org/s>";
    private const string P = "<http://ex.org/p>";

    // 700 characters: its bytes do not fit the 512 bytes the trigram index keeps on the stack, so it rents a buffer.
    private static readonly string LongLiteral = "\"" + string.Concat(Enumerable.Repeat("memory ", 100)) + "\"";

    // What the trigram index asks its buffer for: the bytes of the literal's value, without the quotes.
    private const int BytesTheTrigramIndexRents = 700;

    /// <summary>
    /// The store's buffer manager. It rents from the shared pool. When the test tells it to fail, it fails for the one
    /// request the trigram index makes for the long literal, and for no other: the atom store and the log rent
    /// buffers too, before the commit, and those must succeed.
    /// </summary>
    private sealed class FailingBufferManager : IBufferManager
    {
        public bool Fails { get; set; }

        /// <summary>Fails for every request, so also for the ones made before the commit.</summary>
        public bool FailsForEveryRequest { get; set; }

        public BufferLease<T> Rent<T>(int minimumLength) where T : unmanaged
        {
            if (FailsForEveryRequest || (Fails && typeof(T) == typeof(byte) && minimumLength == BytesTheTrigramIndexRents))
                throw new InvalidOperationException("The test's buffer manager has no buffer to give.");
            return PooledBufferManager.Shared.Rent<T>(minimumLength);
        }

        public void Return<T>(T[] buffer, bool clearArray = false) where T : unmanaged =>
            PooledBufferManager.Shared.Return(buffer, clearArray);
    }

    /// <summary>Keeps what the store logs at the level Critical.</summary>
    private sealed class CriticalMessages : ILogger
    {
        public List<string> Messages { get; } = new();
        public bool IsEnabled(LogLevel level) => level == LogLevel.Critical;
        public void Log(LogLevel level, ReadOnlySpan<char> message) { if (level == LogLevel.Critical) Messages.Add(message.ToString()); }
        public void Log<T>(LogLevel level, ReadOnlySpan<char> message, T arg) => Log(level, message);
        public void Log<T1, T2>(LogLevel level, ReadOnlySpan<char> message, T1 arg1, T2 arg2) => Log(level, message);
        public void Log<T1, T2, T3>(LogLevel level, ReadOnlySpan<char> message, T1 arg1, T2 arg2, T3 arg3) => Log(level, message);
    }

    private readonly string _testDir;

    public CommittedWriteNotAppliedTests()
    {
        var tempPath = TempPath.Test("adr061-not-applied");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    /// <summary>
    /// How many periods each of the four indexes holds for the quad (S, P, <paramref name="o"/>). The four must agree
    /// (ADR-061 I13): the assertion that they do is here.
    /// </summary>
    private static int PeriodsInEveryIndex(QuadStore store, string o)
    {
        var throughEachIndex = new int[4];
        store.AcquireReadLock();
        try
        {
            for (int index = 0; index < 4; index++)
            {
                var entries = index switch
                {
                    0 => store.QueryEvolution(S, default, default),
                    1 => store.QueryEvolution(default, P, default),
                    2 => store.QueryEvolution(default, default, o),
                    _ => store.QueryChanges(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, default, default, default),
                };
                while (entries.MoveNext())
                {
                    var entry = entries.Current;
                    if (entry.Subject.SequenceEqual(S) && entry.Predicate.SequenceEqual(P) && entry.Object.SequenceEqual(o))
                        throughEachIndex[index]++;
                }
                entries.Dispose();
            }
        }
        finally { store.ReleaseReadLock(); }

        Assert.Equal(throughEachIndex[0], throughEachIndex[1]);
        Assert.Equal(throughEachIndex[0], throughEachIndex[2]);
        Assert.Equal(throughEachIndex[0], throughEachIndex[3]);
        return throughEachIndex[0];
    }

    [Fact]
    public void ABatchThatCannotBeApplied_StopsTheStore_AndOpeningItAgainAppliesTheLog()
    {
        var path = Path.Combine(_testDir, "batch");
        var buffers = new FailingBufferManager();
        var critical = new CriticalMessages();
        var store = new QuadStore(path, critical, buffers, new StorageOptions());

        store.AddCurrent(S, P, "\"before\"");

        // The batch memorizes the long literal first and a short one after it. The commit reaches the log; applying
        // the first record fails in the trigram index, and the second record reaches no index.
        store.BeginBatch();
        store.AddCurrentBatched(S, P, LongLiteral);
        store.AddCurrentBatched(S, P, "\"after\"");
        buffers.Fails = true;
        var failure = Assert.Throws<CommittedWriteNotAppliedException>(() => store.CommitBatch());
        buffers.Fails = false;

        // The error says what it is, where, and why, and that the write is not lost.
        Assert.Contains("Internal error in the Mercury store", failure.Message);
        Assert.Contains(path, failure.Message);
        Assert.Contains("The test's buffer manager has no buffer to give.", failure.Message);
        Assert.Contains("The write is not lost", failure.Message);
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.Equal(path, failure.StorePath);
        Assert.Contains("Internal error in the Mercury store", Assert.Single(critical.Messages));

        // The cause is gone, and the store stays stopped: every read, write and checkpoint gives the same error.
        Assert.Throws<CommittedWriteNotAppliedException>(() => store.AcquireReadLock());
        Assert.Throws<CommittedWriteNotAppliedException>(() => store.AddCurrent(S, P, "\"later\""));
        Assert.Throws<CommittedWriteNotAppliedException>(() => store.DeleteCurrent(S, P, "\"before\""));
        Assert.Throws<CommittedWriteNotAppliedException>(() => store.BeginBatch());
        Assert.Throws<CommittedWriteNotAppliedException>(() => store.Checkpoint());
        Assert.Throws<CommittedWriteNotAppliedException>(() => store.Clear());
        var refusedRead = Assert.Throws<CommittedWriteNotAppliedException>(() => store.AcquireReadLock());
        Assert.IsType<InvalidOperationException>(refusedRead.InnerException);

        var query = SparqlEngine.Query(store, "SELECT ?o WHERE { ?s ?p ?o }", TestContext.Current.CancellationToken);
        Assert.False(query.Success);
        Assert.Contains("Internal error in the Mercury store", query.ErrorMessage);

        // It closes without a checkpoint: the log still holds the batch.
        store.Dispose();

        using var reopened = new QuadStore(path, null, null, new StorageOptions());
        Assert.Equal(1, PeriodsInEveryIndex(reopened, "\"before\""));
        Assert.Equal(1, PeriodsInEveryIndex(reopened, LongLiteral));
        Assert.Equal(1, PeriodsInEveryIndex(reopened, "\"after\""));
        Assert.Equal(3, reopened.GetStatistics().QuadCount);

        // The trigram index received the long literal too.
        var found = SparqlEngine.Query(reopened, "SELECT ?o WHERE { ?s ?p ?o FILTER(text:match(?o, \"memory memory\")) }", TestContext.Current.CancellationToken);
        Assert.True(found.Success, found.ErrorMessage);
        Assert.Single(found.Rows!);
    }

    [Fact]
    public void ASingleWriteThatCannotBeApplied_StopsTheStore_AndOpeningItAgainAppliesTheLog()
    {
        var path = Path.Combine(_testDir, "single");
        var buffers = new FailingBufferManager();
        var store = new QuadStore(path, null, buffers, new StorageOptions());

        buffers.Fails = true;
        Assert.Throws<CommittedWriteNotAppliedException>(() => store.AddCurrent(S, P, LongLiteral));
        buffers.Fails = false;

        Assert.Throws<CommittedWriteNotAppliedException>(() => store.AcquireReadLock());
        store.Dispose();

        using var reopened = new QuadStore(path, null, null, new StorageOptions());
        Assert.Equal(1, PeriodsInEveryIndex(reopened, LongLiteral));
        Assert.Equal(1, reopened.GetStatistics().QuadCount);
    }

    [Fact]
    public void ASparqlUpdateThatCannotBeApplied_ReportsTheInternalError_AndSoDoesTheNextRequest()
    {
        var path = Path.Combine(_testDir, "sparql");
        var buffers = new FailingBufferManager();
        var store = new QuadStore(path, null, buffers, new StorageOptions());

        buffers.Fails = true;
        var update = SparqlEngine.Update(store, $"INSERT DATA {{ {S} {P} {LongLiteral} }}", TestContext.Current.CancellationToken);
        buffers.Fails = false;

        Assert.False(update.Success);
        Assert.Contains("Internal error in the Mercury store", update.ErrorMessage);
        Assert.Contains("The write is not lost", update.ErrorMessage);

        var next = SparqlEngine.Update(store, $"INSERT DATA {{ {S} {P} \"next\" }}", TestContext.Current.CancellationToken);
        Assert.False(next.Success);
        Assert.Contains("Internal error in the Mercury store", next.ErrorMessage);

        store.Dispose();

        using var reopened = new QuadStore(path, null, null, new StorageOptions());
        Assert.Equal(1, PeriodsInEveryIndex(reopened, LongLiteral));
        Assert.Equal(0, PeriodsInEveryIndex(reopened, "\"next\""));
    }

    [Fact]
    public void AStoreWhoseLogCannotBeApplied_DoesNotOpen_KeepsItsLog_AndOpensWhenTheCauseIsGone()
    {
        var path = Path.Combine(_testDir, "open");
        var buffers = new FailingBufferManager();
        var store = new QuadStore(path, null, buffers, new StorageOptions());

        store.BeginBatch();
        store.AddCurrentBatched(S, P, LongLiteral);
        store.AddCurrentBatched(S, P, "\"after\"");
        buffers.Fails = true;
        Assert.Throws<CommittedWriteNotAppliedException>(() => store.CommitBatch());
        store.Dispose();
        var logAsItWasLeft = File.ReadAllBytes(Path.Combine(path, "wal.log"));

        // The cause is still there: recovery meets it at the same record, and the store does not open.
        var failure = Assert.Throws<CommittedWriteNotAppliedException>(() => new QuadStore(path, null, buffers, new StorageOptions()));
        Assert.Contains("If opening fails in the same way, the log is kept", failure.Message);
        Assert.Equal(logAsItWasLeft, File.ReadAllBytes(Path.Combine(path, "wal.log")));

        // The cause is gone: the same process opens the store, and the log is applied.
        buffers.Fails = false;
        using var reopened = new QuadStore(path, null, buffers, new StorageOptions());
        Assert.Equal(1, PeriodsInEveryIndex(reopened, LongLiteral));
        Assert.Equal(1, PeriodsInEveryIndex(reopened, "\"after\""));
        Assert.Equal(2, reopened.GetStatistics().QuadCount);
    }

    [Fact]
    public void AWriteThatFailsBeforeItsCommit_LeavesTheStoreAsItWas_AndInUse()
    {
        var path = Path.Combine(_testDir, "before-commit");
        var buffers = new FailingBufferManager();
        using var store = new QuadStore(path, null, buffers, new StorageOptions());
        store.AddCurrent(S, P, "\"before\"");

        // The atom store rents a buffer for the long literal before anything is written to the log. That failure is
        // the other kind of error: nothing changed, and the store goes on.
        buffers.FailsForEveryRequest = true;
        Assert.Throws<InvalidOperationException>(() => store.AddCurrent(S, P, LongLiteral));
        buffers.FailsForEveryRequest = false;

        Assert.True(store.AddCurrent(S, P, "\"later\""));
        Assert.Equal(1, PeriodsInEveryIndex(store, "\"before\""));
        Assert.Equal(0, PeriodsInEveryIndex(store, LongLiteral));
        Assert.Equal(1, PeriodsInEveryIndex(store, "\"later\""));
        Assert.Equal(2, store.GetStatistics().QuadCount);
    }
}
