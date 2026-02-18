using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Tests for PruneEngine facade.
/// </summary>
public class PruneEngineTests : IDisposable
{
    private QuadStorePool? _pool;

    public void Dispose()
    {
        _pool?.Dispose();
    }

    private QuadStorePool CreatePool()
    {
        _pool = QuadStorePool.CreateTemp("prune-engine-test", QuadStorePoolOptions.ForTesting);
        return _pool;
    }

    [Fact]
    public void Execute_BasicPrune_TransfersAllQuads()
    {
        var pool = CreatePool();
        var primary = pool["primary"];
        primary.AddCurrent("<http://ex/s1>", "<http://ex/p>", "\"v1\"");
        primary.AddCurrent("<http://ex/s2>", "<http://ex/p>", "\"v2\"");
        primary.AddCurrent("<http://ex/s3>", "<http://ex/p>", "\"v3\"");

        var result = PruneEngine.Execute(pool);

        Assert.True(result.Success);
        Assert.Equal(3, result.QuadsScanned);
        Assert.Equal(3, result.QuadsWritten);
        Assert.False(result.DryRun);

        // Verify the active store has the quads
        var stats = SparqlEngine.GetStatistics(pool.Active);
        Assert.Equal(3, stats.QuadCount);
    }

    [Fact]
    public void Execute_ExcludeGraphs_FiltersQuads()
    {
        var pool = CreatePool();
        var primary = pool["primary"];
        primary.AddCurrent("<http://ex/s1>", "<http://ex/p>", "\"v1\"");
        primary.AddCurrent("<http://ex/s2>", "<http://ex/p>", "\"v2\"", "<http://ex/temp>");
        primary.AddCurrent("<http://ex/s3>", "<http://ex/p>", "\"v3\"", "<http://ex/temp>");

        var options = new PruneOptions
        {
            ExcludeGraphs = ["<http://ex/temp>"]
        };

        var result = PruneEngine.Execute(pool, options);

        Assert.True(result.Success);
        Assert.Equal(3, result.QuadsScanned);
        Assert.Equal(1, result.QuadsWritten); // Only default graph quad
    }

    [Fact]
    public void Execute_ExcludePredicates_FiltersQuads()
    {
        var pool = CreatePool();
        var primary = pool["primary"];
        primary.AddCurrent("<http://ex/s1>", "<http://ex/keep>", "\"v1\"");
        primary.AddCurrent("<http://ex/s2>", "<http://ex/remove>", "\"v2\"");
        primary.AddCurrent("<http://ex/s3>", "<http://ex/keep>", "\"v3\"");

        var options = new PruneOptions
        {
            ExcludePredicates = ["<http://ex/remove>"]
        };

        var result = PruneEngine.Execute(pool, options);

        Assert.True(result.Success);
        Assert.Equal(3, result.QuadsScanned);
        Assert.Equal(2, result.QuadsWritten);
    }

    [Fact]
    public void Execute_DryRun_DoesNotSwitch()
    {
        var pool = CreatePool();
        var primary = pool["primary"];
        primary.AddCurrent("<http://ex/s1>", "<http://ex/p>", "\"v1\"");
        primary.AddCurrent("<http://ex/s2>", "<http://ex/p>", "\"v2\"");

        var options = new PruneOptions { DryRun = true };

        var result = PruneEngine.Execute(pool, options);

        Assert.True(result.Success);
        Assert.True(result.DryRun);
        Assert.Equal(2, result.QuadsScanned);
        Assert.Equal(2, result.QuadsWritten);

        // Primary should still have the original data (no switch happened)
        var (quadCount, _, _) = pool["primary"].GetStatistics();
        Assert.Equal(2, quadCount);
    }

    [Fact]
    public void Execute_FlattenToCurrent_IsDefault()
    {
        var pool = CreatePool();
        var primary = pool["primary"];
        primary.AddCurrent("<http://ex/s>", "<http://ex/p>", "\"v1\"");

        var result = PruneEngine.Execute(pool);

        Assert.True(result.Success);
        Assert.Equal(1, result.QuadsWritten);
    }

    [Fact]
    public void Execute_PreserveVersions_TransfersHistory()
    {
        var pool = CreatePool();
        var primary = pool["primary"];
        primary.AddCurrent("<http://ex/s>", "<http://ex/p>", "\"v1\"");

        var options = new PruneOptions
        {
            HistoryMode = HistoryMode.PreserveVersions
        };

        var result = PruneEngine.Execute(pool, options);

        Assert.True(result.Success);
        Assert.Equal(1, result.QuadsWritten);
    }

    [Fact]
    public void Execute_HasTimingInfo()
    {
        var pool = CreatePool();
        pool["primary"].AddCurrent("<http://ex/s>", "<http://ex/p>", "\"v\"");

        var result = PruneEngine.Execute(pool);

        Assert.True(result.Duration > TimeSpan.Zero);
    }
}
