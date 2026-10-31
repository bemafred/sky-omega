#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
#:property PublishAot=false

// ADR-060: facts about .NET's Regex that the query time-limit design depends on. Prints what it sees; asserts nothing.
using System.Diagnostics;
using System.Text.RegularExpressions;

Console.WriteLine($".NET {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}, {Environment.ProcessorCount} cores");

// 1. What (a+)+$ costs on n a's followed by '!' (a near match), with the interpreter Mercury uses (RegexOptions.None).
//    The 60 s bound only keeps the probe finite; the loop stops once one input takes longer than 10 s.
Console.WriteLine("\n1. (a+)+$ on n a's then '!'");
foreach (var n in new[] { 16, 18, 20, 22, 24, 26, 28, 30, 32 })
{
    var regex = new Regex("(a+)+$", RegexOptions.None, TimeSpan.FromSeconds(60));
    var input = new string('a', n) + "!";
    var sw = Stopwatch.StartNew();
    try
    {
        var matched = regex.IsMatch(input);
        Console.WriteLine($"   n={n}: matched={matched} after {sw.Elapsed.TotalMilliseconds:F1} ms");
    }
    catch (RegexMatchTimeoutException)
    {
        Console.WriteLine($"   n={n}: timed out after {sw.Elapsed.TotalMilliseconds:F1} ms");
    }
    if (sw.Elapsed > TimeSpan.FromSeconds(10))
        break;
}

// 2. How long a match runs past its time limit before RegexMatchTimeoutException (n = 40, far beyond any limit here).
Console.WriteLine("\n2. elapsed until RegexMatchTimeoutException, (a+)+$ on 40 a's then '!'");
foreach (var limit in new[] { TimeSpan.FromTicks(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100) })
{
    var regex = new Regex("(a+)+$", RegexOptions.None, limit);
    var sw = Stopwatch.StartNew();
    try
    {
        regex.IsMatch(new string('a', 40) + "!");
        Console.WriteLine($"   limit {limit.TotalMilliseconds} ms: completed after {sw.Elapsed.TotalMilliseconds:F3} ms");
    }
    catch (RegexMatchTimeoutException)
    {
        Console.WriteLine($"   limit {limit.TotalMilliseconds} ms: timed out after {sw.Elapsed.TotalMilliseconds:F3} ms");
    }
}

// 3. Which time limits the constructor accepts.
Console.WriteLine("\n3. Regex(pattern, options, matchTimeout) accepts");
foreach (var (name, limit) in new[] {
    ("zero", TimeSpan.Zero),
    ("one tick", TimeSpan.FromTicks(1)),
    ("24 days", TimeSpan.FromDays(24)),
    ("int.MaxValue ms", TimeSpan.FromMilliseconds(int.MaxValue)),
    ("int.MaxValue ms + 1 ms", TimeSpan.FromMilliseconds((long)int.MaxValue + 1)),
    ("25 days", TimeSpan.FromDays(25)),
    ("InfiniteMatchTimeout", Regex.InfiniteMatchTimeout) })
{
    try
    {
        _ = new Regex("a", RegexOptions.None, limit);
        Console.WriteLine($"   {name}: accepted");
    }
    catch (ArgumentOutOfRangeException)
    {
        Console.WriteLine($"   {name}: ArgumentOutOfRangeException");
    }
}

// 4. Whether any public constructor takes a clock (TimeProvider).
Console.WriteLine("\n4. public Regex constructors");
foreach (var ctor in typeof(Regex).GetConstructors())
    Console.WriteLine($"   Regex({string.Join(", ", ctor.GetParameters().Select(p => p.ParameterType.Name))})");

// 5. .NET replacement syntax: Regex.Replace(input, replacement) against Replace with a MatchEvaluator calling
//    Match.Result(replacement), on the cases where XPath's fn:replace differs from .NET.
Console.WriteLine("\n5. Replace(input, r) vs Replace(input, m => m.Result(r)), input \"abc\"");
foreach (var (pattern, replacement) in new[] {
    ("b", @"\$"), ("b", @"\\"), ("b", "$$"), ("(b)", "[$1]"), ("(b)", "${1}"), ("(b)", "$2"), ("x*", "-"), ("b", "$0$0"), ("b", @"\x") })
{
    var regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
    var direct = regex.Replace("abc", replacement);
    var evaluated = regex.Replace("abc", m => m.Result(replacement));
    Console.WriteLine($"   pattern {pattern,-4} replacement {replacement,-5} -> {direct,-8} | evaluator {evaluated,-8} {(direct == evaluated ? "same" : "DIFFERENT")}");
}
