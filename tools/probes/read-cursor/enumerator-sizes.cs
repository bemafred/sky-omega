#!/usr/bin/env -S dotnet
#:project ../../../src/Mercury/Mercury.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false

// ADR-062 Step 2: the size of the read's enumerators as they are before the cursor.
// Question: how many bytes does a read carry by value today? TemporalResultEnumerator holds one enumerator per
// profile, all four by value, and three flags (ADR-062, Context). The cursor of D3 is measured against these numbers.
// Construction: the types are read by reflection from the Mercury assembly and their size taken with
// Unsafe.SizeOf. No store is opened, no file is written, nothing is asserted.
using System.Runtime.CompilerServices;

var assembly = typeof(SkyOmega.Mercury.SparqlEngine).Assembly;
var sizeOf = typeof(Unsafe).GetMethod("SizeOf", Type.EmptyTypes)!;

foreach (var name in new[]
{
    "SkyOmega.Mercury.Storage.TemporalResultEnumerator",                        // what every profile's read returns
    "SkyOmega.Mercury.Storage.TemporalQuadIndex+TemporalQuadEnumerator",        // Cognitive
    "SkyOmega.Mercury.Storage.VersionedQuadIndex+VersionedQuadEnumerator",      // Graph
    "SkyOmega.Mercury.Storage.ReferenceQuadIndex+ReferenceQuadEnumerator",      // Reference
    "SkyOmega.Mercury.Storage.MinimalQuadIndex+MinimalQuadEnumerator",          // Minimal
    "SkyOmega.Mercury.Storage.NamedGraphEnumerator",                            // two enumerators and a mode (Q5)
    "SkyOmega.Mercury.Sparql.Execution.MergedGraphEnumerator",                  // the default graph as a merge
    "SkyOmega.Mercury.Storage.ResolvedTemporalQuad",                            // the result every profile yields
    "SkyOmega.Mercury.Storage.TemporalQuad",
})
{
    var type = assembly.GetType(name);
    if (type is null) { Console.WriteLine($"{name,-72} (not found)"); continue; }
    try
    {
        var bytes = (int)sizeOf.MakeGenericMethod(type).Invoke(null, null)!;
        Console.WriteLine($"{name,-72} {bytes,6:N0} bytes{(type.IsByRefLike ? "  (ref struct)" : "")}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"{name,-72} (size not taken: {e.GetType().Name})");
    }
}

Console.WriteLine();
Console.WriteLine($"runtime {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
