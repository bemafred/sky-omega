using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using SkyOmega.Mercury.Runtime;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Tests for <see cref="SparseFile"/>: a presized storage file must cost disk only where written.
/// </summary>
public class SparseFileTests : IDisposable
{
    private readonly string _testPath;

    public SparseFileTests()
    {
        var tempPath = TempPath.Test("sparse-file");
        tempPath.MarkOwnership();
        _testPath = tempPath;
    }

    public void Dispose()
    {
        TempPath.SafeCleanup(_testPath);
    }

    [Fact]
    public void SetLength_OneTerabyte_OnASmallerDisk_ReservesNoDisk()
    {
        // The bulk-mode Reference index starts at 1 TB. On NTFS a plain SetLength reserved the whole
        // length and failed with "There is not enough space on the disk" (omega-windows, 2026-10-26).
        const long oneTerabyte = 1024L << 30;
        var available = DiskSpaceGuard.GetAvailableSpace(_testPath);
        Assert.SkipUnless(available > 0 && available < oneTerabyte,
            $"Needs a disk with less than 1 TB free to show the length is not reserved; {available} bytes free.");

        var path = Path.Combine(_testPath, "presized.tdb");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        SparseFile.SetLength(file, oneTerabyte);

        Assert.Equal(oneTerabyte, file.Length);
        if (OperatingSystem.IsWindows())
            Assert.True((File.GetAttributes(path) & FileAttributes.SparseFile) != 0, "The file is not marked sparse.");

        // Free space did not drop by the length: at most the few clusters written, with room for
        // whatever tests running in parallel allocate meanwhile.
        var availableAfter = DiskSpaceGuard.GetAvailableSpace(_testPath);
        Assert.True(available - availableAfter < 8L << 30,
            $"Free space dropped from {available} to {availableAfter} bytes after a sparse 1 TB SetLength.");

        // The whole length is mappable, and writes at both ends read back.
        using var map = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
        using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        view.Write(0, 0x1122334455667788L);
        view.Write(oneTerabyte - sizeof(long), 0x1122334455667788L);
        Assert.Equal(0x1122334455667788L, view.ReadInt64(0));
        Assert.Equal(0x1122334455667788L, view.ReadInt64(oneTerabyte - sizeof(long)));
    }

    [Fact]
    public void SetLength_Growth_KeepsTheFileSparse()
    {
        // The sparse attribute lives with the file, so growth after creation stays sparse.
        var path = Path.Combine(_testPath, "growing.tdb");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        SparseFile.SetLength(file, 64L << 20);
        SparseFile.SetLength(file, 128L << 30);

        Assert.Equal(128L << 30, file.Length);
        if (OperatingSystem.IsWindows())
            Assert.True((File.GetAttributes(path) & FileAttributes.SparseFile) != 0, "The file is not marked sparse after growth.");
    }
}
