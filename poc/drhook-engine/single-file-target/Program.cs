using System.Diagnostics;

Debugger.Break();                       // setup stop — smoke arms the source breakpoint here
Console.WriteLine($"r={Compute(7)}");

static int Compute(int seed)
{
    int doubled = seed * 2;
    return doubled + 10;                // SF_BREAK — doubled (local) is in scope = 14
}
