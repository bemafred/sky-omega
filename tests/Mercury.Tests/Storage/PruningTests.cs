using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Pruning;
using SkyOmega.Mercury.Pruning.Filters;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

public class PruningTests : IDisposable
{
    private readonly string _sourceDir;
    private readonly string _targetDir;
    private QuadStore? _source;
    private QuadStore? _target;

    public PruningTests()
    {
        var sourcePath = TempPath.Test("prune_src");
        sourcePath.MarkOwnership();
        _sourceDir = sourcePath;

        var targetPath = TempPath.Test("prune_tgt");
        targetPath.MarkOwnership();
        _targetDir = targetPath;
    }

    public void Dispose()
    {
        _source?.Dispose();
        _target?.Dispose();

        TempPath.SafeCleanup(_sourceDir);
        TempPath.SafeCleanup(_targetDir);
    }

    private void CreateStores()
    {
        _source = new QuadStore(_sourceDir);
        _target = new QuadStore(_targetDir);
    }

    [Fact]
    public void BasicTransfer_CopiesAllQuads()
    {
        CreateStores();

        // Add some quads to source
        _source!.AddCurrent("<http://example.org/s1>", "<http://example.org/p>", "<http://example.org/o1>");
        _source.AddCurrent("<http://example.org/s2>", "<http://example.org/p>", "<http://example.org/o2>");
        _source.AddCurrent("<http://example.org/s3>", "<http://example.org/p>", "<http://example.org/o3>");

        // Transfer
        var transfer = new PruningTransfer(_source, _target!);
        var result = transfer.Execute();

        // Verify
        Assert.True(result.Success);
        Assert.Equal(3, result.TotalScanned);
        Assert.Equal(3, result.TotalMatched);
        Assert.Equal(3, result.TotalWritten);

        // Verify target has the quads
        var (targetCount, _, _) = _target!.GetStatistics();
        Assert.Equal(3, targetCount);
    }

    [Fact]
    public void GraphFilter_IncludeMode_OnlyIncludesSpecifiedGraphs()
    {
        CreateStores();

        // Add quads to different graphs
        _source!.AddCurrent("<http://example.org/s1>", "<http://example.org/p>", "<http://example.org/o1>",
            "<http://example.org/graph1>");
        _source.AddCurrent("<http://example.org/s2>", "<http://example.org/p>", "<http://example.org/o2>",
            "<http://example.org/graph2>");
        _source.AddCurrent("<http://example.org/s3>", "<http://example.org/p>", "<http://example.org/o3>",
            "<http://example.org/graph1>");

        // Transfer only graph1
        var options = new TransferOptions
        {
            Filter = GraphFilter.Include("<http://example.org/graph1>")
        };
        var transfer = new PruningTransfer(_source, _target!, options);
        var result = transfer.Execute();

        // Verify
        Assert.True(result.Success);
        Assert.Equal(3, result.TotalScanned);
        Assert.Equal(2, result.TotalMatched); // Only 2 quads from graph1
        Assert.Equal(2, result.TotalWritten);
    }

    [Fact]
    public void GraphFilter_ExcludeMode_ExcludesSpecifiedGraphs()
    {
        CreateStores();

        // Add quads to different graphs
        _source!.AddCurrent("<http://example.org/s1>", "<http://example.org/p>", "<http://example.org/o1>",
            "<http://example.org/graph1>");
        _source.AddCurrent("<http://example.org/s2>", "<http://example.org/p>", "<http://example.org/o2>",
            "<http://example.org/graph2>");
        _source.AddCurrent("<http://example.org/s3>", "<http://example.org/p>", "<http://example.org/o3>");  // default graph

        // Exclude graph1
        var options = new TransferOptions
        {
            Filter = GraphFilter.Exclude("<http://example.org/graph1>")
        };
        var transfer = new PruningTransfer(_source, _target!, options);
        var result = transfer.Execute();

        // Verify
        Assert.True(result.Success);
        Assert.Equal(3, result.TotalScanned);
        Assert.Equal(2, result.TotalMatched); // graph2 and default graph
        Assert.Equal(2, result.TotalWritten);
    }

