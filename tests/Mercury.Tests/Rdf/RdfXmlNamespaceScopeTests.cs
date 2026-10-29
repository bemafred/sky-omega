using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SkyOmega.Mercury.Abstractions;
using Xunit;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// Namespaces in XML §6.1: a namespace declaration's scope is the element it is on and that element's content.
/// Through 1.8.35 the RDF/XML parser kept one table of prefixes for the whole document, so a declaration stayed bound
/// after its element ended: a prefix redeclared inside one element kept the inner namespace for every later sibling,
/// and so did a default namespace. Declaration_EndsWithItsElement parses the content under an rdf:RDF that binds both the default
/// namespace and eg: to http://a/; the inner elements redeclare them as http://b/. Generated blank node labels are
/// read as _:b, since only their places matter here.
/// </summary>
public class RdfXmlNamespaceScopeTests
{
    [Theory]
    [InlineData( // a node element's redeclaration ends with it
        """<rdf:Description rdf:about="http://s/1" xmlns:eg="http://b/"><eg:p>1</eg:p></rdf:Description><rdf:Description rdf:about="http://s/2"><eg:p>2</eg:p></rdf:Description>""",
        new[] { """<http://s/1> <http://b/p> "1" """, """<http://s/2> <http://a/p> "2" """ })]
    [InlineData( // so does a default namespace
        """<Thing rdf:about="http://s/1" xmlns="http://b/"/><Thing rdf:about="http://s/2"/>""",
        new[] { """<http://s/1> <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://b/Thing> """, """<http://s/2> <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://a/Thing> """ })]
    [InlineData( // a property element's declaration names it and its content, not its next sibling
        """<rdf:Description rdf:about="http://s/1"><eg:p xmlns:eg="http://b/"><eg:Q rdf:about="http://s/2"/></eg:p><eg:r>x</eg:r></rdf:Description>""",
        new[] { """<http://s/1> <http://b/p> <http://s/2> """, """<http://s/2> <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://b/Q> """, """<http://s/1> <http://a/r> "x" """ })]
    [InlineData( // a self-closing property element's declaration ends with it
        """<rdf:Description rdf:about="http://s/1"><eg:p xmlns:eg="http://b/" rdf:resource="http://o/"/><eg:r>x</eg:r></rdf:Description>""",
        new[] { """<http://s/1> <http://b/p> <http://o/> """, """<http://s/1> <http://a/r> "x" """ })]
    [InlineData( // parseType="Resource": in force for the property's content, not after it
        """<rdf:Description rdf:about="http://s/1"><eg:p xmlns:eg="http://b/" rdf:parseType="Resource"><eg:q>x</eg:q></eg:p><eg:r>y</eg:r></rdf:Description>""",
        new[] { """<http://s/1> <http://b/p> _:b """, """_:b <http://b/q> "x" """, """<http://s/1> <http://a/r> "y" """ })]
    [InlineData( // a collection item's declaration ends with the item
        """<rdf:Description rdf:about="http://s/1"><eg:p rdf:parseType="Collection"><eg:Q rdf:about="http://s/2" xmlns:eg="http://b/"/><eg:Q rdf:about="http://s/3"/></eg:p></rdf:Description>""",
        new[]
        {
            """<http://s/1> <http://a/p> _:b """,
            """_:b <http://www.w3.org/1999/02/22-rdf-syntax-ns#first> <http://s/2> """,
            """<http://s/2> <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://b/Q> """,
            """_:b <http://www.w3.org/1999/02/22-rdf-syntax-ns#rest> _:b """,
            """_:b <http://www.w3.org/1999/02/22-rdf-syntax-ns#first> <http://s/3> """,
            """<http://s/3> <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://a/Q> """,
            """_:b <http://www.w3.org/1999/02/22-rdf-syntax-ns#rest> <http://www.w3.org/1999/02/22-rdf-syntax-ns#nil> """,
        })]
    [InlineData( // an XML literal after a redeclaring sibling declares the binding in scope
        """<rdf:Description rdf:about="http://s/1" xmlns:eg="http://b/"><eg:p>1</eg:p></rdf:Description><rdf:Description rdf:about="http://s/2"><eg:p rdf:parseType="Literal"><eg:x/></eg:p></rdf:Description>""",
        new[] { """<http://s/1> <http://b/p> "1" """, """<http://s/2> <http://a/p> "<eg:x xmlns:eg="http://a/"></eg:x>"^^<http://www.w3.org/1999/02/22-rdf-syntax-ns#XMLLiteral> """ })]
    public async Task Declaration_EndsWithItsElement(string content, string[] expected)
    {
        var document = $"""
            <rdf:RDF xmlns="http://a/" xmlns:eg="http://a/" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              {content}
            </rdf:RDF>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));

        var triples = await RdfEngine.ParseTriplesAsync(stream, RdfFormat.RdfXml, ct: TestContext.Current.CancellationToken);

        // Each expected row is "subject predicate object " (the raw literals end with a space before their closing quotes)
        var actual = triples.Select(t => Regex.Replace($"{t.Subject} {t.Predicate} {t.Object} ", @"_:\S+", "_:b"));
        Assert.Equal(expected.OrderBy(row => row, System.StringComparer.Ordinal), actual.OrderBy(row => row, System.StringComparer.Ordinal));
    }

    // Namespaces in XML: a prefix must be declared (constraint Prefix Declared), and a prefixed declaration's value must
    // not be empty (1.0 §3); only xml is bound without a declaration. Through 1.8.35 an undeclared prefix read as an empty
    // namespace, so eg:p was stored as the IRI <p>, and rdf was bound without a declaration.
    [Theory]
    [InlineData("""<rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><zz:Thing rdf:about="http://s/1"/></rdf:RDF>""", "'zz' is not declared")]
    [InlineData("""<rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about="http://s/1"><zz:p>1</zz:p></rdf:Description></rdf:RDF>""", "'zz' is not declared")]
    [InlineData("""<rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about="http://s/1" zz:p="1"/></rdf:RDF>""", "'zz' is not declared")]
    [InlineData( // out of scope: declared on the earlier sibling only
        """<rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about="http://s/1" xmlns:zz="http://b/"><zz:p>1</zz:p></rdf:Description><rdf:Description rdf:about="http://s/2"><zz:p>2</zz:p></rdf:Description></rdf:RDF>""",
        "'zz' is not declared")]
    [InlineData( // inside an XML literal
        """<rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:eg="http://a/"><rdf:Description rdf:about="http://s/1"><eg:p rdf:parseType="Literal"><zz:x/></eg:p></rdf:Description></rdf:RDF>""",
        "'zz' is not declared")]
    [InlineData("""<rdf:RDF xmlns:eg="http://a/"><rdf:Description rdf:about="http://s/1"><eg:p>1</eg:p></rdf:Description></rdf:RDF>""", "'rdf' is not declared")]
    [InlineData("""<rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about="http://s/1" xmlns:zz=""/></rdf:RDF>""", "'zz' is declared with an empty namespace name")]
    [InlineData( // an empty prefixed declaration inside an XML literal
        """<rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:eg="http://a/"><rdf:Description rdf:about="http://s/1"><eg:p rdf:parseType="Literal"><x xmlns:zz=""/></eg:p></rdf:Description></rdf:RDF>""",
        "'zz' is declared with an empty namespace name")]
    public async Task Prefix_MustBeDeclared(string document, string message)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            RdfEngine.ParseTriplesAsync(stream, RdfFormat.RdfXml, ct: TestContext.Current.CancellationToken));

        Assert.Contains(message, error.Message);
    }
}
