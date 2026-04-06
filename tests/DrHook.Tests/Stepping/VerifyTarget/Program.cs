// Verification target for DrHook integration tests.
// Built once by VerifyTargetFixture, run via dotnet exec per test.

Console.WriteLine($"PID: {Environment.ProcessId}");

DoWork();
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