    [Fact]
    public void PredicateFilter_ExcludesSystemPredicates()
    {
        CreateStores();

        // Add quads with different predicates
        _source!.AddCurrent("<http://example.org/s>", "<http://example.org/name>", "\"John\"");
        _source.AddCurrent("<http://example.org/s>", "<http://internal/createdAt>", "\"2024-01-01\"");
        _source.AddCurrent("<http://example.org/s>", "<http://example.org/age>", "\"30\"");

        // Exclude internal predicates
        var options = new TransferOptions
        {
            Filter = PredicateFilter.Exclude("<http://internal/createdAt>")
        };
        var transfer = new PruningTransfer(_source, _target!, options);
        var result = transfer.Execute();

        // Verify
        Assert.True(result.Success);
        Assert.Equal(3, result.TotalScanned);
        Assert.Equal(2, result.TotalMatched);
        Assert.Equal(2, result.TotalWritten);
    }

    [Fact]
    public void CompositeFilter_AndLogic_RequiresAllConditions()
    {
        CreateStores();

        // Add quads
        _source!.AddCurrent("<http://example.org/s1>", "<http://example.org/keep>", "<http://example.org/o1>",
            "<http://example.org/goodGraph>");
        _source.AddCurrent("<http://example.org/s2>", "<http://example.org/skip>", "<http://example.org/o2>",
            "<http://example.org/goodGraph>");
        _source.AddCurrent("<http://example.org/s3>", "<http://example.org/keep>", "<http://example.org/o3>",
            "<http://example.org/badGraph>");

        // Composite filter: graph = goodGraph AND predicate = keep
        var options = new TransferOptions
        {
            Filter = CompositeFilter.All(
                GraphFilter.Include("<http://example.org/goodGraph>"),
                PredicateFilter.Include("<http://example.org/keep>"))
        };
        var transfer = new PruningTransfer(_source, _target!, options);
        var result = transfer.Execute();

        // Verify - only first quad matches both conditions
        Assert.True(result.Success);
        Assert.Equal(3, result.TotalScanned);
        Assert.Equal(1, result.TotalMatched);
        Assert.Equal(1, result.TotalWritten);
    }

    [Fact]
    public void SoftDeletedQuads_NotCopied_InDefaultMode()
    {
        CreateStores();

        // Add and then delete a quad
        _source!.AddCurrent("<http://example.org/s1>", "<http://example.org/p>", "<http://example.org/o1>");
        _source.AddCurrent("<http://example.org/s2>", "<http://example.org/p>", "<http://example.org/o2>");
        _source.DeleteCurrent("<http://example.org/s1>", "<http://example.org/p>", "<http://example.org/o1>");

        // Transfer with default options (FlattenToCurrent)
        var transfer = new PruningTransfer(_source, _target!);
        var result = transfer.Execute();

        // Verify - only non-deleted quad transferred
        Assert.True(result.Success);
        Assert.Equal(1, result.TotalScanned); // Only current quads scanned
        Assert.Equal(1, result.TotalMatched);
        Assert.Equal(1, result.TotalWritten);
    }

