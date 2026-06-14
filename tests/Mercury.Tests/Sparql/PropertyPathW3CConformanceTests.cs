using System;
using System.Collections.Generic;
using System.Linq;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-047 — the property-path correctness oracle for the cutover. WDBench breadth showed that on composite paths
/// NEITHER executor is reliable (the old path returns {c} for <c>p1/(p2)*</c> where W3C says {b,c,d}; the tree is
/// right on <c>*</c>/<c>+</c> but wrong on a <c>?</c> sequence-tail and on alternation-of-sequences). At the SPARQL
/// edges the only source of truth is the grammar itself — so these cases are SYNTHESIZED to conform to the SPARQL 1.1
/// property-path EBNF (productions [88]–[94]), and the expected answer is computed by hand from the W3C path-evaluation
/// semantics (§18.4 / §9.3), NOT taken from either engine. The assertion is therefore tree ≡ W3C (the old path is
/// printed for reference but is not an oracle). Cases the tree fails are the cutover's genuine path-engine bugs.
///
/// Graph:  a—p→b—p→c—p→d ;  a—q→e—q→f ;  b—r→g.
/// </summary>
public class PropertyPathW3CConformanceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly QuadStore _store;
    private readonly TempPath _testPath;

    public PropertyPathW3CConformanceTests(ITestOutputHelper output)
    {
        _output = output;
        var tempPath = TempPath.Test("proppath-w3c");
        tempPath.MarkOwnership();
        _testPath = tempPath;
        _store = new QuadStore(_testPath);

        _store.BeginBatch();
        _store.AddCurrentBatched("<urn:a>", "<urn:p>", "<urn:b>");
        _store.AddCurrentBatched("<urn:b>", "<urn:p>", "<urn:c>");
        _store.AddCurrentBatched("<urn:c>", "<urn:p>", "<urn:d>");
        _store.AddCurrentBatched("<urn:a>", "<urn:q>", "<urn:e>");
        _store.AddCurrentBatched("<urn:e>", "<urn:q>", "<urn:f>");
        _store.AddCurrentBatched("<urn:b>", "<urn:r>", "<urn:g>");
        _store.CommitBatch();
    }

    public void Dispose()
    {
        _store.Dispose();
        TempPath.SafeCleanup(_testPath);
    }

    [Theory]
    // ── [92] PathElt ::= PathPrimary PathMod? — a modifier on a bare IRI primary ──
    [InlineData("mod-opt", "<urn:a> <urn:p>? ?x", "a,b")]                 // {a}(zero) ∪ {b}(one)
    [InlineData("mod-star", "<urn:a> <urn:p>* ?x", "a,b,c,d")]            // reflexive + transitive
    [InlineData("mod-plus", "<urn:a> <urn:p>+ ?x", "b,c,d")]             // one-or-more (no reflexive)
    // ── [94] PathPrimary ::= '(' Path ')' — a modifier on a grouped path ──
    [InlineData("grp-opt", "<urn:a> (<urn:p>)? ?x", "a,b")]
    [InlineData("grp-seq-opt", "<urn:a> (<urn:p>/<urn:p>)? ?x", "a,c")]   // {a}(zero) ∪ {c}(one of p/p)
    [InlineData("grp-seq-star", "<urn:a> (<urn:p>/<urn:p>)* ?x", "a,c")]  // a(0), c(1); (p/p) from c is empty
    // ── [90] PathSequence — a quantified segment inside a sequence ──
    [InlineData("seq-tail-star", "<urn:a> (<urn:p>/<urn:p>*) ?x", "b,c,d")] // a→b, then p* from b
    [InlineData("seq-tail-plus", "<urn:a> (<urn:p>/<urn:p>+) ?x", "c,d")]   // a→b, then p+ from b
    [InlineData("seq-tail-opt", "<urn:a> (<urn:p>/<urn:p>?) ?x", "b,c")]    // a→b, then p? from b = {b,c}
    [InlineData("seq-head-star", "<urn:a> (<urn:p>*/<urn:p>) ?x", "b,c,d")] // FIXED: p* from a, then one p
    // ── [89] PathAlternative ──
    [InlineData("alt-simple", "<urn:a> (<urn:p>|<urn:q>) ?x", "b,e")]
    [InlineData("alt-obj-bound", "?x (<urn:p>|<urn:q>) <urn:c>", "b")]  // object-bound: ?x reaches c via p|q ⇒ b (b-p→c)
    [InlineData("alt-of-seq", "<urn:a> ((<urn:p>/<urn:p>)|(<urn:q>/<urn:q>)) ?x", "c,f")]  // FIXED (walker re-entrancy)
    // ── [91] PathEltOrInverse ::= '^' PathElt ──
    [InlineData("inv-simple", "<urn:d> ^<urn:p> ?x", "c")]
    [InlineData("inv-star", "<urn:d> (^<urn:p>)* ?x", "a,b,c,d")]          // reverse closure
    [InlineData("inv-grp-seq", "<urn:d> ^(<urn:p>/<urn:p>) ?x", "b")]      // b (p/p) d ⇒ d ^(p/p) b
    // ── [94] negated property set ──
    [InlineData("neg-simple", "<urn:a> !<urn:q> ?x", "b")]                 // a's forward non-q edge: p→b
    [InlineData("neg-set", "<urn:a> !(<urn:q>|<urn:p>) ?x", "")]           // a has only p,q ⇒ empty
    public void Tree_MatchesW3CPathSemantics(string name, string body, string expectedX)
        => AssertTreeMatchesW3CPathSemantics(name, body, expectedX);

    [Theory(Skip = "ADR-047 — composite-path evaluator, remaining gap. FIXED so far: the parser gap (p*/p), and the " +
        "recursive walker's re-entrancy bug (alternation-of-sequences). REMAINING: a QUANTIFIER (* + ?) inside a " +
        "composite — the recursive walker (WalkOneBranchInto) treats a quantified leg like p* as a literal predicate, " +
        "so a sequence/alternation containing one evaluates to empty. Needs quantifier-closure in the walker. The " +
        "expected column is the EBNF-grounded W3C oracle these satisfy once fixed; un-skip a row as its fix lands.")]
    // ── quantifier inside a composite — walker lacks closure on a leg/branch ──
    [InlineData("alt-of-seq-star", "<urn:a> ((<urn:p>/<urn:p>*)|(<urn:q>/<urn:q>*)) ?x", "b,c,d,e,f")]
    [InlineData("seq-alt-of-star", "<urn:a> (<urn:p>/(<urn:p>*|<urn:r>*)) ?x", "b,c,d,g")] // p* from b ∪ r* from b
    public void Tree_MatchesW3CPathSemantics_KnownBugs(string name, string body, string expectedX)
        => AssertTreeMatchesW3CPathSemantics(name, body, expectedX);

    private void AssertTreeMatchesW3CPathSemantics(string name, string body, string expectedX)
    {
        var query = $"SELECT ?x WHERE {{ {body} }}";
        var tree = SparqlEngine.QueryViaTreeForDifferential(_store, query);
        var old = SparqlEngine.Query(_store, query);
        _output.WriteLine($"[{name}] W3C={expectedX,-12} tree={Render(tree)}  (old={Render(old)})");

        Assert.True(tree.Success, $"[{name}] tree: {tree.ErrorMessage}");

        var expected = expectedX.Length == 0
            ? new List<string>()
            : expectedX.Split(',').Select(v => $"<urn:{v}>").OrderBy(v => v, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, XValues(tree));
    }

    private static List<string> XValues(SkyOmega.Mercury.Abstractions.QueryResult r) =>
        (r.Rows ?? new List<Dictionary<string, string>>())
            .Select(row => row.GetValueOrDefault("x") ?? "")
            .OrderBy(v => v, StringComparer.Ordinal).ToList();

    private static string Render(SkyOmega.Mercury.Abstractions.QueryResult r) =>
        r.Success ? $"{{{string.Join(",", XValues(r).Select(v => v.Replace("<urn:", "").Replace(">", "")))}}}" : $"ERR:{r.ErrorMessage}";
}
