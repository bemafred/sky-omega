#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
// The tests of one xunit TRX report (dotnet test --report-xunit-trx), ranked by time, as Markdown: the test classes by
// the sum of their tests' durations, and the slowest tests (the README beside this file). In a parallel run a test's
// duration includes the time it waited on others; the ranking says where the time is, not what it is.
//
//   dotnet run tools/probes/runner-flush-cost/the-tests-by-time.cs -- <report.trx> [how many rows, 25]
using System.Globalization;
using System.Xml.Linq;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
XNamespace t = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
var report = XDocument.Load(args[0]);
var rows = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 25;

var classOf = report.Descendants(t + "UnitTest").ToDictionary(
    u => (string)u.Attribute("id")!,
    u => ((string)u.Element(t + "TestMethod")!.Attribute("className")!).Replace("SkyOmega.Mercury.Tests.", ""));
var results = report.Descendants(t + "UnitTestResult").Select(r => new
{
    Name = (string)r.Attribute("testName")!,
    Class = classOf[(string)r.Attribute("testId")!],
    Outcome = (string)r.Attribute("outcome")!,
    Duration = TimeSpan.Parse((string?)r.Attribute("duration") ?? "0", CultureInfo.InvariantCulture),
    Start = DateTimeOffset.Parse((string?)r.Attribute("startTime") ?? (string)r.Attribute("endTime")!, CultureInfo.InvariantCulture),
    End = DateTimeOffset.Parse((string)r.Attribute("endTime")!, CultureInfo.InvariantCulture),
}).ToList();

var sum = results.Sum(r => r.Duration.TotalSeconds);
var wall = (results.Max(r => r.End) - results.Min(r => r.Start)).TotalSeconds;
Console.WriteLine($"{results.Count} tests, {results.Count(r => r.Outcome != "Passed" && r.Outcome != "NotExecuted")} not passed. " +
                  $"First start to last end: {wall:F0} s. The tests' durations add up to {sum:F0} s.");
Console.WriteLine();
Console.WriteLine("| Test class | Tests | Their durations, s | Share |");
Console.WriteLine("|---|--:|--:|--:|");
foreach (var c in results.GroupBy(r => r.Class).Select(g => (Class: g.Key, Count: g.Count(), Seconds: g.Sum(r => r.Duration.TotalSeconds)))
             .OrderByDescending(c => c.Seconds).Take(rows))
    Console.WriteLine($"| {c.Class} | {c.Count} | {c.Seconds:F1} | {c.Seconds / sum:P0} |");
Console.WriteLine();
Console.WriteLine("| Test | Class | Duration, s |");
Console.WriteLine("|---|---|--:|");
foreach (var r in results.OrderByDescending(r => r.Duration).Take(rows))
    Console.WriteLine($"| {r.Name} | {r.Class} | {r.Duration.TotalSeconds:F1} |");
Console.WriteLine();
