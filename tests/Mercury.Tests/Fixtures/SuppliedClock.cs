using System;

namespace SkyOmega.Mercury.Tests.Fixtures;

/// <summary>
/// A store's clock whose reading the test sets (<c>StorageOptions.Clock</c>, ADR-061 D4 and I11). It stands still
/// until the test moves it. A test whose outcome depends on two writes being in one clock reading, or in two, must
/// supply the readings: with the system clock, two writes that follow each other are in one millisecond on a fast
/// machine and in two on a slow one.
/// </summary>
public sealed class SuppliedClock : TimeProvider
{
    public DateTimeOffset Reading { get; set; }
    public override DateTimeOffset GetUtcNow() => Reading;
}
