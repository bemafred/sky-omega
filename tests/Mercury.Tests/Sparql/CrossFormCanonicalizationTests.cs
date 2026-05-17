using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-044 Part 4 — cross-form canonicalization tests.
///
/// Validates the cross-form scenario the ADR was written to close: a literal
/// containing `\"` ingested via Turtle (canonical wrapped-decoded form, 5 bytes)
/// must be findable via a SPARQL filter / pattern literal authored with `\"`
/// (verbatim source form, 6 bytes). Before ADR-044, the substrate stored these
/// as distinct atoms and cross-form queries silently returned empty rows.
///
/// Test shape: load via Turtle (writes canonical bytes), query via SPARQL (which
/// now canonicalizes filter/pattern literals through LiteralForm.CanonicalizeContent
/// / Canonicalize). If canonicalization is wired correctly, the row is returned.
/// </summary>
[Collection("QuadStore")]
public class CrossFormCanonicalizationTests : PooledStoreTestBase
{
    public CrossFormCanonicalizationTests(QuadStorePoolFixture fixture) : base(fixture) { }

    private async Task LoadTurtle(string turtle)
    {
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(turtle));
        using var parser = new TurtleStreamParser(ms);
        await foreach (var t in parser.ParseAsync())
        {
            Store.AddCurrent(t.Subject, t.Predicate, t.Object);
        }
    }

    #region H1: paired-ingestion atom identity (Validation plan item 2)

    [Fact]
    public async Task TurtleThenSparqlInsertOfSameLogicalTriple_ProducesSingleAtomForObject()
    {
        // Turtle stores the canonical form `"a"b"` (5 bytes).
        await LoadTurtle("<http://ex/s> <http://ex/p> \"a\\\"b\" .\n");
        var afterTurtle = SparqlEngine.GetStatistics(Store);

        // SPARQL INSERT DATA the same logical triple. After ADR-044, this also
        // produces the canonical 5-byte form → same atom ID, no new atoms interned
        // for the object position.
        var insert = SparqlEngine.Update(Store,
            "INSERT DATA { <http://ex/s> <http://ex/p> \"a\\\"b\" }");
        Assert.True(insert.Success, insert.ErrorMessage);

        var afterSparql = SparqlEngine.GetStatistics(Store);

        // Atom count unchanged — the SPARQL-side `"a\"b"` canonicalizes to the same
        // 5-byte atom Turtle interned. H1 directly verified.
        Assert.Equal(afterTurtle.AtomCount, afterSparql.AtomCount);
    }

    #endregion

    #region H4: cross-form FILTER (Validation plan item 3)

    [Fact]
    public async Task TurtleInsert_SparqlFilterEquality_FindsTheRow()
    {
        await LoadTurtle("<http://ex/s> <http://ex/p> \"a\\\"b\" .\n");

        var query = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { ?s <http://ex/p> ?o FILTER(?o = \"a\\\"b\") }");

        Assert.True(query.Success, query.ErrorMessage);
        Assert.Single(query.Rows!);
    }

    [Fact]
    public async Task TurtleInsert_SparqlFilterContains_FindsTheRow()
    {
        await LoadTurtle("<http://ex/s> <http://ex/p> \"prefix \\\"escaped\\\" needle\" .\n");

        var query = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { ?s <http://ex/p> ?o FILTER(CONTAINS(?o, \"\\\"escaped\\\" needle\")) }");

        Assert.True(query.Success, query.ErrorMessage);
        Assert.Single(query.Rows!);
    }

    [Fact]
    public async Task TurtleInsert_SparqlFilterStrStarts_FindsTheRow()
    {
        await LoadTurtle("<http://ex/s> <http://ex/p> \"\\\"opening quote inside\" .\n");

        var query = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { ?s <http://ex/p> ?o FILTER(STRSTARTS(?o, \"\\\"opening\")) }");

        Assert.True(query.Success, query.ErrorMessage);
        Assert.Single(query.Rows!);
    }

    [Fact]
    public async Task TurtleInsert_SparqlFilterStrEnds_FindsTheRow()
    {
        await LoadTurtle("<http://ex/s> <http://ex/p> \"tail with quote\\\"\" .\n");

        var query = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { ?s <http://ex/p> ?o FILTER(STRENDS(?o, \"quote\\\"\")) }");

        Assert.True(query.Success, query.ErrorMessage);
        Assert.Single(query.Rows!);
    }

    [Fact]
    public async Task TurtleInsert_SparqlFilterRegex_FindsTheRow()
    {
        await LoadTurtle("<http://ex/s> <http://ex/p> \"middle \\\"X\\\" middle\" .\n");

        var query = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { ?s <http://ex/p> ?o FILTER(REGEX(?o, \"\\\"X\\\"\")) }");

        Assert.True(query.Success, query.ErrorMessage);
        Assert.Single(query.Rows!);
    }

    [Fact]
    public async Task TurtleInsert_UnicodeEscapeFilter_FindsViaEquivalentBackslashQuote()
    {
        // Turtle source: literal contains the unicode-escape form " (= "), decoded by
        // the Turtle parser to a raw " in the stored atom.
        await LoadTurtle("<http://ex/s> <http://ex/p> \"a\\u0022b\" .\n");

        // SPARQL filter uses the \"-escape form for the same logical char. Both canonicalize
        // to the same 3-char content `a"b`.
        var query = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { ?s <http://ex/p> ?o FILTER(?o = \"a\\\"b\") }");

        Assert.True(query.Success, query.ErrorMessage);
        Assert.Single(query.Rows!);
    }

    #endregion

    #region Cross-form pattern (Validation plan item 4)

    [Fact]
    public async Task TurtleInsert_SparqlPatternWithEscapeLiteralInObjectPosition_FindsTheRow()
    {
        await LoadTurtle("<http://ex/s> <http://ex/p> \"a\\\"b\" .\n");

        var query = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { ?s <http://ex/p> \"a\\\"b\" }");

        Assert.True(query.Success, query.ErrorMessage);
        Assert.Single(query.Rows!);
    }

    #endregion

    #region Cross-form DELETE WHERE (Validation plan item 5)

    [Fact]
    public async Task TurtleInsert_SparqlDeleteWhereWithEscapeLiteral_DeletesTheRow()
    {
        await LoadTurtle("<http://ex/s> <http://ex/p> \"a\\\"b\" .\n");

        var before = SparqlEngine.Query(Store, "SELECT * WHERE { ?s ?p ?o }");
        Assert.Single(before.Rows!);

        var delete = SparqlEngine.Update(Store,
            "DELETE WHERE { <http://ex/s> <http://ex/p> \"a\\\"b\" }");
        Assert.True(delete.Success, delete.ErrorMessage);

        var after = SparqlEngine.Query(Store, "SELECT * WHERE { ?s ?p ?o }");
        Assert.Empty(after.Rows!);
    }

    #endregion

    #region BIND (Validation plan item 6)

    [Fact]
    public async Task TurtleInsert_SparqlBindEscapeLiteral_BoundValueMatchesStoredAtom()
    {
        await LoadTurtle("<http://ex/s> <http://ex/p> \"a\\\"b\" .\n");

        // BIND a literal with escape syntax, then FILTER for equality to the stored value.
        // If BIND canonicalization is wired correctly, ?x and ?o both have canonical form.
        var query = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { ?s <http://ex/p> ?o BIND(\"a\\\"b\" AS ?x) FILTER(?o = ?x) }");

        Assert.True(query.Success, query.ErrorMessage);
        Assert.Single(query.Rows!);
    }

    #endregion

    #region Uncertainty-surface tests (Validation plan item 11)

    // These cover paths not directly verified during the rev 3 surface analysis.
    // A failure here surfaces a missed materialization site that needs the same
    // canonicalization gate as the verified sites.

    [Fact]
    public async Task TurtleInsert_SparqlSubqueryWithEscapeLiteralFilter_FindsTheRow()
    {
        await LoadTurtle("<http://ex/s> <http://ex/p> \"a\\\"b\" .\n");

        var query = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { { SELECT ?s ?o WHERE { ?s <http://ex/p> ?o FILTER(?o = \"a\\\"b\") } } }");

        Assert.True(query.Success, query.ErrorMessage);
        Assert.Single(query.Rows!);
    }

    [Fact]
    public async Task TurtleInsert_SparqlSubqueryWithEscapeLiteralInPattern_FindsTheRow()
    {
        await LoadTurtle("<http://ex/s> <http://ex/p> \"a\\\"b\" .\n");

        var query = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { { SELECT ?s WHERE { ?s <http://ex/p> \"a\\\"b\" } } }");

        Assert.True(query.Success, query.ErrorMessage);
        Assert.Single(query.Rows!);
    }

    [Fact]
    public async Task TurtleInsert_SparqlAskWithEscapeLiteral_ReturnsTrue()
    {
        await LoadTurtle("<http://ex/s> <http://ex/p> \"a\\\"b\" .\n");

        var query = SparqlEngine.Query(Store,
            "ASK { ?s <http://ex/p> \"a\\\"b\" }");

        Assert.True(query.Success, query.ErrorMessage);
        Assert.True(query.AskResult);
    }

    [Fact]
    public async Task TurtleInsert_SparqlConstructWithEscapeLiteralPattern_ProducesTheTriple()
    {
        await LoadTurtle("<http://ex/s> <http://ex/p> \"a\\\"b\" .\n");

        var query = SparqlEngine.Query(Store,
            "CONSTRUCT { ?s <http://ex/derived> \"yes\" } WHERE { ?s <http://ex/p> \"a\\\"b\" }");

        Assert.True(query.Success, query.ErrorMessage);
        Assert.NotNull(query.Triples);
        Assert.Single(query.Triples);
    }

    #endregion
}
