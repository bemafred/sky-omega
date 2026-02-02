using System;
using System.IO;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Tests for StorageOptions and disk space enforcement in storage components.
/// </summary>
public class StorageOptionsTests : IDisposable
{
    private readonly string _testPath;

    public StorageOptionsTests()
    {
        var tempPath = TempPath.Test("storage-options");
        tempPath.MarkOwnership();
        _testPath = tempPath;
    }

    public void Dispose()
    {
        TempPath.SafeCleanup(_testPath);
    }

    #region StorageOptions Configuration

    [Fact]
    public void StorageOptions_Default_HasReasonableValues()
    {
        var options = StorageOptions.Default;

        Assert.Equal(1L << 30, options.MinimumFreeDiskSpace); // 1GB
        Assert.Equal(AtomStore.DefaultMaxAtomSize, options.MaxAtomSize);
    }

    [Fact]
    public void StorageOptions_NoEnforcement_HasZeroMinimum()
    {
        var options = StorageOptions.NoEnforcement;

        Assert.Equal(0, options.MinimumFreeDiskSpace);
    }

    [Fact]
    public void StorageOptions_WithMinimumFreeSpace_SetsValue()
    {
        var options = StorageOptions.WithMinimumFreeSpace(5L << 30); // 5GB

        Assert.Equal(5L << 30, options.MinimumFreeDiskSpace);
    }

    [Fact]
    public void StorageOptions_CustomValues_CanBeSet()
    {
        var options = new StorageOptions
        {
            MinimumFreeDiskSpace = 2L << 30, // 2GB
            MaxAtomSize = 512 << 10 // 512KB
        };

        Assert.Equal(2L << 30, options.MinimumFreeDiskSpace);
        Assert.Equal(512 << 10, options.MaxAtomSize);
    }

    #endregion

    #region DiskSpaceChecker

    [Fact]
    public void DiskSpaceChecker_GetAvailableSpace_ReturnsPositiveValue()
    {
        var available = DiskSpaceChecker.GetAvailableSpace(_testPath);

        // Should return a positive value for a valid path
        Assert.True(available > 0, $"Expected positive available space, got {available}");
    }

    [Fact]
    public void DiskSpaceChecker_EnsureSufficientSpace_ZeroMinimum_NeverThrows()
    {
        // With zero minimum, should never throw
        DiskSpaceChecker.EnsureSufficientSpace(_testPath, long.MaxValue, 0);
        // If we get here, test passed
    }

    [Fact]
    public void DiskSpaceChecker_EnsureSufficientSpace_ImpossibleRequest_Throws()
    {
        // Request more space than could possibly exist on any drive
        // This should throw InsufficientDiskSpaceException
        var available = DiskSpaceChecker.GetAvailableSpace(_testPath);

        if (available > 0)
        {
            // Set minimum so high that it's impossible to satisfy
            var impossibleMinimum = available + (100L << 30); // Current + 100GB

            Assert.Throws<InsufficientDiskSpaceException>(() =>
                DiskSpaceChecker.EnsureSufficientSpace(_testPath, 1, impossibleMinimum));
        }
    }

    #endregion

    #region InsufficientDiskSpaceException

    [Fact]
    public void InsufficientDiskSpaceException_ContainsUsefulInformation()
    {
        var ex = new InsufficientDiskSpaceException(
            path: "/test/path",
            requestedBytes: 1L << 30,
            availableBytes: 500L << 20,
            minimumFreeSpace: 1L << 30);

        Assert.Equal("/test/path", ex.Path);
        Assert.Equal(1L << 30, ex.RequestedBytes);
        Assert.Equal(500L << 20, ex.AvailableBytes);
        Assert.Equal(1L << 30, ex.MinimumFreeSpace);

        // Message should contain useful info
        Assert.Contains("1.00 GB", ex.Message);
        Assert.Contains("500.00 MB", ex.Message);
    }

    #endregion

    #region QuadStore with StorageOptions

    [Fact]
    public void QuadStore_AcceptsStorageOptions()
    {
        var options = new StorageOptions
        {
            MinimumFreeDiskSpace = 100L << 20 // 100MB
        };

        using var store = new QuadStore(_testPath, null, null, options);

        // Should create successfully with custom options
        store.AddCurrent("<http://test/s>", "<http://test/p>", "<http://test/o>");
    }

    [Fact]
    public void QuadStore_DefaultOptions_UsesOneGBMinimum()
    {
        // Default constructor should use 1GB minimum
        using var store = new QuadStore(_testPath);

        // Should work fine on a system with > 1GB free
        store.AddCurrent("<http://test/s>", "<http://test/p>", "<http://test/o>");
    }

    [Fact]
    public void QuadStore_NoEnforcement_DisablesDiskSpaceCheck()
    {
        using var store = new QuadStore(_testPath, null, null, StorageOptions.NoEnforcement);

        // Should work even with disk space checks disabled
        store.AddCurrent("<http://test/s>", "<http://test/p>", "<http://test/o>");
    }

    #endregion

    #region AtomStore with MaxAtomSize

    [Fact]
    public void AtomStore_AcceptsMaxAtomSize()
    {
        long maxAtomSize = 512 << 10; // 512KB

        var atomPath = Path.Combine(_testPath, "atoms");
        using var store = new AtomStore(atomPath, null, maxAtomSize);

        // Should create successfully
        var id = store.Intern("test value");
        Assert.True(id > 0);
    }

    [Fact]
    public void AtomStore_MaxAtomSize_Enforced()
    {
        long maxAtomSize = 100; // Very small limit

        var atomPath = Path.Combine(_testPath, "atoms");
        using var store = new AtomStore(atomPath, null, maxAtomSize);

        // Small value should work
        var id = store.Intern("small");
        Assert.True(id > 0);

        // Large value should fail
        var largeValue = new string('x', 200);
        Assert.Throws<ArgumentException>(() => store.Intern(largeValue));
    }

    #endregion

    #region Integration: Stress Test with Low Disk Space Limit

    [Fact]
    [Trait("Category", "Stress")]
    public void Integration_VeryHighMinimum_PreventsGrowth()
    {
        // This test verifies that an impossibly high minimum prevents storage growth
        // We can't actually fill the disk, but we can set the minimum higher than available

        var available = DiskSpaceChecker.GetAvailableSpace(_testPath);
        if (available <= 0)
        {
            // Can't determine available space - skip test
            return;
        }

        // Set minimum to current available + 10GB
        // This means ANY growth would violate the limit
        var impossibleMinimum = available + (10L << 30);

        var options = new StorageOptions
        {
            MinimumFreeDiskSpace = impossibleMinimum
        };

        // Store creation might work if files already exist or initial allocation is small
        // But growth should fail
        try
        {
            using var store = new QuadStore(_testPath, null, null, options);

            // If we got here, initial creation worked
            // Now try to add enough data to force growth
            // This should eventually fail with InsufficientDiskSpaceException

            bool exceptionThrown = false;
            try
            {
                for (int i = 0; i < 100000 && !exceptionThrown; i++)
                {
                    store.AddCurrent(
                        $"<http://test/subject{i}>",
                        "<http://test/predicate>",
                        $"<http://test/object{i}>");
                }
            }
            catch (InsufficientDiskSpaceException)
            {
                exceptionThrown = true;
            }

            // We expect the exception to be thrown at some point
            Assert.True(exceptionThrown, "Expected InsufficientDiskSpaceException when growing with impossible minimum");
        }
        catch (InsufficientDiskSpaceException)
        {
            // Exception during creation is also acceptable
        }
    }

    #endregion
}
