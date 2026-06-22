#!/usr/bin/env -S dotnet
#:project ../src/Mercury/Mercury.csproj
//
// DrHook target: reproduce the >=1023-char literal silent-drop. Insert a 1023-char literal,
// then SELECT ?o — the read-back drops the ?o binding (suspected BindingTable.Bind buffer overflow).

using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Storage;

var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "drhook-lit-" + System.Guid.NewGuid().ToString("N"));
System.IO.Directory.CreateDirectory(dir);
using var store = new QuadStore(dir);

var lit = new string('x', 1023);
SparqlEngine.Update(store, $"INSERT DATA {{ GRAPH <urn:t> {{ <urn:big> <urn:p> \"{lit}\" }} }}");
System.Console.WriteLine("inserted 1023-char literal; about to SELECT");

var r = SparqlEngine.Query(store, "SELECT ?o WHERE { GRAPH <urn:t> { <urn:big> <urn:p> ?o } }");   // BP_SELECT
int rows = r.Rows?.Count ?? 0;
int oLen = -1;
if (rows > 0) { try { oLen = r.Rows![0]["o"].Length; } catch { oLen = -3; } }
System.Console.WriteLine($"DONE rows={rows} oLen={oLen}   (oLen=-3 means the ?o binding was dropped)");
