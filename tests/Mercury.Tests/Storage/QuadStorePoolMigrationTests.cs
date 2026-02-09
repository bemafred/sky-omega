using System.IO;
using System.Text.Json;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Tests for QuadStorePool flat-store auto-migration.
/// </summary>
public class QuadStorePoolMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private QuadStorePool? _pool;

    public QuadStorePoolMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pool-migrate-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        _pool?.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void Migration_DetectsFlatStoreAndMigrates()
    {
        // Create a flat QuadStore (old format)
        using (var store = new QuadStore(_tempDir))
        {
            store.AddCurrent("<http://ex/s>", "<http://ex/p>", "\"hello\"");
        }

        // Verify flat format exists
        Assert.True(File.Exists(Path.Combine(_tempDir, "gspo.tdb")));

        // Open as pool — should auto-migrate
        _pool = new QuadStorePool(_tempDir, QuadStorePoolOptions.ForTesting);

        // Flat files should be gone from root
        Assert.False(File.Exists(Path.Combine(_tempDir, "gspo.tdb")));

        // pool.json should exist with "primary" mapping
        var poolJson = Path.Combine(_tempDir, "pool.json");
        Assert.True(File.Exists(poolJson));

        var json = File.ReadAllText(poolJson);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("primary", doc.RootElement.GetProperty("active").GetString());
        Assert.True(doc.RootElement.GetProperty("stores").TryGetProperty("primary", out _));

        // Data should be accessible via pool
        Assert.Equal("primary", _pool.ActiveName);
        var (count, _, _) = _pool.Active.GetStatistics();
        Assert.Equal(1, count);
    }

    [Fact]
    public void Migration_NoOpForAlreadyMigratedStore()
    {
        // Create pool format directly
        _pool = new QuadStorePool(_tempDir, QuadStorePoolOptions.ForTesting);
        _pool["primary"].AddCurrent("<http://ex/s>", "<http://ex/p>", "\"test\"");
        _pool.Dispose();

        // Re-open — should not error
        _pool = new QuadStorePool(_tempDir, QuadStorePoolOptions.ForTesting);
        var (count, _, _) = _pool.Active.GetStatistics();
        Assert.Equal(1, count);
    }

    [Fact]
    public void Migration_NoOpForEmptyDirectory()
    {
        Directory.CreateDirectory(_tempDir);

        // Open pool on empty directory — no migration needed
        _pool = new QuadStorePool(_tempDir, QuadStorePoolOptions.ForTesting);

        // Should create "primary" on first access
        _pool["primary"].AddCurrent("<http://ex/s>", "<http://ex/p>", "\"test\"");
        var (count, _, _) = _pool.Active.GetStatistics();
        Assert.Equal(1, count);
    }

    [Fact]
    public void Migration_PreservesMultipleQuads()
    {
        // Create flat store with multiple quads
        using (var store = new QuadStore(_tempDir))
        {
            store.AddCurrent("<http://ex/s1>", "<http://ex/p>", "\"one\"");
            store.AddCurrent("<http://ex/s2>", "<http://ex/p>", "\"two\"");
            store.AddCurrent("<http://ex/s3>", "<http://ex/p>", "\"three\"");
        }

        // Migrate
        _pool = new QuadStorePool(_tempDir, QuadStorePoolOptions.ForTesting);
        var (count, _, _) = _pool.Active.GetStatistics();
        Assert.Equal(3, count);
    }
}
