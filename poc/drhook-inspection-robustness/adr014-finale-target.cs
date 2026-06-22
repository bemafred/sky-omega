#!/usr/bin/env -S dotnet
#:project ../../src/Mercury/Mercury.csproj
//
// ADR-014 finale target: drive the REAL Mercury BindingTable. A SELECT binds a 1100-char string
// literal, so BindingTable.EnsureStringCapacity hits its ADR-050 growth path with `this` = the real
// ref struct (Span<Binding> + Span<char> + scalars + char[]?). The driver breaks there and inspects
// `this` — the exact frame whose drhook_locals aborted the engine before the D1 checked-read fix.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Storage;

string dir = Path.Combine(Path.GetTempPath(), $"adr014-finale-{Environment.ProcessId}");
Directory.CreateDirectory(dir);
using var store = new QuadStore(dir);
string big = new string('x', 1100);                       // > the 1024 initial buffer → forces growth
store.BeginBatch();
store.AddCurrentBatched("<urn:s>", "<urn:p>", $"\"{big}\"");
store.CommitBatch();

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();
for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

while (true)
{
    var result = SparqlEngine.Query(store, "SELECT ?o WHERE { <urn:s> <urn:p> ?o }");
    _ = result.Rows?.Count;                               // materialize → binds ?o → EnsureStringCapacity
    Thread.Sleep(50);
}
