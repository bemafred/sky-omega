using SkyOmega.Mercury.Pruning;
using SkyOmega.Mercury.Pruning.Filters;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Tests for pruning workflow via QuadStorePool Switch pattern.
/// </summary>
public class QuadStorePoolPruneTests : IDisposable
{
    private QuadStorePool? _pool;

    public void Dispose()
    {
        _pool?.Dispose();
    }

    [Fact]
    public void Prune_BasicWorkflow_TransfersAndSwitches()
    {
        _pool = QuadStorePool.CreateTemp("prune-test", QuadStorePoolOptions.ForTesting);

        // Add data to primary
        var primary = _pool["primary"];
        primary.AddCurrent("<http://ex/s1>", "<http://ex/p>", "\"one\"");
        primary.AddCurrent("<http://ex/s2>", "<http://ex/p>", "\"two\"");

        // Execute pruning workflow
        _pool.Clear("secondary");
        var transfer = new PruningTransfer(_pool["primary"], _pool["secondary"]);
        var result = transfer.Execute();

        Assert.True(result.Success);
        Assert.Equal(2, result.TotalWritten);

        // Switch
        _pool.Switch("primary", "secondary");
        _pool.Clear("secondary");

        // Verify new primary has the data
        var (count, _, _) = _pool.Active.GetStatistics();
        Assert.Equal(2, count);
    }

    [Fact]
    public void Prune_WithGraphFilter_ExcludesFilteredGraphs()
    {
        _pool = QuadStorePool.CreateTemp("prune-test", QuadStorePoolOptions.ForTesting);

        var primary = _pool["primary"];
        primary.AddCurrent("<http://ex/s1>", "<http://ex/p>", "\"keep\"");
        primary.AddCurrent("<http://ex/s2>", "<http://ex/p>", "\"exclude\"", "<http://temp>");

        var options = new TransferOptions
        {
            Filter = GraphFilter.Exclude("<http://temp>")
        };

        _pool.Clear("secondary");
        var transfer = new PruningTransfer(_pool["primary"], _pool["secondary"], options);
        var result = transfer.Execute();

        Assert.True(result.Success);
        Assert.Equal(1, result.TotalWritten);

        _pool.Switch("primary", "secondary");
        _pool.Clear("secondary");

        var (count, _, _) = _pool.Active.GetStatistics();
        Assert.Equal(1, count);
    }

    [Fact]
    public void Prune_DryRun_DoesNotModifyStore()
    {
        _pool = QuadStorePool.CreateTemp("prune-test", QuadStorePoolOptions.ForTesting);

        var primary = _pool["primary"];
        primary.AddCurrent("<http://ex/s1>", "<http://ex/p>", "\"data\"");

        var options = new TransferOptions { DryRun = true };

        _pool.Clear("secondary");
        var transfer = new PruningTransfer(_pool["primary"], _pool["secondary"], options);
        var result = transfer.Execute();

        Assert.True(result.Success);
        Assert.Equal(1, result.TotalScanned);
        Assert.Equal(1, result.TotalWritten); // Would have written

        // Secondary should still be empty (dry run)
        var (secCount, _, _) = _pool["secondary"].GetStatistics();
        Assert.Equal(0, secCount);

        // Primary unchanged
        var (priCount, _, _) = _pool["primary"].GetStatistics();
        Assert.Equal(1, priCount);
    }

    [Fact]
    public void Prune_ActiveFollowsSwitch()
    {
        _pool = QuadStorePool.CreateTemp("prune-test", QuadStorePoolOptions.ForTesting);

        var primary = _pool["primary"];
        primary.AddCurrent("<http://ex/s1>", "<http://ex/p>", "\"original\"");

        // Verify Active points to primary initially
        var activeBefore = _pool.Active;
        Assert.Same(primary, activeBefore);

        // Prune + switch
        _pool.Clear("secondary");
        var transfer = new PruningTransfer(_pool["primary"], _pool["secondary"]);
        transfer.Execute();
        _pool.Switch("primary", "secondary");

        // Active should now return the new primary (was secondary)
        var activeAfter = _pool.Active;
        Assert.NotSame(activeBefore, activeAfter);

        // Data should still be accessible
        var (count, _, _) = _pool.Active.GetStatistics();
        Assert.Equal(1, count);
    }

    [Fact]
    public void Prune_WithPredicateFilter_ExcludesFilteredPredicates()
    {
        _pool = QuadStorePool.CreateTemp("prune-test", QuadStorePoolOptions.ForTesting);

        var primary = _pool["primary"];
        primary.AddCurrent("<http://ex/s>", "<http://ex/name>", "\"keep\"");
        primary.AddCurrent("<http://ex/s>", "<http://ex/debug>", "\"exclude\"");

        var options = new TransferOptions
        {
            Filter = PredicateFilter.Exclude("<http://ex/debug>")
        };

        _pool.Clear("secondary");
        var transfer = new PruningTransfer(_pool["primary"], _pool["secondary"], options);
        var result = transfer.Execute();

        Assert.True(result.Success);
        Assert.Equal(1, result.TotalWritten);

        _pool.Switch("primary", "secondary");
        _pool.Clear("secondary");

        var (count, _, _) = _pool.Active.GetStatistics();
        Assert.Equal(1, count);
    }

    [Fact]
    public void Prune_WithCompositeFilter_CombinesFilters()
    {
        _pool = QuadStorePool.CreateTemp("prune-test", QuadStorePoolOptions.ForTesting);

        var primary = _pool["primary"];
        primary.AddCurrent("<http://ex/s>", "<http://ex/name>", "\"keep\"");
        primary.AddCurrent("<http://ex/s>", "<http://ex/debug>", "\"exclude-pred\"");
        primary.AddCurrent("<http://ex/s>", "<http://ex/name>", "\"exclude-graph\"", "<http://temp>");

        var options = new TransferOptions
        {
            Filter = CompositeFilter.All(
                GraphFilter.Exclude("<http://temp>"),
                PredicateFilter.Exclude("<http://ex/debug>"))
        };

        _pool.Clear("secondary");
        var transfer = new PruningTransfer(_pool["primary"], _pool["secondary"], options);
        var result = transfer.Execute();

        Assert.True(result.Success);
        Assert.Equal(1, result.TotalWritten);
    }
}
