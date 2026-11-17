using System;
using System.IO;
using System.Linq;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-061 D6 to D10: the words of Cognitive SPARQL. <c>MEMORIZED</c> and <c>FORGOTTEN</c> follow a triple pattern,
/// bind the two times of the period the pattern matched, and select by them in the half-open range of <c>SINCE</c>
/// and <c>BEFORE</c>. <c>INCLUDING FORGOTTEN</c> after the query makes every pattern match forgotten memories too.
/// Without them a pattern matches what is remembered, and reads no clock (D7). <c>AS OF</c>, <c>DURING</c> and
/// <c>ALL VERSIONS</c> are gone (D8). Each test supplies the store's clock and states its sequence inline: a fact is
/// memorized at one reading and forgotten at a later one.
/// </summary>
public class MemoryWordsTests : IDisposable
{
    private const string Xsd = "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> ";
    private const string DateTimeType = "^^<http://www.w3.org/2001/XMLSchema#dateTime>";

    private static readonly DateTimeOffset Hour0 = new(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Hour1 = Hour0.AddHours(1);
    private static readonly DateTimeOffset Hour2 = Hour0.AddHours(2);
    private static readonly DateTimeOffset Hour3 = Hour0.AddHours(3);

    // The four readings as bounds, and as the time a word binds: an xsd:dateTime in UTC with a millisecond fraction
    private const string H0 = "\"2031-01-01T00:00:00Z\"^^xsd:dateTime";
    private const string H1 = "\"2031-01-01T01:00:00Z\"^^xsd:dateTime";
    private const string H2 = "\"2031-01-01T02:00:00Z\"^^xsd:dateTime";
    private const string H3 = "\"2031-01-01T03:00:00Z\"^^xsd:dateTime";
    private const string Bound0 = "\"2031-01-01T00:00:00.000Z\"" + DateTimeType;
    private const string Bound1 = "\"2031-01-01T01:00:00.000Z\"" + DateTimeType;
    private const string Bound2 = "\"2031-01-01T02:00:00.000Z\"" + DateTimeType;
    private const string Bound3 = "\"2031-01-01T03:00:00.000Z\"" + DateTimeType;

    private readonly string _testDir;

    public MemoryWordsTests()
    {
        var tempPath = TempPath.Test("adr061-memory-words");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    [Fact]
    public void MemorizedWithAVariable_BindsWhenTheMatchedMemoryWasMemorized()
    {
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");
        clock.Reading = Hour1;
        store.AddCurrent("<urn:s>", "<urn:p>", "\"b\"");

        var result = SparqlEngine.Query(store, "SELECT ?o ?t WHERE { <urn:s> <urn:p> ?o MEMORIZED ?t } ORDER BY ?o",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "\"a\" " + Bound0, "\"b\" " + Bound1 }, result.Rows!.Select(row => row["o"] + " " + row["t"]));
    }

    [Fact]
    public void ForgottenWithAVariable_BindsWhenItWasForgotten_AndLeavesItUnboundForAMemoryThatIsRemembered()
    {
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");
        store.AddCurrent("<urn:s>", "<urn:p>", "\"b\"");
        clock.Reading = Hour1;
        store.DeleteCurrent("<urn:s>", "<urn:p>", "\"a\"");

        var result = SparqlEngine.Query(store, "SELECT ?o ?f WHERE { <urn:s> <urn:p> ?o FORGOTTEN ?f } ORDER BY ?o",
            TestContext.Current.CancellationToken);

        // A pattern that carries FORGOTTEN matches forgotten memories too; a remembered one has no forgotten time
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.Rows!.Count);
        Assert.Equal("\"a\"", result.Rows[0]["o"]);
        Assert.Equal(Bound1, result.Rows[0]["f"]);
        Assert.Equal("\"b\"", result.Rows[1]["o"]);
        Assert.False(result.Rows[1].ContainsKey("f"));
    }

