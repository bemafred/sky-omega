using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql.Execution;
using Xunit;
using Xunit.Abstractions;
using SkyOmega.Mercury.Sparql.Execution.Operators;

namespace SkyOmega.Mercury.Tests.Infrastructure;

/// <summary>
/// Tests documenting and enforcing stack size constraints for ref structs.
/// These tests track progress on ADR-011 (QueryResults Stack Reduction).
///
/// BASELINE MEASUREMENTS (pre-ADR-011):
/// - QueryResults:              89,640 bytes (~90KB) - caused stack overflow!
/// - MultiPatternScan:          18,080 bytes (~18KB)
/// - DefaultGraphUnionScan:     33,456 bytes (~33KB)
/// - CrossGraphMultiPatternScan: 15,800 bytes (~16KB)
/// - SubQueryScan:               1,976 bytes (~2KB)
/// - TriplePatternScan:           ~500 bytes
///
/// POST-ADR-011 MEASUREMENTS (2026-01-26):
/// - QueryResults:               6,128 bytes (~6KB) - 93% reduction!
/// - MultiPatternScan:             384 bytes (~0.4KB) - 98% reduction!
/// - DefaultGraphUnionScan:      1,040 bytes (~1KB) - 97% reduction!
/// - CrossGraphMultiPatternScan:    96 bytes (~0.1KB) - 99% reduction!
/// - SubQueryScan:               1,976 bytes (~2KB) - unchanged
/// - TriplePatternScan:            608 bytes (~0.6KB) - unchanged
///
/// Key changes:
/// - Changed TemporalResultEnumerator from ref struct to struct
/// - Pooled enumerator arrays in MultiPatternScan and CrossGraphMultiPatternScan
/// - Boxed GraphPattern (~4KB) to move from stack to heap
/// </summary>
public class StackSizeTests
{
    private readonly ITestOutputHelper _output;

    public StackSizeTests(ITestOutputHelper output)
    {
        _output = output;
    }
    /// <summary>
    /// Documents the current QueryResults size.
    /// BASELINE: 89,640 bytes (~90KB!) - this causes stack overflow on Windows (1MB stack).
    /// ADR-011 aims to reduce this to under 5KB.
    /// </summary>
    [Fact]
    public void QueryResults_Size_DocumentBaseline()
    {
        var size = Unsafe.SizeOf<QueryResults>();

        // ACTUAL BASELINE: 89,640 bytes (~90KB)
        // This is ~4x larger than the ADR-011 estimate of ~22KB
        // Embeds: 2x MultiPatternScan + 2x TriplePatternScan + DefaultGraphUnionScan + CrossGraphMultiPatternScan + SubQueryScan
        Assert.True(size > 0, "QueryResults should have measurable size");

        // Document current size - allow some growth tolerance
        Assert.True(size < 100000, $"QueryResults size {size} bytes exceeds baseline tolerance of 100KB");

        // Target assertions (uncomment as phases complete):
        // Phase 2: Assert.True(size < 35000, $"QueryResults size {size} exceeds Phase 2 target of 35KB");
        // Phase 3: Assert.True(size < 5000, $"QueryResults size {size} exceeds Phase 3 target of 5KB");
    }

    /// <summary>
    /// Documents the current MultiPatternScan size.
    /// BASELINE: 18,080 bytes (~18KB)
    /// This is the main contributor to QueryResults stack usage.
    /// ADR-011 Phase 3 aims to reduce this via pooling.
    /// </summary>

    /// <summary>
    /// Documents the current TriplePatternScan size.
    /// This is one of the smaller scan types.
    /// </summary>
    [Fact]
    public void TriplePatternScan_Size_DocumentBaseline()
    {
        var size = Unsafe.SizeOf<TriplePatternScan>();

        // Current baseline: ~500B - relatively small
        Assert.True(size > 0, "TriplePatternScan should have measurable size");
        Assert.True(size < 2000, $"TriplePatternScan size {size} bytes exceeds expected maximum of 2KB");
    }

    /// <summary>
    /// Documents the current DefaultGraphUnionScan size.
    /// BASELINE: 33,456 bytes (~33KB)
    /// This is the largest individual scan type (embeds multiple scan types).
    /// </summary>

    /// <summary>
    /// Documents the current CrossGraphMultiPatternScan size.
    /// BASELINE: 15,800 bytes (~16KB)
    /// </summary>

    /// <summary>
    /// Documents the current SubQueryScan size.
    /// BASELINE: 1,976 bytes (~2KB)
    /// </summary>

    /// <summary>
    /// Summary test that outputs all sizes for documentation.
    /// </summary>

    // ============================================================
    // TARGET ASSERTIONS (uncomment as phases complete)
    // ============================================================

    // [Fact]
    // public void QueryResults_Phase2_Under35KB()
    // {
    //     var size = Unsafe.SizeOf<QueryResults>();
    //     Assert.True(size < 35000, $"QueryResults size {size} exceeds Phase 2 target of 35KB");
    // }

    // [Fact]
    // public void QueryResults_Phase3_Under5KB()
    // {
    //     var size = Unsafe.SizeOf<QueryResults>();
    //     Assert.True(size < 5000, $"QueryResults size {size} exceeds Phase 3 target of 5KB");
    // }

    // [Fact]
    // public void MultiPatternScan_Phase3_Under1KB()
    // {
    //     var size = Unsafe.SizeOf<MultiPatternScan>();
    //     Assert.True(size < 1000, $"MultiPatternScan size {size} exceeds Phase 3 target of 1KB");
    // }
}