    [Fact]
    public void HistoryMode_PreserveVersions_TransfersAllVersions()
    {
        CreateStores();

        // Add a quad with history (simulate by adding multiple versions)
        _source!.Add("<http://example.org/s>", "<http://example.org/p>", "<http://example.org/v1>",
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
        _source.Add("<http://example.org/s>", "<http://example.org/p>", "<http://example.org/v2>",
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.MaxValue);

        // Transfer with history preservation
        var options = new TransferOptions
        {
            HistoryMode = HistoryMode.PreserveVersions
        };
        var transfer = new PruningTransfer(_source, _target!, options);
        var result = transfer.Execute();

        // Verify - both versions transferred
        Assert.True(result.Success);
        Assert.Equal(2, result.TotalScanned);
        Assert.Equal(2, result.TotalMatched);
        Assert.Equal(2, result.TotalWritten);
    }

    [Fact]
    public void DryRun_NoWrites()
    {
        CreateStores();

        // Add quads to source
        _source!.AddCurrent("<http://example.org/s1>", "<http://example.org/p>", "<http://example.org/o1>");
        _source.AddCurrent("<http://example.org/s2>", "<http://example.org/p>", "<http://example.org/o2>");

        // Dry run
        var options = new TransferOptions { DryRun = true };
        var transfer = new PruningTransfer(_source, _target!, options);
        var result = transfer.Execute();

        // Verify - counts are populated but target is empty
        Assert.True(result.Success);
        Assert.Equal(2, result.TotalScanned);
        Assert.Equal(2, result.TotalMatched);
        Assert.Equal(2, result.TotalWritten); // "Would write" count

        // Target should be empty
        var (targetCount, _, _) = _target!.GetStatistics();
        Assert.Equal(0, targetCount);
    }

    [Fact]
    public void Verification_PassesWhenCountsMatch()
    {
        CreateStores();

        // Add quads
        _source!.AddCurrent("<http://example.org/s1>", "<http://example.org/p>", "<http://example.org/o1>");
        _source.AddCurrent("<http://example.org/s2>", "<http://example.org/p>", "<http://example.org/o2>");

        // Transfer with verification
        var options = new TransferOptions { VerifyAfterTransfer = true };
        var transfer = new PruningTransfer(_source, _target!, options);
        var result = transfer.Execute();

        // Verify
        Assert.True(result.Success);
        Assert.NotNull(result.Verification);
        Assert.True(result.Verification!.Value.Passed);
        Assert.Equal(2, result.Verification.Value.SourceCount);
        Assert.Equal(2, result.Verification.Value.TargetCount);
        Assert.Equal(0, result.Verification.Value.MissingCount);
    }

    [Fact]
    public void Checksum_ComputedWhenEnabled()
    {
        CreateStores();

        // Add quads
        _source!.AddCurrent("<http://example.org/s1>", "<http://example.org/p>", "<http://example.org/o1>");
        _source.AddCurrent("<http://example.org/s2>", "<http://example.org/p>", "<http://example.org/o2>");

        // Transfer with checksum
        var options = new TransferOptions
        {
            ComputeChecksum = true,
            VerifyAfterTransfer = true
        };
        var transfer = new PruningTransfer(_source, _target!, options);
        var result = transfer.Execute();

        // Verify checksum is computed
        Assert.True(result.Success);
        Assert.NotNull(result.ContentChecksum);
        Assert.NotNull(result.Verification);
        Assert.True(result.Verification!.Value.Passed);
        Assert.NotNull(result.Verification.Value.SourceChecksum);
        Assert.NotNull(result.Verification.Value.TargetChecksum);
        Assert.Equal(result.Verification.Value.SourceChecksum, result.Verification.Value.TargetChecksum);
    }

    [Fact]
    public void Progress_ReportsAtIntervals()
    {
        CreateStores();

        // Add enough quads to trigger progress
        for (int i = 0; i < 150; i++)
        {
            _source!.AddCurrent($"<http://example.org/s{i}>", "<http://example.org/p>", $"<http://example.org/o{i}>");
        }

        // Transfer with progress
        var progressReports = new List<TransferProgress>();
        var options = new TransferOptions { ProgressInterval = 50 };
        var transfer = new PruningTransfer(_source!, _target!, options);
        var result = transfer.Execute((in TransferProgress p) => progressReports.Add(p));

        // Verify progress was reported
        Assert.True(result.Success);
        Assert.True(progressReports.Count >= 2); // Should have at least 2 reports (at 50, 100)
        Assert.All(progressReports, p => Assert.True(p.QuadsScanned > 0));
    }

    [Fact]
    public void Cancellation_StopsTransfer()
    {
        CreateStores();

        // Add quads
        for (int i = 0; i < 100; i++)
        {
            _source!.AddCurrent($"<http://example.org/s{i}>", "<http://example.org/p>", $"<http://example.org/o{i}>");
        }

        // Transfer with cancellation
        var cts = new CancellationTokenSource();
        var options = new TransferOptions
        {
            CancellationToken = cts.Token,
            ProgressInterval = 10
        };
        var transfer = new PruningTransfer(_source!, _target!, options);

        // Cancel after first progress report
        var result = transfer.Execute((in TransferProgress p) =>
        {
            if (p.QuadsScanned >= 10)
                cts.Cancel();
        });

        // Verify - transfer was cancelled but didn't throw
        Assert.False(result.Success);
        Assert.Equal("Transfer was cancelled", result.ErrorMessage);
        Assert.True(result.TotalScanned > 0);
    }

    [Fact]
    public void LargeTransfer_BatchesCorrectly()
    {
        CreateStores();

        // Add many quads
        const int count = 25_000;
        _source!.BeginBatch();
        for (int i = 0; i < count; i++)
        {
            _source.AddCurrentBatched($"<http://example.org/s{i}>", "<http://example.org/p>", $"<http://example.org/o{i}>");
        }
        _source.CommitBatch();

        // Transfer with small batch size
        var options = new TransferOptions { BatchSize = 1000 };
        var transfer = new PruningTransfer(_source, _target!, options);
        var result = transfer.Execute();

        // Verify
        Assert.True(result.Success);
        Assert.Equal(count, result.TotalWritten);

        var (targetCount, _, _) = _target!.GetStatistics();
        Assert.Equal(count, targetCount);
    }

    [Fact]
    public void AllPassFilter_AcceptsEverything()
    {
        var filter = AllPassFilter.Instance;

        Assert.True(filter.ShouldInclude(
            "<http://example.org/g>",
            "<http://example.org/s>",
            "<http://example.org/p>",
            "<http://example.org/o>",
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue));

        // Also empty values
        Assert.True(filter.ShouldInclude(
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty,
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue));
    }

    [Fact]
    public void GraphFilter_DefaultGraphOnly_Works()
    {
        var filter = GraphFilter.DefaultGraphOnly();

        // Default graph (empty) should pass
        Assert.True(filter.ShouldInclude(
            ReadOnlySpan<char>.Empty,
            "<http://example.org/s>",
            "<http://example.org/p>",
            "<http://example.org/o>",
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue));

        // Named graph should fail
        Assert.False(filter.ShouldInclude(
            "<http://example.org/graph>",
            "<http://example.org/s>",
            "<http://example.org/p>",
            "<http://example.org/o>",
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue));
    }

    [Fact]
    public void CompositeFilter_OrLogic_AcceptsAnyMatch()
    {
        var filter = CompositeFilter.Any(
            GraphFilter.Include("<http://example.org/graph1>"),
            PredicateFilter.Include("<http://example.org/important>"));

        // Matches graph
        Assert.True(filter.ShouldInclude(
            "<http://example.org/graph1>",
            "<http://example.org/s>",
            "<http://example.org/other>",
            "<http://example.org/o>",
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue));

        // Matches predicate
        Assert.True(filter.ShouldInclude(
            "<http://example.org/graph2>",
            "<http://example.org/s>",
            "<http://example.org/important>",
            "<http://example.org/o>",
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue));

        // Matches neither
        Assert.False(filter.ShouldInclude(
            "<http://example.org/graph2>",
            "<http://example.org/s>",
            "<http://example.org/other>",
            "<http://example.org/o>",
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue));
    }
}
