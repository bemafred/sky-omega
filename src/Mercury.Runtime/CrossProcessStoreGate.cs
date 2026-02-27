using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace SkyOmega.Mercury.Runtime;

/// <summary>
/// Cross-process coordination gate that limits the total number of concurrent QuadStore
/// instances across all processes on the machine.
/// </summary>
/// <remarks>
/// <para>
/// This gate solves the "NCrunch disk exhaustion" problem where multiple test runner
/// processes each create their own QuadStorePool, potentially exhausting disk space
/// when run in parallel.
/// </para>
/// <para>
/// The gate uses file-based slot locking: numbered lock files (slot-0.lock through slot-N.lock)
/// with exclusive file locks. The OS releases locks automatically on process death, so slots
/// are never permanently lost when a test runner is killed.
/// </para>
/// <para>
/// Usage:
/// <code>
/// // In QuadStorePool before creating a new store:
/// CrossProcessStoreGate.Instance.Acquire();
/// try
/// {
///     // Create store...
/// }
/// catch
/// {
///     CrossProcessStoreGate.Instance.Release();
///     throw;
/// }
/// // On successful store creation, the slot is held for the pool's lifetime.
/// // Slots are released in batch when QuadStorePool is disposed.
/// </code>
/// </para>
/// </remarks>
public sealed class CrossProcessStoreGate : IDisposable
{
    /// <summary>
    /// Default maximum stores across all processes. Conservative default.
    /// </summary>
    public const int DefaultMaxGlobalStores = 6;

    /// <summary>
    /// Lock directory for file-based slot locking.
    /// </summary>
    private static readonly string LockDirectory = Path.Combine(Path.GetTempPath(), ".sky-omega-pool-locks");

    private readonly object _lock = new();
    private readonly IGateStrategy _strategy;
    private int _acquiredCount;
    private bool _disposed;

    /// <summary>
    /// Singleton instance for process-wide coordination.
    /// </summary>
    public static CrossProcessStoreGate Instance { get; } = new(CalculateMaxStores());

    /// <summary>
    /// Creates a gate with a specified maximum number of global stores.
    /// </summary>
    /// <param name="maxGlobalStores">Maximum stores across all processes.</param>
    public CrossProcessStoreGate(int maxGlobalStores)
    {
        MaxGlobalStores = Math.Max(1, maxGlobalStores);
        _strategy = CreateStrategy(MaxGlobalStores);
    }

    /// <summary>
    /// Maximum stores allowed across all processes on this machine.
    /// </summary>
    public int MaxGlobalStores { get; }

    /// <summary>
    /// Number of slots currently held by this process.
    /// </summary>
    public int AcquiredCount
    {
        get
        {
            lock (_lock)
            {
                return _acquiredCount;
            }
        }
    }

    /// <summary>
    /// The strategy being used (for diagnostics).
    /// </summary>
    public string StrategyName => _strategy.Name;

    /// <summary>
    /// Acquires a slot for creating a QuadStore. Blocks until a slot is available.
    /// </summary>
    /// <param name="timeout">Maximum time to wait. Default: 60 seconds.</param>
    /// <returns>True if acquired, false if timed out.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if gate is disposed.</exception>
    public bool Acquire(TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);

