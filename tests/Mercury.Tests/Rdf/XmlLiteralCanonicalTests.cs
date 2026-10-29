using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury.Abstractions;
using Xunit;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// RDF/XML §7.2.17: a parseType="Literal" property element's content is an rdf:XMLLiteral, in Exclusive XML
/// Canonicalization (W3C xml-exc-c14n) with comments, as RDF/XML 2004 defined it and the W3C tests expect. Through 1.8.34
/// Mercury wrote every in-scope namespace on every element of the literal, dropped the default namespace, wrote an end
/// tag as "&lt;//b&gt;", copied text and references as written, and gave a self-closing parseType="Literal" a plain
/// literal (W3C xml-canon-test001, -002; rdfms-xml-literal-namespaces-test001, -002 and
/// rdfms-empty-property-elements-test003, which the upstream manifest leaves out). Each test parses one property element
/// whose content is the literal, under the declarations of rdf:RDF.
/// </summary>
public class XmlLiteralCanonicalTests
{
    [Theory]
    [InlineData("""<br />""", """<br xmlns="http://www.w3.org/1999/xhtml"></br>""")]                     // the default it uses; unused rdf:, eg: left out
    [InlineData("""<eg:a><eg:b/></eg:a>""", """<eg:a xmlns:eg="http://example.org/"><eg:b></eg:b></eg:a>""")] // declared once
    [InlineData("""<h:p xmlns:h="http://h/"><b>x</b></h:p>""", """<h:p xmlns:h="http://h/"><b xmlns="http://www.w3.org/1999/xhtml">x</b></h:p>""")] // the default where used
    [InlineData("""<b><c xmlns="">y</c></b>""", """<b xmlns="http://www.w3.org/1999/xhtml"><c xmlns="">y</c></b>""")] // the default undeclared
    [InlineData("""<b eg:z="1" a="2" xml:lang="en"/>""", """<b xmlns="http://www.w3.org/1999/xhtml" xmlns:eg="http://example.org/" a="2" eg:z="1" xml:lang="en"></b>""")] // attributes by namespace IRI
    [InlineData("""<b a='x&amp;y&lt;"z"&#9;&gt;'/>""", """<b xmlns="http://www.w3.org/1999/xhtml" a="x&amp;y&lt;&quot;z&quot;&#x9;>"></b>""")] // attribute escapes; '>' is not one
    [InlineData("""a &gt; b &#62; c &amp; d""", """a &gt; b &gt; c &amp; d""")]                           // text references in canonical form
    [InlineData("""<![CDATA[x < y & z]]>""", """x &lt; y &amp; z""")]                                     // CDATA is its text
    [InlineData("""<!-- note --><b/>""", """<!-- note --><b xmlns="http://www.w3.org/1999/xhtml"></b>""")] // comments kept
    public async Task ParseTypeLiteral_IsItsExclusiveCanonicalForm(string content, string expected)
    {
        var document = $"""
            <rdf:RDF xmlns="http://www.w3.org/1999/xhtml" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:eg="http://example.org/">
              <rdf:Description rdf:about="http://example.org/s"><eg:p rdf:parseType="Literal">{content}</eg:p></rdf:Description>
            </rdf:RDF>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));

        var triples = await RdfEngine.ParseTriplesAsync(stream, RdfFormat.RdfXml, ct: TestContext.Current.CancellationToken);

        Assert.Equal($"\"{expected}\"^^<http://www.w3.org/1999/02/22-rdf-syntax-ns#XMLLiteral>", Assert.Single(triples).Object);
    }

    [Theory]
    [InlineData("""<eg:p rdf:parseType="Literal"/>""", "", true)]
    [InlineData("""<eg:p rdf:parseType="Other"/>""", "", true)]                                         // §7.2.20: as Literal
    [InlineData("""<eg:p rdf:parseType="Other"><b/></eg:p>""", "<b></b>", true)]
    [InlineData("""<eg:p rdf:parseType="Collection"/>""", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#nil>", false)] // §7.2.19: empty
    public async Task EmptyOrOtherParseType_IsItsTerm(string property, string expected, bool isXmlLiteral)
    {
        var document = $"""
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:eg="http://example.org/">
              <rdf:Description rdf:about="http://example.org/s">{property}</rdf:Description>
            </rdf:RDF>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));

        var triples = await RdfEngine.ParseTriplesAsync(stream, RdfFormat.RdfXml, ct: TestContext.Current.CancellationToken);

        // An XML literal's term is its lexical form, quoted, with the rdf:XMLLiteral datatype
        var term = isXmlLiteral ? $"\"{expected}\"^^<http://www.w3.org/1999/02/22-rdf-syntax-ns#XMLLiteral>" : expected;
        Assert.Equal(term, Assert.Single(triples).Object);
    }
}
