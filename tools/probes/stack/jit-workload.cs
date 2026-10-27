#!/usr/bin/env -S dotnet
#:project ../../../src/Mercury/Mercury.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false

// ADR-055 Q3: a parse + execute workload to run under DOTNET_JitDisasm='*'; frames.py reads the listing.
using SkyOmega.Mercury;
using SkyOmega.Mercury.Storage;
using var store = new QuadStore(Path.Combine(Path.GetTempPath(), "q3-" + Guid.NewGuid().ToString("N")));
store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>", "<http://ex/g>");
foreach (var q in new[] {
  "SELECT * WHERE { ?s ?p ?o }",
  "SELECT ?s (COUNT(?o) AS ?n) WHERE { ?s <http://ex/p> ?o OPTIONAL { ?o ?q ?r FILTER(?r != 1) } { SELECT ?s WHERE { ?s ?p ?o } LIMIT 5 } MINUS { ?s <http://ex/x> ?y } FILTER NOT EXISTS { ?s <http://ex/z> ?w } } GROUP BY ?s ORDER BY ?s LIMIT 10",
  "SELECT * WHERE { { ?s ?p ?o } UNION { GRAPH ?g { ?s ?p ?o } } }",
  "ASK { GRAPH ?g { ?s <http://ex/p>/<http://ex/q>* ?o } }",
  "CONSTRUCT { ?s ?p ?o } WHERE { GRAPH ?g { ?s ?p ?o } }" })
{ var r = SparqlEngine.Query(store, q); Console.WriteLine($"{r.Success} {r.ErrorMessage}"); }
SparqlEngine.Update(store, "INSERT { GRAPH <http://ex/g> { ?s <http://ex/q> 1 } } WHERE { GRAPH <http://ex/g> { ?s ?p ?o } }");