    [Fact]
    public void WithoutTheWords_APatternMatchesWhatIsRemembered_AndReadsNoClock()
    {
        // "a" is memorized at a later reading than the query's, "b" was forgotten at a later one. A default view that
        // read the clock would give "b" and not "a". What is remembered is "a".
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:s>", "<urn:p>", "\"b\"");
        clock.Reading = Hour1;
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");
        store.DeleteCurrent("<urn:s>", "<urn:p>", "\"b\"");
        clock.Reading = Hour0.AddMinutes(30);

        var result = SparqlEngine.Query(store, "SELECT ?o WHERE { <urn:s> <urn:p> ?o }", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("\"a\"", result.Rows!.Single()["o"]);
    }

    [Fact]
    public void MemorizedSinceAndBefore_KeepTheMemoriesMemorizedInTheHalfOpenRange_InEitherOrder()
    {
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");
        clock.Reading = Hour1;
        store.AddCurrent("<urn:s>", "<urn:p>", "\"b\"");
        clock.Reading = Hour2;
        store.AddCurrent("<urn:s>", "<urn:p>", "\"c\"");
        clock.Reading = Hour3;
        store.AddCurrent("<urn:s>", "<urn:p>", "\"d\"");

        var sinceBefore = SparqlEngine.Query(store, Xsd + "SELECT ?o WHERE { <urn:s> <urn:p> ?o MEMORIZED SINCE " + H1 + " BEFORE " + H3 + " } ORDER BY ?o",
            TestContext.Current.CancellationToken);
        var beforeSince = SparqlEngine.Query(store, Xsd + "SELECT ?o WHERE { <urn:s> <urn:p> ?o MEMORIZED BEFORE " + H3 + " SINCE " + H1 + " } ORDER BY ?o",
            TestContext.Current.CancellationToken);
        var since = SparqlEngine.Query(store, Xsd + "SELECT ?o WHERE { <urn:s> <urn:p> ?o MEMORIZED SINCE " + H2 + " } ORDER BY ?o",
            TestContext.Current.CancellationToken);
        var before = SparqlEngine.Query(store, Xsd + "SELECT ?o WHERE { <urn:s> <urn:p> ?o MEMORIZED BEFORE " + H1 + " } ORDER BY ?o",
            TestContext.Current.CancellationToken);

        Assert.True(sinceBefore.Success, sinceBefore.ErrorMessage);
        Assert.Equal(new[] { "\"b\"", "\"c\"" }, sinceBefore.Rows!.Select(row => row["o"]));
        Assert.True(beforeSince.Success, beforeSince.ErrorMessage);
        Assert.Equal(new[] { "\"b\"", "\"c\"" }, beforeSince.Rows!.Select(row => row["o"]));
        Assert.True(since.Success, since.ErrorMessage);
        Assert.Equal(new[] { "\"c\"", "\"d\"" }, since.Rows!.Select(row => row["o"]));
        Assert.True(before.Success, before.ErrorMessage);
        Assert.Equal(new[] { "\"a\"" }, before.Rows!.Select(row => row["o"]));
    }

