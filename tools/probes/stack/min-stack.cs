#!/usr/bin/env -S dotnet
#:project ../../../src/Mercury/Mercury.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false

// ADR-055 Q3: run one query shape on a thread of <stackKB> KB. Exit 0 = completed, 3 = query error,
// anything else = the stack ran out. min-stack.py binary-searches the smallest stack.
using SkyOmega.Mercury;
using SkyOmega.Mercury.Storage;
// args: <stackKB> <kind> [tieredOff handled by env]
int kb = int.Parse(args[0]); string kind = args[1];
string Nest(int n) => new string('{', n) + " ?s ?p ?o " + new string('}', n);
string q = kind switch {
  "simple" => "SELECT * WHERE { ?s ?p ?o }",
  "complex" => "SELECT ?s (COUNT(?o) AS ?n) WHERE { ?s <http://ex/p> ?o OPTIONAL { ?o ?q ?r FILTER(?r != 1) } { SELECT ?s WHERE { ?s ?p ?o } LIMIT 5 } MINUS { ?s <http://ex/x> ?y } FILTER NOT EXISTS { ?s <http://ex/z> ?w } } GROUP BY ?s ORDER BY ?s LIMIT 10",
  var k when k.StartsWith("nest") => $"SELECT * WHERE {Nest(int.Parse(k[4..]))}",
  var k when k.StartsWith("optnest") => "SELECT * WHERE { ?s ?p ?o " + string.Concat(Enumerable.Repeat("OPTIONAL { ?s ?p ?o ", int.Parse(k[7..]))) + new string('}', int.Parse(k[7..])) + " }",
  "update" => "INSERT { GRAPH <http://ex/g> { ?s <http://ex/q> 1 } } WHERE { GRAPH <http://ex/g> { ?s ?p ?o OPTIONAL { ?s ?p ?o } } }",
  _ => throw new ArgumentException(kind) };
var dir = Path.Combine(Path.GetTempPath(), "q3s-" + Guid.NewGuid().ToString("N"));
using var store = new QuadStore(dir);
store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>", "<http://ex/g>");
store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");
bool ok = false; string err = "";
var t = new Thread(() => {
  if (kind == "update") { var u = SparqlEngine.Update(store, q); ok = u.Success; err = u.ErrorMessage ?? ""; }
  else { var r = SparqlEngine.Query(store, q); ok = r.Success; err = r.ErrorMessage ?? ""; }
}, kb * 1024);
t.Start(); t.Join();
if (!ok) { Console.Error.WriteLine("query failed: " + err); return 3; }
return 0;
