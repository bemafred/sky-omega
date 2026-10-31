#!/usr/bin/env -S dotnet
#:project ../../../src/Mercury/Mercury.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false

// ADR-060: what Mercury does today with (1) a regex that times out in FILTER, BIND and a SELECT expression, and
// (2) the REPLACE replacement strings where XPath's fn:replace differs from .NET. Prints; asserts nothing.
using SkyOmega.Mercury;
using SkyOmega.Mercury.Storage;

var path = Path.Combine(Path.GetTempPath(), "adr060-" + Guid.NewGuid().ToString("N"));
using (var store = new QuadStore(path))
{
    store.AddCurrent("<http://ex/s1>", "<http://ex/p>", "\"abracadabra\"", "<http://ex/g>");
    store.AddCurrent("<http://ex/s2>", "<http://ex/p>", "\"" + new string('a', 40) + "!\"", "<http://ex/g>");
    store.AddCurrent("<http://ex/s3>", "<http://ex/p>", "\"banana\"", "<http://ex/g>");

    // 1. A regex time-out (the 100 ms limit in RegexOf) inside each expression context: does the query fail, or does
    //    the time-out become an expression error that drops or unbinds one row while the query reports success?
    Console.WriteLine("1. (a+)+$ over three rows, one of them 40 a's then '!'");
    foreach (var (context, query) in new[] {
        ("FILTER", "SELECT ?s WHERE { GRAPH ?g { ?s ?p ?o FILTER(REGEX(STR(?o), \"(a+)+$\")) } }"),
        ("BIND", "SELECT ?s ?m WHERE { GRAPH ?g { ?s ?p ?o BIND(REGEX(STR(?o), \"(a+)+$\") AS ?m) } }"),
        ("SELECT expression", "SELECT ?s (REGEX(STR(?o), \"(a+)+$\") AS ?m) WHERE { GRAPH ?g { ?s ?p ?o } }"),
        ("OPTIONAL FILTER", "SELECT ?s WHERE { GRAPH ?g { ?s ?p ?o OPTIONAL { ?s ?p ?o2 FILTER(REGEX(STR(?o2), \"(a+)+$\")) } } }"),
        ("REPLACE in BIND", "SELECT ?s ?r WHERE { GRAPH ?g { ?s ?p ?o BIND(REPLACE(STR(?o), \"(a+)+$\", \"x\") AS ?r) } }") })
    {
        var result = SparqlEngine.Query(store, query);
        Console.WriteLine($"   {context}: success={result.Success} rows={result.Rows?.Count.ToString() ?? "-"} error={result.ErrorMessage ?? "-"}");
    }

    // 2. REPLACE replacement strings. The SPARQL literals below are written with SPARQL escapes ("\\$" is the two
    //    characters \$). The expected column is XPath fn:replace (F&O 3.1 §5.6.4).
    Console.WriteLine("\n2. REPLACE(\"abc\", pattern, replacement)");
    foreach (var (pattern, replacement, xpath) in new[] {
        ("b", @"\\$", "a$c"),
        ("b", @"\\\\", @"a\c"),
        ("b", "$$", "error FORX0004"),
        ("(b)", "[$1]", "a[b]c"),
        ("(b)", "${1}", "error FORX0004"),
        ("(b)", "$2", "ac"),
        ("x*", "-", "error FORX0003"),
        ("b", "$0$0", "abbc"),
        ("b", @"\\x", "error FORX0004") })
    {
        var query = $"SELECT (REPLACE(\"abc\", \"{pattern}\", \"{replacement}\") AS ?r) WHERE {{ }}";
        var result = SparqlEngine.Query(store, query);
        string got = !result.Success ? "query failed: " + result.ErrorMessage
            : result.Rows is { Count: > 0 } && result.Rows[0].TryGetValue("r", out var r) ? r
            : "(unbound)";
        Console.WriteLine($"   pattern {pattern,-4} replacement {replacement,-6} -> {got,-12} XPath: {xpath}");
    }
}
Directory.Delete(path, recursive: true);
