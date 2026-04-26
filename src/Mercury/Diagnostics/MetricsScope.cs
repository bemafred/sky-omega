using System;
using System.Diagnostics;
using System.Threading;
using SkyOmega.Mercury.Abstractions;

namespace SkyOmega.Mercury.Diagnostics;

/// <summary>
/// Scope correlation for nested operations. Use as <c>using var s = MetricsScope.Begin(listener, "name")</c>;
/// the scope's id is stamped on enter, the parent scope's id is captured from a thread-local stack,
/// and exit fires on Dispose with measured duration. ADR-035 Decision 3.
/// </summary>
/// <remarks>
/// <para>
/// Implemented as <c>readonly struct</c> + <see cref="IDisposable"/>. The using-pattern is a
/// pattern-based dispatch; no boxing. Static state (the scope id counter and thread-local
/// current-scope) is the only allocation.
/// </para>
/// <para>
/// Thread-local stacking means scopes must be Disposed on the thread that called Begin —
/// the standard using-block contract. Async scopes that hop threads must not use this.
/// </para>
/// </remarks>
public readonly struct MetricsScope : IDisposable
{
    private readonly IObservabilityListener? _listener;
    private readonly long _scopeId;
    private readonly long _parentScopeId;
    private readonly long _startTimestamp;

    private static long _nextScopeId;
    [ThreadStatic] private static long _currentScopeId;

    private MetricsScope(IObservabilityListener? listener, string name, long parentScopeId)
    {
        _listener = listener;
        _parentScopeId = parentScopeId;
        _scopeId = Interlocked.Increment(ref _nextScopeId);
        _startTimestamp = Stopwatch.GetTimestamp();
        _currentScopeId = _scopeId;
        listener?.OnScopeEnter(_scopeId, parentScopeId, name, DateTimeOffset.UtcNow);
    }

    /// <summary>Open a new scope. Returns the scope token; use within a <c>using</c> block.</summary>
    public static MetricsScope Begin(IObservabilityListener? listener, string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        return new MetricsScope(listener, name, _currentScopeId);
    }

    /// <summary>Current thread-local scope id (0 if no active scope).</summary>
    public static long CurrentScopeId => _currentScopeId;

    public long Id => _scopeId;
    public long ParentId => _parentScopeId;

    public void Dispose()
    {
        if (_scopeId == 0) return;

        var elapsed = Stopwatch.GetElapsedTime(_startTimestamp);
        _currentScopeId = _parentScopeId;
        _listener?.OnScopeExit(_scopeId, elapsed, DateTimeOffset.UtcNow);
    }
}
