// OwlReasoner.cs
// OWL/RDFS inference engine for RDF graphs
// Implements forward-chaining rule-based reasoning
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Owl;

/// <summary>
/// Inference rule types supported by the reasoner.
/// </summary>
[Flags]
public enum InferenceRules
{
    /// <summary>No inference.</summary>
    None = 0,

    /// <summary>RDFS subclass transitivity and instance inference.</summary>
    RdfsSubClass = 1 << 0,

    /// <summary>RDFS subproperty transitivity and property inheritance.</summary>
    RdfsSubProperty = 1 << 1,

    /// <summary>RDFS domain inference (s p o, p rdfs:domain D => s rdf:type D).</summary>
    RdfsDomain = 1 << 2,

    /// <summary>RDFS range inference (s p o, p rdfs:range R => o rdf:type R).</summary>
    RdfsRange = 1 << 3,

    /// <summary>OWL transitive property inference.</summary>
    OwlTransitive = 1 << 4,

    /// <summary>OWL symmetric property inference.</summary>
    OwlSymmetric = 1 << 5,

    /// <summary>OWL inverse property inference.</summary>
    OwlInverse = 1 << 6,

    /// <summary>OWL sameAs identity inference.</summary>
    OwlSameAs = 1 << 7,

    /// <summary>OWL equivalent class inference.</summary>
    OwlEquivalentClass = 1 << 8,

    /// <summary>OWL equivalent property inference.</summary>
    OwlEquivalentProperty = 1 << 9,

    /// <summary>All RDFS rules.</summary>
    AllRdfs = RdfsSubClass | RdfsSubProperty | RdfsDomain | RdfsRange,

    /// <summary>All OWL rules.</summary>
    AllOwl = OwlTransitive | OwlSymmetric | OwlInverse | OwlSameAs | OwlEquivalentClass | OwlEquivalentProperty,

    /// <summary>All inference rules.</summary>
    All = AllRdfs | AllOwl
}

/// <summary>
/// OWL/RDFS reasoner for inferring implicit facts from explicit RDF data.
/// Uses forward-chaining rule-based reasoning.
///
/// Supported inferences:
/// - RDFS: subClassOf, subPropertyOf, domain, range
/// - OWL: TransitiveProperty, SymmetricProperty, inverseOf, sameAs, equivalentClass, equivalentProperty
/// </summary>
public sealed class OwlReasoner
{
    private readonly QuadStore _store;
    private readonly InferenceRules _rules;

    // RDF/RDFS/OWL vocabulary IRIs
    private static readonly string RdfType = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>";
    private static readonly string RdfsSubClassOf = "<http://www.w3.org/2000/01/rdf-schema#subClassOf>";
    private static readonly string RdfsSubPropertyOf = "<http://www.w3.org/2000/01/rdf-schema#subPropertyOf>";
    private static readonly string RdfsDomain = "<http://www.w3.org/2000/01/rdf-schema#domain>";
    private static readonly string RdfsRange = "<http://www.w3.org/2000/01/rdf-schema#range>";
    private static readonly string OwlTransitiveProperty = "<http://www.w3.org/2002/07/owl#TransitiveProperty>";
    private static readonly string OwlSymmetricProperty = "<http://www.w3.org/2002/07/owl#SymmetricProperty>";
    private static readonly string OwlInverseOf = "<http://www.w3.org/2002/07/owl#inverseOf>";
    private static readonly string OwlSameAs = "<http://www.w3.org/2002/07/owl#sameAs>";
    private static readonly string OwlEquivalentClass = "<http://www.w3.org/2002/07/owl#equivalentClass>";
    private static readonly string OwlEquivalentProperty = "<http://www.w3.org/2002/07/owl#equivalentProperty>";

