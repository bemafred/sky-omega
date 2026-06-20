using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury.TriG;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.Sparql.Execution;
using Xunit;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// Numeric/boolean datatype-identity (docs/divergence S1c). The Turtle and TriG parsers and the SPARQL
/// <see cref="LiteralForm"/> canonicalizer all select the xsd datatype IRI and emit the boolean literal form
/// through the single shared <c>RdfNumeric</c>. A numeric or boolean must therefore produce a byte-identical
/// typed literal whether it arrives as RDF source or as a SPARQL constant — otherwise a SPARQL constant
/// <c>30</c> would not match a Turtle-ingested <c>30</c>. This test is the lock on that agreement, which
/// was previously only implicit (the same rule hand-copied in four places).
/// </summary>
public class RdfNumericConvergenceTests
{
    [Theory]
    [InlineData("30", "\"30\"^^<http://www.w3.org/2001/XMLSchema#integer>")]
    [InlineData("-42", "\"-42\"^^<http://www.w3.org/2001/XMLSchema#integer>")]
    [InlineData("3.14", "\"3.14\"^^<http://www.w3.org/2001/XMLSchema#decimal>")]
    [InlineData("1e5", "\"1e5\"^^<http://www.w3.org/2001/XMLSchema#double>")]
    [InlineData("-2.5E10", "\"-2.5E10\"^^<http://www.w3.org/2001/XMLSchema#double>")]
    [InlineData("true", "\"true\"^^<http://www.w3.org/2001/XMLSchema#boolean>")]
    [InlineData("false", "\"false\"^^<http://www.w3.org/2001/XMLSchema#boolean>")]
    public async Task NumericAndBoolean_TypeIdentically_AcrossParsersAndSparql(string token, string expectedTyped)
    {
        string triple = $"<http://ex.org/s> <http://ex.org/p> {token} .";

        string ttlDecoded = null!;
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple)))
            await new TurtleStreamParser(stream).ParseAsync((_, _, o) => ttlDecoded = o.ToString());

        string trigDecoded = null!;
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple)))
            await new TriGStreamParser(stream).ParseAsync((_, _, o, _) => trigDecoded = o.ToString());

        // The SPARQL-side canonicalizer (used for VALUES / constant-object matching) must agree byte-for-byte.
        string sparqlCanonical = LiteralForm.CanonicalizeNumericOrBoolean(token.AsSpan(), out _).ToString();

        Assert.Equal(expectedTyped, ttlDecoded);
        Assert.Equal(expectedTyped, trigDecoded);
        Assert.Equal(expectedTyped, sparqlCanonical);
    }
}
