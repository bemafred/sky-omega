// Test: Verify that the SINGLETON provides real cross-process coordination
// This simulates what happens with NCrunch parallel test runners

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

class CrossProcessTest
{
    // Simulates CrossProcessStoreGate.Instance behavior
    const string SharedSemaphoreName = "SkyOmega-QuadStore-Gate-v1";
    const int MaxSlots = 4; // Fixed for testing

    public static void Run(string[] args)
    {
        string semName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"Global\\{SharedSemaphoreName}"
            : SharedSemaphoreName;

        Console.WriteLine("=== Cross-Process Coordination Test ===");
        Console.WriteLine($"PID: {Environment.ProcessId}");
        Console.WriteLine($"Semaphore: {semName}");
        Console.WriteLine($"Max slots: {MaxSlots}");
        Console.WriteLine();

        // Check if named semaphores are supported
        Semaphore sem;
        try
        {
            sem = new Semaphore(MaxSlots, MaxSlots, semName, out bool createdNew);
            Console.WriteLine($"Semaphore created new: {createdNew}");
            if (!createdNew)
            {
                Console.WriteLine("  (Opened existing semaphore from another process)");
            }
        }
        catch (PlatformNotSupportedException)
        {
            Console.WriteLine("Named semaphores not supported on this platform.");
            Console.WriteLine("CrossProcessStoreGate would use file-based locking instead.");
            return;
        }

        // Parse how many slots to acquire
        int slotsToAcquire = 2;
        if (args.Length > 1 && int.TryParse(args[1], out int n))
            slotsToAcquire = n;

        Console.WriteLine();
        Console.WriteLine($"Attempting to acquire {slotsToAcquire} slots...");

        int acquired = 0;
        for (int i = 0; i < slotsToAcquire; i++)
        {
            Console.Write($"  Slot {i + 1}: ");
            if (sem.WaitOne(TimeSpan.FromSeconds(10)))
            {
                acquired++;
                Console.WriteLine("ACQUIRED");
            }
            else
            {
                Console.WriteLine("BLOCKED (another process holds slots)");
                break;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Holding {acquired} slots.");

        // Check if running interactively or from integration test
        bool interactive = !Console.IsInputRedirected;

        if (interactive)
        {
            Console.WriteLine();
            Console.WriteLine("=== TO TEST CROSS-PROCESS COORDINATION ===");
            Console.WriteLine("1. Run this in Terminal A: dotnet run -- crossprocess 3");
            Console.WriteLine("2. While A is running, run in Terminal B: dotnet run -- crossprocess 3");
            Console.WriteLine("3. Terminal B should BLOCK because A holds 3 of 4 slots");
            Console.WriteLine();
            Console.WriteLine("Press Enter to release slots and exit...");
            Console.ReadLine();
        }
        else
        {
            // Non-interactive: wait briefly then exit
            Console.WriteLine("(Non-interactive mode - waiting for input or timeout)");
            // Wait for parent to send newline or timeout
            var readTask = Task.Run(() => Console.ReadLine());
            readTask.Wait(TimeSpan.FromSeconds(3));
        }

        Console.WriteLine($"Releasing {acquired} slots...");
        for (int i = 0; i < acquired; i++)
        {
            sem.Release();
        }

        sem.Dispose();
        Console.WriteLine("Done.");
    }
}
