using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime.IO;
using Xunit;

namespace SkyOmega.Mercury.Tests.Repl;

/// <summary>
/// Tests for :prune REPL command.
/// </summary>
public class ReplPruneTests : IDisposable
{
    private ReplSession? _session;

    public void Dispose()
    {
        _session?.Dispose();
    }

    private ReplSession CreateSession(Func<string, PruneResult>? executePrune = null)
    {
        return new ReplSession(
            executeQuery: _ => new QueryResult { Success = true, Kind = ExecutionResultKind.Select },
            executeUpdate: _ => new UpdateResult { Success = true },
            getStatistics: () => new StoreStatistics(),
            getNamedGraphs: () => [],
            executePrune: executePrune);
    }

    [Fact]
    public void Prune_WhenDelegateIsNull_ReturnsError()
    {
        _session = CreateSession(executePrune: null);
        var result = _session.Execute(":prune");

        Assert.Equal(ExecutionResultKind.Error, result.Kind);
        Assert.Contains("not available", result.Message);
    }

    [Fact]
    public void Prune_WhenDelegateProvided_ExecutesPrune()
    {
        string? receivedArgs = null;
        _session = CreateSession(executePrune: args =>
        {
            receivedArgs = args;
            return new PruneResult
            {
                Success = true,
                QuadsScanned = 100,
                QuadsWritten = 90,
                BytesSaved = 1024,
                Duration = TimeSpan.FromMilliseconds(50)
            };
        });

        var result = _session.Execute(":prune");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.True(result.Success);
        Assert.Contains("100", result.Message);
        Assert.Contains("90", result.Message);
        Assert.Equal("", receivedArgs);
    }

    [Fact]
    public void Prune_PassesArgsThroughToDelegate()
    {
        string? receivedArgs = null;
        _session = CreateSession(executePrune: args =>
        {
            receivedArgs = args;
            return new PruneResult { Success = true };
        });

        _session.Execute(":prune --dry-run --history preserve");

        Assert.Equal("--dry-run --history preserve", receivedArgs);
    }

    [Fact]
    public void Prune_DryRun_ShowsDryRunLabel()
    {
        _session = CreateSession(executePrune: _ => new PruneResult
        {
            Success = true,
            DryRun = true,
            QuadsScanned = 50,
            QuadsWritten = 50,
            Duration = TimeSpan.FromMilliseconds(10)
        });

        var result = _session.Execute(":prune --dry-run");

        Assert.Contains("dry-run", result.Message);
    }

    [Fact]
    public void Prune_WhenFails_ReturnsError()
    {
        _session = CreateSession(executePrune: _ => new PruneResult
        {
            Success = false,
            ErrorMessage = "Disk full"
        });

        var result = _session.Execute(":prune");

        Assert.Equal(ExecutionResultKind.Error, result.Kind);
        Assert.Contains("Disk full", result.Message);
    }

    [Fact]
    public void Prune_WhenExceptionThrown_ReturnsError()
    {
        _session = CreateSession(executePrune: _ => throw new InvalidOperationException("boom"));

        var result = _session.Execute(":prune");

        Assert.Equal(ExecutionResultKind.Error, result.Kind);
        Assert.Contains("boom", result.Message);
    }

    [Fact]
    public void Prune_AppearsInHelp()
    {
        _session = CreateSession();
        var result = _session.Execute(":help");

        Assert.Contains(":prune", result.Message);
    }
}
