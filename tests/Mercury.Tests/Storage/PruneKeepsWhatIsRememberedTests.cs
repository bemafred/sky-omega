using System;
using System.IO;
using System.Linq;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Pruning;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-061 D12 and I14: a prune transfers what is remembered now, each quad with the memorized time of its present
/// period, and nothing that is forgotten. The pruned store answers the default view and every <c>MEMORIZED</c>
/// question about what is remembered as its source did; it answers a <c>FORGOTTEN</c> question with nothing; a source
/// in which everything is forgotten gives an empty store. Each test supplies both stores' clocks: the source is
/// written at four readings, and the prune runs at a fifth, on the target's clock.
/// </summary>
public class PruneKeepsWhatIsRememberedTests : IDisposable
{
    private static readonly DateTimeOffset Hour0 = new(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Hour1 = Hour0.AddHours(1);
    private static readonly DateTimeOffset Hour2 = Hour0.AddHours(2);
    private static readonly DateTimeOffset Hour5 = Hour0.AddHours(5);

    private readonly string _testDir;

    public PruneKeepsWhatIsRememberedTests()
    {
        var tempPath = TempPath.Test("adr061-prune-keeps-what-is-remembered");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    [Theory]
    [InlineData("SELECT ?s ?o WHERE { ?s <urn:p> ?o } ORDER BY ?s ?o")]
    [InlineData("SELECT ?g ?s ?o WHERE { GRAPH ?g { ?s <urn:p> ?o } } ORDER BY ?g ?s ?o")]
    [InlineData("SELECT ?s ?o ?m WHERE { ?s <urn:p> ?o MEMORIZED ?m } ORDER BY ?s ?o")]
    [InlineData("SELECT ?g ?s ?o ?m WHERE { GRAPH ?g { ?s <urn:p> ?o MEMORIZED ?m } } ORDER BY ?g ?s ?o")]
    [InlineData("PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT ?s ?o WHERE { ?s <urn:p> ?o MEMORIZED SINCE \"2031-01-01T01:00:00Z\"^^xsd:dateTime } ORDER BY ?s ?o")]
    [InlineData("PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT ?s ?o ?m WHERE { GRAPH ?g { ?s <urn:p> ?o MEMORIZED ?m BEFORE \"2031-01-01T02:00:00Z\"^^xsd:dateTime } } ORDER BY ?s ?o")]
    public void APrunedStore_AnswersTheDefaultViewAndEveryMemorizedQuestionAboutWhatIsRemembered_AsItsSourceDid(string query)
    {
        var sourceClock = new SuppliedClock { Reading = Hour0 };
        var targetClock = new SuppliedClock { Reading = Hour5 };
        using var source = new QuadStore(Path.Combine(_testDir, "source"), null, null, new StorageOptions { Clock = sourceClock });
        using var target = new QuadStore(Path.Combine(_testDir, "target"), null, null, new StorageOptions { Clock = targetClock });

        // Hour 0: three facts in the default graph, one in a graph that is wholly forgotten later, and one that is
        // forgotten and memorized again
        source.AddCurrent("<urn:s1>", "<urn:p>", "\"a\"");
        source.AddCurrent("<urn:s1>", "<urn:p>", "\"b\"");
        source.AddCurrent("<urn:s2>", "<urn:p>", "\"c\"");
        source.AddCurrent("<urn:s3>", "<urn:p>", "\"f\"");
        source.AddCurrent("<urn:s4>", "<urn:p>", "\"x\"", "<urn:gone>");
        // Hour 1: two facts in a named graph; "f" and the graph <urn:gone> are forgotten
        sourceClock.Reading = Hour1;
        source.AddCurrent("<urn:s1>", "<urn:p>", "\"d\"", "<urn:g>");
        source.AddCurrent("<urn:s2>", "<urn:p>", "\"e\"", "<urn:g>");
        source.DeleteCurrent("<urn:s3>", "<urn:p>", "\"f\"");
        source.DeleteCurrent("<urn:s4>", "<urn:p>", "\"x\"", "<urn:gone>");
        // Hour 2: "b" and "e" are forgotten, "f" is memorized again
        sourceClock.Reading = Hour2;
        source.DeleteCurrent("<urn:s1>", "<urn:p>", "\"b\"");
        source.DeleteCurrent("<urn:s2>", "<urn:p>", "\"e\"", "<urn:g>");
        source.AddCurrent("<urn:s3>", "<urn:p>", "\"f\"");

        var transfer = new PruningTransfer(source, target).Execute();
        Assert.True(transfer.Success, transfer.ErrorMessage);

        var asked = SparqlEngine.Query(source, query, TestContext.Current.CancellationToken);
        var answered = SparqlEngine.Query(target, query, TestContext.Current.CancellationToken);
        Assert.True(asked.Success, asked.ErrorMessage);
        Assert.True(answered.Success, answered.ErrorMessage);
        Assert.NotEmpty(asked.Rows!);
        Assert.Equal(
            asked.Rows!.Select(row => string.Join(" ", row.OrderBy(binding => binding.Key).Select(binding => binding.Key + "=" + binding.Value))),
            answered.Rows!.Select(row => string.Join(" ", row.OrderBy(binding => binding.Key).Select(binding => binding.Key + "=" + binding.Value))));
    }

    [Fact]
    public void AQuadRememberedAgain_KeepsOnlyItsPresentPeriod_WithTheTimeItWasMemorizedAgain()
    {
        var sourceClock = new SuppliedClock { Reading = Hour0 };
        var targetClock = new SuppliedClock { Reading = Hour5 };
        using var source = new QuadStore(Path.Combine(_testDir, "source"), null, null, new StorageOptions { Clock = sourceClock });
        using var target = new QuadStore(Path.Combine(_testDir, "target"), null, null, new StorageOptions { Clock = targetClock });
        source.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");
        sourceClock.Reading = Hour1;
        source.DeleteCurrent("<urn:s>", "<urn:p>", "\"a\"");
        sourceClock.Reading = Hour2;
        source.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");

        var transfer = new PruningTransfer(source, target).Execute();
        Assert.True(transfer.Success, transfer.ErrorMessage);

        // INCLUDING FORGOTTEN asks for every period the target holds: the one that holds now, and no other
        var result = SparqlEngine.Query(target,
            "SELECT ?m ?f WHERE { <urn:s> <urn:p> \"a\" MEMORIZED ?m FORGOTTEN ?f } INCLUDING FORGOTTEN",
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.ErrorMessage);
        var period = Assert.Single(result.Rows!);
        Assert.Equal("\"2031-01-01T02:00:00.000Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>", period["m"]);
        Assert.False(period.ContainsKey("f"), "The period that holds has no forgotten time");
    }

    [Fact]
    public void AForgottenQuestion_OnAPrunedStore_FindsNothing_AndIncludingForgottenAddsNothing()
    {
        var sourceClock = new SuppliedClock { Reading = Hour0 };
        var targetClock = new SuppliedClock { Reading = Hour5 };
        using var source = new QuadStore(Path.Combine(_testDir, "source"), null, null, new StorageOptions { Clock = sourceClock });
        using var target = new QuadStore(Path.Combine(_testDir, "target"), null, null, new StorageOptions { Clock = targetClock });
        source.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");
        source.AddCurrent("<urn:s>", "<urn:p>", "\"b\"");
        source.AddCurrent("<urn:s>", "<urn:p>", "\"c\"", "<urn:g>");
        sourceClock.Reading = Hour1;
        source.DeleteCurrent("<urn:s>", "<urn:p>", "\"b\"");
        source.DeleteCurrent("<urn:s>", "<urn:p>", "\"c\"", "<urn:g>");

        var transfer = new PruningTransfer(source, target).Execute();
        Assert.True(transfer.Success, transfer.ErrorMessage);

        // On the source the question finds "b" and "c"; on the pruned store, nothing
        const string forgotten = "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT ?o WHERE { { <urn:s> <urn:p> ?o FORGOTTEN SINCE \"2031-01-01T00:00:00Z\"^^xsd:dateTime } UNION { GRAPH ?g { <urn:s> <urn:p> ?o FORGOTTEN SINCE \"2031-01-01T00:00:00Z\"^^xsd:dateTime } } }";
        var onTheSource = SparqlEngine.Query(source, forgotten, TestContext.Current.CancellationToken);
        Assert.True(onTheSource.Success, onTheSource.ErrorMessage);
        Assert.Equal(new[] { "\"b\"", "\"c\"" }, onTheSource.Rows!.Select(row => row["o"]).OrderBy(o => o));
        var onThePrunedStore = SparqlEngine.Query(target, forgotten, TestContext.Current.CancellationToken);
        Assert.True(onThePrunedStore.Success, onThePrunedStore.ErrorMessage);
        Assert.Empty(onThePrunedStore.Rows!);

        const string everything = "SELECT ?o WHERE { { <urn:s> <urn:p> ?o } UNION { GRAPH ?g { <urn:s> <urn:p> ?o } } }";
        var without = SparqlEngine.Query(target, everything, TestContext.Current.CancellationToken);
        var including = SparqlEngine.Query(target, everything + " INCLUDING FORGOTTEN", TestContext.Current.CancellationToken);
        Assert.True(including.Success, including.ErrorMessage);
        Assert.Equal(new[] { "\"a\"" }, without.Rows!.Select(row => row["o"]));
        Assert.Equal(new[] { "\"a\"" }, including.Rows!.Select(row => row["o"]));
    }

    [Fact]
    public void ASourceInWhichEverythingIsForgotten_GivesAnEmptyStore_WhateverTimeIsAskedAbout()
    {
        var sourceClock = new SuppliedClock { Reading = Hour0 };
        var targetClock = new SuppliedClock { Reading = Hour5 };
        using var source = new QuadStore(Path.Combine(_testDir, "source"), null, null, new StorageOptions { Clock = sourceClock });
        using var target = new QuadStore(Path.Combine(_testDir, "target"), null, null, new StorageOptions { Clock = targetClock });
        source.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");
        source.AddCurrent("<urn:s>", "<urn:p>", "\"b\"", "<urn:g>");
        sourceClock.Reading = Hour1;
        source.DeleteCurrent("<urn:s>", "<urn:p>", "\"a\"");
        source.DeleteCurrent("<urn:s>", "<urn:p>", "\"b\"", "<urn:g>");
        sourceClock.Reading = Hour2;
        source.AddCurrent("<urn:s>", "<urn:p>", "\"a\"");
        source.DeleteCurrent("<urn:s>", "<urn:p>", "\"a\"");

        var transfer = new PruningTransfer(source, target).Execute();
        Assert.True(transfer.Success, transfer.ErrorMessage);

        var (quads, _, _) = target.GetStatistics();
        Assert.Equal(0, quads);
        // Q10's M(t) at a time when both facts were remembered, and every period the store holds
        var atHalfPastZero = SparqlEngine.Query(target,
            "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT ?o WHERE { " +
            "{ ?s ?p ?o MEMORIZED BEFORE \"2031-01-01T00:30:00Z\"^^xsd:dateTime } UNION " +
            "{ ?s ?p ?o MEMORIZED BEFORE \"2031-01-01T00:30:00Z\"^^xsd:dateTime FORGOTTEN SINCE \"2031-01-01T00:30:00Z\"^^xsd:dateTime } UNION " +
            "{ GRAPH ?g { ?s ?p ?o MEMORIZED BEFORE \"2031-01-01T00:30:00Z\"^^xsd:dateTime FORGOTTEN SINCE \"2031-01-01T00:30:00Z\"^^xsd:dateTime } } }",
            TestContext.Current.CancellationToken);
        Assert.True(atHalfPastZero.Success, atHalfPastZero.ErrorMessage);
        Assert.Empty(atHalfPastZero.Rows!);
        var every = SparqlEngine.Query(target,
            "SELECT * WHERE { { ?s ?p ?o } UNION { GRAPH ?g { ?s ?p ?o } } } INCLUDING FORGOTTEN",
            TestContext.Current.CancellationToken);
        Assert.True(every.Success, every.ErrorMessage);
        Assert.Empty(every.Rows!);
    }
}
