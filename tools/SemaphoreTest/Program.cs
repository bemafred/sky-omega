// Cross-process semaphore behavior test
// Run multiple instances to verify coordination works correctly
//
// Usage:
//   dotnet run                      - Show help
//   dotnet run -- maxcount          - Test Windows max-count sharing behavior
//   dotnet run -- crossprocess [N]  - Test cross-process coordination (acquire N slots)
//   dotnet run -- hold              - Legacy: hold slots interactively

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

if (args.Length == 0)
{
    Console.WriteLine("=== Semaphore Test Tool ===");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run -- maxcount          Test Windows max-count sharing behavior");
    Console.WriteLine("  dotnet run -- crossprocess [N]  Test cross-process coordination (default N=2)");
    Console.WriteLine("  dotnet run -- hold              Hold slots interactively");
    Console.WriteLine();
    Console.WriteLine("For cross-process testing on Windows:");
    Console.WriteLine("  1. Open two terminals");
    Console.WriteLine("  2. Terminal A: dotnet run -- crossprocess 3");
    Console.WriteLine("  3. Terminal B: dotnet run -- crossprocess 3");
    Console.WriteLine("  4. B should block because A holds 3 of 4 available slots");
    return;
}

if (args[0].Equals("maxcount", StringComparison.OrdinalIgnoreCase))
{
    MaxCountTest.Run();
    return;
}

if (args[0].Equals("crossprocess", StringComparison.OrdinalIgnoreCase))
{
    CrossProcessTest.Run(args);
    return;
}

if (!args[0].Equals("hold", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine($"Unknown command: {args[0]}");
    Console.WriteLine("Run without arguments for help.");
    return;
}

const string SemaphoreName = "SkyOmega-Test-Gate";
const int MaxSlots = 3;

string GetSemaphoreName()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        return $"Global\\{SemaphoreName}";
    return SemaphoreName;
}

Console.WriteLine($"=== Cross-Process Semaphore Test ===");
Console.WriteLine($"PID: {Environment.ProcessId}");
Console.WriteLine($"Platform: {RuntimeInformation.OSDescription}");
Console.WriteLine($"Semaphore name: {GetSemaphoreName()}");
Console.WriteLine($"Max slots: {MaxSlots}");
Console.WriteLine();

// Test 1: Create or open semaphore
Console.WriteLine("Test 1: Creating/opening named semaphore...");
Semaphore sem;
try
{
    sem = new Semaphore(MaxSlots, MaxSlots, GetSemaphoreName(), out bool createdNew);
    Console.WriteLine($"  Created new: {createdNew}");
    Console.WriteLine($"  If 'false', semaphore already existed (opened with original max count)");
}
catch (Exception ex)
{
    Console.WriteLine($"  ERROR: {ex.GetType().Name}: {ex.Message}");
    return;
}

// Test 2: Acquire slots
Console.WriteLine();
Console.WriteLine("Test 2: Acquiring slots (press Ctrl+C to release and exit)...");

int acquired = 0;
for (int i = 0; i < MaxSlots + 2; i++) // Try to acquire more than max
{
    Console.Write($"  Attempting slot {i + 1}... ");

    if (sem.WaitOne(TimeSpan.FromMilliseconds(500)))
    {
        acquired++;
        Console.WriteLine($"ACQUIRED (total: {acquired})");
    }
    else
    {
        Console.WriteLine($"BLOCKED/TIMEOUT (expected if at capacity)");
        break;
    }
}

Console.WriteLine();
Console.WriteLine($"Acquired {acquired} slots. Holding them...");
Console.WriteLine("Run another instance of this program to test cross-process behavior.");
Console.WriteLine("Press Enter to release slots and exit.");

Console.ReadLine();

// Release
Console.WriteLine($"Releasing {acquired} slots...");
for (int i = 0; i < acquired; i++)
{
    try
    {
        sem.Release();
        Console.WriteLine($"  Released slot {i + 1}");
    }
    catch (SemaphoreFullException)
    {
        Console.WriteLine($"  Slot {i + 1}: Already at max (SemaphoreFullException)");
    }
}

sem.Dispose();
Console.WriteLine("Done.");
