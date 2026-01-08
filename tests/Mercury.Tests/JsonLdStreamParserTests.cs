using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury.JsonLd;
using SkyOmega.Mercury.NQuads;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for JsonLdStreamParser - streaming JSON-LD to RDF parser.
/// </summary>
public class JsonLdStreamParserTests
{
    #region Basic Parsing

    [Fact]
    public async Task ParseAsync_SimpleObject_ParsesTriples()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/person1"",
            ""http://xmlns.com/foaf/0.1/name"": ""Alice""
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.Equal("<http://example.org/person1>", quads[0].Subject);
        Assert.Equal("<http://xmlns.com/foaf/0.1/name>", quads[0].Predicate);
        Assert.Equal("\"Alice\"", quads[0].Object);
    }

    [Fact]
    public async Task ParseAsync_WithType_ParsesRdfType()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/person1"",
            ""@type"": ""http://xmlns.com/foaf/0.1/Person""
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.Equal("<http://example.org/person1>", quads[0].Subject);
        Assert.Equal("<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", quads[0].Predicate);
        Assert.Equal("<http://xmlns.com/foaf/0.1/Person>", quads[0].Object);
    }

    [Fact]
    public async Task ParseAsync_MultipleTypes_ParsesAll()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/person1"",
            ""@type"": [""http://xmlns.com/foaf/0.1/Person"", ""http://schema.org/Person""]
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Equal(2, quads.Count);
        Assert.All(quads, q => Assert.Equal("<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", q.Predicate));
    }

    [Fact]
    public async Task ParseAsync_NoId_GeneratesBlankNode()
    {
        var jsonld = @"{
            ""http://xmlns.com/foaf/0.1/name"": ""Anonymous""
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.StartsWith("_:b", quads[0].Subject);
    }

    [Fact]
    public async Task ParseAsync_Array_ParsesMultipleNodes()
    {
        var jsonld = @"[
            {""@id"": ""http://example.org/p1"", ""http://ex.org/name"": ""Alice""},
            {""@id"": ""http://example.org/p2"", ""http://ex.org/name"": ""Bob""}
        ]";

        var quads = await ParseJsonLd(jsonld);

        Assert.Equal(2, quads.Count);
        Assert.Contains(quads, q => q.Subject == "<http://example.org/p1>");
        Assert.Contains(quads, q => q.Subject == "<http://example.org/p2>");
    }

    #endregion

    #region Context Processing

    [Fact]
    public async Task ParseAsync_SimpleContext_ExpandsTerms()
    {
        var jsonld = @"{
            ""@context"": {
                ""name"": ""http://xmlns.com/foaf/0.1/name"",
                ""foaf"": ""http://xmlns.com/foaf/0.1/""
            },
            ""@id"": ""http://example.org/person1"",
            ""name"": ""Alice""
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.Equal("<http://xmlns.com/foaf/0.1/name>", quads[0].Predicate);
    }

    [Fact]
    public async Task ParseAsync_VocabContext_ExpandsTerms()
    {
        var jsonld = @"{
            ""@context"": {
                ""@vocab"": ""http://schema.org/""
            },
            ""@id"": ""http://example.org/person1"",
            ""name"": ""Alice""
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.Equal("<http://schema.org/name>", quads[0].Predicate);
    }

    [Fact]
    public async Task ParseAsync_BaseContext_ResolvesRelativeIris()
    {
        var jsonld = @"{
            ""@context"": {
                ""@base"": ""http://example.org/""
            },
            ""@id"": ""person1"",
            ""http://ex.org/name"": ""Alice""
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.Equal("<http://example.org/person1>", quads[0].Subject);
    }

    [Fact]
    public async Task ParseAsync_PrefixContext_ExpandsCompactIris()
    {
        var jsonld = @"{
            ""@context"": {
                ""foaf"": ""http://xmlns.com/foaf/0.1/""
            },
            ""@id"": ""http://example.org/person1"",
            ""foaf:name"": ""Alice""
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.Equal("<http://xmlns.com/foaf/0.1/name>", quads[0].Predicate);
    }

    [Fact]
    public async Task ParseAsync_TypeCoercion_ConvertsToIri()
    {
        var jsonld = @"{
            ""@context"": {
                ""knows"": {""@id"": ""http://xmlns.com/foaf/0.1/knows"", ""@type"": ""@id""}
            },
            ""@id"": ""http://example.org/alice"",
            ""knows"": ""http://example.org/bob""
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.Equal("<http://example.org/bob>", quads[0].Object);
    }

    #endregion

    #region Literals

    [Fact]
    public async Task ParseAsync_PlainLiteral_ParsesCorrectly()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/s"",
            ""http://ex.org/name"": ""Hello World""
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.Equal("\"Hello World\"", quads[0].Object);
    }

    [Fact]
    public async Task ParseAsync_LanguageTaggedLiteral_ParsesCorrectly()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/s"",
            ""http://ex.org/name"": {""@value"": ""Bonjour"", ""@language"": ""fr""}
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.Equal("\"Bonjour\"@fr", quads[0].Object);
    }

    [Fact]
    public async Task ParseAsync_TypedLiteral_ParsesCorrectly()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/s"",
            ""http://ex.org/age"": {""@value"": ""42"", ""@type"": ""http://www.w3.org/2001/XMLSchema#integer""}
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.Equal("\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>", quads[0].Object);
    }

    [Fact]
    public async Task ParseAsync_IntegerValue_ParsesAsTypedLiteral()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/s"",
            ""http://ex.org/age"": 42
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.Equal("\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>", quads[0].Object);
    }

    [Fact]
    public async Task ParseAsync_DoubleValue_ParsesAsTypedLiteral()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/s"",
            ""http://ex.org/value"": 3.14
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        // Canonical XSD double representation uses scientific notation
        Assert.Equal("\"3.14E0\"^^<http://www.w3.org/2001/XMLSchema#double>", quads[0].Object);
    }

    [Fact]
    public async Task ParseAsync_BooleanTrue_ParsesAsTypedLiteral()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/s"",
            ""http://ex.org/active"": true
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.Equal("\"true\"^^<http://www.w3.org/2001/XMLSchema#boolean>", quads[0].Object);
    }

    [Fact]
    public async Task ParseAsync_BooleanFalse_ParsesAsTypedLiteral()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/s"",
            ""http://ex.org/active"": false
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.Equal("\"false\"^^<http://www.w3.org/2001/XMLSchema#boolean>", quads[0].Object);
    }

    [Fact]
    public async Task ParseAsync_NullValue_Ignored()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/s"",
            ""http://ex.org/value"": null
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Empty(quads);
    }

    #endregion

    #region Nested Objects

    [Fact]
    public async Task ParseAsync_NestedObject_CreatesBlankNode()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/alice"",
            ""http://xmlns.com/foaf/0.1/knows"": {
                ""http://xmlns.com/foaf/0.1/name"": ""Bob""
            }
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Equal(2, quads.Count);
        var knowsTriple = quads.First(q => q.Predicate == "<http://xmlns.com/foaf/0.1/knows>");
        Assert.StartsWith("_:b", knowsTriple.Object);

        var nameTriple = quads.First(q => q.Predicate == "<http://xmlns.com/foaf/0.1/name>");
        Assert.Equal(knowsTriple.Object, nameTriple.Subject);
    }

    [Fact]
    public async Task ParseAsync_NestedObjectWithId_UsesId()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/alice"",
            ""http://xmlns.com/foaf/0.1/knows"": {
                ""@id"": ""http://example.org/bob"",
                ""http://xmlns.com/foaf/0.1/name"": ""Bob""
            }
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Equal(2, quads.Count);
        var knowsTriple = quads.Find(q => q.Predicate == "<http://xmlns.com/foaf/0.1/knows>");
        Assert.Equal("<http://example.org/bob>", knowsTriple.Object);
    }

    [Fact]
    public async Task ParseAsync_IriReference_ParsesCorrectly()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/alice"",
            ""http://xmlns.com/foaf/0.1/knows"": {""@id"": ""http://example.org/bob""}
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.Equal("<http://example.org/bob>", quads[0].Object);
    }

    [Fact]
    public async Task ParseAsync_MultipleValues_ParsesAll()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/alice"",
            ""http://xmlns.com/foaf/0.1/knows"": [
                {""@id"": ""http://example.org/bob""},
                {""@id"": ""http://example.org/charlie""}
            ]
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Equal(2, quads.Count);
        Assert.Contains(quads, q => q.Object == "<http://example.org/bob>");
        Assert.Contains(quads, q => q.Object == "<http://example.org/charlie>");
    }

    #endregion

    #region Named Graphs

    [Fact]
    public async Task ParseAsync_GraphKeyword_ParsesNamedGraph()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/graph1"",
            ""@graph"": [
                {
                    ""@id"": ""http://example.org/alice"",
                    ""http://xmlns.com/foaf/0.1/name"": ""Alice""
                }
            ]
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.Equal("<http://example.org/graph1>", quads[0].Graph);
    }

    [Fact]
    public async Task ParseAsync_DefaultGraph_HasEmptyGraph()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/alice"",
            ""http://xmlns.com/foaf/0.1/name"": ""Alice""
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.True(string.IsNullOrEmpty(quads[0].Graph));
    }

    [Fact]
    public async Task ParseAsync_MultipleGraphs_ParsesAll()
    {
        var jsonld = @"[
            {
                ""@id"": ""http://example.org/graph1"",
                ""@graph"": [{""@id"": ""http://ex.org/s1"", ""http://ex.org/p"": ""v1""}]
            },
            {
                ""@id"": ""http://example.org/graph2"",
                ""@graph"": [{""@id"": ""http://ex.org/s2"", ""http://ex.org/p"": ""v2""}]
            }
        ]";

        var quads = await ParseJsonLd(jsonld);

        Assert.Equal(2, quads.Count);
        Assert.Contains(quads, q => q.Graph == "<http://example.org/graph1>");
        Assert.Contains(quads, q => q.Graph == "<http://example.org/graph2>");
    }

    #endregion

    #region Lists

    [Fact]
    public async Task ParseAsync_ListKeyword_CreatesRdfList()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/s"",
            ""http://ex.org/items"": {""@list"": [""a"", ""b"", ""c""]}
        }";

        var quads = await ParseJsonLd(jsonld);

        // Should have: s -> items -> list head
        // Plus 3 first + 3 rest triples
        Assert.True(quads.Count >= 4);
        Assert.Contains(quads, q => q.Predicate == "<http://www.w3.org/1999/02/22-rdf-syntax-ns#first>");
        Assert.Contains(quads, q => q.Predicate == "<http://www.w3.org/1999/02/22-rdf-syntax-ns#rest>");
    }

    [Fact]
    public async Task ParseAsync_EmptyList_ReturnsNil()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/s"",
            ""http://ex.org/items"": {""@list"": []}
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.Equal("<http://www.w3.org/1999/02/22-rdf-syntax-ns#nil>", quads[0].Object);
    }

    #endregion

    #region String Escaping

    [Fact]
    public async Task ParseAsync_EscapedQuotes_ParsesCorrectly()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/s"",
            ""http://ex.org/text"": ""He said \""Hello\""""
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.Equal("\"He said \\\"Hello\\\"\"", quads[0].Object);
    }

    [Fact]
    public async Task ParseAsync_Newlines_ParsesCorrectly()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/s"",
            ""http://ex.org/text"": ""Line1\nLine2""
        }";

        var quads = await ParseJsonLd(jsonld);

        Assert.Single(quads);
        Assert.Equal("\"Line1\\nLine2\"", quads[0].Object);
    }

    #endregion

    #region Allocating API

    [Fact]
    public async Task ParseAsync_AllocatingApi_ReturnsQuads()
    {
        var jsonld = @"{
            ""@id"": ""http://example.org/alice"",
            ""http://xmlns.com/foaf/0.1/name"": ""Alice""
        }";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonld));
        await using var parser = new JsonLdStreamParser(stream);

        var quads = new List<RdfQuad>();
        await foreach (var quad in parser.ParseAsync())
        {
            quads.Add(quad);
        }

        Assert.Single(quads);
        Assert.Equal("<http://example.org/alice>", quads[0].Subject);
    }

    #endregion

    #region Helper Methods

    private static async Task<List<RdfQuad>> ParseJsonLd(string jsonld)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonld));
        await using var parser = new JsonLdStreamParser(stream);

        var quads = new List<RdfQuad>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add(new RdfQuad(s.ToString(), p.ToString(), o.ToString(),
                g.IsEmpty ? null : g.ToString()));
        });

        return quads;
    }

    #endregion
}
