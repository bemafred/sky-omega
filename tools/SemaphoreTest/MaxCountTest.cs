// Test: What happens when two processes create semaphores with different max counts?
// This specifically tests the Windows behavior where opening an existing semaphore
// ignores the max count parameter.

using System;
using System.Runtime.InteropServices;
using System.Threading;

class MaxCountTest
{
    public static void Run()
    {
        Console.WriteLine("=== Max Count Behavior Test ===");
        Console.WriteLine($"Platform: {RuntimeInformation.OSDescription}");
        Console.WriteLine();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.WriteLine("NOTE: Named semaphores are only fully supported on Windows.");
            Console.WriteLine("On Unix/macOS, .NET throws PlatformNotSupportedException.");
            Console.WriteLine("The CrossProcessStoreGate falls back to file-based locking on Unix.");
            Console.WriteLine();
            Console.WriteLine("To test the Windows behavior, run this on Windows.");
            return;
        }

        const string baseName = "SkyOmega-MaxCount-Test";
        string semName = $"Global\\{baseName}";

        Console.WriteLine($"Testing semaphore: {semName}");
        Console.WriteLine();

        // Step 1: Create with max=5
        Console.WriteLine("Step 1: Creating semaphore with initial=5, max=5...");
        var sem1 = new Semaphore(5, 5, semName, out bool created1);
        Console.WriteLine($"  Created new: {created1}");

        // Acquire 3 slots
        sem1.WaitOne(); sem1.WaitOne(); sem1.WaitOne();
        Console.WriteLine("  Acquired 3 slots (2 remaining in sem1)");

        // Step 2: "Create" with max=2 (same name)
        Console.WriteLine();
        Console.WriteLine("Step 2: Creating semaphore with initial=2, max=2 (SAME NAME)...");
        var sem2 = new Semaphore(2, 2, semName, out bool created2);
        Console.WriteLine($"  Created new: {created2}");

        // Try to acquire through sem2
        Console.WriteLine();
        Console.WriteLine("Step 3: Trying to acquire through sem2...");
        int acquiredViaSem2 = 0;
        for (int i = 0; i < 5; i++)
        {
            if (sem2.WaitOne(TimeSpan.FromMilliseconds(100)))
            {
                acquiredViaSem2++;
                Console.WriteLine($"  Acquired slot {acquiredViaSem2} via sem2");
            }
            else
            {
                Console.WriteLine($"  Blocked on attempt {i + 1}");
                break;
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== RESULT ===");
        if (created2)
        {
            Console.WriteLine("Platform creates NEW semaphore even with same name");
            Console.WriteLine("(This would be unusual behavior)");
        }
        else if (acquiredViaSem2 > 2)
        {
            Console.WriteLine("CONFIRMED: sem2 opened EXISTING semaphore with original max=5");
            Console.WriteLine("The max=2 parameter was IGNORED");
            Console.WriteLine("This is the Windows behavior that causes the test failure!");
        }
        else
        {
            Console.WriteLine("sem2 appears to respect max=2");
            Console.WriteLine("(This might indicate platform-specific behavior)");
        }

        // Cleanup
        Console.WriteLine();
        Console.WriteLine("Releasing all slots...");
        for (int i = 0; i < 3 + acquiredViaSem2; i++)
        {
            try { sem1.Release(); }
            catch (SemaphoreFullException) { }
        }

        sem1.Dispose();
        sem2.Dispose();
        Console.WriteLine("Done.");
    }
}
