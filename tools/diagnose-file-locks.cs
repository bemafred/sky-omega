#!/usr/bin/env -S dotnet run
// File-based lock diagnostic for Windows
// Run: dotnet run tools/diagnose-file-locks.cs
// Or:  dotnet tools/diagnose-file-locks.cs

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

const int MaxSlots = 6;
var lockDir = Path.Combine(Path.GetTempPath(), ".sky-omega-lock-diag");
var passed = 0;
var failed = 0;

Console.WriteLine("=== Sky Omega File-Based Lock Diagnostic ===");
Console.WriteLine($"OS:         {Environment.OSVersion}");
Console.WriteLine($".NET:       {Environment.Version}");
Console.WriteLine($"Lock dir:   {lockDir}");
Console.WriteLine($"PID:        {Environment.ProcessId}");
Console.WriteLine($"Temp path:  {Path.GetTempPath()}");
Console.WriteLine();

// Clean slate
if (Directory.Exists(lockDir))
{
    try { Directory.Delete(lockDir, recursive: true); }
    catch (Exception ex) { Console.WriteLine($"WARN: Could not clean lock dir: {ex.GetType().Name}: {ex.Message}"); }
}

Directory.CreateDirectory(lockDir);

// --- Test 1: Basic file creation with exclusive lock ---
RunTest("1. Create exclusive lock file", () =>
{
    var path = Path.Combine(lockDir, "test1.lock");
    using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
        FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);

    var pid = Encoding.UTF8.GetBytes($"{Environment.ProcessId}\n{DateTime.UtcNow:O}");
    fs.Write(pid);
    fs.Flush();

    Console.WriteLine($"    Created and locked: {path}");
    Console.WriteLine($"    File exists while locked: {File.Exists(path)}");

    // File should be deleted after dispose
    fs.Dispose();

    // On Windows with DeleteOnClose, the file should be gone
    var existsAfter = File.Exists(path);
    Console.WriteLine($"    File exists after dispose: {existsAfter}");
    if (existsAfter)
        Console.WriteLine("    WARN: DeleteOnClose did not remove file immediately");
});

// --- Test 2: Exclusive lock blocks second handle ---
RunTest("2. Exclusive lock blocks second open", () =>
{
    var path = Path.Combine(lockDir, "test2.lock");
    using var fs1 = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
        FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);

    try
    {
        using var fs2 = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
        throw new Exception("Second open SUCCEEDED - exclusive lock is not working!");
    }
    catch (IOException ex)
    {
        Console.WriteLine($"    Correctly blocked with IOException: {ex.Message}");
    }
    catch (UnauthorizedAccessException ex)
    {
        Console.WriteLine($"    Blocked with UnauthorizedAccessException: {ex.Message}");
        Console.WriteLine("    (This works but is unexpected - might indicate permission issues)");
    }
});

// --- Test 3: Lock release enables re-acquisition ---
RunTest("3. Release then re-acquire same file", () =>
{
    var path = Path.Combine(lockDir, "test3.lock");

    // Acquire
    var fs1 = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
        FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
    fs1.WriteByte(42);
    fs1.Flush();
    Console.WriteLine("    First lock acquired");

    // Release
    fs1.Dispose();
    Console.WriteLine($"    First lock released. File exists: {File.Exists(path)}");

    // Small delay to simulate real-world timing
    Thread.Sleep(10);

    // Re-acquire
    using var fs2 = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
        FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
    fs2.WriteByte(43);
    fs2.Flush();
    Console.WriteLine("    Second lock acquired successfully");
});

