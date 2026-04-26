using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Diagnostics;
using Xunit;

namespace SkyOmega.Mercury.Tests.Diagnostics;

/// <summary>
/// ADR-035 Decision 3: MetricsScope correlation behavior. Verifies enter/exit firing,
/// parent-id correlation across nested scopes, scope-id uniqueness, and graceful behavior
/// when no listener is attached.
/// </summary>
public class MetricsScopeTests
{
    private sealed class CapturingListener : IObservabilityListener
    {
        public readonly List<(long Id, long ParentId, string Name)> Enters = new();
        public readonly List<(long Id, TimeSpan Duration)> Exits = new();
        private readonly object _gate = new();

        public void OnScopeEnter(long scopeId, long parentScopeId, string name, DateTimeOffset timestamp)
        {
            lock (_gate) Enters.Add((scopeId, parentScopeId, name));
        }
        public void OnScopeExit(long scopeId, TimeSpan duration, DateTimeOffset timestamp)
        {
            lock (_gate) Exits.Add((scopeId, duration));
        }
    }

    [Fact]
    public void SingleScope_EmitsEnterAndExit()
    {
        var listener = new CapturingListener();
        long capturedId;
        using (var scope = MetricsScope.Begin(listener, "test.scope"))
        {
            capturedId = scope.Id;
            Assert.NotEqual(0, capturedId);
            Assert.Equal(0, scope.ParentId);
        }

        Assert.Single(listener.Enters);
        Assert.Single(listener.Exits);
        Assert.Equal(capturedId, listener.Enters[0].Id);
        Assert.Equal("test.scope", listener.Enters[0].Name);
        Assert.Equal(capturedId, listener.Exits[0].Id);
    }

    [Fact]
    public void NestedScopes_ParentIdsCorrelate()
    {
        var listener = new CapturingListener();
        using (var outer = MetricsScope.Begin(listener, "outer"))
        {
            Assert.Equal(outer.Id, MetricsScope.CurrentScopeId);
            using (var inner = MetricsScope.Begin(listener, "inner"))
            {
                Assert.Equal(outer.Id, inner.ParentId);
                Assert.Equal(inner.Id, MetricsScope.CurrentScopeId);
            }
            Assert.Equal(outer.Id, MetricsScope.CurrentScopeId);
        }
        Assert.Equal(0, MetricsScope.CurrentScopeId);

        Assert.Equal(2, listener.Enters.Count);
        Assert.Equal("outer", listener.Enters[0].Name);
        Assert.Equal(0, listener.Enters[0].ParentId);
        Assert.Equal("inner", listener.Enters[1].Name);
        Assert.Equal(listener.Enters[0].Id, listener.Enters[1].ParentId);
    }

    [Fact]
    public void NullListener_DoesNotEmitButPreservesCurrentScopeId()
    {
        using (var scope = MetricsScope.Begin(null, "no-listener"))
        {
            Assert.Equal(scope.Id, MetricsScope.CurrentScopeId);
        }
        Assert.Equal(0, MetricsScope.CurrentScopeId);
    }

    [Fact]
    public void ScopeIds_AreUnique()
    {
        var listener = new CapturingListener();
        var ids = new HashSet<long>();
        for (int i = 0; i < 1000; i++)
        {
            using var s = MetricsScope.Begin(listener, "scope-" + i);
            Assert.True(ids.Add(s.Id));
        }
    }

    [Fact]
    public async Task ConcurrentScopes_OnDifferentThreads_HaveIndependentParents()
    {
        var listener = new CapturingListener();
        const int threads = 4;
        var tasks = new Task[threads];
        for (int t = 0; t < threads; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                using var outer = MetricsScope.Begin(listener, "thread-outer");
                using var inner = MetricsScope.Begin(listener, "thread-inner");
                Assert.Equal(outer.Id, inner.ParentId);
            });
        }
        await Task.WhenAll(tasks);

        Assert.Equal(threads * 2, listener.Enters.Count);
        Assert.Equal(threads * 2, listener.Exits.Count);
    }

    [Fact]
    public void Dispose_OfDefaultStruct_DoesNotEmit()
    {
        var listener = new CapturingListener();
        var defaultScope = default(MetricsScope);
        defaultScope.Dispose();
        Assert.Empty(listener.Enters);
        Assert.Empty(listener.Exits);
    }
}
