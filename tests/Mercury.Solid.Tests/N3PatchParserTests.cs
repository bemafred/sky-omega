// N3PatchParserTests.cs
// Tests for N3 Patch parser.

using System.Text;
using SkyOmega.Mercury.Solid.N3;
using Xunit;

namespace SkyOmega.Mercury.Solid.Tests;

public class N3PatchParserTests
{
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
        var result = await parser.ParseAsync();

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
