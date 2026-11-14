using System;
using System.IO;
using System.Linq;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-061 D10, Q15: a graph variable ranges over every graph that holds a period when the query asks for forgotten
/// memories too, and over the graphs that hold something remembered in the default view. Before, the graphs were
/// listed by what is remembered now in every case, so forgetting the last fact of a graph hid the graph's whole
/// history from every query that did not name it. The live memory store held eight such graphs (ADR-061 M11).
/// Each test supplies the clock and states its sequence inline.
/// </summary>
public class ForgottenGraphTests : IDisposable
{
    private static readonly DateTimeOffset Memorized = new(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Forgotten = Memorized.AddHours(1);
    private static readonly DateTimeOffset Later = Memorized.AddHours(2);

    private readonly string _testDir;

    public ForgottenGraphTests()
    {
        var tempPath = TempPath.Test("adr061-forgotten-graph");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    [Fact]
    public void AGraphInWhichEverythingIsForgotten_IsFoundByAGraphVariable_WhenForgottenMemoriesAreAskedFor()
    {
        var clock = new SuppliedClock { Reading = Memorized };
        using var store = new QuadStore(Path.Combine(_testDir, "all-versions"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "\"gone\"", "<http://ex.org/forgotten>");
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "\"kept\"", "<http://ex.org/remembered>");
        clock.Reading = Forgotten;
        store.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "\"gone\"", "<http://ex.org/forgotten>");
        clock.Reading = Later;

        var everything = SparqlEngine.Query(store, "SELECT ?g ?o WHERE { GRAPH ?g { ?s ?p ?o } } ALL VERSIONS", TestContext.Current.CancellationToken);

        Assert.True(everything.Success, everything.ErrorMessage);
        Assert.Equal(
            new[] { "<http://ex.org/forgotten> \"gone\"", "<http://ex.org/remembered> \"kept\"" },
            everything.Rows!.Select(row => row["g"] + " " + row["o"]).OrderBy(line => line, StringComparer.Ordinal));
    }

    [Fact]
    public void AGraphInWhichEverythingIsForgotten_IsNotFound_InTheDefaultView()
    {
        var clock = new SuppliedClock { Reading = Memorized };
        using var store = new QuadStore(Path.Combine(_testDir, "default-view"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "\"gone\"", "<http://ex.org/forgotten>");
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "\"kept\"", "<http://ex.org/remembered>");
        clock.Reading = Forgotten;
        store.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "\"gone\"", "<http://ex.org/forgotten>");
        clock.Reading = Later;

        var remembered = SparqlEngine.Query(store, "SELECT DISTINCT ?g WHERE { GRAPH ?g { ?s ?p ?o } }", TestContext.Current.CancellationToken);

        Assert.True(remembered.Success, remembered.ErrorMessage);
        Assert.Equal("<http://ex.org/remembered>", remembered.Rows!.Single().Values.Single());
        Assert.Equal(new[] { "<http://ex.org/remembered>" }, SparqlEngine.GetNamedGraphs(store));
    }

    [Fact]
    public void AGraphForgottenSince_IsFoundByAGraphVariable_AsOfATimeWhenItWasRemembered()
    {
        // AS OF goes with ADR-061 Step 5. Until then it reads the past through the same listing.
        var clock = new SuppliedClock { Reading = Memorized };
        using var store = new QuadStore(Path.Combine(_testDir, "as-of"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "\"gone\"", "<http://ex.org/forgotten>");
        clock.Reading = Forgotten;
        store.DeleteCurrent("<http://ex.org/s>", "<http://ex.org/p>", "\"gone\"", "<http://ex.org/forgotten>");
        clock.Reading = Later;

        var before = SparqlEngine.Query(store, "SELECT ?g WHERE { GRAPH ?g { ?s ?p ?o } } AS OF \"2031-01-01T00:30:00Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>", TestContext.Current.CancellationToken);
        var after = SparqlEngine.Query(store, "SELECT ?g WHERE { GRAPH ?g { ?s ?p ?o } } AS OF \"2031-01-01T01:30:00Z\"^^<http://www.w3.org/2001/XMLSchema#dateTime>", TestContext.Current.CancellationToken);

        Assert.True(before.Success, before.ErrorMessage);
        Assert.Equal("<http://ex.org/forgotten>", before.Rows!.Single().Values.Single());
        Assert.True(after.Success, after.ErrorMessage);
        Assert.Empty(after.Rows!);
    }
}
