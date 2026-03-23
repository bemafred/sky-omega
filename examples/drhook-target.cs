#!/usr/bin/env -S dotnet run --file

// DrHook — SteppingHost
//
// A menu-driven CLI host designed as an inspection target for DrHook.
// Run this, note the PID, then use DrHook to observe and step through it.
//
// Five scenarios exercising the primary bug categories:
//   1. Tight computation loop — step-pause target
//   2. Recursive Fibonacci(8) — step-into / step-out / step-break-function
//   3. Caught exception — step-break-exception
//   4. Mutable state — step-vars with object depth
//   5. Async with state mutation — stepping across await points
//
// Menu loop allows repeated runs without restarting the host.

using System.Diagnostics;

Console.Clear();

Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║  DrHook — SteppingHost                               ║");
Console.WriteLine($"║  PID: {Environment.ProcessId,-46} ║");
Console.WriteLine($"║  Runtime: {Environment.Version,-42} ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");
Console.WriteLine();

while (true)
{
    Console.WriteLine("╔══════════════════════════════════════════════════════╗");
    Console.WriteLine("║  Select a scenario:                                  ║");
    Console.WriteLine("║  [1] Tight loop (30s CPU spin)                       ║");
    Console.WriteLine("║  [2] Recursive Fibonacci(8)                          ║");
    Console.WriteLine("║  [3] Caught exception                                ║");
    Console.WriteLine("║  [4] Mutable state (build a list)                    ║");
    Console.WriteLine("║  [5] Async with state mutation                       ║");
    Console.WriteLine("║  [q] Quit                                            ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════╝");
    Console.Write("> ");

    var choice = Console.ReadKey().KeyChar.ToString();
    Console.WriteLine();

    switch (choice)
    {
        case "1":
            RunTightLoop();
            break;
        case "2":
            RunFibonacci();
            break;
        case "3":
            RunException();
            break;
        case "4":
            RunMutableState();
            break;
        case "5":
            await RunAsyncWithMutationAsync();
            break;
        case "q":
        case "Q":
            Console.WriteLine("[SteppingHost] Exiting.");
            return;
        default:
            Console.WriteLine("Invalid choice. Try again.");
            continue;
    }

    Console.WriteLine();
}

// ── Scenario 1: Tight loop ──────────────────────────────────────────────
// BREAKPOINT TARGET: line inside while loop.
// Test: step-launch → step-continue → select scenario 1 → step-pause mid-loop.
static void RunTightLoop()
{
    Console.WriteLine("[Scenario 1] Tight loop — 30 second CPU spin");
    var sw = Stopwatch.StartNew();
    long counter = 0;
    while (sw.Elapsed.TotalSeconds < 30)
    {
        counter++;                                // ← pause target
    }
    Console.WriteLine($"[Scenario 1] Complete. Iterations: {counter:N0}");
}

// ── Scenario 2: Recursive Fibonacci ─────────────────────────────────────
// BREAKPOINT TARGET: Fibonacci method or call site.
// Test: step-into Fibonacci, step-out back to caller, step-break-function.
static void RunFibonacci()
{
    Console.WriteLine("[Scenario 2] Recursive computation — Fibonacci(8)");
    var result = Fibonacci(8);                    // ← breakpoint here for stepping
    Console.WriteLine($"[Scenario 2] Complete. Fibonacci(8) = {result}");
}

static long Fibonacci(int n) => n <= 1 ? n : Fibonacci(n - 1) + Fibonacci(n - 2);

// ── Scenario 3: Exception ───────────────────────────────────────────────
// BREAKPOINT TARGET: ThrowsOnPurpose.
// Test: step-break-exception with 'all' filter, then run this scenario.
static void RunException()
{
    Console.WriteLine("[Scenario 3] Caught exception");
    try
    {
        ThrowsOnPurpose();                        // ← breakpoint here for stepping
    }
    catch (InvalidOperationException ex)
    {
        Console.WriteLine($"[Scenario 3] Caught: {ex.Message}");
    }
}

static void ThrowsOnPurpose() =>
    throw new InvalidOperationException("DrHook epistemic validation: exception deliberately raised");

// ── Scenario 4: Mutable state ───────────────────────────────────────────
// BREAKPOINT TARGET: inside the loop body.
// Test: step-vars with depth 2 to inspect Person fields inside the list.
static void RunMutableState()
{
    Console.WriteLine("[Scenario 4] Mutable state — building a list of Person records");
    var people = new List<Person>();
    people.Add(new Person("Alice", 30));          // ← breakpoint here
    people.Add(new Person("Bob", 25));
    people.Add(new Person("Carol", 42));
    Console.WriteLine($"[Scenario 4] Complete. People: {people.Count}");
    foreach (var p in people)
        Console.WriteLine($"  {p.Name}, age {p.Age}");
}

// ── Scenario 5: Async with state mutation ───────────────────────────────
// BREAKPOINT TARGET: inside SlowCountAsync.
// Test: stepping across await points — verifying DAP handles async state machines.
static async Task RunAsyncWithMutationAsync()
{
    Console.WriteLine("[Scenario 5] Async with state mutation");
    var result = await SlowCountAsync(3);         // ← breakpoint here
    Console.WriteLine($"[Scenario 5] Complete. Final count: {result}");
}

static async Task<int> SlowCountAsync(int steps)
{
    var counter = 0;
    for (var i = 0; i < steps; i++)
    {
        await Task.Delay(100);                    // ← step-next across await
        counter += i + 1;
    }
    return counter;
}

record Person(string Name, int Age);
