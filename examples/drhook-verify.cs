#!/usr/bin/env -S dotnet

// Minimal verification target for DrHook stepping.
// No gates or waits — DrHook owns the lifecycle via drhook_step_run.

Console.WriteLine($"PID: {Environment.ProcessId}");

DoWork();

Console.WriteLine("Done.");

static void DoWork()
{
    var sum = 0;
    for (var i = 1; i <= 5; i++)
    {
        sum += i;
        Console.WriteLine($"  i={i}, sum={sum}");
    }
    Console.WriteLine($"Final sum: {sum}");
}
