#!/usr/bin/env -S dotnet
#:project ../../src/Mercury/Mercury.csproj
//
// ADR-013 finale target — drives REAL Mercury code through
//   BindingTable.Bind(ReadOnlySpan<char> variableName, ReadOnlySpan<char> value)   [BindingTable.cs:150]
// so the DrHook D3 fix can be dogfooded LIVE. It inserts a triple whose object is a LONG string literal
// (2000 chars > the 1024 initial binding buffer, so EnsureStringCapacity grows), then SELECTs it back in
// a loop. Each SELECT binds the literal at BindingTable.cs:153, where:
//   - `value`              is the literal span      → read value.Length            (D3 — span member)
//   - `this._stringOffset` is the current offset     → expand `this`               (D1/D2 — struct field)
//   - `this._stringBuffer` is a Span<char>           → expand `this`._stringBuffer → _length (D1/D2)
//
// LIVE FINALE (run via the DrHook MCP, after reconnecting it so the D3 engine loads):
//   1. dotnet build adr013-finale-bindingtable-target.cs        (build to a net10 DLL first)
//   2. drhook_launch  dotnet  exec <built dll>                  (hold-gate stops pre-main)
//   3. drhook_break_source  BindingTable.cs : 153
//   4. drhook_continue  → the breakpoint catches the string bind
//   5. drhook_locals    → read `value` (a ReadOnlySpan<char>); value.Length should be 2000
//      drhook_expand `this` → _stringOffset (int) and _stringBuffer (Span<char>) → expand → _length
//   The loop + sleep keep the process alive for repeated hits.

using System;
using System.IO;
using System.Threading;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Storage;

string dir = Path.Combine(Path.GetTempPath(), "adr013-finale-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);

string longLiteral = new string('x', 2000);   // > 1024 → forces EnsureStringCapacity to grow the buffer

try
{
    using var store = new QuadStore(dir);
    store.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", $"\"{longLiteral}\"");

    for (int i = 0; i < 30; i++)
    {
        QueryResult result = SparqlEngine.Query(store, "SELECT ?o WHERE { ?s ?p ?o }");
        string bound = "(none)";
        if (result.Success && result.Rows is { Count: > 0 } rows && rows[0].TryGetValue("o", out string? v))
            bound = v;
        Console.WriteLine($"iter {i,2}: success={result.Success} rows={result.Rows?.Count ?? 0} boundLen={bound.Length}");
        Thread.Sleep(200);
    }
}
finally
{
    try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
}
