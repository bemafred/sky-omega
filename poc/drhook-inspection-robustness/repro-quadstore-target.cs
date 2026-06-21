#!/usr/bin/env -S dotnet
#:project ../../src/Mercury/Mercury.csproj
//
// DrHook crash-reproduction TARGET (ADR-007 — inspection at scale).
// Holds a frame (Holder.Inspect) with the LIVE, populated QuadStore graph (indexes -> BTreeFiles
// -> mmap + PageCache + pinned pointers) — the wide native-backed aggregate whose depth-2
// inspection dropped the DrHook engine. Named-class method + loop mirrors probe 18 so the line
// breakpoint binds reliably (a static local function's line mapping does not).

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Storage;

var dir = Path.Combine(Path.GetTempPath(), "drhook-repro-qs-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
using var store = new QuadStore(dir);

// Realize the storage internals (index B+Trees, mmap views, page cache, safe handles).
for (int i = 0; i < 200; i++)
    SparqlEngine.Update(store, $"INSERT DATA {{ GRAPH <urn:g> {{ <urn:s{i}> <urn:p{i % 7}> \"value-{i}-xxxxxxxx\" }} }}");
var result = SparqlEngine.Query(store, "SELECT ?s ?p ?o WHERE { GRAPH <urn:g> { ?s ?p ?o } }");

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();
for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

var holder = new Holder();
while (true)
{
    holder.Inspect(store, result);
    Thread.Sleep(50);
}

sealed class Holder
{
    public long Sink;
    // Args at the stop: arg0=this (Holder), arg1=store (QuadStore), arg2=result (QueryResult).
    public void Inspect(QuadStore store, QueryResult result)
    {
        long rows = result.Rows?.Count ?? 0;
        Sink += rows;   // QS_BP
    }
}