    /// <summary>
    /// Create a new OWL reasoner for a quad store.
    /// </summary>
    /// <param name="store">The quad store to reason over.</param>
    /// <param name="rules">Which inference rules to apply.</param>
    public OwlReasoner(QuadStore store, InferenceRules rules = InferenceRules.All)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _rules = rules;
    }

    /// <summary>
    /// Perform forward-chaining inference and materialize inferred triples.
    /// Returns the number of new triples inferred.
    /// </summary>
    /// <param name="graph">Optional graph to limit inference to.</param>
    /// <param name="maxIterations">Maximum number of inference passes (prevents infinite loops).</param>
    public int Materialize(ReadOnlySpan<char> graph = default, int maxIterations = 100)
    {
        int totalInferred = 0;
        int iteration = 0;

        // Fixed-point iteration: keep inferring until no new facts
        while (iteration < maxIterations)
        {
            int inferred = RunInferencePass(graph);
            if (inferred == 0)
                break;

            totalInferred += inferred;
            iteration++;
        }

        return totalInferred;
    }

    /// <summary>
    /// Run a single inference pass applying all enabled rules.
    /// </summary>
    private int RunInferencePass(ReadOnlySpan<char> graph)
    {
        int inferred = 0;

        // Collect inferred triples to avoid modifying store during iteration
        var inferredTriples = new List<(string s, string p, string o)>();

        _store.AcquireReadLock();
        try
        {
            // RDFS rules
            if ((_rules & InferenceRules.RdfsSubClass) != 0)
            {
                InferSubClassTransitivity(graph, inferredTriples);
                InferTypeFromSubClass(graph, inferredTriples);
            }

            if ((_rules & InferenceRules.RdfsSubProperty) != 0)
            {
                InferSubPropertyTransitivity(graph, inferredTriples);
                InferPropertyFromSubProperty(graph, inferredTriples);
            }

            if ((_rules & InferenceRules.RdfsDomain) != 0)
            {
                InferTypeFromDomain(graph, inferredTriples);
            }

            if ((_rules & InferenceRules.RdfsRange) != 0)
            {
                InferTypeFromRange(graph, inferredTriples);
            }

            // OWL rules
            if ((_rules & InferenceRules.OwlTransitive) != 0)
            {
                InferTransitive(graph, inferredTriples);
            }

            if ((_rules & InferenceRules.OwlSymmetric) != 0)
            {
                InferSymmetric(graph, inferredTriples);
            }

            if ((_rules & InferenceRules.OwlInverse) != 0)
            {
                InferInverse(graph, inferredTriples);
            }

            if ((_rules & InferenceRules.OwlSameAs) != 0)
            {
                InferSameAs(graph, inferredTriples);
            }

            if ((_rules & InferenceRules.OwlEquivalentClass) != 0)
            {
                InferEquivalentClass(graph, inferredTriples);
            }

            if ((_rules & InferenceRules.OwlEquivalentProperty) != 0)
            {
                InferEquivalentProperty(graph, inferredTriples);
            }
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        // Add inferred triples (avoiding duplicates)
        foreach (var (s, p, o) in inferredTriples)
        {
            if (!TripleExists(s, p, o, graph))
            {
                _store.AddCurrent(s.AsSpan(), p.AsSpan(), o.AsSpan(), graph);
                inferred++;
            }
        }

        return inferred;
    }

    /// <summary>
    /// Check if a triple already exists in the store.
    /// </summary>
    private bool TripleExists(string subject, string predicate, string obj, ReadOnlySpan<char> graph)
    {
        _store.AcquireReadLock();
        try
        {
            var results = _store.QueryCurrent(subject.AsSpan(), predicate.AsSpan(), obj.AsSpan(), graph);
            try
            {
                return results.MoveNext();
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    #region RDFS Inference Rules

    /// <summary>
    /// RDFS subClassOf transitivity:
    /// If A rdfs:subClassOf B and B rdfs:subClassOf C, then A rdfs:subClassOf C
    /// </summary>
    private void InferSubClassTransitivity(ReadOnlySpan<char> graph, List<(string, string, string)> inferred)
    {
        // Get all subClassOf statements
        var subClassPairs = GetAllTriples(ReadOnlySpan<char>.Empty, RdfsSubClassOf.AsSpan(), ReadOnlySpan<char>.Empty, graph);

        // Build transitive closure
        foreach (var (subClass, _, superClass) in subClassPairs)
        {
            // Find superclasses of superClass
            var superSupers = GetAllTriples(superClass.AsSpan(), RdfsSubClassOf.AsSpan(), ReadOnlySpan<char>.Empty, graph);
            foreach (var (_, _, superSuper) in superSupers)
            {
                if (subClass != superSuper) // Avoid reflexive loops
                {
                    inferred.Add((subClass, RdfsSubClassOf, superSuper));
                }
            }
        }
    }

    /// <summary>
    /// RDFS type inference from subClassOf:
    /// If x rdf:type A and A rdfs:subClassOf B, then x rdf:type B
    /// </summary>
    private void InferTypeFromSubClass(ReadOnlySpan<char> graph, List<(string, string, string)> inferred)
    {
        // Get all type statements
        var typeStatements = GetAllTriples(ReadOnlySpan<char>.Empty, RdfType.AsSpan(), ReadOnlySpan<char>.Empty, graph);

        foreach (var (instance, _, type) in typeStatements)
        {
            // Find superclasses of type
            var superClasses = GetAllTriples(type.AsSpan(), RdfsSubClassOf.AsSpan(), ReadOnlySpan<char>.Empty, graph);
            foreach (var (_, _, superClass) in superClasses)
            {
                inferred.Add((instance, RdfType, superClass));
            }
        }
    }

    /// <summary>
    /// RDFS subPropertyOf transitivity:
    /// If P rdfs:subPropertyOf Q and Q rdfs:subPropertyOf R, then P rdfs:subPropertyOf R
    /// </summary>
    private void InferSubPropertyTransitivity(ReadOnlySpan<char> graph, List<(string, string, string)> inferred)
    {
        var subPropertyPairs = GetAllTriples(ReadOnlySpan<char>.Empty, RdfsSubPropertyOf.AsSpan(), ReadOnlySpan<char>.Empty, graph);

        foreach (var (subProp, _, superProp) in subPropertyPairs)
        {
            var superSupers = GetAllTriples(superProp.AsSpan(), RdfsSubPropertyOf.AsSpan(), ReadOnlySpan<char>.Empty, graph);
            foreach (var (_, _, superSuper) in superSupers)
            {
                if (subProp != superSuper)
                {
                    inferred.Add((subProp, RdfsSubPropertyOf, superSuper));
                }
            }
        }
    }

    /// <summary>
    /// RDFS property inheritance from subPropertyOf:
    /// If P rdfs:subPropertyOf Q and S P O, then S Q O
    /// </summary>
    private void InferPropertyFromSubProperty(ReadOnlySpan<char> graph, List<(string, string, string)> inferred)
    {
        var subPropertyPairs = GetAllTriples(ReadOnlySpan<char>.Empty, RdfsSubPropertyOf.AsSpan(), ReadOnlySpan<char>.Empty, graph);

        foreach (var (subProp, _, superProp) in subPropertyPairs)
        {
            // Find all uses of subProp
            var usages = GetAllTriples(ReadOnlySpan<char>.Empty, subProp.AsSpan(), ReadOnlySpan<char>.Empty, graph);
            foreach (var (s, _, o) in usages)
            {
                inferred.Add((s, superProp, o));
            }
        }
    }

    /// <summary>
    /// RDFS domain inference:
    /// If P rdfs:domain D and S P O, then S rdf:type D
    /// </summary>
    private void InferTypeFromDomain(ReadOnlySpan<char> graph, List<(string, string, string)> inferred)
    {
        var domainStatements = GetAllTriples(ReadOnlySpan<char>.Empty, RdfsDomain.AsSpan(), ReadOnlySpan<char>.Empty, graph);

        foreach (var (prop, _, domain) in domainStatements)
        {
            // Find all uses of prop
            var usages = GetAllTriples(ReadOnlySpan<char>.Empty, prop.AsSpan(), ReadOnlySpan<char>.Empty, graph);
            foreach (var (s, _, _) in usages)
            {
                inferred.Add((s, RdfType, domain));
            }
        }
    }

    /// <summary>
    /// RDFS range inference:
    /// If P rdfs:range R and S P O, then O rdf:type R
    /// </summary>
    private void InferTypeFromRange(ReadOnlySpan<char> graph, List<(string, string, string)> inferred)
    {
        var rangeStatements = GetAllTriples(ReadOnlySpan<char>.Empty, RdfsRange.AsSpan(), ReadOnlySpan<char>.Empty, graph);

        foreach (var (prop, _, range) in rangeStatements)
        {
            // Find all uses of prop
            var usages = GetAllTriples(ReadOnlySpan<char>.Empty, prop.AsSpan(), ReadOnlySpan<char>.Empty, graph);
            foreach (var (_, _, o) in usages)
            {
                // Only infer type for resources, not literals
                if (!o.StartsWith("\""))
                {
                    inferred.Add((o, RdfType, range));
                }
            }
        }
    }

    #endregion

    #region OWL Inference Rules

    /// <summary>
    /// OWL transitive property inference:
    /// If P rdf:type owl:TransitiveProperty and A P B and B P C, then A P C
    /// </summary>
    private void InferTransitive(ReadOnlySpan<char> graph, List<(string, string, string)> inferred)
    {
        // Find all transitive properties
        var transitiveProps = GetAllTriples(ReadOnlySpan<char>.Empty, RdfType.AsSpan(), OwlTransitiveProperty.AsSpan(), graph);

        foreach (var (prop, _, _) in transitiveProps)
        {
            // Get all instances of this property
            var instances = GetAllTriples(ReadOnlySpan<char>.Empty, prop.AsSpan(), ReadOnlySpan<char>.Empty, graph);

            // Build adjacency for transitive closure
            var edges = new Dictionary<string, List<string>>();
            foreach (var (s, _, o) in instances)
            {
                if (!edges.TryGetValue(s, out var targets))
                {
                    targets = new List<string>();
                    edges[s] = targets;
                }
                targets.Add(o);
            }

            // Compute transitive closure using Floyd-Warshall-like approach
            foreach (var (a, bList) in edges)
            {
                foreach (var b in bList)
                {
                    if (edges.TryGetValue(b, out var cList))
                    {
                        foreach (var c in cList)
                        {
                            if (a != c) // Avoid reflexive
                            {
                                inferred.Add((a, prop, c));
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// OWL symmetric property inference:
    /// If P rdf:type owl:SymmetricProperty and A P B, then B P A
    /// </summary>
    private void InferSymmetric(ReadOnlySpan<char> graph, List<(string, string, string)> inferred)
    {
        var symmetricProps = GetAllTriples(ReadOnlySpan<char>.Empty, RdfType.AsSpan(), OwlSymmetricProperty.AsSpan(), graph);

        foreach (var (prop, _, _) in symmetricProps)
        {
            var instances = GetAllTriples(ReadOnlySpan<char>.Empty, prop.AsSpan(), ReadOnlySpan<char>.Empty, graph);
            foreach (var (s, _, o) in instances)
            {
                if (s != o) // Avoid reflexive
                {
                    inferred.Add((o, prop, s));
                }
            }
        }
    }

    /// <summary>
    /// OWL inverse property inference:
    /// If P owl:inverseOf Q and A P B, then B Q A
    /// </summary>
    private void InferInverse(ReadOnlySpan<char> graph, List<(string, string, string)> inferred)
    {
        var inversePairs = GetAllTriples(ReadOnlySpan<char>.Empty, OwlInverseOf.AsSpan(), ReadOnlySpan<char>.Empty, graph);

        foreach (var (prop1, _, prop2) in inversePairs)
        {
            // P owl:inverseOf Q => for all (A P B), infer (B Q A)
            var instances = GetAllTriples(ReadOnlySpan<char>.Empty, prop1.AsSpan(), ReadOnlySpan<char>.Empty, graph);
            foreach (var (s, _, o) in instances)
            {
                inferred.Add((o, prop2, s));
            }

            // Also the reverse: for all (A Q B), infer (B P A)
            var reverseInstances = GetAllTriples(ReadOnlySpan<char>.Empty, prop2.AsSpan(), ReadOnlySpan<char>.Empty, graph);
            foreach (var (s, _, o) in reverseInstances)
            {
                inferred.Add((o, prop1, s));
            }
        }
    }

    /// <summary>
    /// OWL sameAs inference:
    /// If A owl:sameAs B, then anything said about A also applies to B
    /// </summary>
    private void InferSameAs(ReadOnlySpan<char> graph, List<(string, string, string)> inferred)
    {
        var sameAsPairs = GetAllTriples(ReadOnlySpan<char>.Empty, OwlSameAs.AsSpan(), ReadOnlySpan<char>.Empty, graph);

        foreach (var (a, _, b) in sameAsPairs)
        {
            // sameAs is symmetric
            inferred.Add((b, OwlSameAs, a));

            // Copy all triples where A is subject to B as subject
            var aAsSubject = GetAllTriples(a.AsSpan(), ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, graph);
            foreach (var (_, p, o) in aAsSubject)
            {
                if (p != OwlSameAs) // Don't copy sameAs statements
                {
                    inferred.Add((b, p, o));
                }
            }

            // Copy all triples where A is object to B as object
            var aAsObject = GetAllTriples(ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, a.AsSpan(), graph);
            foreach (var (s, p, _) in aAsObject)
            {
                if (p != OwlSameAs)
                {
                    inferred.Add((s, p, b));
                }
            }
        }
    }

    /// <summary>
    /// OWL equivalentClass inference:
    /// If A owl:equivalentClass B, then A rdfs:subClassOf B and B rdfs:subClassOf A
    /// </summary>
    private void InferEquivalentClass(ReadOnlySpan<char> graph, List<(string, string, string)> inferred)
    {
        var equivalentPairs = GetAllTriples(ReadOnlySpan<char>.Empty, OwlEquivalentClass.AsSpan(), ReadOnlySpan<char>.Empty, graph);

        foreach (var (classA, _, classB) in equivalentPairs)
        {
            // equivalentClass implies mutual subClassOf
            inferred.Add((classA, RdfsSubClassOf, classB));
            inferred.Add((classB, RdfsSubClassOf, classA));

            // equivalentClass is symmetric
            inferred.Add((classB, OwlEquivalentClass, classA));
        }
    }

    /// <summary>
    /// OWL equivalentProperty inference:
    /// If P owl:equivalentProperty Q, then P rdfs:subPropertyOf Q and Q rdfs:subPropertyOf P
    /// </summary>
    private void InferEquivalentProperty(ReadOnlySpan<char> graph, List<(string, string, string)> inferred)
    {
        var equivalentPairs = GetAllTriples(ReadOnlySpan<char>.Empty, OwlEquivalentProperty.AsSpan(), ReadOnlySpan<char>.Empty, graph);

        foreach (var (propA, _, propB) in equivalentPairs)
        {
            // equivalentProperty implies mutual subPropertyOf
            inferred.Add((propA, RdfsSubPropertyOf, propB));
            inferred.Add((propB, RdfsSubPropertyOf, propA));

            // equivalentProperty is symmetric
            inferred.Add((propB, OwlEquivalentProperty, propA));
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Get all triples matching a pattern.
    /// </summary>
    private List<(string s, string p, string o)> GetAllTriples(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        ReadOnlySpan<char> graph)
    {
        var result = new List<(string, string, string)>();

        var enumerator = _store.QueryCurrent(subject, predicate, obj, graph);
        try
        {
            while (enumerator.MoveNext())
            {
                var current = enumerator.Current;
                result.Add((current.Subject.ToString(), current.Predicate.ToString(), current.Object.ToString()));
            }
        }
        finally
        {
            enumerator.Dispose();
        }

        return result;
    }

    #endregion
}