    [Fact]
    public void AnEmptyRange_MatchesNothing_AndIsNotAnError()
    {
        var clock = new SuppliedClock { Reading = Hour1 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");

        var reversed = SparqlEngine.Query(store, Xsd + "SELECT ?o WHERE { <urn:s> <urn:p> ?o MEMORIZED SINCE " + H2 + " BEFORE " + H0 + " }",
            TestContext.Current.CancellationToken);
        var equal = SparqlEngine.Query(store, Xsd + "SELECT ?o WHERE { <urn:s> <urn:p> ?o MEMORIZED SINCE " + H1 + " BEFORE " + H1 + " }",
            TestContext.Current.CancellationToken);

        Assert.True(reversed.Success, reversed.ErrorMessage);
        Assert.Empty(reversed.Rows!);
        Assert.True(equal.Success, equal.ErrorMessage);
        Assert.Empty(equal.Rows!);
    }

    [Fact]
    public void ForgottenSinceAndBefore_KeepOnlyTheMemoriesForgottenInTheRange()
    {
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");
        store.AddCurrent("<urn:s>", "<urn:p>", "\"b\"");
        store.AddCurrent("<urn:s>", "<urn:p>", "\"c\"");
        store.AddCurrent("<urn:s>", "<urn:p>", "\"d\"");
        clock.Reading = Hour1;
        store.DeleteCurrent("<urn:s>", "<urn:p>", "\"a\"");
        clock.Reading = Hour2;
        store.DeleteCurrent("<urn:s>", "<urn:p>", "\"b\"");
        clock.Reading = Hour3;
        store.DeleteCurrent("<urn:s>", "<urn:p>", "\"c\"");

        var since = SparqlEngine.Query(store, Xsd + "SELECT ?o WHERE { <urn:s> <urn:p> ?o FORGOTTEN SINCE " + H2 + " } ORDER BY ?o",
            TestContext.Current.CancellationToken);
        var before = SparqlEngine.Query(store, Xsd + "SELECT ?o WHERE { <urn:s> <urn:p> ?o FORGOTTEN BEFORE " + H2 + " } ORDER BY ?o",
            TestContext.Current.CancellationToken);
        var unbounded = SparqlEngine.Query(store, "SELECT ?o WHERE { <urn:s> <urn:p> ?o FORGOTTEN } ORDER BY ?o",
            TestContext.Current.CancellationToken);

        // "d" is remembered: it has no forgotten time, so no FORGOTTEN bound selects it
        Assert.True(since.Success, since.ErrorMessage);
        Assert.Equal(new[] { "\"b\"", "\"c\"" }, since.Rows!.Select(row => row["o"]));
        Assert.True(before.Success, before.ErrorMessage);
        Assert.Equal(new[] { "\"a\"" }, before.Rows!.Select(row => row["o"]));
        Assert.True(unbounded.Success, unbounded.ErrorMessage);
        Assert.Equal(new[] { "\"a\"", "\"b\"", "\"c\"", "\"d\"" }, unbounded.Rows!.Select(row => row["o"]));
    }

    [Fact]
    public void BothWordsOnOnePattern_EachSelectsAndBindsByItsOwnTime_InEitherOrder()
    {
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");
        clock.Reading = Hour1;
        store.AddCurrent("<urn:s>", "<urn:p>", "\"b\"");
        clock.Reading = Hour2;
        store.DeleteCurrent("<urn:s>", "<urn:p>", "\"a\"");
        clock.Reading = Hour3;
        store.DeleteCurrent("<urn:s>", "<urn:p>", "\"b\"");

        var memorizedFirst = SparqlEngine.Query(store,
            Xsd + "SELECT ?o ?m ?f WHERE { <urn:s> <urn:p> ?o MEMORIZED ?m BEFORE " + H1 + " FORGOTTEN ?f SINCE " + H2 + " }",
            TestContext.Current.CancellationToken);
        var forgottenFirst = SparqlEngine.Query(store,
            Xsd + "SELECT ?o ?m ?f WHERE { <urn:s> <urn:p> ?o FORGOTTEN ?f SINCE " + H2 + " MEMORIZED ?m BEFORE " + H1 + " }",
            TestContext.Current.CancellationToken);

        Assert.True(memorizedFirst.Success, memorizedFirst.ErrorMessage);
        var row = Assert.Single(memorizedFirst.Rows!);
        Assert.Equal("\"a\" " + Bound0 + " " + Bound2, row["o"] + " " + row["m"] + " " + row["f"]);
        Assert.True(forgottenFirst.Success, forgottenFirst.ErrorMessage);
        row = Assert.Single(forgottenFirst.Rows!);
        Assert.Equal("\"a\" " + Bound0 + " " + Bound2, row["o"] + " " + row["m"] + " " + row["f"]);
    }

    [Fact]
    public void IncludingForgotten_MakesEveryPatternOfTheQueryMatchForgottenMemoriesToo()
    {
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");
        store.AddCurrent("<urn:s>", "<urn:q>", "\"x\"");
        clock.Reading = Hour1;
        store.DeleteCurrent("<urn:s>", "<urn:p>", "\"a\"");
        store.DeleteCurrent("<urn:s>", "<urn:q>", "\"x\"");

        var including = SparqlEngine.Query(store,
            "SELECT ?o ?x WHERE { <urn:s> <urn:p> ?o . <urn:s> <urn:q> ?x } LIMIT 10 OFFSET 0 INCLUDING FORGOTTEN",
            TestContext.Current.CancellationToken);
        var remembered = SparqlEngine.Query(store, "SELECT ?o ?x WHERE { <urn:s> <urn:p> ?o . <urn:s> <urn:q> ?x }",
            TestContext.Current.CancellationToken);

        Assert.True(including.Success, including.ErrorMessage);
        var row = Assert.Single(including.Rows!);
        Assert.Equal("\"a\" \"x\"", row["o"] + " " + row["x"]);
        Assert.True(remembered.Success, remembered.ErrorMessage);
        Assert.Empty(remembered.Rows!);
    }

    [Fact]
    public void AFactMemorizedForgottenAndMemorizedAgain_GivesOneSolutionPerPeriod_EachWithItsOwnTimes()
    {
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");
        clock.Reading = Hour1;
        store.DeleteCurrent("<urn:s>", "<urn:p>", "\"a\"");
        clock.Reading = Hour2;
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");

        var result = SparqlEngine.Query(store, "SELECT ?m ?f WHERE { <urn:s> <urn:p> \"a\" MEMORIZED ?m FORGOTTEN ?f } ORDER BY ?m",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.Rows!.Count);
        Assert.Equal(Bound0 + " " + Bound1, result.Rows[0]["m"] + " " + result.Rows[0]["f"]);
        Assert.Equal(Bound2, result.Rows[1]["m"]);
        Assert.False(result.Rows[1].ContainsKey("f"));
    }

    [Fact]
    public void InAJoin_EachPatternKeepsItsOwnTimes_AndABoundOnTheAnchorDoesNotCutAnOlderMemory()
    {
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:component>", "<urn:name>", "\"Mercury\"");
        clock.Reading = Hour1;
        store.AddCurrent("<urn:component>", "<urn:finding>", "\"one\"");

        var times = SparqlEngine.Query(store,
            "SELECT ?tn ?tf WHERE { <urn:component> <urn:name> ?n MEMORIZED ?tn . <urn:component> <urn:finding> ?x MEMORIZED ?tf }",
            TestContext.Current.CancellationToken);
        var anchored = SparqlEngine.Query(store,
            Xsd + "SELECT ?n WHERE { ?c <urn:finding> ?x MEMORIZED SINCE " + H1 + " . ?c <urn:name> ?n }",
            TestContext.Current.CancellationToken);

        Assert.True(times.Success, times.ErrorMessage);
        var row = Assert.Single(times.Rows!);
        Assert.Equal(Bound0 + " " + Bound1, row["tn"] + " " + row["tf"]);
        Assert.True(anchored.Success, anchored.ErrorMessage);
        Assert.Equal("\"Mercury\"", Assert.Single(anchored.Rows!)["n"]);
    }

