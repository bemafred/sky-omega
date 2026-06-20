using SkyOmega.Mercury.Rdf;
using Xunit;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// Grounds the shared <see cref="RdfIri"/> resolver (docs/divergence S1b) directly in the RFC 3986 §5.4
/// normative reference-resolution examples — the spec's own oracle, not any prior implementation. All vectors
/// use the base from §5.4: <c>http://a/b/c/d;p?q</c>. If these pass, the resolver is RFC-correct, and every
/// format parser routed through it resolves relative IRIs identically.
/// </summary>
public class RdfIriResolutionTests
{
    private const string Base = "http://a/b/c/d;p?q";

    [Theory]
    // RFC 3986 §5.4.1 — Normal Examples.
    [InlineData("g:h", "g:h")]
    [InlineData("g", "http://a/b/c/g")]
    [InlineData("./g", "http://a/b/c/g")]
    [InlineData("g/", "http://a/b/c/g/")]
    [InlineData("/g", "http://a/g")]
    [InlineData("//g", "http://g")]
    [InlineData("?y", "http://a/b/c/d;p?y")]
    [InlineData("g?y", "http://a/b/c/g?y")]
    [InlineData("#s", "http://a/b/c/d;p?q#s")]
    [InlineData("g#s", "http://a/b/c/g#s")]
    [InlineData("g?y#s", "http://a/b/c/g?y#s")]
    [InlineData(";x", "http://a/b/c/;x")]
    [InlineData("g;x", "http://a/b/c/g;x")]
    [InlineData("g;x?y#s", "http://a/b/c/g;x?y#s")]
    [InlineData("", "http://a/b/c/d;p?q")]
    [InlineData(".", "http://a/b/c/")]
    [InlineData("./", "http://a/b/c/")]
    [InlineData("..", "http://a/b/")]
    [InlineData("../", "http://a/b/")]
    [InlineData("../g", "http://a/b/g")]
    [InlineData("../..", "http://a/")]
    [InlineData("../../", "http://a/")]
    [InlineData("../../g", "http://a/g")]
    // RFC 3986 §5.4.2 — Abnormal Examples.
    [InlineData("../../../g", "http://a/g")]
    [InlineData("../../../../g", "http://a/g")]
    [InlineData("/./g", "http://a/g")]
    [InlineData("/../g", "http://a/g")]
    [InlineData("g.", "http://a/b/c/g.")]
    [InlineData(".g", "http://a/b/c/.g")]
    [InlineData("g..", "http://a/b/c/g..")]
    [InlineData("..g", "http://a/b/c/..g")]
    [InlineData("./../g", "http://a/b/g")]
    [InlineData("./g/.", "http://a/b/c/g/")]
    [InlineData("g/./h", "http://a/b/c/g/h")]
    [InlineData("g/../h", "http://a/b/c/h")]
    [InlineData("g;x=1/./y", "http://a/b/c/g;x=1/y")]
    [InlineData("g;x=1/../y", "http://a/b/c/y")]
    [InlineData("g?y/./x", "http://a/b/c/g?y/./x")]
    [InlineData("g?y/../x", "http://a/b/c/g?y/../x")]
    [InlineData("g#s/./x", "http://a/b/c/g#s/./x")]
    [InlineData("g#s/../x", "http://a/b/c/g#s/../x")]
    public void Resolve_MatchesRfc3986NormativeVectors(string reference, string expected)
    {
        Assert.Equal(expected, RdfIri.Resolve(Base, reference));
    }

    [Fact]
    public void Resolve_AbsoluteReference_ReturnedVerbatim_NoNormalisation()
    {
        // RDF does not normalise IRIs — an absolute reference, even with dot segments, is stored as-is
        // (unlike generic RFC-3986 §5.2.2 which would remove_dot_segments). Identity is string equality.
        Assert.Equal("http://x/a/../b", RdfIri.Resolve(Base, "http://x/a/../b"));
        Assert.Equal("urn:foo:bar", RdfIri.Resolve(Base, "urn:foo:bar"));
    }

    [Fact]
    public void Resolve_EmptyBase_ReturnsReferenceUnchanged()
    {
        Assert.Equal("../g", RdfIri.Resolve("", "../g"));
    }
}
