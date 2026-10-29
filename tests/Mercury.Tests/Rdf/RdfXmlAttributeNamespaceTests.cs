using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SkyOmega.Mercury.Abstractions;
using Xunit;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// RDF/XML §5.1: the RDF namespace "is typically used in XML with the prefix rdf although other prefix strings may be
/// used". Through 1.8.35 the parser matched its syntax attributes by the written name ("rdf:about", "rdf:resource" …),
/// so r:about under xmlns:r bound to the RDF namespace was read as a property attribute, and rdf:about under rdf bound
/// to another namespace as the subject. Attributes are now keyed by namespace and local name. Also through 1.8.35, a
/// property element's rdf:value (or any rdf: attribute but the syntax ones) did not make its object a blank node, since
/// the test for property attributes skipped every rdf: name while the emitter wrote them. Generated blank node labels
/// are read as _:b.
/// </summary>
public class RdfXmlAttributeNamespaceTests
{
    [Theory]
    [InlineData( // the RDF namespace under another prefix
        """<r:RDF xmlns:r="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:eg="http://a/"><r:Description r:about="http://s/1"><eg:p r:resource="http://o/"/></r:Description></r:RDF>""",
        new[] { """<http://s/1> <http://a/p> <http://o/> """ })]
    [InlineData( // rdf bound to another namespace is not RDF syntax
        """<r:RDF xmlns:r="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:rdf="http://not-rdf/"><r:Description r:about="http://s/1" rdf:about="x"/></r:RDF>""",
        new[] { """<http://s/1> <http://not-rdf/about> "x" """ })]
    [InlineData( // rdf:datatype under another prefix
        """<r:RDF xmlns:r="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:eg="http://a/"><r:Description r:about="http://s/1"><eg:p r:datatype="http://www.w3.org/2001/XMLSchema#integer">5</eg:p></r:Description></r:RDF>""",
        new[] { """<http://s/1> <http://a/p> "5"^^<http://www.w3.org/2001/XMLSchema#integer> """ })]
    [InlineData( // rdf:parseType under another prefix
        """<r:RDF xmlns:r="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:eg="http://a/"><r:Description r:about="http://s/1"><eg:p r:parseType="Resource"><eg:q>x</eg:q></eg:p></r:Description></r:RDF>""",
        new[] { """<http://s/1> <http://a/p> _:b """, """_:b <http://a/q> "x" """ })]
    [InlineData( // a declaration written after the attribute that uses it
        """<rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about="http://s/1" zz:p="1" xmlns:zz="http://b/"/></rdf:RDF>""",
        new[] { """<http://s/1> <http://b/p> "1" """ })]
    [InlineData( // rdf:value on a property element is a property attribute: the object is a blank node
        """<rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:eg="http://a/"><rdf:Description rdf:about="http://s/1"><eg:p rdf:value="x"/></rdf:Description></rdf:RDF>""",
        new[] { """<http://s/1> <http://a/p> _:b """, """_:b <http://www.w3.org/1999/02/22-rdf-syntax-ns#value> "x" """ })]
    public async Task SyntaxAttributes_AreMatchedByNamespace(string document, string[] expected)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));

        var triples = await RdfEngine.ParseTriplesAsync(stream, RdfFormat.RdfXml, ct: TestContext.Current.CancellationToken);

        // Each expected row is "subject predicate object " (the raw literals end with a space before their closing quotes)
        var actual = triples.Select(t => Regex.Replace($"{t.Subject} {t.Predicate} {t.Object} ", @"_:\S+", "_:b"));
        Assert.Equal(expected.OrderBy(row => row, System.StringComparer.Ordinal), actual.OrderBy(row => row, System.StringComparer.Ordinal));
    }

    [Fact]
    public async Task SameAttribute_UnderTwoPrefixes_IsAnError()
    {
        // Namespaces in XML §6.3: rdf:about and r:about are one attribute when both prefixes are bound to the RDF namespace
        var document = """<rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:r="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about="http://s/1" r:about="http://s/2"/></rdf:RDF>""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            RdfEngine.ParseTriplesAsync(stream, RdfFormat.RdfXml, ct: TestContext.Current.CancellationToken));

        Assert.Contains("'r:about' appears twice", error.Message);
    }
}
