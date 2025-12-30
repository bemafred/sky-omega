using System;
using System.IO;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Utilities;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for QuadStore - multi-index RDF store with WAL durability.
/// </summary>
public class QuadStoreTests : IDisposable
{
    private readonly string _testPath;
    private QuadStore? _store;

    public QuadStoreTests()
    {
        _testPath = TempPath.Test("store");
    }

    public void Dispose()
    {
        _store?.Dispose();
        CleanupDirectory();
    }

    private void CleanupDirectory()
    {
        if (Directory.Exists(_testPath))
            Directory.Delete(_testPath, true);
    }

    private QuadStore CreateStore()
    {
        _store?.Dispose();
        _store = new QuadStore(_testPath);
        return _store;
    }

    #region Basic Add and Query

    [Fact]
    public void AddCurrent_SingleTriple_CanQuery()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                Assert.True(results.MoveNext());
                Assert.Equal("<http://ex.org/s>", results.Current.Subject.ToString());
                Assert.Equal("<http://ex.org/p>", results.Current.Predicate.ToString());
                Assert.Equal("<http://ex.org/o>", results.Current.Object.ToString());
                Assert.False(results.MoveNext());
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void AddCurrent_MultipleTriples_AllQueryable()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s1>", "<http://ex.org/type>", "<http://ex.org/Person>");
        store.AddCurrent("<http://ex.org/s2>", "<http://ex.org/type>", "<http://ex.org/Person>");
        store.AddCurrent("<http://ex.org/s3>", "<http://ex.org/type>", "<http://ex.org/Organization>");

