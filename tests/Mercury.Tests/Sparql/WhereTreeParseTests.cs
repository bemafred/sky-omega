using System.Collections.Generic;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Sparql.Types;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-055 D1 / Q2 kind B: a query's WHERE is parsed once, into the pattern tree, from the '{' the parser records
/// (<see cref="WhereClause.GroupStart"/>). These tests assert the tree a WHERE parses into, one construct per test;
/// they replace the parser tests that read the flat GraphPattern. Each test parses the query, parses the tree at the
/// recorded start, and walks the root's direct children.
/// </summary>
public class WhereTreeParseTests
{
    [Theory]
    [InlineData("SELECT * WHERE { ?s ?p ?o }", "Variable", "?s", "Variable", "?p", "Variable", "?o")]
    [InlineData("SELECT * WHERE { <http://ex/a> <http://ex/p> \"hello\" }", "Iri", "<http://ex/a>", "Iri", "<http://ex/p>", "Literal", "\"hello\"")]
    [InlineData("PREFIX foaf: <http://xmlns.com/foaf/0.1/> SELECT * WHERE { ?s foaf:name \"hi\"@en }", "Variable", "?s", "Iri", "foaf:name", "Literal", "\"hi\"@en")]
    // A labeled blank node in a pattern acts as a variable (SPARQL 1.1 §4.1.4): the tree holds it as one, named _:b1
    [InlineData("SELECT * WHERE { _:b1 <http://ex/p> \"42\"^^<http://www.w3.org/2001/XMLSchema#integer> }", "Variable", "_:b1", "Iri", "<http://ex/p>", "Literal", "\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>")]
    [InlineData("SELECT * WHERE { ?s ?p 42 }", "Variable", "?s", "Variable", "?p", "Literal", "42")]
    [InlineData("SELECT * WHERE { $s ?p ?o }", "Variable", "$s", "Variable", "?p", "Variable", "?o")]
    public void Triple_TermsKeepTheirTypeAndSpan(string query, string st, string s, string pt, string p, string ot, string o)
    {
        var parsed = new SparqlParser(query.AsSpan()).ParseQuery();
        var tree = new PatternArray(new byte[PatternSlot.Size * 64]);
        int root = new SparqlParser(query.AsSpan()).ParsePatternTreeAt(parsed.WhereClause.GroupStart, ref tree);

        var children = tree.EnumerateDirectChildren(root);
        Assert.True(children.MoveNext());
        var t = children.Current;
        Assert.Equal(PatternKind.Triple, t.Kind);
        Assert.Equal(st, t.SubjectType.ToString());
        Assert.Equal(s, query.Substring(t.SubjectStart, t.SubjectLength));
        Assert.Equal(pt, t.PredicateType.ToString());
        Assert.Equal(p, query.Substring(t.PredicateStart, t.PredicateLength));
        Assert.Equal(ot, t.ObjectType.ToString());
        Assert.Equal(o, query.Substring(t.ObjectStart, t.ObjectLength));
        Assert.False(children.MoveNext());
    }

    [Fact]
    public void TypeShorthand_IsRdfType()
    {
        const string query = "SELECT * WHERE { ?s a ?type }";
        var parsed = new SparqlParser(query.AsSpan()).ParseQuery();
        var tree = new PatternArray(new byte[PatternSlot.Size * 64]);
        int root = new SparqlParser(query.AsSpan()).ParsePatternTreeAt(parsed.WhereClause.GroupStart, ref tree);

        var children = tree.EnumerateDirectChildren(root);
        Assert.True(children.MoveNext());
        Assert.Equal(TermType.Iri, children.Current.PredicateType);
        Assert.Equal("a", query.Substring(children.Current.PredicateStart, children.Current.PredicateLength));
    }

    [Theory]
    [InlineData("SELECT * WHERE { ?s ?p ?o . ?x ?y ?z }", 2)]
    [InlineData("SELECT * WHERE { ?s <urn:p1> ?o1 ; <urn:p2> ?o2 . }", 2)]
    [InlineData("SELECT * WHERE { ?s <urn:p> ?o1 , ?o2 , ?o3 }", 3)]
    [InlineData("SELECT * WHERE { ?person <http://foaf/name> ?name . ?person <http://foaf/age> ?age . FILTER(?age > 18) }", 2)]
    public void TriplesBlock_IsOneTripleSlotPerTriple(string query, int triples)
    {
        var parsed = new SparqlParser(query.AsSpan()).ParseQuery();
        var tree = new PatternArray(new byte[PatternSlot.Size * 64]);
        int root = new SparqlParser(query.AsSpan()).ParsePatternTreeAt(parsed.WhereClause.GroupStart, ref tree);

        int count = 0;
        var children = tree.EnumerateDirectChildren(root);
        while (children.MoveNext())
            if (children.Current.Kind == PatternKind.Triple) count++;
        Assert.Equal(triples, count);
    }

    [Fact]
    public void Filters_AreFilterSlotsWithTheirExpression()
    {
        const string query = "SELECT * WHERE { ?s ?p ?o FILTER(?o > 10) FILTER(?o < 100) }";
        var parsed = new SparqlParser(query.AsSpan()).ParseQuery();
        var tree = new PatternArray(new byte[PatternSlot.Size * 64]);
        int root = new SparqlParser(query.AsSpan()).ParsePatternTreeAt(parsed.WhereClause.GroupStart, ref tree);

        var filters = new List<string>();
        var children = tree.EnumerateDirectChildren(root);
        while (children.MoveNext())
            if (children.Current.Kind == PatternKind.Filter)
                filters.Add(query.Substring(children.Current.FilterStart, children.Current.FilterLength).Trim());
        Assert.Equal(2, filters.Count);
        Assert.Contains("?o > 10", filters[0]);
        Assert.Contains("?o < 100", filters[1]);
    }

    [Theory]
    [InlineData("SELECT * WHERE { ?s <http://ex/p> ?o }", "None")]
    [InlineData("SELECT * WHERE { ?child ^<http://ex/parent> ?parent }", "Inverse")]
    [InlineData("SELECT * WHERE { ?s <http://ex/knows>* ?o }", "ZeroOrMore")]
    [InlineData("SELECT * WHERE { ?s <http://ex/knows>+ ?o }", "OneOrMore")]
    [InlineData("SELECT * WHERE { ?s <http://ex/knows>? ?o }", "ZeroOrOne")]
    [InlineData("SELECT * WHERE { ?s <http://ex/a>|<http://ex/b> ?o }", "Alternative")]
    [InlineData("SELECT * WHERE { ?s ^(<http://ex/a>/<http://ex/b>) ?o }", "InverseGroup")]
    [InlineData("SELECT * WHERE { ?s !(<http://ex/a>|<http://ex/b>) ?o }", "NegatedSet")]
    [InlineData("SELECT * WHERE { ?s !<http://ex/a> ?o }", "NegatedSet")]
    [InlineData("SELECT * WHERE { ?s !^a ?o }", "NegatedSet")]
    [InlineData("SELECT * WHERE { ?s !a ?o }", "NegatedSet")]
    [InlineData("PREFIX ex: <http://ex/> SELECT * WHERE { ?s !(a|^ex:foo) ?o }", "NegatedSet")]
    public void PropertyPath_IsOneTripleWithItsPathKindAndSpan(string query, string kind)
    {
        var parsed = new SparqlParser(query.AsSpan()).ParseQuery();
        var tree = new PatternArray(new byte[PatternSlot.Size * 64]);
        int root = new SparqlParser(query.AsSpan()).ParsePatternTreeAt(parsed.WhereClause.GroupStart, ref tree);

        var children = tree.EnumerateDirectChildren(root);
        Assert.True(children.MoveNext());
        Assert.Equal(PatternKind.Triple, children.Current.Kind);
        Assert.Equal(kind, children.Current.PathKind.ToString());
        if (kind != nameof(PathType.None))
        {
            // The slot carries the whole path expression, which the evaluator re-parses
            var span = query.Substring(children.Current.PathIriStart, children.Current.PathIriLength);
            Assert.Contains(span.Trim(), query);
            Assert.True(span.Length > 1);
        }
        Assert.False(children.MoveNext());
    }

    [Fact]
    public void SequencePath_IsOneTriplePerStep_JoinedByAJoinVariable()
    {
        // [88] PathSequence: ?s a/b*/c ?o is three triples, ?s a ?#j0, ?#j0 b* ?#j1 and ?#j1 c ?o; each step's slot
        // carries its own span
        const string query = "SELECT * WHERE { ?s <http://ex/a>/<http://ex/b>*/<http://ex/c> ?o }";
        var parsed = new SparqlParser(query.AsSpan()).ParseQuery();
        var tree = new PatternArray(new byte[PatternSlot.Size * 64]);
        int root = new SparqlParser(query.AsSpan()).ParsePatternTreeAt(parsed.WhereClause.GroupStart, ref tree);

        var children = tree.EnumerateDirectChildren(root);
        Assert.True(children.MoveNext());
        var first = children.Current;
        Assert.True(children.MoveNext());
        var second = children.Current;
        Assert.True(children.MoveNext());
        var third = children.Current;
        Assert.False(children.MoveNext());

        Assert.Equal("?s", query.Substring(first.SubjectStart, first.SubjectLength));
        Assert.Equal("None", first.PathKind.ToString());
        Assert.Equal("<http://ex/a>", query.Substring(first.PredicateStart, first.PredicateLength));
        Assert.True(first.ObjectStart < 0); // a join variable
        Assert.Equal(first.ObjectStart, second.SubjectStart);
        Assert.Equal("ZeroOrMore", second.PathKind.ToString());
        Assert.Equal("<http://ex/b>*", query.Substring(second.PathIriStart, second.PathIriLength));
        Assert.True(second.ObjectStart < 0 && second.ObjectStart != first.ObjectStart);
        Assert.Equal(second.ObjectStart, third.SubjectStart);
        Assert.Equal("<http://ex/c>", query.Substring(third.PredicateStart, third.PredicateLength));
        Assert.Equal("?o", query.Substring(third.ObjectStart, third.ObjectLength));
    }

    [Theory]
    [InlineData("SELECT * WHERE { GRAPH <http://ex/g1> { ?s ?p ?o } }", "<http://ex/g1>", 1)]
    [InlineData("SELECT * WHERE { GRAPH ?g { ?s ?p ?o . ?x ?y ?z } }", "?g", 2)]
    public void Graph_IsAHeaderOverItsGroup(string query, string graph, int triples)
    {
        var parsed = new SparqlParser(query.AsSpan()).ParseQuery();
        var tree = new PatternArray(new byte[PatternSlot.Size * 64]);
        int root = new SparqlParser(query.AsSpan()).ParsePatternTreeAt(parsed.WhereClause.GroupStart, ref tree);

        var children = tree.EnumerateDirectChildren(root);
        Assert.True(children.MoveNext());
        Assert.Equal(PatternKind.GraphHeader, children.Current.Kind);
        Assert.Equal(graph, query.Substring(children.Current.GraphTermStart, children.Current.GraphTermLength));
        int header = children.CurrentIndex;
        Assert.False(children.MoveNext());

        int count = 0;
        var inGraph = tree.EnumerateDirectChildren(header);
        while (inGraph.MoveNext())
        {
            Assert.Equal(PatternKind.Triple, inGraph.Current.Kind);
            count++;
        }
        Assert.Equal(triples, count);
    }

    [Fact]
    public void Graph_SeveralClausesAndDefaultTriples_AreSiblings()
    {
        const string query = "SELECT * WHERE { ?s ?p ?o . GRAPH <http://ex/g1> { ?a ?b ?c } GRAPH <http://ex/g2> { ?x ?y ?z } }";
        var parsed = new SparqlParser(query.AsSpan()).ParseQuery();
        var tree = new PatternArray(new byte[PatternSlot.Size * 64]);
        int root = new SparqlParser(query.AsSpan()).ParsePatternTreeAt(parsed.WhereClause.GroupStart, ref tree);

        var kinds = new List<PatternKind>();
        var children = tree.EnumerateDirectChildren(root);
        while (children.MoveNext()) kinds.Add(children.Current.Kind);
        Assert.Equal(new[] { PatternKind.Triple, PatternKind.GraphHeader, PatternKind.GraphHeader }, kinds);
    }

    [Fact]
    public void Optional_IsAHeaderOverItsGroup()
    {
        const string query = "SELECT * WHERE { ?s ?p ?o OPTIONAL { ?s <http://ex/opt> ?v } }";
        var parsed = new SparqlParser(query.AsSpan()).ParseQuery();
        var tree = new PatternArray(new byte[PatternSlot.Size * 64]);
        int root = new SparqlParser(query.AsSpan()).ParsePatternTreeAt(parsed.WhereClause.GroupStart, ref tree);

        var children = tree.EnumerateDirectChildren(root);
        Assert.True(children.MoveNext());
        Assert.Equal(PatternKind.Triple, children.Current.Kind);
        Assert.True(children.MoveNext());
        Assert.Equal(PatternKind.OptionalHeader, children.Current.Kind);
        Assert.False(children.MoveNext());
    }

    [Theory]
    [InlineData("SELECT * WHERE { SERVICE <http://remote/sparql> { ?s ?p ?o } }", "SERVICE <http://remote/sparql>")]
    [InlineData("SELECT * WHERE { SERVICE SILENT <http://remote/sparql> { ?s ?p ?o } }", "SERVICE SILENT <http://remote/sparql>")]
    [InlineData("SELECT * WHERE { SERVICE ?endpoint { ?s <http://ex/a> ?o . ?o <http://ex/b> ?x } }", "SERVICE ?endpoint")]
    public void Service_IsALeafCarryingItsWholeClause(string query, string head)
    {
        var parsed = new SparqlParser(query.AsSpan()).ParseQuery();
        var tree = new PatternArray(new byte[PatternSlot.Size * 64]);
        int root = new SparqlParser(query.AsSpan()).ParsePatternTreeAt(parsed.WhereClause.GroupStart, ref tree);

        var children = tree.EnumerateDirectChildren(root);
        Assert.True(children.MoveNext());
        Assert.Equal(PatternKind.ServiceHeader, children.Current.Kind);
        var span = query.Substring(children.Current.GraphTermStart, children.Current.GraphTermLength);
        Assert.StartsWith(head, span);
        Assert.EndsWith("}", span);
    }

    [Theory]
    [InlineData("SELECT * WHERE { { ?i <http://ex/t> <http://ex/L> } UNION { SERVICE <http://remote/sparql> { ?i <http://ex/t> <http://ex/R> } } }", 1)]
    [InlineData("SELECT * WHERE { { SERVICE <http://remote/sparql> { ?i <http://ex/t> <http://ex/R> } } UNION { ?i <http://ex/t> <http://ex/L> } }", 0)]
    public void Service_InAUnionBranch_IsThatBranchsChild(string query, int serviceBranch)
    {
        var parsed = new SparqlParser(query.AsSpan()).ParseQuery();
        var tree = new PatternArray(new byte[PatternSlot.Size * 64]);
        int root = new SparqlParser(query.AsSpan()).ParsePatternTreeAt(parsed.WhereClause.GroupStart, ref tree);

        var top = tree.EnumerateDirectChildren(root);
        Assert.True(top.MoveNext());
        Assert.Equal(PatternKind.UnionHeader, top.Current.Kind);
        var branches = new List<int>();
        var e = tree.EnumerateDirectChildren(top.CurrentIndex);
        while (e.MoveNext()) branches.Add(e.CurrentIndex);
        Assert.Equal(2, branches.Count);

        var inBranch = tree.EnumerateDirectChildren(branches[serviceBranch]);
        Assert.True(inBranch.MoveNext());
        Assert.Equal(PatternKind.ServiceHeader, inBranch.Current.Kind);
    }

    [Theory]
    [InlineData("SELECT ?person WHERE { { SELECT ?person WHERE { ?person <http://xmlns.com/foaf/0.1/name> ?name } } }", 1, 0)]
    [InlineData("PREFIX : <http://ex/> SELECT ?g WHERE { { SELECT (GROUP_CONCAT(?o) AS ?g) WHERE { [] :p1 ?o } } }", 0, 1)]
    public void SubSelect_OwnsItsWhereAsItsChild(string query, int projected, int expressions)
    {
        var parsed = new SparqlParser(query.AsSpan()).ParseQuery();
        var tree = new PatternArray(new byte[PatternSlot.Size * 64]);
        int root = new SparqlParser(query.AsSpan()).ParsePatternTreeAt(parsed.WhereClause.GroupStart, ref tree);

        var children = tree.EnumerateDirectChildren(root);
        Assert.True(children.MoveNext());
        var header = children.Current;
        int headerIndex = children.CurrentIndex;
        Assert.Equal(PatternKind.SubSelectHeader, header.Kind);
        Assert.False(children.MoveNext());
        Assert.StartsWith("SELECT", query.Substring(header.GraphTermStart, header.GraphTermLength));
        Assert.EndsWith("}", query.Substring(header.GraphTermStart, header.GraphTermLength).TrimEnd()); // the span ends past trailing whitespace

        var where = tree.EnumerateDirectChildren(headerIndex);
        Assert.True(where.MoveNext());
        Assert.Equal(PatternKind.GroupHeader, where.Current.Kind);
        int whereIndex = where.CurrentIndex;
        Assert.False(where.MoveNext());
        var triples = tree.EnumerateDirectChildren(whereIndex);
        Assert.True(triples.MoveNext());
        Assert.Equal(PatternKind.Triple, triples.Current.Kind);
        Assert.False(triples.MoveNext());

        var sub = new SparqlParser(query.AsSpan()).ParseSubSelectClausesAt(header.GraphTermStart, header.SubSelectModifiersStart);
        Assert.Equal(projected, sub.Select.ProjectedVariableCount);
        Assert.Equal(expressions, sub.Select.AggregateCount);
    }

    [Fact]
    public void SubSelect_Nested_IsAChildOfTheOuterSubSelectsWhere()
    {
        const string query = "PREFIX ex: <http://ex/> SELECT * WHERE { { SELECT * WHERE { { SELECT ?x WHERE { ?x ex:p ?y } } } } }";
        var parsed = new SparqlParser(query.AsSpan()).ParseQuery();
        var tree = new PatternArray(new byte[PatternSlot.Size * 64]);
        int root = new SparqlParser(query.AsSpan()).ParsePatternTreeAt(parsed.WhereClause.GroupStart, ref tree);

        var children = tree.EnumerateDirectChildren(root);
        Assert.True(children.MoveNext());
        var middle = children.Current;
        Assert.Equal(PatternKind.SubSelectHeader, middle.Kind);
        Assert.True(new SparqlParser(query.AsSpan()).ParseSubSelectClausesAt(middle.GraphTermStart, middle.SubSelectModifiersStart).Select.SelectAll);

        // The middle sub-SELECT's WHERE is the group { { SELECT ?x … } }, whose one child is the innermost sub-SELECT
        var middleWhere = tree.EnumerateDirectChildren(children.CurrentIndex);
        Assert.True(middleWhere.MoveNext());
        Assert.Equal(PatternKind.GroupHeader, middleWhere.Current.Kind);
        var inMiddleWhere = tree.EnumerateDirectChildren(middleWhere.CurrentIndex);
        Assert.True(inMiddleWhere.MoveNext());
        Assert.Equal(PatternKind.SubSelectHeader, inMiddleWhere.Current.Kind);
        var innermost = inMiddleWhere.Current;
        var clauses = new SparqlParser(query.AsSpan()).ParseSubSelectClausesAt(innermost.GraphTermStart, innermost.SubSelectModifiersStart);
        Assert.False(clauses.Select.SelectAll);
        Assert.Equal(1, clauses.Select.ProjectedVariableCount);

        // Every slot, the innermost triple included, is in the one tree
        Assert.Equal(6, tree.Count); // root, middle header, its WHERE group, innermost header, its WHERE group, triple
    }

    [Theory]
    [InlineData("SELECT ?c WHERE { << <http://ex/Alice> <http://ex/knows> <http://ex/Bob> >> <http://ex/confidence> ?c }", 5)]
    [InlineData("SELECT ?c ?s WHERE { << <http://ex/Alice> <http://ex/knows> <http://ex/Bob> >> <http://ex/confidence> ?c . << <http://ex/Bob> <http://ex/knows> <http://ex/Carol> >> <http://ex/source> ?s }", 10)]
    [InlineData("SELECT ?m WHERE { << << <http://ex/Alice> <http://ex/knows> <http://ex/Bob> >> <http://ex/said> <http://ex/Carol> >> <http://ex/meta> ?m }", 9)]
    public void QuotedTriple_ExpandsToReificationTriples(string query, int triples)
    {
        // Each quoted triple becomes four rdf:Statement triples about a reifier, plus the triple that uses it
        var parsed = new SparqlParser(query.AsSpan()).ParseQuery();
        var tree = new PatternArray(new byte[PatternSlot.Size * 64]);
        int root = new SparqlParser(query.AsSpan()).ParsePatternTreeAt(parsed.WhereClause.GroupStart, ref tree);

        int count = 0;
        var children = tree.EnumerateDirectChildren(root);
        while (children.MoveNext())
        {
            Assert.Equal(PatternKind.Triple, children.Current.Kind);
            count++;
        }
        Assert.Equal(triples, count);
    }

    [Theory]
    [InlineData("DESCRIBE * WHERE { ?s ?p ?o }", true)]
    [InlineData("DESCRIBE * WHERE { ?person <http://foaf/name> \"Alice\" }", true)]
    [InlineData("DESCRIBE *", false)]
    public void Describe_RecordsItsWhereGroup_OrNone(string query, bool hasWhere)
    {
        var parsed = new SparqlParser(query.AsSpan()).ParseQuery();

        Assert.True(parsed.Describe.All);
        if (hasWhere)
            Assert.Equal('{', query[parsed.WhereClause.GroupStart]);
        else
            Assert.Equal(-1, parsed.WhereClause.GroupStart);
    }
}
