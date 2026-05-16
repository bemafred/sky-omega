using SkyOmega.Mercury.Runtime;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-040 / ADR-042 Part 5: smoke tests for the host-physical-memory probe.
/// Exact values are host-dependent; the tests verify the probe returns sane
/// values within expected bounds rather than pinning a specific number.
/// </summary>
public class ProcessMemoryProbeTests
{
    [Fact]
    public void AvailablePhysicalBytes_ReturnsPositiveValue()
    {
        long available = ProcessMemoryProbe.AvailablePhysicalBytes();
        Assert.True(available > 0, $"available={available} should be positive");
    }

    [Fact]
    public void AvailablePhysicalBytes_DoesNotExceedTotalMemoryByOrderOfMagnitude()
    {
        // GC.GetGCMemoryInfo's TotalAvailableMemoryBytes gives:
        //  - Linux cgroup limit or system total
        //  - macOS: hw.memsize (total RAM)
        //  - Windows: ullTotalPhys
        // In every case the BCL value is an upper bound on what our probe can return
        // (since the probe's "available" must be ≤ total).
        long available = ProcessMemoryProbe.AvailablePhysicalBytes();
        long bclTotal = System.GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        // 10× tolerance defends against the (rare) case where BCL's value is from a
        // cgroup quota smaller than our probe's host-level reading.
        Assert.True(available <= bclTotal * 10,
            $"available={available} much larger than BCL total={bclTotal} suggests a probe bug");
    }

    [Fact]
    public void AvailablePhysicalBytes_AtLeastOneMegabyte()
    {
        // Any host the substrate runs on has >1 MB free, period. This rules out
        // the probe returning a stuck-zero or near-zero from a P/Invoke that's
        // silently failing.
        long available = ProcessMemoryProbe.AvailablePhysicalBytes();
        Assert.True(available > 1024 * 1024,
            $"available={available} bytes implausibly small — probe likely failed");
    }
}