    /// <summary>
    /// ADR-061 Q10: "was q remembered at t" has no word of its own. With b one millisecond after t, the union gives
    /// each quad of M(t) once, with the bounds of the period that holds t, or nothing. The quad is memorized at hour 0,
    /// forgotten at hour 1 and memorized again at hour 2.
    /// </summary>
    [Theory]
    [InlineData(-30, -1, -1)]
    [InlineData(0, 0, 1)]
    [InlineData(30, 0, 1)]
    [InlineData(60, -1, -1)]
    [InlineData(90, -1, -1)]
    [InlineData(120, 2, -1)]
    [InlineData(150, 2, -1)]
    public void TheQueryOfQ10_GivesThePeriodThatHoldsATime(int minutes, int memorizedHour, int forgottenHour)
    {
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");
        clock.Reading = Hour1;
        store.DeleteCurrent("<urn:s>", "<urn:p>", "\"a\"");
        clock.Reading = Hour2;
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");
        clock.Reading = Hour3;

        var b = "\"" + Hour0.AddMinutes(minutes).AddMilliseconds(1).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            System.Globalization.CultureInfo.InvariantCulture) + "\"^^xsd:dateTime";
        var result = SparqlEngine.Query(store, Xsd + "SELECT ?m ?f WHERE { "
            + "{ <urn:s> <urn:p> \"a\" MEMORIZED ?m BEFORE " + b + " } UNION "
            + "{ <urn:s> <urn:p> \"a\" MEMORIZED ?m BEFORE " + b + " FORGOTTEN ?f SINCE " + b + " } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        if (memorizedHour < 0)
        {
            Assert.Empty(result.Rows!);
            return;
        }
        var row = Assert.Single(result.Rows!);
        Assert.Equal(new[] { Bound0, Bound1, Bound2 }[memorizedHour], row["m"]);
        if (forgottenHour < 0)
            Assert.False(row.ContainsKey("f"));
        else
            Assert.Equal(new[] { Bound0, Bound1, Bound2 }[forgottenHour], row["f"]);
    }

