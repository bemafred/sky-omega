// N3PatchParserTests.cs
// Tests for N3 Patch parser.

using System.Text;
using SkyOmega.Mercury.Solid.N3;
using SkyOmega.Mercury.Tests.Rdf;
using Xunit;

namespace SkyOmega.Mercury.Solid.Tests;

public class N3PatchParserTests
{
    [Fact]
    public async Task ParseAsync_PatchStartingWithAByteOrderMark_IsThePatchAfterIt()
    {
        // A document may begin with a UTF-8 byte order mark, which the parser skips.
        var patch = "\uFEFF@prefix solid: <http://www.w3.org/ns/solid/terms#>.\n" +
                    "_:patch <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> solid:InsertDeletePatch;\n" +
                    "  solid:inserts { <http://e/s> <http://e/p> <http://e/o>. }.\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(patch));
        using var parser = new N3PatchParser(stream);
        var result = await parser.ParseAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(result.Inserts);
        Assert.Single(result.Inserts.Patterns);
        Assert.Equal("http://e/s", result.Inserts.Patterns[0].Subject.Value);
    }

    [Fact]
    public async Task ParseAsync_PatchWrittenWithA_ReadsItsClauses()
    {
        // The form of the Solid N3 Patch specification's examples. 'a' was read as a prefixed name, so the patch
        // resource was never found and every clause was dropped.
        var patch = "@prefix solid: <http://www.w3.org/ns/solid/terms#>.\n" +
                    "@prefix ex: <http://www.example.org/terms#>.\n" +
                    "_:rename a solid:InsertDeletePatch;\n" +
                    "  solid:where   { ?person ex:familyName \"Garcia\". };\n" +
                    "  solid:inserts { ?person ex:givenName \"Alex\". };\n" +
                    "  solid:deletes { ?person ex:givenName \"Claudia\". }.\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(patch));
        using var parser = new N3PatchParser(stream);
        var result = await parser.ParseAsync(TestContext.Current.CancellationToken);

        Assert.Equal("http://www.example.org/terms#familyName", Assert.Single(result.Where!.Patterns).Predicate.Value);
        Assert.Equal("Alex", Assert.Single(result.Inserts!.Patterns).Object.Value);
        Assert.Equal("Claudia", Assert.Single(result.Deletes!.Patterns).Object.Value);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ParseAsync_SmallReads_GiveTheSamePatch(int bytesPerRead)
    {
        // A request body may arrive in reads shorter than asked for. Until 2026-10-30 the parser read its buffer
        // directly and refilled only between statements, so a term split by a read was cut short.
        var ct = TestContext.Current.CancellationToken;
        var patch = "@prefix solid: <http://www.w3.org/ns/solid/terms#>.\n" +
                    "PREFIX ex: <http://www.example.org/terms#>\n" +
                    "_:rename a solid:InsertDeletePatch;\n" +
                    "  solid:where   { ?person ex:familyName \"García\". };\n" +
                    "  solid:inserts { ?person ex:givenName \"Alex 😀\"@es; ex:knows <http://e/b>. };\n" +
                    "  solid:deletes { ?person ex:givenName \"Claudia\". }.\n";

        using var whole = new N3PatchParser(new MemoryStream(Encoding.UTF8.GetBytes(patch)));
        var expected = await whole.ParseAsync(ct);
        using var slow = new N3PatchParser(new SlowReadStream(new MemoryStream(Encoding.UTF8.GetBytes(patch)), bytesPerRead));
        var actual = await slow.ParseAsync(ct);

        Assert.Equal("García", Assert.Single(expected.Where!.Patterns).Object.Value);
        Assert.Equal(new[] { "Alex 😀", "http://e/b" }, expected.Inserts!.Patterns.Select(t => t.Object.Value));
        Assert.Equal("Claudia", Assert.Single(expected.Deletes!.Patterns).Object.Value);
        // The patterns are value types, compared term by term: value, type, datatype and language.
        Assert.Equal(expected.Where.Patterns, actual.Where!.Patterns);
        Assert.Equal(expected.Inserts.Patterns, actual.Inserts!.Patterns);
        Assert.Equal(expected.Deletes.Patterns, actual.Deletes!.Patterns);
    }

    [Fact]
    public async Task ParseAsync_NonAsciiText_IsDecodedFromUtf8()
    {
        // The parser read each byte as a character, so "café" was "cafÃ©".
        var patch = "@prefix solid: <http://www.w3.org/ns/solid/terms#>.\n" +
                    "@prefix ex: <http://www.example.org/>.\n" +
                    "_:p a solid:InsertDeletePatch; solid:inserts { <http://e/s> ex:éléphant \"café\". }.\n";

        using var parser = new N3PatchParser(new MemoryStream(Encoding.UTF8.GetBytes(patch)));
        var inserted = Assert.Single((await parser.ParseAsync(TestContext.Current.CancellationToken)).Inserts!.Patterns);

        Assert.Equal("http://www.example.org/éléphant", inserted.Predicate.Value);
        Assert.Equal("café", inserted.Object.Value);
    }

    [Theory]
    [InlineData("%\n")]
    [InlineData(" @prefix solid: <http://www.w3.org/ns/solid/terms#>.\n")]
    public async Task ParseAsync_CharacterThatStartsNoTerm_IsAParseError(string patch)
    {
        // Before 2026-10-30 such a body made the parser loop forever, so one PATCH request held a server thread. The
        // time limit makes a regression a failure rather than a hung run.
        var ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(patch));
        using var parser = new N3PatchParser(stream);

        var parse = Task.Run(() => parser.ParseAsync(ct), ct);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => parse.WaitAsync(TimeSpan.FromSeconds(10), ct));