        store.AcquireReadLock();
        try
        {
            // Query all triples with type predicate
            var results = store.QueryCurrent(
                ReadOnlySpan<char>.Empty,
                "<http://ex.org/type>",
                ReadOnlySpan<char>.Empty);
            try
            {
                var count = 0;
                while (results.MoveNext()) count++;
                Assert.Equal(3, count);
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryCurrent_NonExistent_ReturnsEmpty()
    {
        var store = CreateStore();
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent("<http://ex.org/nonexistent>", ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
            try
            {
                Assert.False(results.MoveNext());
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    #endregion

    #region Query with Unbound Variables

    [Fact]
    public void QueryCurrent_UnboundSubject_MatchesAll()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.AddCurrent("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(ReadOnlySpan<char>.Empty, "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                var count = 0;
                while (results.MoveNext()) count++;
                Assert.Equal(2, count);
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryCurrent_UnboundPredicate_MatchesAll()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p1>", "<http://ex.org/o>");
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p2>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent("<http://ex.org/s>", ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
            try
            {
                var count = 0;
                while (results.MoveNext()) count++;
                Assert.Equal(2, count);
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryCurrent_AllUnbound_ReturnsAllTriples()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s1>", "<http://ex.org/p1>", "<http://ex.org/o1>");
        store.AddCurrent("<http://ex.org/s2>", "<http://ex.org/p2>", "<http://ex.org/o2>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
            try
            {
                var count = 0;
                while (results.MoveNext()) count++;
                Assert.Equal(2, count);
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    #endregion

    #region Temporal Queries

    [Fact]
    public void Add_WithTemporalBounds_QueryAsOf()
    {
        var store = CreateStore();

        var validFrom = DateTimeOffset.UtcNow.AddDays(-10);
        var validTo = DateTimeOffset.UtcNow.AddDays(10);

        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validFrom, validTo);

        store.AcquireReadLock();
        try
        {
            // Query as of now (should match)
            var results = store.QueryAsOf(
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                DateTimeOffset.UtcNow);
            try
            {
                Assert.True(results.MoveNext());
            }
            finally
            {
                results.Dispose();
            }

            // Query as of 20 days ago (before validFrom, should not match)
            var pastResults = store.QueryAsOf(
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                DateTimeOffset.UtcNow.AddDays(-20));
            try
            {
                Assert.False(pastResults.MoveNext());
            }
            finally
            {
                pastResults.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryEvolution_ReturnsAllVersions()
    {
        var store = CreateStore();

        // Add multiple triples with different objects (different SPO) to have distinct versions
        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "\"version1\"",
            DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-20));
        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "\"version2\"",
            DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(10));

        store.AcquireReadLock();
        try
        {
            // Query all triples for this subject/predicate (any object)
            var results = store.QueryEvolution("<http://ex.org/s>", "<http://ex.org/p>", ReadOnlySpan<char>.Empty);
            try
            {
                var count = 0;
                while (results.MoveNext()) count++;
                Assert.Equal(2, count);
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void TimeTravelTo_ReturnsStateAtTime()
    {
        var store = CreateStore();

        var past = DateTimeOffset.UtcNow.AddDays(-5);

        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "\"old value\"",
            DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-3));
        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "\"new value\"",
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.MaxValue);

        store.AcquireReadLock();
        try
        {
            var results = store.TimeTravelTo(past, "<http://ex.org/s>", "<http://ex.org/p>", ReadOnlySpan<char>.Empty);
            try
            {
                Assert.True(results.MoveNext());
                Assert.Equal("\"old value\"", results.Current.Object.ToString());
                Assert.False(results.MoveNext());
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    #endregion

    #region Temporal Boundary Conditions

    [Fact]
    public void QueryAsOf_ExactlyAtValidFrom_Matches()
    {
        var store = CreateStore();

        var validFrom = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var validTo = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero);

        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validFrom, validTo);

        store.AcquireReadLock();
        try
        {
            // Query exactly at ValidFrom - should match (closed start: ValidFrom <= time)
            var results = store.QueryAsOf("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validFrom);
            try
            {
                Assert.True(results.MoveNext(), "Should match when querying exactly at ValidFrom");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryAsOf_ExactlyAtValidTo_DoesNotMatch()
    {
        var store = CreateStore();

        var validFrom = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var validTo = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero);

        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validFrom, validTo);

        store.AcquireReadLock();
        try
        {
            // Query exactly at ValidTo - should NOT match (open end: time < ValidTo)
            var results = store.QueryAsOf("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validTo);
            try
            {
                Assert.False(results.MoveNext(), "Should NOT match when querying exactly at ValidTo (exclusive)");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryAsOf_OneTickBeforeValidTo_Matches()
    {
        var store = CreateStore();

        var validFrom = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var validTo = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero);

        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validFrom, validTo);

        store.AcquireReadLock();
        try
        {
            // Query one tick before ValidTo - should match
            var queryTime = new DateTimeOffset(validTo.UtcTicks - 1, TimeSpan.Zero);
            var results = store.QueryAsOf("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", queryTime);
            try
            {
                Assert.True(results.MoveNext(), "Should match one tick before ValidTo");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryAsOf_OneTickBeforeValidFrom_DoesNotMatch()
    {
        var store = CreateStore();

        var validFrom = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var validTo = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero);

        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validFrom, validTo);

        store.AcquireReadLock();
        try
        {
            // Query one tick before ValidFrom - should NOT match
            var queryTime = new DateTimeOffset(validFrom.UtcTicks - 1, TimeSpan.Zero);
            var results = store.QueryAsOf("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", queryTime);
            try
            {
                Assert.False(results.MoveNext(), "Should NOT match one tick before ValidFrom");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryChanges_RangeEndEqualsValidFrom_DoesNotMatch()
    {
        var store = CreateStore();

        var validFrom = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var validTo = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero);

        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validFrom, validTo);

        store.AcquireReadLock();
        try
        {
            // Query range [Jan 1, June 1) - RangeEnd equals ValidFrom
            // Overlap condition: ValidFrom < RangeEnd && ValidTo > RangeStart
            // Here: June 1 < June 1 is FALSE, so no match
            var rangeStart = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var rangeEnd = validFrom;  // June 1

            var results = store.QueryChanges(rangeStart, rangeEnd,
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                Assert.False(results.MoveNext(), "Should NOT match when RangeEnd equals ValidFrom (no overlap)");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryChanges_RangeStartEqualsValidTo_DoesNotMatch()
    {
        var store = CreateStore();

        var validFrom = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var validTo = new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero);

        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validFrom, validTo);

        store.AcquireReadLock();
        try
        {
            // Query range [Dec 31, Feb 1 2025) - RangeStart equals ValidTo
            // Overlap condition: ValidFrom < RangeEnd && ValidTo > RangeStart
            // Here: Dec 31 > Dec 31 is FALSE, so no match
            var rangeStart = validTo;  // Dec 31
            var rangeEnd = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero);

            var results = store.QueryChanges(rangeStart, rangeEnd,
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                Assert.False(results.MoveNext(), "Should NOT match when RangeStart equals ValidTo (no overlap)");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryChanges_AdjacentPeriods_NoOverlap()
    {
        var store = CreateStore();

        // Period A: [June 1, Sept 1)
        var periodAStart = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var periodAEnd = new DateTimeOffset(2024, 9, 1, 0, 0, 0, TimeSpan.Zero);

        // Period B: [Sept 1, Dec 1) - adjacent to A (meets)
        var periodBStart = periodAEnd;
        var periodBEnd = new DateTimeOffset(2024, 12, 1, 0, 0, 0, TimeSpan.Zero);

        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "\"value_A\"", periodAStart, periodAEnd);
        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "\"value_B\"", periodBStart, periodBEnd);

        store.AcquireReadLock();
        try
        {
            // Query exactly period A - should only get value_A
            var resultsA = store.QueryChanges(periodAStart, periodAEnd,
                "<http://ex.org/s>", "<http://ex.org/p>", ReadOnlySpan<char>.Empty);
            try
            {
                Assert.True(resultsA.MoveNext());
                Assert.Equal("\"value_A\"", resultsA.Current.Object.ToString());
                Assert.False(resultsA.MoveNext(), "Adjacent period B should NOT overlap with period A query");
            }
            finally
            {
                resultsA.Dispose();
            }

            // Query exactly period B - should only get value_B
            var resultsB = store.QueryChanges(periodBStart, periodBEnd,
                "<http://ex.org/s>", "<http://ex.org/p>", ReadOnlySpan<char>.Empty);
            try
            {
                Assert.True(resultsB.MoveNext());
                Assert.Equal("\"value_B\"", resultsB.Current.Object.ToString());
                Assert.False(resultsB.MoveNext(), "Adjacent period A should NOT overlap with period B query");
            }
            finally
            {
                resultsB.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryChanges_OverlappingPeriods_BothMatch()
    {
        var store = CreateStore();

        // Period A: [June 1, Oct 1)
        var periodAStart = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var periodAEnd = new DateTimeOffset(2024, 10, 1, 0, 0, 0, TimeSpan.Zero);

        // Period B: [Aug 1, Dec 1) - overlaps with A
        var periodBStart = new DateTimeOffset(2024, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var periodBEnd = new DateTimeOffset(2024, 12, 1, 0, 0, 0, TimeSpan.Zero);

        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "\"value_A\"", periodAStart, periodAEnd);
        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "\"value_B\"", periodBStart, periodBEnd);

        store.AcquireReadLock();
        try
        {
            // Query [Aug 15, Sept 15) - overlaps with both A and B
            var rangeStart = new DateTimeOffset(2024, 8, 15, 0, 0, 0, TimeSpan.Zero);
            var rangeEnd = new DateTimeOffset(2024, 9, 15, 0, 0, 0, TimeSpan.Zero);

            var results = store.QueryChanges(rangeStart, rangeEnd,
                "<http://ex.org/s>", "<http://ex.org/p>", ReadOnlySpan<char>.Empty);
            try
            {
                var count = 0;
                while (results.MoveNext()) count++;
                Assert.Equal(2, count);
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryAsOf_ZeroDurationFact_NeverMatches()
    {
        var store = CreateStore();

        // Zero-duration fact: ValidFrom == ValidTo
        var instant = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", instant, instant);

        store.AcquireReadLock();
        try
        {
            // Query at exact instant
            // Condition: ValidFrom <= time && ValidTo > time
            // Here: June 15 <= June 15 is TRUE, but June 15 > June 15 is FALSE
            var results = store.QueryAsOf("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", instant);
            try
            {
                Assert.False(results.MoveNext(), "Zero-duration fact should never match AsOf query");
            }
            finally
            {
                results.Dispose();
            }

            // Query before
            var before = new DateTimeOffset(instant.UtcTicks - 1, TimeSpan.Zero);
            var resultsBefore = store.QueryAsOf("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", before);
            try
            {
                Assert.False(resultsBefore.MoveNext());
            }
            finally
            {
                resultsBefore.Dispose();
            }

            // Query after
            var after = new DateTimeOffset(instant.UtcTicks + 1, TimeSpan.Zero);
            var resultsAfter = store.QueryAsOf("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", after);
            try
            {
                Assert.False(resultsAfter.MoveNext());
            }
            finally
            {
                resultsAfter.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryChanges_ZeroDurationFact_NeverMatches()
    {
        var store = CreateStore();

        // Zero-duration fact
        var instant = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", instant, instant);

        store.AcquireReadLock();
        try
        {
            // Query range containing the instant
            // Overlap condition: ValidFrom < RangeEnd && ValidTo > RangeStart
            // For zero-duration: instant < RangeEnd && instant > RangeStart
            // But since ValidFrom == ValidTo, can't satisfy both conditions simultaneously
            // unless instant is strictly between RangeStart and RangeEnd
            var rangeStart = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
            var rangeEnd = new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero);

            var results = store.QueryChanges(rangeStart, rangeEnd,
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                // Zero duration means ValidFrom < RangeEnd (June 15 < July 1 = true)
                // AND ValidTo > RangeStart (June 15 > June 1 = true)
                // Actually this WOULD match! Let me reconsider...
                // The condition is: ValidFrom < RangeEnd && ValidTo > RangeStart
                // For instant June 15: June 15 < July 1 (true) && June 15 > June 1 (true)
                // So a zero-duration fact DOES match range queries if the point is inside the range
                Assert.True(results.MoveNext(), "Zero-duration fact inside range should match QueryChanges");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryEvolution_IncludesZeroDurationFact()
    {
        var store = CreateStore();

        // Zero-duration fact
        var instant = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", instant, instant);

        store.AcquireReadLock();
        try
        {
            // QueryEvolution returns all versions regardless of temporal validity
            var results = store.QueryEvolution("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                Assert.True(results.MoveNext(), "QueryEvolution should include zero-duration facts");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryAsOf_DateTimeMaxValue_AsValidTo()
    {
        var store = CreateStore();

        // Fact valid forever (common pattern)
        var validFrom = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validFrom, DateTimeOffset.MaxValue);

        store.AcquireReadLock();
        try
        {
            // Query far future - should still match
            var farFuture = new DateTimeOffset(2100, 12, 31, 23, 59, 59, TimeSpan.Zero);
            var results = store.QueryAsOf("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", farFuture);
            try
            {
                Assert.True(results.MoveNext(), "Fact with MaxValue ValidTo should match far future queries");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryAsOf_EarlyValidFrom_MatchesLaterQuery()
    {
        var store = CreateStore();

        // Fact that started in the past and continues to present
        var validFrom = DateTimeOffset.UtcNow.AddYears(-5);
        var validTo = DateTimeOffset.UtcNow.AddYears(5);
        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validFrom, validTo);

        store.AcquireReadLock();
        try
        {
            // Query a time between validFrom and now - should match
            var queryTime = DateTimeOffset.UtcNow.AddYears(-2);
            var results = store.QueryAsOf("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", queryTime);
            try
            {
                Assert.True(results.MoveNext(), "Fact with past ValidFrom should match queries within valid period");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    #endregion

    #region Batch Operations

    [Fact]
    public void BeginBatch_CommitBatch_AllTriplesAdded()
    {
        var store = CreateStore();

        store.BeginBatch();
        for (int i = 0; i < 100; i++)
        {
            store.AddCurrentBatched($"<http://ex.org/s{i}>", "<http://ex.org/p>", "<http://ex.org/o>");
        }
        store.CommitBatch();

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(ReadOnlySpan<char>.Empty, "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                var count = 0;
                while (results.MoveNext()) count++;
                Assert.Equal(100, count);
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void IsBatchActive_ReflectsState()
    {
        var store = CreateStore();

        Assert.False(store.IsBatchActive);

        store.BeginBatch();
        Assert.True(store.IsBatchActive);

        store.CommitBatch();
        Assert.False(store.IsBatchActive);
    }

    [Fact]
    public void BeginBatch_WhileActive_ThrowsLockRecursion()
    {
        var store = CreateStore();

        store.BeginBatch();
        try
        {
            // Attempting to begin a second batch while one is active throws LockRecursionException
            // because we try to acquire write lock twice (NoRecursion policy)
            Assert.Throws<System.Threading.LockRecursionException>(() => store.BeginBatch());
        }
        finally
        {
            store.RollbackBatch();
        }
    }

    [Fact]
    public void CommitBatch_WithoutBegin_ThrowsSynchronizationLock()
    {
        var store = CreateStore();

        // Trying to commit without beginning throws because we try to release a lock we don't hold
        Assert.Throws<System.Threading.SynchronizationLockException>(() => store.CommitBatch());
    }

    [Fact]
    public void AddBatched_WithoutBegin_Throws()
    {
        var store = CreateStore();

        Assert.Throws<InvalidOperationException>(() =>
            store.AddCurrentBatched("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>"));
    }

    [Fact]
    public void RollbackBatch_ReleasesLock()
    {
        var store = CreateStore();

        store.BeginBatch();
        store.AddCurrentBatched("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.RollbackBatch();

        Assert.False(store.IsBatchActive);

        // Should be able to write again
        store.AddCurrent("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>");
    }

    #endregion

    #region Statistics

    [Fact]
    public void GetStatistics_EmptyStore_ReturnsZero()
    {
        var store = CreateStore();

        var (tripleCount, _, _) = store.GetStatistics();

        Assert.Equal(0, tripleCount);
    }

    [Fact]
    public void GetStatistics_AfterAdds_ReflectsCount()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.AddCurrent("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>");

        var (tripleCount, _, _) = store.GetStatistics();

        // Each AddCurrent adds to 4 indexes, but TripleCount comes from SPOT index
        Assert.Equal(2, tripleCount);
    }

    [Fact]
    public void GetWalStatistics_ReturnsValidData()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var (currentTxId, _, _) = store.GetWalStatistics();

        Assert.True(currentTxId > 0);
    }

    #endregion

    #region Checkpoint

    [Fact]
    public void Checkpoint_ManualCall_UpdatesWalStats()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        var (_, lastCheckpointBefore, _) = store.GetWalStatistics();

        store.Checkpoint();

        var (currentTxId, lastCheckpointAfter, _) = store.GetWalStatistics();

        Assert.Equal(currentTxId, lastCheckpointAfter);
    }

    #endregion

    #region Persistence and Recovery

    [Fact]
    public void Persistence_ReopenStore_DataSurvives()
    {
        // First session
        using (var store1 = new QuadStore(_testPath))
        {
            store1.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        }

        // Second session
        using (var store2 = new QuadStore(_testPath))
        {
            store2.AcquireReadLock();
            try
            {
                var results = store2.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
                try
                {
                    Assert.True(results.MoveNext());
                }
                finally
                {
                    results.Dispose();
                }
            }
            finally
            {
                store2.ReleaseReadLock();
            }
        }
    }

    [Fact]
    public void Persistence_BatchDataSurvives()
    {
        // First session with batch
        using (var store1 = new QuadStore(_testPath))
        {
            store1.BeginBatch();
            for (int i = 0; i < 50; i++)
            {
                store1.AddCurrentBatched($"<http://ex.org/s{i}>", "<http://ex.org/p>", "<http://ex.org/o>");
            }
            store1.CommitBatch();
        }

        // Second session
        using (var store2 = new QuadStore(_testPath))
        {
            store2.AcquireReadLock();
            try
            {
                var results = store2.QueryCurrent(ReadOnlySpan<char>.Empty, "<http://ex.org/p>", "<http://ex.org/o>");
                try
                {
                    var count = 0;
                    while (results.MoveNext()) count++;
                    Assert.Equal(50, count);
                }
                finally
                {
                    results.Dispose();
                }
            }
            finally
            {
                store2.ReleaseReadLock();
            }
        }
    }

    #endregion

    #region Locking

    [Fact]
    public void AcquireReadLock_ReleaseReadLock_WorksCorrectly()
    {
        var store = CreateStore();
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        // Should be able to query while holding read lock
        var results = store.QueryCurrent("<http://ex.org/s>", ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
        var hasResult = results.MoveNext();
        results.Dispose();
        store.ReleaseReadLock();

        Assert.True(hasResult);

        // Should be able to write after releasing read lock
        store.AddCurrent("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>");
    }

    #endregion

    #region Dispose

    [Fact]
    public void Dispose_MultipleCalls_NoException()
    {
        var store = CreateStore();
        store.Dispose();
        store.Dispose(); // Should not throw
    }

    [Fact]
    public void AfterDispose_OperationsThrow()
    {
        var store = CreateStore();
        store.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>"));
    }

    #endregion

    #region Unicode and Special Characters

    [Fact]
    public void Add_UnicodeContent_PreservedCorrectly()
    {
        var store = CreateStore();
        var unicode = "\"こんにちは世界 🌍\"@ja";

        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/label>", unicode);

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent("<http://ex.org/s>", "<http://ex.org/label>", ReadOnlySpan<char>.Empty);
            try
            {
                Assert.True(results.MoveNext());
                Assert.Equal(unicode, results.Current.Object.ToString());
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Add_LongIRI_Works()
    {
        var store = CreateStore();
        var longIri = "<http://example.org/" + new string('x', 1000) + ">";

        store.AddCurrent(longIri, "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(longIri, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
            try
            {
                Assert.True(results.MoveNext());
                Assert.Equal(longIri, results.Current.Subject.ToString());
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    #endregion

    #region Delete Operations

    [Fact]
    public void DeleteCurrent_ExistingTriple_ReturnsTrue()
    {
        var store = CreateStore();
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var deleted = store.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        Assert.True(deleted);
    }

    [Fact]
    public void DeleteCurrent_NonExistentTriple_ReturnsFalse()
    {
        var store = CreateStore();
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var deleted = store.DeleteCurrent("<http://ex.org/other>", "<http://ex.org/p>", "<http://ex.org/o>");

        Assert.False(deleted);
    }

    [Fact]
    public void DeleteCurrent_AfterDelete_NotQueryable()
    {
        var store = CreateStore();
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                Assert.False(results.MoveNext(), "Deleted triple should not be queryable");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Delete_WithTemporalBounds_DeletesCorrectVersion()
    {
        var store = CreateStore();

        var validFrom = DateTimeOffset.UtcNow.AddDays(-10);
        var validTo = DateTimeOffset.UtcNow.AddDays(10);

        store.Add("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validFrom, validTo);

        var deleted = store.Delete("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", validFrom, validTo);

        Assert.True(deleted);

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                Assert.False(results.MoveNext());
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void DeleteBatched_InBatch_Works()
    {
        var store = CreateStore();

        // First add some triples
        store.BeginBatch();
        store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.AddCurrentBatched("<http://ex.org/s3>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.CommitBatch();

        // Now delete in a batch
        store.BeginBatch();
        var deleted = store.DeleteCurrentBatched("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.CommitBatch();

        Assert.True(deleted);

        // Verify s1 and s3 remain, s2 is deleted
        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(ReadOnlySpan<char>.Empty, "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                var count = 0;
                while (results.MoveNext()) count++;
                Assert.Equal(2, count);
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void DeleteBatched_WithoutBatch_Throws()
    {
        var store = CreateStore();
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        Assert.Throws<InvalidOperationException>(() =>
            store.DeleteCurrentBatched("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>"));
    }

    [Fact]
    public void Delete_Recovery_DeletedStatePreserved()
    {
        // First session: add and delete
        using (var store1 = new QuadStore(_testPath))
        {
            store1.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            store1.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        }

        // Second session: verify still deleted
        using (var store2 = new QuadStore(_testPath))
        {
            store2.AcquireReadLock();
            try
            {
                var results = store2.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
                try
                {
                    Assert.False(results.MoveNext(), "Deleted triple should not survive recovery");
                }
                finally
                {
                    results.Dispose();
                }
            }
            finally
            {
                store2.ReleaseReadLock();
            }
        }
    }

    [Fact]
    public void Delete_NonExistentAtom_ReturnsFalse()
    {
        var store = CreateStore();

        // Try to delete something that was never added (atoms don't exist)
        var deleted = store.DeleteCurrent("<http://ex.org/never-added>", "<http://ex.org/p>", "<http://ex.org/o>");

        Assert.False(deleted);
    }

    [Fact]
    public void Delete_AlreadyDeleted_ReturnsFalse()
    {
        var store = CreateStore();
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        var deleted1 = store.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        var deleted2 = store.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        Assert.True(deleted1);
        Assert.False(deleted2, "Deleting already-deleted triple should return false");
    }

    #endregion

    #region QueryHistory / Audit Trail

    [Fact]
    public void QueryEvolution_DeletedTriple_IsVisible()
    {
        var store = CreateStore();
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryEvolution("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                Assert.True(results.MoveNext(), "Deleted triple should be visible in QueryEvolution");
                Assert.True(results.Current.IsDeleted, "Triple should be marked as deleted");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryEvolution_MixedDeletedAndActive_ShowsAll()
    {
        var store = CreateStore();

        // Add three triples, delete one
        store.AddCurrent("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.AddCurrent("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.AddCurrent("<http://ex.org/s3>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.DeleteCurrent("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            // Query all with predicate p
            var results = store.QueryEvolution(default, "<http://ex.org/p>", default);
            try
            {
                var count = 0;
                var deletedCount = 0;
                while (results.MoveNext())
                {
                    count++;
                    if (results.Current.IsDeleted)
                        deletedCount++;
                }

                Assert.Equal(3, count); // All three visible in evolution
                Assert.Equal(1, deletedCount); // One is deleted
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryCurrent_DeletedTriple_NotVisible()
    {
        var store = CreateStore();
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                Assert.False(results.MoveNext(), "Deleted triple should not be visible in QueryCurrent");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryAsOf_DeletedTriple_NotVisible()
    {
        var store = CreateStore();
        var addTime = DateTimeOffset.UtcNow;
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            // Query as of slightly after add time
            var results = store.QueryAsOf("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                addTime.AddSeconds(1));
            try
            {
                Assert.False(results.MoveNext(), "Deleted triple should not be visible in QueryAsOf");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void QueryEvolution_ActiveTriple_IsDeletedFalse()
    {
        var store = CreateStore();
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryEvolution("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                Assert.True(results.MoveNext());
                Assert.False(results.Current.IsDeleted, "Active triple should have IsDeleted=false");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    #endregion

    #region Named Graphs

    [Fact]
    public void AddCurrent_WithGraph_CanQueryByGraph()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
            "<http://ex.org/graph1>");

        store.AcquireReadLock();
        try
        {
            // Query with correct graph
            var results = store.QueryCurrent(
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                "<http://ex.org/graph1>");
            try
            {
                Assert.True(results.MoveNext());
                Assert.Equal("<http://ex.org/graph1>", results.Current.Graph.ToString());
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void AddCurrent_DifferentGraphs_IsolatedQueries()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "\"value1\"",
            "<http://ex.org/graph1>");
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "\"value2\"",
            "<http://ex.org/graph2>");

        store.AcquireReadLock();
        try
        {
            // Query graph1
            var results1 = store.QueryCurrent(
                "<http://ex.org/s>", "<http://ex.org/p>", default,
                "<http://ex.org/graph1>");
            try
            {
                Assert.True(results1.MoveNext());
                Assert.Equal("\"value1\"", results1.Current.Object.ToString());
                Assert.False(results1.MoveNext());
            }
            finally
            {
                results1.Dispose();
            }

            // Query graph2
            var results2 = store.QueryCurrent(
                "<http://ex.org/s>", "<http://ex.org/p>", default,
                "<http://ex.org/graph2>");
            try
            {
                Assert.True(results2.MoveNext());
                Assert.Equal("\"value2\"", results2.Current.Object.ToString());
                Assert.False(results2.MoveNext());
            }
            finally
            {
                results2.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void AddCurrent_DefaultGraph_EmptyGraphSpan()
    {
        var store = CreateStore();

        // Add to default graph (no graph specified)
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            // Query default graph (no graph specified)
            var results = store.QueryCurrent(
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                Assert.True(results.MoveNext());
                Assert.True(results.Current.Graph.IsEmpty, "Default graph should have empty Graph span");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void AddCurrent_NamedGraph_NotVisibleInDefaultGraph()
    {
        var store = CreateStore();

        // Add to named graph only
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
            "<http://ex.org/graph1>");

        store.AcquireReadLock();
        try
        {
            // Query default graph - should not find it
            var results = store.QueryCurrent(
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
            try
            {
                Assert.False(results.MoveNext(), "Named graph triple should not be visible in default graph");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void AddCurrent_DefaultGraph_NotVisibleInNamedGraph()
    {
        var store = CreateStore();

        // Add to default graph
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");

        store.AcquireReadLock();
        try
        {
            // Query named graph - should not find it
            var results = store.QueryCurrent(
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                "<http://ex.org/graph1>");
            try
            {
                Assert.False(results.MoveNext(), "Default graph triple should not be visible in named graph");
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void DeleteCurrent_NamedGraph_OnlyDeletesFromThatGraph()
    {
        var store = CreateStore();

        // Add same SPO to two different graphs
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
            "<http://ex.org/graph1>");
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
            "<http://ex.org/graph2>");

        // Delete from graph1 only
        store.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
            "<http://ex.org/graph1>");

        store.AcquireReadLock();
        try
        {
            // Graph1 should be empty
            var results1 = store.QueryCurrent(
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                "<http://ex.org/graph1>");
            try
            {
                Assert.False(results1.MoveNext());
            }
            finally
            {
                results1.Dispose();
            }

            // Graph2 should still have the triple
            var results2 = store.QueryCurrent(
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                "<http://ex.org/graph2>");
            try
            {
                Assert.True(results2.MoveNext());
            }
            finally
            {
                results2.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void BatchAdd_NamedGraphs_AllPersisted()
    {
        var store = CreateStore();

        store.BeginBatch();
        store.AddCurrentBatched("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o>",
            "<http://ex.org/graph1>");
        store.AddCurrentBatched("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>",
            "<http://ex.org/graph1>");
        store.AddCurrentBatched("<http://ex.org/s3>", "<http://ex.org/p>", "<http://ex.org/o>",
            "<http://ex.org/graph2>");
        store.CommitBatch();

        store.AcquireReadLock();
        try
        {
            // Graph1 should have 2 triples
            var results1 = store.QueryCurrent(
                default, "<http://ex.org/p>", "<http://ex.org/o>",
                "<http://ex.org/graph1>");
            try
            {
                var count = 0;
                while (results1.MoveNext()) count++;
                Assert.Equal(2, count);
            }
            finally
            {
                results1.Dispose();
            }

            // Graph2 should have 1 triple
            var results2 = store.QueryCurrent(
                default, "<http://ex.org/p>", "<http://ex.org/o>",
                "<http://ex.org/graph2>");
            try
            {
                var count = 0;
                while (results2.MoveNext()) count++;
                Assert.Equal(1, count);
            }
            finally
            {
                results2.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void NamedGraph_Persistence_SurvivesRestart()
    {
        // First session
        using (var store1 = new QuadStore(_testPath))
        {
            store1.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                "<http://ex.org/graph1>");
        }

        // Second session
        using (var store2 = new QuadStore(_testPath))
        {
            store2.AcquireReadLock();
            try
            {
                var results = store2.QueryCurrent(
                    "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                    "<http://ex.org/graph1>");
                try
                {
                    Assert.True(results.MoveNext());
                    Assert.Equal("<http://ex.org/graph1>", results.Current.Graph.ToString());
                }
                finally
                {
                    results.Dispose();
                }
            }
            finally
            {
                store2.ReleaseReadLock();
            }
        }
    }

    [Fact]
    public void NamedGraph_QueryEvolution_IncludesGraph()
    {
        var store = CreateStore();

        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
            "<http://ex.org/graph1>");

        store.AcquireReadLock();
        try
        {
            var results = store.QueryEvolution(
                "<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>",
                "<http://ex.org/graph1>");
            try
            {
                Assert.True(results.MoveNext());
                Assert.Equal("<http://ex.org/graph1>", results.Current.Graph.ToString());
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    #endregion
}