    [Theory]
    [InlineData(StoreProfile.Graph)]
    [InlineData(StoreProfile.Reference)]
    [InlineData(StoreProfile.Minimal)]
    public void TheWords_AreAnError_OnAStoreThatKeepsNoTimes_EvenWhenThePatternIsNeverScanned(StoreProfile profile)
    {
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null,
            new StorageOptions { Profile = profile, IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 });

        // The store is empty, so a query that read nothing would succeed with no rows; the second pattern of the
        // last query is never scanned, because the first matches nothing
        var queries = new[]
        {
            "SELECT ?t WHERE { ?s ?p ?o MEMORIZED ?t }",
            Xsd + "SELECT ?s WHERE { ?s ?p ?o FORGOTTEN SINCE " + H1 + " }",
            "SELECT ?s WHERE { ?s ?p ?o } INCLUDING FORGOTTEN",
            "SELECT ?x WHERE { <urn:none> <urn:p> ?o . ?o <urn:q> ?x MEMORIZED }",
        };
        foreach (var query in queries)
        {
            var result = SparqlEngine.Query(store, query, TestContext.Current.CancellationToken);
            Assert.False(result.Success, query);
            Assert.Contains("requires temporal semantics", result.ErrorMessage);
            Assert.Contains(profile.ToString(), result.ErrorMessage);
        }
    }

    [Theory]
    [InlineData(StoreProfile.Graph)]
    [InlineData(StoreProfile.Minimal)]
    public void AWordInAnExistsInsideACompoundFilter_IsAnError_WhenARowReachesIt(StoreProfile profile)
    {
        // The parser keeps such an EXISTS as text, and the executor reads it when a row reaches the FILTER. The
        // pattern with the word is never scanned: the one before it matches nothing
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null,
            new StorageOptions { Profile = profile, IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 });
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");

        var result = SparqlEngine.Query(store,
            "SELECT ?o WHERE { <urn:s> <urn:p> ?o FILTER(?o = \"z\" || EXISTS { <urn:none> <urn:q> ?y . ?y <urn:q> ?x FORGOTTEN }) }",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("requires temporal semantics", result.ErrorMessage);
    }

    [Theory]
    [InlineData("SELECT * WHERE { ?s ?p ?o } AS OF \"2031-01-01\"^^xsd:date")]
    [InlineData("SELECT * WHERE { ?s ?p ?o } DURING [\"2031-01-01\"^^xsd:date, \"2031-02-01\"^^xsd:date]")]
    [InlineData("SELECT * WHERE { ?s ?p ?o } ALL VERSIONS")]
    public void TheOldClauses_AreSyntaxErrors(string query)
    {
        using var store = new QuadStore(Path.Combine(_testDir, "store"));
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");

        var result = SparqlEngine.Query(store, Xsd + query, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("Unexpected text after the query", result.ErrorMessage);
    }

    [Theory]
    [InlineData("SELECT ?o WHERE { <urn:s> <urn:p> ?o MEMORIZED SINCE " + H0 + " SINCE " + H1 + " }", "at most once")]
    [InlineData("SELECT ?o WHERE { <urn:s> <urn:p> ?o FORGOTTEN BEFORE " + H0 + " BEFORE " + H1 + " }", "at most once")]
    [InlineData("SELECT ?o WHERE { <urn:s> <urn:p> ?o MEMORIZED ?a MEMORIZED ?b }", "at most once")]
    [InlineData("SELECT ?o WHERE { <urn:s> <urn:p>/<urn:q> ?o MEMORIZED ?t }", "property path")]
    [InlineData("SELECT ?o WHERE { <urn:s> <urn:p>* ?o MEMORIZED ?t }", "property path")]
    [InlineData("SELECT ?o WHERE { <urn:s> ^<urn:p> ?o FORGOTTEN }", "property path")]
    [InlineData("SELECT ?o WHERE { SERVICE <http://example.org/sparql> { <urn:s> <urn:p> ?o MEMORIZED ?t } }", "SERVICE")]
    [InlineData("SELECT ?o WHERE { <urn:s> <urn:p> ?o MEMORIZED SINCE ?x }", "without variables")]
    [InlineData("SELECT ?o WHERE { <urn:s> <urn:p> ?o MEMORIZED SINCE }", "a time")]
    [InlineData("SELECT ?o WHERE { <urn:s> <urn:p> ?o MEMORIZED ?t BIND(1 AS ?t) }", "already in scope")]
    [InlineData("SELECT ?s WHERE { { SELECT ?s WHERE { ?s ?p ?o } INCLUDING FORGOTTEN } }", "")]
    public void AWordOutOfItsPlace_IsASyntaxError(string query, string reason)
    {
        using var store = new QuadStore(Path.Combine(_testDir, "store"));

        var result = SparqlEngine.Query(store, Xsd + query, TestContext.Current.CancellationToken);

        Assert.False(result.Success, query);
        Assert.Contains(reason, result.ErrorMessage);
    }

    [Fact]
    public void ABoundBetweenTwoMilliseconds_KeepsTheRangeHalfOpen()
    {
        // Every stored time is a whole millisecond. A memory memorized at 01:00:00.000 is before 01:00:00.0005 and
        // not since it.
        var clock = new SuppliedClock { Reading = Hour1 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");

        var since = SparqlEngine.Query(store,
            Xsd + "SELECT ?o WHERE { <urn:s> <urn:p> ?o MEMORIZED SINCE \"2031-01-01T01:00:00.0005Z\"^^xsd:dateTime }",
            TestContext.Current.CancellationToken);
        var before = SparqlEngine.Query(store,
            Xsd + "SELECT ?o WHERE { <urn:s> <urn:p> ?o MEMORIZED BEFORE \"2031-01-01T01:00:00.0005Z\"^^xsd:dateTime }",
            TestContext.Current.CancellationToken);

        Assert.True(since.Success, since.ErrorMessage);
        Assert.Empty(since.Rows!);
        Assert.True(before.Success, before.ErrorMessage);
        Assert.Equal("\"a\"", Assert.Single(before.Rows!)["o"]);
    }

    [Fact]
    public void ABoundFollowedByAGroup_EndsWhereTheGroupBegins()
    {
        var clock = new SuppliedClock { Reading = Hour1 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");
        store.AddCurrent("<urn:s>", "<urn:q>", "\"b\"");

        var result = SparqlEngine.Query(store,
            Xsd + "SELECT ?o ?x WHERE { <urn:s> <urn:p> ?o MEMORIZED SINCE " + H0 + " { <urn:s> <urn:q> ?x } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("\"a\" \"b\"", row["o"] + " " + row["x"]);
    }

    [Fact]
    public void ABoundThatIsNotADateOrADateTime_IsAnError()
    {
        using var store = new QuadStore(Path.Combine(_testDir, "store"));
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");

        var result = SparqlEngine.Query(store, "SELECT ?o WHERE { <urn:s> <urn:p> ?o MEMORIZED SINCE \"yesterday\" }",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("xsd:dateTime", result.ErrorMessage);
    }

    [Fact]
    public void ABound_CanBeAnExpressionWithoutVariables_EvaluatedWithTheQuerysNow()
    {
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");
        clock.Reading = Hour3;
        store.AddCurrent("<urn:s>", "<urn:p>", "\"b\"");
        clock.Reading = Hour3.AddMinutes(1);

        var result = SparqlEngine.Query(store,
            Xsd + "SELECT ?o WHERE { <urn:s> <urn:p> ?o MEMORIZED SINCE NOW() - \"PT1H\"^^xsd:dayTimeDuration }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("\"b\"", Assert.Single(result.Rows!)["o"]);
    }

    [Fact]
    public void ADateBound_IsTheFirstInstantOfTheDay_InUtc_UnlessItCarriesAnOffset()
    {
        var clock = new SuppliedClock { Reading = Hour0.AddMinutes(-30) };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:s>", "<urn:p>", "\"late on the 31st\"");
        clock.Reading = Hour0;
        store.AddCurrent("<urn:s>", "<urn:p>", "\"on the 1st\"");

        var utc = SparqlEngine.Query(store,
            Xsd + "SELECT ?o WHERE { <urn:s> <urn:p> ?o MEMORIZED SINCE \"2031-01-01\"^^xsd:date }",
            TestContext.Current.CancellationToken);
        var offset = SparqlEngine.Query(store,
            Xsd + "SELECT ?o WHERE { <urn:s> <urn:p> ?o MEMORIZED SINCE \"2031-01-01+01:00\"^^xsd:date } ORDER BY ?o",
            TestContext.Current.CancellationToken);

        Assert.True(utc.Success, utc.ErrorMessage);
        Assert.Equal("\"on the 1st\"", Assert.Single(utc.Rows!)["o"]);
        Assert.True(offset.Success, offset.ErrorMessage);
        Assert.Equal(new[] { "\"late on the 31st\"", "\"on the 1st\"" }, offset.Rows!.Select(row => row["o"]));
    }

    [Fact]
    public void InTheAbbreviatedForms_EachObjectCarriesItsOwnWords()
    {
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");
        store.AddCurrent("<urn:s>", "<urn:link>", "<urn:n>");
        clock.Reading = Hour1;
        store.AddCurrent("<urn:s>", "<urn:p>", "\"b\"");
        store.AddCurrent("<urn:s>", "<urn:q>", "\"c\"");
        store.AddCurrent("<urn:n>", "<urn:label>", "\"L\"");

        var lists = SparqlEngine.Query(store,
            Xsd + "SELECT ?x ?y ?z WHERE { <urn:s> <urn:p> ?x MEMORIZED BEFORE " + H1 + ", ?y MEMORIZED SINCE " + H1
                + " ; <urn:q> ?z MEMORIZED SINCE " + H1 + " }",
            TestContext.Current.CancellationToken);
        var nested = SparqlEngine.Query(store,
            "SELECT ?inner ?outer WHERE { <urn:s> <urn:link> [ <urn:label> ?l MEMORIZED ?inner ] MEMORIZED ?outer }",
            TestContext.Current.CancellationToken);

        Assert.True(lists.Success, lists.ErrorMessage);
        var row = Assert.Single(lists.Rows!);
        Assert.Equal("\"a\" \"b\" \"c\"", row["x"] + " " + row["y"] + " " + row["z"]);
        Assert.True(nested.Success, nested.ErrorMessage);
        row = Assert.Single(nested.Rows!);
        Assert.Equal(Bound1 + " " + Bound0, row["inner"] + " " + row["outer"]);
    }

    [Fact]
    public void InAGraphThePatternNames_TheWordsSeeThatGraphOnly()
    {
        var clock = new SuppliedClock { Reading = Hour1 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:s1>", "<urn:p>", "\"one\"", "<urn:g1>");
        store.AddCurrent("<urn:s2>", "<urn:p>", "\"two\"", "<urn:g2>");

        var result = SparqlEngine.Query(store,
            Xsd + "SELECT ?s WHERE { GRAPH <urn:g1> { ?s <urn:p> ?o MEMORIZED SINCE " + H0 + " BEFORE " + H2 + " } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<urn:s1>", Assert.Single(result.Rows!)["s"]);
    }

    [Fact]
    public void SelectStar_ProjectsTheTimeVariables()
    {
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");

        var result = SparqlEngine.Query(store, "SELECT * WHERE { <urn:s> <urn:p> ?o MEMORIZED ?t }", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(Bound0, Assert.Single(result.Rows!)["t"]);
    }

    [Fact]
    public void TheWords_WorkInAnUpdatesWhere()
    {
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");
        clock.Reading = Hour1;
        store.AddCurrent("<urn:s>", "<urn:p>", "\"b\"");

        // Forget what was memorized before hour 1; then memorize again what was forgotten since hour 2
        clock.Reading = Hour2;
        var forget = SparqlEngine.Update(store,
            Xsd + "DELETE { <urn:s> <urn:p> ?o } WHERE { <urn:s> <urn:p> ?o MEMORIZED BEFORE " + H1 + " }",
            TestContext.Current.CancellationToken);
        var afterForget = SparqlEngine.Query(store, "SELECT ?o WHERE { <urn:s> <urn:p> ?o }", TestContext.Current.CancellationToken);
        clock.Reading = Hour3;
        var memorize = SparqlEngine.Update(store,
            Xsd + "INSERT { <urn:s> <urn:p> ?o } WHERE { <urn:s> <urn:p> ?o FORGOTTEN SINCE " + H2 + " }",
            TestContext.Current.CancellationToken);
        var afterMemorize = SparqlEngine.Query(store, "SELECT ?o ?t WHERE { <urn:s> <urn:p> ?o MEMORIZED ?t } ORDER BY ?o",
            TestContext.Current.CancellationToken);

        Assert.True(forget.Success, forget.ErrorMessage);
        Assert.Equal(1, forget.AffectedCount);
        Assert.Equal("\"b\"", Assert.Single(afterForget.Rows!)["o"]);
        Assert.True(memorize.Success, memorize.ErrorMessage);
        Assert.Equal(1, memorize.AffectedCount);
        Assert.Equal(new[] { "\"a\" " + Bound3, "\"b\" " + Bound1 }, afterMemorize.Rows!.Select(row => row["o"] + " " + row["t"]));
    }

    [Fact]
    public void Describe_IncludingForgotten_ReadsTheForgottenTriplesToo()
    {
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<urn:a>", "<urn:worksFor>", "<urn:Acme>");
        clock.Reading = Hour1;
        store.DeleteCurrent("<urn:a>", "<urn:worksFor>", "<urn:Acme>");
        store.AddCurrent("<urn:a>", "<urn:worksFor>", "<urn:Other>");

        var remembered = SparqlEngine.Query(store, "DESCRIBE <urn:a>", TestContext.Current.CancellationToken);
        var including = SparqlEngine.Query(store, "DESCRIBE <urn:a> INCLUDING FORGOTTEN", TestContext.Current.CancellationToken);

        Assert.True(remembered.Success, remembered.ErrorMessage);
        Assert.Equal(("<urn:a>", "<urn:worksFor>", "<urn:Other>"), Assert.Single(remembered.Triples!));
        Assert.True(including.Success, including.ErrorMessage);
        Assert.Equal(new[] { "<urn:Acme>", "<urn:Other>" }, including.Triples!.Select(triple => triple.Object).OrderBy(o => o, StringComparer.Ordinal));
    }
}
