#!/usr/bin/env -S dotnet
#:project ../../../src/Mercury/Mercury.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false
// How long a query waits on a SERVICE whose endpoint drops what is sent to it, and what it says (ADR-066 D5): through
// SparqlEngine.Query, with the process's connect timeout at its default, 5 s, then set to 2 s. 10.255.255.1 is an
// address no machine here answers; where the network refuses it at once instead, the probe says so.
//
//   dotnet run tools/probes/service-connect-timeout/a-service-to-an-address-that-drops.cs
using System.Diagnostics;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Storage;

var dir = Directory.CreateTempSubdirectory("mercury-probe-connect-");
try
{
    using var store = new QuadStore(Path.Combine(dir.FullName, "store"), null, null, StorageOptions.ForTesting);
    foreach (var set in new TimeSpan?[] { null, TimeSpan.FromSeconds(2) })
    {
        if (set is { } timeout)
            SparqlEngine.ServiceConnectTimeout = timeout;
        var sw = Stopwatch.StartNew();
        var result = SparqlEngine.Query(store, "SELECT * WHERE { SERVICE <http://10.255.255.1/sparql> { ?s ?p ?o } }");
        Console.WriteLine($"connect timeout {SparqlEngine.ServiceConnectTimeout.TotalSeconds} s: " +
            $"ended after {sw.Elapsed.TotalSeconds:F1} s, success {result.Success}: {result.ErrorMessage}");
    }
}
finally
{
    dir.Delete(recursive: true);
}
