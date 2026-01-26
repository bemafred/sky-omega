using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql.Execution;
using Xunit;

namespace SkyOmega.Mercury.Tests.Infrastructure;

/// <summary>
/// Tests documenting and enforcing stack size constraints for ref structs.
/// These tests track progress on ADR-011 (QueryResults Stack Reduction).
///
/// ACTUAL BASELINE MEASUREMENTS (2024-01-26):
/// - QueryResults:              89,640 bytes (~90KB) - CRITICAL!
/// - MultiPatternScan:          18,080 bytes (~18KB)
/// - DefaultGraphUnionScan:     33,456 bytes (~33KB)
/// - CrossGraphMultiPatternScan: 15,800 bytes (~16KB)
/// - SubQueryScan:               1,976 bytes (~2KB)
/// - TriplePatternScan:           ~500 bytes
///
/// Phase targets:
/// - Phase 2 target: QueryResults &lt; 35KB (discriminated union)
/// - Phase 3 target: QueryResults &lt; 5KB (pooled enumerators)
/// </summary>
public class StackSizeTests
{
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
    [Fact]
    public void MultiPatternScan_Size_DocumentBaseline()
    {
        var size = Unsafe.SizeOf<MultiPatternScan>();

        // ACTUAL BASELINE: 18,080 bytes (~18KB)
        // Contains 12 inline TemporalResultEnumerator + GraphPattern + state
        Assert.True(size > 0, "MultiPatternScan should have measurable size");
        Assert.True(size < 25000, $"MultiPatternScan size {size} bytes exceeds baseline tolerance of 25KB");

        // Phase 3: Assert.True(size < 1000, $"MultiPatternScan size {size} exceeds Phase 3 target of 1KB");
    }

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
    [Fact]
    public void DefaultGraphUnionScan_Size_DocumentBaseline()
    {
        var size = Unsafe.SizeOf<DefaultGraphUnionScan>();

        // ACTUAL BASELINE: 33,456 bytes (~33KB)
        // Embeds TriplePatternScan + MultiPatternScan + GraphPattern + state
        Assert.True(size > 0, "DefaultGraphUnionScan should have measurable size");
        Assert.True(size < 40000, $"DefaultGraphUnionScan size {size} bytes exceeds baseline tolerance of 40KB");
    }

    /// <summary>
    /// Documents the current CrossGraphMultiPatternScan size.
    /// BASELINE: 15,800 bytes (~16KB)
    /// </summary>
    [Fact]
    public void CrossGraphMultiPatternScan_Size_DocumentBaseline()
    {
        var size = Unsafe.SizeOf<CrossGraphMultiPatternScan>();

        // ACTUAL BASELINE: 15,800 bytes (~16KB)
        // Contains 4 enumerators + GraphPattern + state
        Assert.True(size > 0, "CrossGraphMultiPatternScan should have measurable size");
        Assert.True(size < 20000, $"CrossGraphMultiPatternScan size {size} bytes exceeds baseline tolerance of 20KB");
    }

    /// <summary>
    /// Documents the current SubQueryScan size.
    /// BASELINE: 1,976 bytes (~2KB)
    /// </summary>
    [Fact]
    public void SubQueryScan_Size_DocumentBaseline()
    {
        var size = Unsafe.SizeOf<SubQueryScan>();

        // ACTUAL BASELINE: 1,976 bytes (~2KB)
        // Contains references to materialized data + SubSelect struct
        Assert.True(size > 0, "SubQueryScan should have measurable size");
        Assert.True(size < 3000, $"SubQueryScan size {size} bytes exceeds baseline tolerance of 3KB");
    }

    /// <summary>
    /// Summary test that outputs all sizes for documentation.
    /// </summary>
    [Fact]
    public void AllScanTypes_PrintSizes()
    {
        var queryResultsSize = Unsafe.SizeOf<QueryResults>();
        var multiPatternScanSize = Unsafe.SizeOf<MultiPatternScan>();
        var defaultGraphUnionScanSize = Unsafe.SizeOf<DefaultGraphUnionScan>();
        var crossGraphSize = Unsafe.SizeOf<CrossGraphMultiPatternScan>();
        var subQuerySize = Unsafe.SizeOf<SubQueryScan>();
        var triplePatternSize = Unsafe.SizeOf<TriplePatternScan>();

        // Output all sizes in the test output
        var message = $@"
=== STACK SIZE MEASUREMENTS ===
QueryResults:              {queryResultsSize,8:N0} bytes ({queryResultsSize / 1024.0:F1} KB)
MultiPatternScan:          {multiPatternScanSize,8:N0} bytes ({multiPatternScanSize / 1024.0:F1} KB)
DefaultGraphUnionScan:     {defaultGraphUnionScanSize,8:N0} bytes ({defaultGraphUnionScanSize / 1024.0:F1} KB)
CrossGraphMultiPatternScan:{crossGraphSize,8:N0} bytes ({crossGraphSize / 1024.0:F1} KB)
SubQueryScan:              {subQuerySize,8:N0} bytes ({subQuerySize / 1024.0:F1} KB)
TriplePatternScan:         {triplePatternSize,8:N0} bytes ({triplePatternSize / 1024.0:F1} KB)
===============================
";

        // Verify sizes meet new baselines after ADR-011 pooling
        Assert.True(queryResultsSize > 0, message);

        // ADR-011: MultiPatternScan reduced from ~18KB to ~15KB by pooling enumerators
        // Further reduction requires boxing GraphPattern (~4KB)
        Assert.True(multiPatternScanSize < 20000,
            $"MultiPatternScan at {multiPatternScanSize} bytes exceeds post-ADR-011 baseline. {message}");

        // ADR-011: QueryResults reduced from ~90KB to ~80KB
        Assert.True(queryResultsSize < 85000,
            $"QueryResults at {queryResultsSize} bytes exceeds post-ADR-011 baseline. {message}");
    }

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
