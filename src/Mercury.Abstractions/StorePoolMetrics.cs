using System;

namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// What bounded a <c>QuadStorePool</c>'s capacity: the number of stores it can lease at once.
/// </summary>
public enum StorePoolCapacityBound
{
    /// <summary>The caller's requested maximum (or the processor count when none was given).</summary>
    Concurrency,

    /// <summary>The disk budget: free space on the pool's filesystem × budget fraction ÷ per-store estimate.</summary>
    DiskBudget,

    /// <summary>The disk budget fits no store at all; the pool still allows one.</summary>
    DiskBudgetFloor,

    /// <summary>The persistent pool's configured <c>MaxPooledStores</c>.</summary>
    MaxPooledStores,
}

/// <summary>
/// A <c>QuadStorePool</c>'s capacity decision, emitted once when the pool is created.
/// Discloses every input, so a pool sized below what the caller asked for is visible at
/// creation rather than only when a lease later waits for a store that cannot come.
/// </summary>
/// <param name="Timestamp">When the pool was created.</param>
/// <param name="PoolPath">The pool's base directory: the path whose filesystem was measured.</param>
/// <param name="RequestedConcurrent">The caller's requested maximum; 0 means "processor count".</param>
/// <param name="ConcurrencyLimit">The concurrency limit applied (requested, processor count, or MaxPooledStores).</param>
/// <param name="DiskLimit">Stores the disk budget fits, or -1 when the disk budget does not bound capacity.</param>
/// <param name="Capacity">The capacity in effect: the most stores leased at once.</param>
/// <param name="Bound">Which input set <paramref name="Capacity"/>.</param>
/// <param name="AvailableBytes">Free space on the pool's filesystem, or -1 if unknown.</param>
/// <param name="DiskBudgetFraction">Fraction of <paramref name="AvailableBytes"/> the pool may use.</param>
/// <param name="PerStoreEstimateBytes">Estimated size of one store, from its storage options.</param>
public readonly record struct StorePoolCapacityEvent(
    DateTimeOffset Timestamp,
    string PoolPath,
    int RequestedConcurrent,
    int ConcurrencyLimit,
    int DiskLimit,
    int Capacity,
    StorePoolCapacityBound Bound,
    long AvailableBytes,
    double DiskBudgetFraction,
    long PerStoreEstimateBytes);

/// <summary>
/// A <c>QuadStorePool.Rent</c> found every store leased and is now waiting for a return.
/// Paired with <see cref="StorePoolRentResumedEvent"/> when the wait ends. A blocked event
/// with no resumed event is a lease waiting on a store that has not been returned.
/// </summary>
/// <param name="Timestamp">When the wait began.</param>
/// <param name="PoolPath">The pool's base directory.</param>
/// <param name="Capacity">The pool's capacity, all of it leased.</param>
/// <param name="ThreadId">Managed thread id of the waiting caller.</param>
public readonly record struct StorePoolRentBlockedEvent(
    DateTimeOffset Timestamp,
    string PoolPath,
    int Capacity,
    int ThreadId);

/// <summary>
/// A <c>QuadStorePool.Rent</c> that waited (see <see cref="StorePoolRentBlockedEvent"/>) got a store.
/// </summary>
/// <param name="Timestamp">When the wait ended.</param>
/// <param name="PoolPath">The pool's base directory.</param>
/// <param name="ThreadId">Managed thread id of the caller that waited.</param>
/// <param name="Waited">How long the caller waited.</param>
public readonly record struct StorePoolRentResumedEvent(
    DateTimeOffset Timestamp,
    string PoolPath,
    int ThreadId,
    TimeSpan Waited);