        if (_strategy.TryAcquire(effectiveTimeout))
        {
            lock (_lock)
            {
                _acquiredCount++;
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// Releases a previously acquired slot.
    /// </summary>
    public void Release()
    {
        if (_disposed)
            return;

        lock (_lock)
        {
            if (_acquiredCount <= 0)
                return; // Nothing to release

            _acquiredCount--;
        }

        _strategy.Release();
    }

    /// <summary>
    /// Calculates the maximum number of global stores based on available disk space.
    /// </summary>
    private static int CalculateMaxStores()
    {
        try
        {
            var tempPath = Path.GetTempPath();
            var available = DiskSpaceGuard.GetAvailableSpace(tempPath);

            if (available < 0)
                return DefaultMaxGlobalStores; // Can't determine, use conservative default

            // Use ForTesting estimate: ~320MB per store
            // Budget: 33% of available disk
            const long perStoreEstimate = 320L << 20; // 320 MB
            const double budgetFraction = 0.33;

            var budget = (long)(available * budgetFraction);
            var calculated = (int)(budget / perStoreEstimate);

            // Clamp between 2 and 12
            return Math.Clamp(calculated, 2, 12);
        }
        catch
        {
            return DefaultMaxGlobalStores;
        }
    }

    private static IGateStrategy CreateStrategy(int maxStores)
    {
        return new FileBasedGateStrategy(maxStores, LockDirectory);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Release any held slots
        lock (_lock)
        {
            while (_acquiredCount > 0)
            {
                _acquiredCount--;
                _strategy.Release();
            }
        }

        _strategy.Dispose();
    }
}

/// <summary>
/// Strategy interface for gate implementations.
/// </summary>
internal interface IGateStrategy : IDisposable
{
    string Name { get; }
    bool TryAcquire(TimeSpan timeout);
    void Release();
}

/// <summary>
/// File-based slot locking strategy for universal cross-platform support.
/// </summary>
/// <remarks>
/// Creates numbered lock files (slot-0.lock through slot-N.lock).
/// Acquiring a slot means exclusively locking a file.
/// OS releases locks automatically on process death.
/// </remarks>
internal sealed class FileBasedGateStrategy : IGateStrategy
{
    private readonly int _maxSlots;
    private readonly string _lockDirectory;
    private readonly object _lock = new();

    // Track which slots this process holds (file streams keep locks)
    private readonly FileStream?[] _heldLocks;

    public FileBasedGateStrategy(int maxSlots, string lockDirectory)
    {
        _maxSlots = maxSlots;
        _lockDirectory = lockDirectory;
        _heldLocks = new FileStream?[maxSlots];

        Directory.CreateDirectory(_lockDirectory);

        // Clean up stale lock files from dead processes on startup
        CleanupStaleLocks();
    }

    public string Name => "FileBased";

    public bool TryAcquire(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            lock (_lock)
            {
                for (int i = 0; i < _maxSlots; i++)
                {
                    if (_heldLocks[i] != null)
                        continue; // We already hold this slot

                    var lockFile = GetLockFilePath(i);

                    try
                    {
                        // Try to exclusively lock the file
                        var fs = new FileStream(
                            lockFile,
                            FileMode.OpenOrCreate,
                            FileAccess.ReadWrite,
                            FileShare.None,
                            bufferSize: 1,
                            FileOptions.DeleteOnClose);

                        // Write our PID for debugging/diagnostics
                        var pidBytes = Encoding.UTF8.GetBytes($"{Environment.ProcessId}\n{DateTime.UtcNow:O}");
                        fs.Write(pidBytes);
                        fs.Flush();

                        _heldLocks[i] = fs;
                        return true;
                    }
                    catch (IOException)
                    {
                        // Slot is held by another process, try next
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Permission issue, try next slot
                    }
                }
            }

            // All slots busy, wait and retry
            Thread.Sleep(100);
        }

        return false; // Timed out
    }

    public void Release()
    {
        lock (_lock)
        {
            // Release the first held slot (LIFO order doesn't matter for semaphore semantics)
            for (int i = _maxSlots - 1; i >= 0; i--)
            {
                if (_heldLocks[i] != null)
                {
                    _heldLocks[i]!.Dispose(); // Closes file, releases lock, deletes file (DeleteOnClose)
                    _heldLocks[i] = null;
                    return;
                }
            }
        }
    }

    private string GetLockFilePath(int slot)
    {
        return Path.Combine(_lockDirectory, $"slot-{slot}.lock");
    }

    /// <summary>
    /// Cleans up lock files from processes that died without cleanup.
    /// </summary>
    private void CleanupStaleLocks()
    {
        try
        {
            foreach (var file in Directory.GetFiles(_lockDirectory, "slot-*.lock"))
            {
                try
                {
                    // Try to read PID from lock file
                    var content = File.ReadAllText(file);
                    var lines = content.Split('\n');
                    if (lines.Length > 0 && int.TryParse(lines[0], out var pid))
                    {
                        // Check if process is still alive
                        try
                        {
                            Process.GetProcessById(pid);
                            // Process exists - lock file is valid, leave it
                            continue;
                        }
                        catch (ArgumentException)
                        {
                            // Process doesn't exist - delete stale lock
                        }
                    }

                    // Delete stale lock file
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // File is locked (in use) - that's fine, it's active
                }
                catch (UnauthorizedAccessException)
                {
                    // Can't access - leave it
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Lock directory doesn't exist yet - nothing to clean
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            for (int i = 0; i < _maxSlots; i++)
            {
                _heldLocks[i]?.Dispose();
                _heldLocks[i] = null;
            }
        }
    }
}
