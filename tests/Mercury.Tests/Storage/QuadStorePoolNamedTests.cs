using System;
using System.IO;
using System.Text.Json;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Tests for QuadStorePool named store functionality (ADR-008).
/// </summary>
public class QuadStorePoolNamedTests : IDisposable
{
    private QuadStorePool? _pool;

    public void Dispose()
    {
        _pool?.Dispose();
    }

    #region CreateTemp Tests

    [Fact]
    public void CreateTemp_CreatesTemporaryPool()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        Assert.True(_pool.IsTemporary);
        Assert.NotNull(_pool.BasePath);
        Assert.True(Directory.Exists(_pool.BasePath));
    }

    [Fact]
    public void CreateTemp_CleansUpOnDispose()
    {
        var pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);
        var basePath = pool.BasePath!;

        // Create a named store
        var store = pool["primary"];
        store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");

        Assert.True(Directory.Exists(basePath));

        pool.Dispose();

        // Directory should be cleaned up (may take a moment on Windows)
        // Note: SafeCleanup may leave directory if files are locked
        Assert.True(!Directory.Exists(basePath) || Directory.GetFiles(basePath, "*", SearchOption.AllDirectories).Length == 0);
    }

    #endregion

    #region Named Store Indexer Tests

    [Fact]
    public void Indexer_CreatesStoreOnFirstAccess()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        var store = _pool["primary"];

        Assert.NotNull(store);
        Assert.Contains("primary", _pool.StoreNames);
    }

    [Fact]
    public void Indexer_ReturnsSameStoreOnSubsequentAccess()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        var store1 = _pool["primary"];
        store1.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");

        var store2 = _pool["primary"];

        Assert.Same(store1, store2);
        var (count, _, _) = store2.GetStatistics();
        Assert.Equal(1, count);
    }

    [Fact]
    public void Indexer_CreatesMultipleStores()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        var primary = _pool["primary"];
        var secondary = _pool["secondary"];
        var tertiary = _pool["tertiary"];

        Assert.NotSame(primary, secondary);
        Assert.NotSame(secondary, tertiary);
        Assert.Equal(3, _pool.StoreNames.Count);
    }

    [Fact]
    public void Indexer_ThrowsOnNullOrWhitespaceName()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        Assert.Throws<ArgumentNullException>(() => _pool[null!]);
        Assert.Throws<ArgumentException>(() => _pool[""]);
        Assert.Throws<ArgumentException>(() => _pool["   "]);
    }

    #endregion

    #region Active Store Tests

    [Fact]
    public void Active_FirstStoreBecomesActive()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        var store = _pool["primary"];

        Assert.Equal("primary", _pool.ActiveName);
        Assert.Same(store, _pool.Active);
    }

    [Fact]
    public void Active_ThrowsWhenNoActiveSet()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        // Pool starts with no stores, so no active
        Assert.Null(_pool.ActiveName);
        Assert.Throws<InvalidOperationException>(() => _pool.Active);
    }

    [Fact]
    public void SetActive_ChangesActiveStore()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        var primary = _pool["primary"];
        var secondary = _pool["secondary"];

        Assert.Equal("primary", _pool.ActiveName);

        _pool.SetActive("secondary");

        Assert.Equal("secondary", _pool.ActiveName);
        Assert.Same(secondary, _pool.Active);
    }

    [Fact]
    public void SetActive_ThrowsForNonexistentStore()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        Assert.Throws<KeyNotFoundException>(() => _pool.SetActive("nonexistent"));
    }

    #endregion

    #region Switch Tests

    [Fact]
    public void Switch_SwapsStores()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        var primary = _pool["primary"];
        var secondary = _pool["secondary"];

        primary.AddCurrent("<http://ex/s>", "<http://ex/p>", "\"primary-data\"");
        secondary.AddCurrent("<http://ex/s>", "<http://ex/p>", "\"secondary-data\"");

        _pool.Switch("primary", "secondary");

        // After switch, "primary" should return what was "secondary"
        var newPrimary = _pool["primary"];
        var newSecondary = _pool["secondary"];

        Assert.Same(secondary, newPrimary);
        Assert.Same(primary, newSecondary);
    }

    [Fact]
    public void Switch_CreatesStoresIfNeeded()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        // Neither store exists yet
        _pool.Switch("a", "b");

        Assert.Contains("a", _pool.StoreNames);
        Assert.Contains("b", _pool.StoreNames);
    }

    [Fact]
    public void Switch_SameNameIsNoOp()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        var store = _pool["primary"];
        store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");

        _pool.Switch("primary", "primary");

        // Store should be unchanged
        var (count, _, _) = _pool["primary"].GetStatistics();
        Assert.Equal(1, count);
    }

    [Fact]
    public void Switch_PersistsToMetadata()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"pool-test-{Guid.NewGuid():N}");
        try
        {
            // Create pool with named stores - add different amounts to each
            using (var pool = new QuadStorePool(tempPath, QuadStorePoolOptions.ForTesting))
            {
                pool["primary"].AddCurrent("<http://ex/s1>", "<http://ex/p>", "<http://ex/o>");
                pool["secondary"].AddCurrent("<http://ex/s1>", "<http://ex/p>", "<http://ex/o>");
                pool["secondary"].AddCurrent("<http://ex/s2>", "<http://ex/p>", "<http://ex/o>");
                pool.Switch("primary", "secondary");
            }

            // Reopen and verify by counting triples
            using (var pool = new QuadStorePool(tempPath, QuadStorePoolOptions.ForTesting))
            {
                // After switch and reload, "primary" should have 2 triples (was secondary)
                var (primaryCount, _, _) = pool["primary"].GetStatistics();
                var (secondaryCount, _, _) = pool["secondary"].GetStatistics();

                Assert.Equal(2, primaryCount);
                Assert.Equal(1, secondaryCount);
            }
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, recursive: true);
        }
    }

    #endregion

    #region Delete Tests

    [Fact]
    public void Delete_RemovesStore()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        _pool["primary"].AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");
        var _ = _pool["secondary"]; // Create secondary so we can delete primary

        _pool.SetActive("secondary");
        _pool.Delete("primary");

        Assert.DoesNotContain("primary", _pool.StoreNames);
    }

    [Fact]
    public void Delete_ThrowsForActiveStore()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        var _ = _pool["primary"];

        Assert.Throws<InvalidOperationException>(() => _pool.Delete("primary"));
    }

    [Fact]
    public void Delete_ThrowsForNonexistentStore()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        Assert.Throws<KeyNotFoundException>(() => _pool.Delete("nonexistent"));
    }

    #endregion

    #region Clear(string) Tests

    [Fact]
    public void Clear_ClearsStoreData()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        var store = _pool["primary"];
        store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");

        var (countBefore, _, _) = store.GetStatistics();
        Assert.Equal(1, countBefore);

        _pool.Clear("primary");

        var (countAfter, _, _) = store.GetStatistics();
        Assert.Equal(0, countAfter);
    }

    [Fact]
    public void Clear_CreatesStoreIfNotExists()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        _pool.Clear("newstore");

        Assert.Contains("newstore", _pool.StoreNames);
    }

    #endregion

    #region Persistence Tests

    [Fact]
    public void Persistence_StoresAreRehydratedOnReopen()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"pool-test-{Guid.NewGuid():N}");
        try
        {
            // Create pool with data
            using (var pool = new QuadStorePool(tempPath, QuadStorePoolOptions.ForTesting))
            {
                pool["primary"].AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");
            }

            // Reopen and verify
            using (var pool = new QuadStorePool(tempPath, QuadStorePoolOptions.ForTesting))
            {
                var (count, _, _) = pool["primary"].GetStatistics();
                Assert.Equal(1, count);
            }
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, recursive: true);
        }
    }

    [Fact]
    public void Persistence_ActiveStoreIsRestored()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"pool-test-{Guid.NewGuid():N}");
        try
        {
            using (var pool = new QuadStorePool(tempPath, QuadStorePoolOptions.ForTesting))
            {
                var _ = pool["primary"];
                var __ = pool["secondary"];
                pool.SetActive("secondary");
            }

            using (var pool = new QuadStorePool(tempPath, QuadStorePoolOptions.ForTesting))
            {
                Assert.Equal("secondary", pool.ActiveName);
            }
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, recursive: true);
        }
    }

    [Fact]
    public void Persistence_PoolJsonIsWrittenCorrectly()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"pool-test-{Guid.NewGuid():N}");
        try
        {
            using (var pool = new QuadStorePool(tempPath, QuadStorePoolOptions.ForTesting))
            {
                var _ = pool["primary"];
                var __ = pool["secondary"];
                pool.SetActive("secondary");
            }

            var poolJsonPath = Path.Combine(tempPath, "pool.json");
            Assert.True(File.Exists(poolJsonPath));

            var json = File.ReadAllText(poolJsonPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal(1, root.GetProperty("version").GetInt32());
            Assert.Equal("secondary", root.GetProperty("active").GetString());
            Assert.True(root.GetProperty("stores").TryGetProperty("primary", out _));
            Assert.True(root.GetProperty("stores").TryGetProperty("secondary", out _));
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, recursive: true);
        }
    }

    #endregion

    #region Disk Usage Tests

    [Fact]
    public void TotalDiskUsage_ReportsNonZeroAfterStoreCreation()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        _pool["primary"].AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");

        // Should have some disk usage (at least the index files)
        Assert.True(_pool.TotalDiskUsage > 0);
    }

    [Fact]
    public void MaxDiskBytes_IsSet()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        // Should have a positive budget
        Assert.True(_pool.MaxDiskBytes > 0);
    }

    #endregion

    #region Backward Compatibility Tests

    [Fact]
    public void Rent_StillWorksWithNamedStores()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        // Create a named store
        _pool["primary"].AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");

        // Rent should still work
        var rented = _pool.Rent();
        try
        {
            rented.AddCurrent("<http://ex/rented>", "<http://ex/p>", "<http://ex/o>");
            var (count, _, _) = rented.GetStatistics();
            Assert.Equal(1, count);
        }
        finally
        {
            _pool.Return(rented);
        }
    }

    [Fact]
    public void RentScoped_StillWorksWithNamedStores()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        var _ = _pool["primary"];

        using (var lease = _pool.RentScoped())
        {
            lease.Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");
        }

        Assert.Equal(1, _pool.AvailableCount);
    }

    #endregion

    #region Directory Structure Tests

    [Fact]
    public void DirectoryStructure_HasExpectedLayout()
    {
        _pool = QuadStorePool.CreateTemp("test", QuadStorePoolOptions.ForTesting);

        var _ = _pool["primary"];
        var rented = _pool.Rent();
        _pool.Return(rented);

        var basePath = _pool.BasePath!;

        Assert.True(Directory.Exists(Path.Combine(basePath, "stores")));
        Assert.True(Directory.Exists(Path.Combine(basePath, "pooled")));
        Assert.True(File.Exists(Path.Combine(basePath, "pool.json")));

        // Named stores go in stores/
        Assert.True(Directory.GetDirectories(Path.Combine(basePath, "stores")).Length >= 1);

        // Pooled stores go in pooled/
        Assert.True(Directory.GetDirectories(Path.Combine(basePath, "pooled")).Length >= 1);
    }

    #endregion
}
