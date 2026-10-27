#!/usr/bin/env -S dotnet
#:project ../../../src/Mercury/Mercury.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false

// ADR-055 Q3: the size of each SPARQL parse struct (internal types, read by reflection).
using System.Runtime.CompilerServices;
var asm = typeof(SkyOmega.Mercury.SparqlEngine).Assembly;
var sizeOf = typeof(Unsafe).GetMethod("SizeOf", Type.EmptyTypes)!;
foreach (var n in new[] { "Types.Query", "Types.WhereClause", "Types.GraphPattern", "Types.SubSelect", "Types.SelectClause",
    "Types.SolutionModifier", "Types.Prologue", "Types.GraphClause", "Types.ServiceClause", "Types.ExistsFilter",
    "Types.TriplePattern", "Types.FilterExpr", "Types.BindExpr", "Types.AggregateExpression", "Types.ConstructTemplate",
    "Types.ValuesClause", "Types.UpdateOperation", "Types.LimitOffsetClause", "Types.OrderByClause", "Types.GroupByClause",
    "Types.Term", "Types.PropertyPath", "Execution.QueryResults", "Patterns.PatternSlot" }) {
  var t = asm.GetType("SkyOmega.Mercury.Sparql." + n);
  if (t == null) { Console.WriteLine($"{n,-28} (not found)"); continue; }
  if (t.IsByRefLike) { Console.WriteLine($"{n,-28} ref struct (size via fields not measured)"); continue; }
  Console.WriteLine($"{n,-28} {(int)sizeOf.MakeGenericMethod(t).Invoke(null, null)!,8:N0} bytes");
}
