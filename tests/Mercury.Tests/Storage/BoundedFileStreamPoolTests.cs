using System.IO;
using System.Linq;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-034 Round 2 follow-up: bounded LRU file-stream cache used by the k-way merge to
/// keep the merge's open-FD footprint under the OS ulimit at 21.3B Wikidata scale
/// (~13K chunks, macOS default ulimit 256-1024).
/// </summary>
public class BoundedFileStreamPoolTests : System.IDisposable
{
    private readonly string _testDir;

    public BoundedFileStreamPoolTests()
    {
        var tempPath = TempPath.Test("bounded_filestream_pool");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    private string MakeFile(string name, byte[] contents)
    {
        var path = Path.Combine(_testDir, name);
        File.WriteAllBytes(path, contents);
        return path;
    }

    [Fact]
    public void Get_FirstCallOpens_SecondCallHits()
    {
        var path = MakeFile("a.bin", new byte[] { 1, 2, 3 });
        using var pool = new BoundedFileStreamPool(maxOpen: 4);

        var fs1 = pool.Get(path);
        Assert.Equal(1, pool.OpenCount);
        Assert.Equal(0, pool.Hits);
        Assert.Equal(1, pool.Misses);

        var fs2 = pool.Get(path);
        Assert.Same(fs1, fs2);
        Assert.Equal(1, pool.Hits);
        Assert.Equal(1, pool.Misses);
    }

    [Fact]
    public void Get_ExceedsCapacity_EvictsLeastRecentlyUsed()
    {
        var p1 = MakeFile("p1.bin", new byte[] { 1 });
        var p2 = MakeFile("p2.bin", new byte[] { 2 });
        var p3 = MakeFile("p3.bin", new byte[] { 3 });
        using var pool = new BoundedFileStreamPool(maxOpen: 2);

        pool.Get(p1);
        pool.Get(p2);
        Assert.Equal(2, pool.OpenCount);

        // p3 evicts p1 (LRU). Then p1 is a miss.
        pool.Get(p3);
        Assert.Equal(2, pool.OpenCount);

        var beforeMisses = pool.Misses;
        pool.Get(p1);
        Assert.Equal(beforeMisses + 1, pool.Misses);
    }

    [Fact]
    public void Get_BumpsToFront_OnHit()
    {
        var p1 = MakeFile("p1.bin", new byte[] { 1 });
        var p2 = MakeFile("p2.bin", new byte[] { 2 });
        var p3 = MakeFile("p3.bin", new byte[] { 3 });
        using var pool = new BoundedFileStreamPool(maxOpen: 2);

        pool.Get(p1);
        pool.Get(p2);
        pool.Get(p1);  // bump p1 to front; now p2 is LRU

        // p3 should evict p2, not p1.
        pool.Get(p3);

        var beforeMisses = pool.Misses;
        pool.Get(p1);
        Assert.Equal(beforeMisses, pool.Misses);  // p1 still cached

        pool.Get(p2);
        Assert.Equal(beforeMisses + 1, pool.Misses);  // p2 was evicted
    }

    [Fact]
    public void Drop_RemovesAndDisposes()
    {
        var path = MakeFile("a.bin", new byte[] { 1, 2 });
        using var pool = new BoundedFileStreamPool(maxOpen: 4);
        var fs = pool.Get(path);
        Assert.True(fs.CanRead);

        pool.Drop(path);
        Assert.Equal(0, pool.OpenCount);
        Assert.False(fs.CanRead);
    }

    [Fact]
    public void Drop_Idempotent()
    {
        var path = MakeFile("a.bin", new byte[] { 1 });
        using var pool = new BoundedFileStreamPool(maxOpen: 4);
        pool.Drop(path);  // never inserted
        pool.Get(path);
        pool.Drop(path);
        pool.Drop(path);  // already removed
        Assert.Equal(0, pool.OpenCount);
    }

    [Fact]
    public void Dispose_ClosesAllStreams()
    {
        var paths = Enumerable.Range(0, 5).Select(i => MakeFile($"d{i}.bin", new byte[] { (byte)i })).ToList();
        var pool = new BoundedFileStreamPool(maxOpen: 8);
        var streams = paths.Select(p => pool.Get(p)).ToList();
        Assert.All(streams, fs => Assert.True(fs.CanRead));

        pool.Dispose();
        Assert.All(streams, fs => Assert.False(fs.CanRead));
    }

    [Fact]
    public void Get_AfterDispose_Throws()
    {
        var path = MakeFile("a.bin", new byte[] { 1 });
        var pool = new BoundedFileStreamPool(maxOpen: 2);
        pool.Dispose();
        Assert.Throws<System.ObjectDisposedException>(() => pool.Get(path));
    }
}
