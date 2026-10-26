using SkyOmega.Mercury.Abstractions;
using Xunit;

namespace SkyOmega.Mercury.Tests.Fixtures;

/// <summary>
/// Sends Mercury's store-pool events to xunit's diagnostic messages.
/// </summary>
/// <remarks>
/// <para>
/// A test run is a Mercury run in a particular host, and the test host observes the substrate
/// the way any host does: through <see cref="IObservabilityListener"/>. With
/// <c>"diagnosticMessages": true</c> in <c>xunit.runner.json</c> these print as they happen,
/// so a lease waiting on a store that never comes back shows up while the test is stuck, with
/// the pool's capacity decision beside it. Without it, a waiting <c>Rent</c> was a silent hang
/// (omega-ubuntu, 2026-10-26).
/// </para>
/// <para>
/// Only what explains a wait is reported: a capacity below the requested maximum, and each
/// Rent that waits and resumes. A capacity the caller got in full is not news.
/// </para>
/// </remarks>
public sealed class DiagnosticMessageListener : IObservabilityListener
{
    public static DiagnosticMessageListener Instance { get; } = new();

    private DiagnosticMessageListener() { }

    public void OnStorePoolCapacity(in StorePoolCapacityEvent ev)
    {
        if (ev.Bound is StorePoolCapacityBound.Concurrency or StorePoolCapacityBound.MaxPooledStores)
            return;

        TestContext.Current.SendDiagnosticMessage(
            $"Store pool {ev.PoolPath}: capacity {ev.Capacity} of {ev.ConcurrencyLimit} requested, bound by {ev.Bound}. " +
            $"Budget {ev.DiskBudgetFraction:P0} of {ByteFormatter.Format(ev.AvailableBytes)} free fits {ev.DiskLimit} " +
            $"stores of {ByteFormatter.Format(ev.PerStoreEstimateBytes)}.");
    }

    public void OnStorePoolRentBlocked(in StorePoolRentBlockedEvent ev) =>
        TestContext.Current.SendDiagnosticMessage(
            $"Store pool {ev.PoolPath}: Rent on thread {ev.ThreadId} is waiting; all {ev.Capacity} stores are leased.");

    public void OnStorePoolRentResumed(in StorePoolRentResumedEvent ev) =>
        TestContext.Current.SendDiagnosticMessage(
            $"Store pool {ev.PoolPath}: Rent on thread {ev.ThreadId} got a store after {ev.Waited.TotalMilliseconds:F0} ms.");
}
