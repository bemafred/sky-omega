namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// Sink for <see cref="QueryMetrics"/> records emitted by SparqlEngine on every query.
/// Listeners are called synchronously on the query thread — implementations must be
/// thread-safe and keep per-call work cheap so the no-op-default contract scales to
/// "listener attached but cheap" without regressing production query latency.
/// </summary>
public interface IQueryMetricsListener
{
    void OnQueryMetrics(in QueryMetrics metrics);
}
