// Verification target for DrHook integration tests.
// Built once by VerifyTargetFixture, run via dotnet exec per test.

using System.Diagnostics;

Console.WriteLine($"PID: {Environment.ProcessId}");

DoWork();
ConditionalStop();
ThrowAndCatch();
ObjectInspection();

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

static void ConditionalStop()
{
    // Pattern 1: Unconditional breakpoint inside code-level if — no func-eval
    for (var i = 1; i <= 5; i++)
    {
        if (i == 3)
            Console.WriteLine($"  conditional stop at i={i}"); // ← breakpoint target
    }

    // Pattern 2: Debugger.Break() — target breaks itself, no breakpoint needed
    for (var j = 1; j <= 5; j++)
    {
        if (j == 4)
            Debugger.Break(); // ← triggers stopped event in DAP
    }
}

static void ThrowAndCatch()
{
    try { throw new InvalidOperationException("verify-exception"); }
    catch (InvalidOperationException) { /* caught */ }
}

static void ObjectInspection()
{
    var person = new Person("Alice", 30);
    var greeting = person.Name;
    Console.WriteLine($"  {greeting}, age {person.Age}");
}

record Person(string Name, int Age);