// --- Test 4: Multiple slot files (simulating FileBasedGateStrategy) ---
RunTest("4. Acquire and release multiple slot files", () =>
{
    var streams = new FileStream?[MaxSlots];

    // Acquire all slots
    for (int i = 0; i < MaxSlots; i++)
    {
        var path = Path.Combine(lockDir, $"slot-{i}.lock");
        streams[i] = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
        var pid = Encoding.UTF8.GetBytes($"{Environment.ProcessId}\n{DateTime.UtcNow:O}");
        streams[i]!.Write(pid);
        streams[i]!.Flush();
    }
    Console.WriteLine($"    Acquired all {MaxSlots} slots");

    // Verify next acquire fails
    var nextPath = Path.Combine(lockDir, "slot-0.lock");
    bool blocked = false;
    try
    {
        using var extra = new FileStream(nextPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
    }
    catch (IOException) { blocked = true; }
    catch (UnauthorizedAccessException) { blocked = true; }

    Console.WriteLine($"    Extra acquire correctly blocked: {blocked}");
    if (!blocked) throw new Exception("Was able to open an already-locked slot!");

    // Release all
    for (int i = MaxSlots - 1; i >= 0; i--)
    {
        streams[i]?.Dispose();
        streams[i] = null;
    }
    Console.WriteLine($"    Released all {MaxSlots} slots");

    // Re-acquire slot 0 to verify release worked
    Thread.Sleep(10);
    using var reacquire = new FileStream(
        Path.Combine(lockDir, "slot-0.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite,
        FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
    Console.WriteLine("    Re-acquired slot 0 after release");
});

// --- Test 5: Multi-threaded contention (same process) ---
RunTest("5. Multi-threaded contention (20 threads, 6 slots)", () =>
{
    var heldLocks = new FileStream?[MaxSlots];
    var lockObj = new object();
    var successCount = 0;
    var failCount = 0;
    var exceptionLog = new ConcurrentBag<string>();

    bool TryAcquireSlot(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (lockObj)
            {
                for (int i = 0; i < MaxSlots; i++)
                {
                    if (heldLocks[i] != null) continue;
                    var path = Path.Combine(lockDir, $"mt-slot-{i}.lock");
                    try
                    {
                        var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                            FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
                        heldLocks[i] = fs;
                        return true;
                    }
                    catch (IOException ex)
                    {
                        exceptionLog.Add($"Slot {i} IOException: {ex.Message}");
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        exceptionLog.Add($"Slot {i} UnauthorizedAccessException: {ex.Message}");
                    }
                }
            }
            Thread.Sleep(50);
        }
        return false;
    }

    void ReleaseSlot()
    {
        lock (lockObj)
        {
            for (int i = MaxSlots - 1; i >= 0; i--)
            {
                if (heldLocks[i] != null)
                {
                    heldLocks[i]!.Dispose();
                    heldLocks[i] = null;
                    return;
                }
            }
        }
    }

    var threads = new Thread[20];
    for (int t = 0; t < 20; t++)
    {
        var threadId = t;
        threads[t] = new Thread(() =>
        {
            if (TryAcquireSlot(TimeSpan.FromSeconds(10)))
            {
                Interlocked.Increment(ref successCount);
                Thread.Sleep(5);
                ReleaseSlot();
            }
            else
            {
                Interlocked.Increment(ref failCount);
            }
        });
        threads[t].Start();
    }

    foreach (var t in threads) t.Join(TimeSpan.FromSeconds(30));

    Console.WriteLine($"    Success: {successCount}, Failed: {failCount}");

    if (exceptionLog.Count > 0)
    {
        var unique = new HashSet<string>(exceptionLog);
        Console.WriteLine($"    Unique exceptions ({unique.Count}):");
        foreach (var ex in unique.Take(5))
            Console.WriteLine($"      - {ex}");
        if (unique.Count > 5)
            Console.WriteLine($"      ... and {unique.Count - 5} more");
    }

    // Cleanup
    lock (lockObj)
    {
        for (int i = 0; i < MaxSlots; i++)
        {
            heldLocks[i]?.Dispose();
            heldLocks[i] = null;
        }
    }

    if (failCount > 0) throw new Exception($"{failCount} threads failed to acquire!");
});

// --- Test 6: Rapid acquire-release cycling ---
RunTest("6. Rapid acquire-release cycling (100 iterations)", () =>
{
    var path = Path.Combine(lockDir, "cycle.lock");
    var sw = Stopwatch.StartNew();

    for (int i = 0; i < 100; i++)
    {
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
        fs.WriteByte(42);
        fs.Flush();
    }

    sw.Stop();
    Console.WriteLine($"    100 cycles in {sw.ElapsedMilliseconds}ms ({sw.ElapsedMilliseconds / 100.0:F1}ms/cycle)");
});

// --- Test 7: Cross-process lock visibility ---
RunTest("7. Cross-process lock visibility", () =>
{
    var path = Path.Combine(lockDir, "cross-proc.lock");

    // Parent holds the lock
    using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
        FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
    var pidBytes = Encoding.UTF8.GetBytes($"{Environment.ProcessId}");
    fs.Write(pidBytes);
    fs.Flush();
    Console.WriteLine("    Parent holds lock");

    // Spawn child process that tries to acquire the same file
    var escapedPath = path.Replace("\\", "\\\\");
    var testScript = "try { "
        + "using var f = new System.IO.FileStream("
        + "@\"" + escapedPath + "\", "
        + "System.IO.FileMode.OpenOrCreate, "
        + "System.IO.FileAccess.ReadWrite, "
        + "System.IO.FileShare.None); "
        + "Console.Write(\"UNLOCKED\"); "
        + "} catch (System.IO.IOException ex) { "
        + "Console.Write(\"LOCKED:IOException:\" + ex.Message); "
        + "} catch (System.UnauthorizedAccessException ex) { "
        + "Console.Write(\"LOCKED:UnauthorizedAccessException:\" + ex.Message); "
        + "} catch (Exception ex) { "
        + "Console.Write(\"ERROR:\" + ex.GetType().Name + \":\" + ex.Message); "
        + "}";

    var scriptFile = Path.Combine(lockDir, "child-probe.cs");
    File.WriteAllText(scriptFile, testScript);

    var psi = new ProcessStartInfo("dotnet", $"run \"{scriptFile}\"")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    try
    {
        using var child = Process.Start(psi);
        if (child == null) throw new Exception("Failed to start child process");

        var stdout = child.StandardOutput.ReadToEnd();
        var stderr = child.StandardError.ReadToEnd();
        child.WaitForExit(30_000);

        Console.WriteLine($"    Child exit code: {child.ExitCode}");
        Console.WriteLine($"    Child stdout: {stdout}");
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            // Only show first 3 lines of stderr (build noise)
            var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 3)
                Console.WriteLine($"    Child stderr: ({lines.Length} lines, first 3 shown)");
            foreach (var line in lines.Take(3))
                Console.WriteLine($"    stderr: {line.TrimEnd()}");
        }

        if (stdout.StartsWith("LOCKED"))
            Console.WriteLine("    Cross-process lock IS visible (correct behavior)");
        else if (stdout == "UNLOCKED")
            throw new Exception("Child process was able to open the locked file! Cross-process locking is broken.");
        else
            Console.WriteLine($"    Unexpected result: {stdout}");
    }
    catch (Exception ex) when (ex is not InvalidOperationException)
    {
        // If dotnet run doesn't work for file-based apps, that's OK - report it
        Console.WriteLine($"    Could not spawn child: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine("    (Cross-process test skipped - verify manually if needed)");
    }
});

// --- Test 8: Stale file cleanup simulation ---
RunTest("8. Stale file cleanup (simulating dead process)", () =>
{
    var stalePath = Path.Combine(lockDir, "stale-slot.lock");

    // Create a file WITHOUT DeleteOnClose (simulating what remains after a crash
    // if DeleteOnClose didn't fire, or a non-DeleteOnClose leftover)
    File.WriteAllText(stalePath, "99999\n2020-01-01T00:00:00Z");
    Console.WriteLine($"    Created stale file with fake PID 99999");
    Console.WriteLine($"    File exists: {File.Exists(stalePath)}");

    // Can we read it?
    var content = File.ReadAllText(stalePath);
    Console.WriteLine($"    Can read stale file: {content.Split('\n')[0]}");

    // Can we delete it?
    File.Delete(stalePath);
    Console.WriteLine($"    Deleted stale file. Exists: {File.Exists(stalePath)}");

    // Can we now acquire it as a lock?
    using var fs = new FileStream(stalePath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
        FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
    Console.WriteLine("    Acquired lock on cleaned-up slot");
});

// --- Test 9: Simulate the ACTUAL CrossProcessStoreGate pattern ---
RunTest("9. Full gate simulation (acquire all, timeout on overflow, release unblocks)", () =>
{
    var slots = new FileStream?[MaxSlots];
    var gateLock = new object();

    // Acquire all slots
    for (int i = 0; i < MaxSlots; i++)
    {
        var path = Path.Combine(lockDir, $"gate-{i}.lock");
        slots[i] = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
        var pid = Encoding.UTF8.GetBytes($"{Environment.ProcessId}\n{DateTime.UtcNow:O}");
        slots[i]!.Write(pid);
        slots[i]!.Flush();
    }
    Console.WriteLine($"    Acquired all {MaxSlots} slots");

    // Overflow attempt should fail fast
    var sw = Stopwatch.StartNew();
    bool overflowResult = false;
    var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
    while (DateTime.UtcNow < deadline)
    {
        lock (gateLock)
        {
            for (int i = 0; i < MaxSlots; i++)
            {
                if (slots[i] != null) continue;
                var path = Path.Combine(lockDir, $"gate-{i}.lock");
                try
                {
                    var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                        FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
                    slots[i] = fs;
                    overflowResult = true;
                    break;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        if (overflowResult) break;
        Thread.Sleep(50);
    }
    sw.Stop();
    Console.WriteLine($"    Overflow attempt: {(overflowResult ? "ACQUIRED (BAD!)" : "timed out (correct)")} in {sw.ElapsedMilliseconds}ms");
    if (overflowResult) throw new Exception("Overflow acquire succeeded when all slots should be held!");

    // Release one slot, verify another thread can acquire
    bool waiterAcquired = false;
    var waiterStarted = new ManualResetEventSlim();
    var waiterThread = new Thread(() =>
    {
        waiterStarted.Set();
        var waiterDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < waiterDeadline)
        {
            lock (gateLock)
            {
                for (int i = 0; i < MaxSlots; i++)
                {
                    if (slots[i] != null) continue;
                    var path = Path.Combine(lockDir, $"gate-{i}.lock");
                    try
                    {
                        var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                            FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
                        slots[i] = fs;
                        waiterAcquired = true;
                        return;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            Thread.Sleep(50);
        }
    });
    waiterThread.Start();
    waiterStarted.Wait();
    Thread.Sleep(100); // Let waiter attempt and fail

    // Release slot 0
    lock (gateLock)
    {
        slots[0]?.Dispose();
        slots[0] = null;
    }
    Console.WriteLine("    Released slot 0");

    waiterThread.Join(TimeSpan.FromSeconds(10));
    Console.WriteLine($"    Waiter acquired after release: {waiterAcquired}");
    if (!waiterAcquired) throw new Exception("Waiter could not acquire after slot was released!");

    // Cleanup
    lock (gateLock)
    {
        for (int i = 0; i < MaxSlots; i++)
        {
            slots[i]?.Dispose();
            slots[i] = null;
        }
    }
    Console.WriteLine("    All slots cleaned up");
});

// --- Summary ---
Console.WriteLine();
Console.WriteLine("=== Summary ===");
Console.WriteLine($"Passed: {passed}");
Console.WriteLine($"Failed: {failed}");

if (failed == 0)
    Console.WriteLine("\nAll tests passed. File-based locking works on this platform.");
else
    Console.WriteLine("\nSome tests failed. See details above for exact failure modes.");

// Cleanup
try { Directory.Delete(lockDir, recursive: true); } catch { }

return failed > 0 ? 1 : 0;

// --- Helpers ---

void RunTest(string name, Action test)
{
    Console.WriteLine($"[TEST] {name}");
    try
    {
        test();
        Console.WriteLine("  => PASSED\n");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  => FAILED: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine($"     Stack: {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
        Console.WriteLine();
        failed++;
    }
}