        Assert.Contains("Unexpected character", error.Message);
    }

    [Theory]
    [InlineData("_:p <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://www.w3.org/ns/solid/terms#InsertDeletePatch", "Unterminated IRI")]
    [InlineData("_:p <http://e/p> \"no closing quote", "Unterminated literal")]
    [InlineData("_:p <http://e/p> \"ends with an escaped quote\\\"", "Unterminated literal")]
    public async Task ParseAsync_TermCutOffByTheEndOfInput_IsAParseError(string patch, string expected)
    {
        // An IRI or a literal that reaches the end of the input was returned as read so far.
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(patch));
        using var parser = new N3PatchParser(stream);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            parser.ParseAsync(TestContext.Current.CancellationToken));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task ParseAsync_EmptyPatch_ReturnsEmptyResult()
    {
        // Arrange - a patch without where/deletes/inserts
        var patch = """
            @prefix solid: <http://www.w3.org/ns/solid/terms#>.
            _:patch a solid:InsertDeletePatch.
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(patch));
        using var parser = new N3PatchParser(stream);

        // Act
        var result = await parser.ParseAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void N3Term_Iri_CreatesCorrectly()
    {
        var term = N3Term.Iri("http://example.com/test");
        Assert.Equal("http://example.com/test", term.Value);
        Assert.Equal(N3TermType.Iri, term.Type);
        Assert.True(term.IsGround);
        Assert.False(term.IsVariable);
    }

    [Fact]
    public void N3Term_Variable_CreatesCorrectly()
    {
        var term = N3Term.Variable("x");
        Assert.Equal("x", term.Value);
        Assert.Equal(N3TermType.Variable, term.Type);
        Assert.False(term.IsGround);
        Assert.True(term.IsVariable);
    }

    [Fact]
    public void N3Term_Literal_CreatesCorrectly()
    {
        var term = N3Term.Literal("hello world");
        Assert.Equal("hello world", term.Value);
        Assert.Equal(N3TermType.Literal, term.Type);
        Assert.True(term.IsGround);
    }

    [Fact]
    public void N3Term_LiteralWithLanguage_CreatesCorrectly()
    {
        var term = N3Term.Literal("hej", language: "sv");
        Assert.Equal("hej", term.Value);
        Assert.Equal("sv", term.Language);
        Assert.Null(term.Datatype);
    }

    [Fact]
    public void N3Term_LiteralWithDatatype_CreatesCorrectly()
    {
        var term = N3Term.Literal("42", datatype: "http://www.w3.org/2001/XMLSchema#integer");
        Assert.Equal("42", term.Value);
        Assert.Equal("http://www.w3.org/2001/XMLSchema#integer", term.Datatype);
        Assert.Null(term.Language);
    }

    [Fact]
    public void N3Term_BlankNode_CreatesCorrectly()
    {
        var term = N3Term.BlankNode("b1");
        Assert.Equal("b1", term.Value);
        Assert.Equal(N3TermType.BlankNode, term.Type);
        Assert.True(term.IsGround);
    }

    [Fact]
    public void N3Term_ToRdfString_FormatsCorrectly()
    {
        Assert.Equal("<http://example.com>", N3Term.Iri("http://example.com").ToRdfString());
        Assert.Equal("\"hello\"", N3Term.Literal("hello").ToRdfString());
        Assert.Equal("\"hej\"@sv", N3Term.Literal("hej", language: "sv").ToRdfString());
        Assert.Equal("\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>",
            N3Term.Literal("42", datatype: "http://www.w3.org/2001/XMLSchema#integer").ToRdfString());
        Assert.Equal("_:b1", N3Term.BlankNode("b1").ToRdfString());
    }

    [Fact]
    public void N3TriplePattern_ToString_FormatsCorrectly()
    {
        var pattern = new N3TriplePattern(
            N3Term.Variable("s"),
            N3Term.Iri("http://example.com/p"),
            N3Term.Literal("value"));

        var str = pattern.ToString();
        Assert.Contains("?s", str);
        Assert.Contains("http://example.com/p", str);
        Assert.Contains("value", str);
    }

    [Fact]
    public void N3Formula_Empty_HasNoVariables()
    {
        var formula = N3Formula.Empty;
        Assert.Empty(formula.Patterns);
        Assert.Empty(formula.Variables);
    }

    [Fact]
    public void N3Formula_WithVariables_TracksVariables()
    {
        var patterns = new[]
        {
            new N3TriplePattern(
                N3Term.Variable("s"),
                N3Term.Iri("http://example.com/p"),
                N3Term.Variable("o"))
        };

        var formula = new N3Formula(patterns);
        Assert.Single(formula.Patterns);
        Assert.Contains("s", formula.Variables);
        Assert.Contains("o", formula.Variables);
        Assert.Equal(2, formula.Variables.Count);
    }

    [Fact]
    public void N3Patch_IsEmpty_WhenNoOperations()
    {
        var patch = new N3Patch(null, null, null);
        Assert.True(patch.IsEmpty);
    }

    [Fact]
    public void N3Patch_IsNotEmpty_WithInserts()
    {
        var inserts = new N3Formula([
            new N3TriplePattern(
                N3Term.Iri("http://example.com/s"),
                N3Term.Iri("http://example.com/p"),
                N3Term.Literal("value"))
        ]);

        var patch = new N3Patch(null, null, inserts);
        Assert.False(patch.IsEmpty);
    }

    [Fact]
    public void N3Patch_GetVariables_CollectsFromAllClauses()
    {
        var where = new N3Formula([
            new N3TriplePattern(N3Term.Variable("a"), N3Term.Iri("http://ex.com/p"), N3Term.Variable("b"))
        ]);
        var deletes = new N3Formula([
            new N3TriplePattern(N3Term.Variable("a"), N3Term.Iri("http://ex.com/q"), N3Term.Variable("c"))
        ]);
        var inserts = new N3Formula([
            new N3TriplePattern(N3Term.Variable("a"), N3Term.Iri("http://ex.com/r"), N3Term.Literal("value"))
        ]);

        var patch = new N3Patch(where, deletes, inserts);
        var vars = patch.GetVariables().ToList();

        Assert.Contains("a", vars);
        Assert.Contains("b", vars);
        Assert.Contains("c", vars);
        Assert.Equal(3, vars.Count);
    }
}
