using System;
using System.IO;
using SkyOmega.Mercury.Owl;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for OWL/RDFS reasoning.
/// </summary>
public class OwlReasonerTests : IDisposable
{
    private readonly string _testDir;
    private readonly QuadStore _store;

    // RDF/RDFS/OWL vocabulary
    private const string RdfType = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>";
    private const string RdfsSubClassOf = "<http://www.w3.org/2000/01/rdf-schema#subClassOf>";
    private const string RdfsSubPropertyOf = "<http://www.w3.org/2000/01/rdf-schema#subPropertyOf>";
    private const string RdfsDomain = "<http://www.w3.org/2000/01/rdf-schema#domain>";
    private const string RdfsRange = "<http://www.w3.org/2000/01/rdf-schema#range>";
    private const string OwlTransitiveProperty = "<http://www.w3.org/2002/07/owl#TransitiveProperty>";
    private const string OwlSymmetricProperty = "<http://www.w3.org/2002/07/owl#SymmetricProperty>";
    private const string OwlInverseOf = "<http://www.w3.org/2002/07/owl#inverseOf>";
    private const string OwlSameAs = "<http://www.w3.org/2002/07/owl#sameAs>";
    private const string OwlEquivalentClass = "<http://www.w3.org/2002/07/owl#equivalentClass>";
    private const string OwlEquivalentProperty = "<http://www.w3.org/2002/07/owl#equivalentProperty>";

    public OwlReasonerTests()
    {
        var tempPath = TempPath.Test("owl");
        tempPath.MarkOwnership();
        _testDir = tempPath;
        _store = new QuadStore(_testDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, recursive: true);
        }
    }

    #region RDFS SubClass Tests

    [Fact]
    public void RdfsSubClassOf_Transitivity_InfersIndirectSubClass()
    {
        // Animal > Mammal > Dog
        _store.AddCurrent("<http://ex.org/Dog>", RdfsSubClassOf, "<http://ex.org/Mammal>");
        _store.AddCurrent("<http://ex.org/Mammal>", RdfsSubClassOf, "<http://ex.org/Animal>");

        var reasoner = new OwlReasoner(_store, InferenceRules.RdfsSubClass);
        int inferred = reasoner.Materialize();

        Assert.True(inferred > 0);
        Assert.True(TripleExists("<http://ex.org/Dog>", RdfsSubClassOf, "<http://ex.org/Animal>"));
    }

    [Fact]
    public void RdfsSubClassOf_TypeInference_InfersSuperclassType()
    {
        // Dog subClassOf Mammal
        _store.AddCurrent("<http://ex.org/Dog>", RdfsSubClassOf, "<http://ex.org/Mammal>");
        // Fido is a Dog
        _store.AddCurrent("<http://ex.org/Fido>", RdfType, "<http://ex.org/Dog>");

        var reasoner = new OwlReasoner(_store, InferenceRules.RdfsSubClass);
        reasoner.Materialize();

        // Fido should also be a Mammal
        Assert.True(TripleExists("<http://ex.org/Fido>", RdfType, "<http://ex.org/Mammal>"));
    }

    [Fact]
    public void RdfsSubClassOf_ChainedTypeInference_InfersAllSuperclasses()
    {
        // Dog subClassOf Mammal subClassOf Animal
        _store.AddCurrent("<http://ex.org/Dog>", RdfsSubClassOf, "<http://ex.org/Mammal>");
        _store.AddCurrent("<http://ex.org/Mammal>", RdfsSubClassOf, "<http://ex.org/Animal>");
        // Fido is a Dog
        _store.AddCurrent("<http://ex.org/Fido>", RdfType, "<http://ex.org/Dog>");

        var reasoner = new OwlReasoner(_store, InferenceRules.RdfsSubClass);
        reasoner.Materialize();

        // Fido should be Dog, Mammal, and Animal
        Assert.True(TripleExists("<http://ex.org/Fido>", RdfType, "<http://ex.org/Mammal>"));
        Assert.True(TripleExists("<http://ex.org/Fido>", RdfType, "<http://ex.org/Animal>"));
    }

    #endregion

    #region RDFS SubProperty Tests

    [Fact]
    public void RdfsSubPropertyOf_Transitivity_InfersIndirectSubProperty()
    {
        // hasFather subPropertyOf hasParent subPropertyOf hasAncestor
        _store.AddCurrent("<http://ex.org/hasFather>", RdfsSubPropertyOf, "<http://ex.org/hasParent>");
        _store.AddCurrent("<http://ex.org/hasParent>", RdfsSubPropertyOf, "<http://ex.org/hasAncestor>");

        var reasoner = new OwlReasoner(_store, InferenceRules.RdfsSubProperty);
        reasoner.Materialize();

        Assert.True(TripleExists("<http://ex.org/hasFather>", RdfsSubPropertyOf, "<http://ex.org/hasAncestor>"));
    }

    [Fact]
    public void RdfsSubPropertyOf_PropertyInheritance_InfersSuperProperty()
    {
        // hasFather subPropertyOf hasParent
        _store.AddCurrent("<http://ex.org/hasFather>", RdfsSubPropertyOf, "<http://ex.org/hasParent>");
        // John hasFather Bob
        _store.AddCurrent("<http://ex.org/John>", "<http://ex.org/hasFather>", "<http://ex.org/Bob>");

        var reasoner = new OwlReasoner(_store, InferenceRules.RdfsSubProperty);
        reasoner.Materialize();

        // John should also hasParent Bob
        Assert.True(TripleExists("<http://ex.org/John>", "<http://ex.org/hasParent>", "<http://ex.org/Bob>"));
    }

    #endregion

    #region RDFS Domain/Range Tests

    [Fact]
    public void RdfsDomain_InfersSubjectType()
    {
        // teaches has domain Teacher
        _store.AddCurrent("<http://ex.org/teaches>", RdfsDomain, "<http://ex.org/Teacher>");
        // Alice teaches Math
        _store.AddCurrent("<http://ex.org/Alice>", "<http://ex.org/teaches>", "<http://ex.org/Math>");

        var reasoner = new OwlReasoner(_store, InferenceRules.RdfsDomain);
        reasoner.Materialize();

        // Alice should be a Teacher
        Assert.True(TripleExists("<http://ex.org/Alice>", RdfType, "<http://ex.org/Teacher>"));
    }

    [Fact]
    public void RdfsRange_InfersObjectType()
    {
        // teaches has range Subject
        _store.AddCurrent("<http://ex.org/teaches>", RdfsRange, "<http://ex.org/Subject>");
        // Alice teaches Math
        _store.AddCurrent("<http://ex.org/Alice>", "<http://ex.org/teaches>", "<http://ex.org/Math>");

        var reasoner = new OwlReasoner(_store, InferenceRules.RdfsRange);
        reasoner.Materialize();

        // Math should be a Subject
        Assert.True(TripleExists("<http://ex.org/Math>", RdfType, "<http://ex.org/Subject>"));
    }

    [Fact]
    public void RdfsRange_SkipsLiterals()
    {
        // age has range Integer
        _store.AddCurrent("<http://ex.org/age>", RdfsRange, "<http://www.w3.org/2001/XMLSchema#integer>");
        // Alice age 30 (literal)
        _store.AddCurrent("<http://ex.org/Alice>", "<http://ex.org/age>", "\"30\"^^<http://www.w3.org/2001/XMLSchema#integer>");

        var reasoner = new OwlReasoner(_store, InferenceRules.RdfsRange);
        int inferred = reasoner.Materialize();

        // Should not infer type for literal
        Assert.Equal(0, inferred);
    }

    #endregion

    #region OWL Transitive Property Tests

    [Fact]
    public void OwlTransitive_InfersTransitiveClosure()
    {
        // ancestor is transitive
        _store.AddCurrent("<http://ex.org/ancestor>", RdfType, OwlTransitiveProperty);
        // A ancestor B, B ancestor C
        _store.AddCurrent("<http://ex.org/A>", "<http://ex.org/ancestor>", "<http://ex.org/B>");
        _store.AddCurrent("<http://ex.org/B>", "<http://ex.org/ancestor>", "<http://ex.org/C>");

        var reasoner = new OwlReasoner(_store, InferenceRules.OwlTransitive);
        reasoner.Materialize();

        // A should be ancestor of C
        Assert.True(TripleExists("<http://ex.org/A>", "<http://ex.org/ancestor>", "<http://ex.org/C>"));
    }

    [Fact]
    public void OwlTransitive_LongChain_InfersAllConnections()
    {
        // partOf is transitive
        _store.AddCurrent("<http://ex.org/partOf>", RdfType, OwlTransitiveProperty);
        // A partOf B partOf C partOf D
        _store.AddCurrent("<http://ex.org/A>", "<http://ex.org/partOf>", "<http://ex.org/B>");
        _store.AddCurrent("<http://ex.org/B>", "<http://ex.org/partOf>", "<http://ex.org/C>");
        _store.AddCurrent("<http://ex.org/C>", "<http://ex.org/partOf>", "<http://ex.org/D>");

        var reasoner = new OwlReasoner(_store, InferenceRules.OwlTransitive);
        reasoner.Materialize();

        // Should infer all transitive connections
        Assert.True(TripleExists("<http://ex.org/A>", "<http://ex.org/partOf>", "<http://ex.org/C>"));
        Assert.True(TripleExists("<http://ex.org/A>", "<http://ex.org/partOf>", "<http://ex.org/D>"));
        Assert.True(TripleExists("<http://ex.org/B>", "<http://ex.org/partOf>", "<http://ex.org/D>"));
    }

    #endregion

    #region OWL Symmetric Property Tests

    [Fact]
    public void OwlSymmetric_InfersReverseTriple()
    {
        // knows is symmetric
        _store.AddCurrent("<http://ex.org/knows>", RdfType, OwlSymmetricProperty);
        // Alice knows Bob
        _store.AddCurrent("<http://ex.org/Alice>", "<http://ex.org/knows>", "<http://ex.org/Bob>");

        var reasoner = new OwlReasoner(_store, InferenceRules.OwlSymmetric);
        reasoner.Materialize();

        // Bob should also know Alice
        Assert.True(TripleExists("<http://ex.org/Bob>", "<http://ex.org/knows>", "<http://ex.org/Alice>"));
    }

    [Fact]
    public void OwlSymmetric_MultipleStatements_InfersAll()
    {
        // sibling is symmetric
        _store.AddCurrent("<http://ex.org/sibling>", RdfType, OwlSymmetricProperty);
        _store.AddCurrent("<http://ex.org/Alice>", "<http://ex.org/sibling>", "<http://ex.org/Bob>");
        _store.AddCurrent("<http://ex.org/Bob>", "<http://ex.org/sibling>", "<http://ex.org/Carol>");

        var reasoner = new OwlReasoner(_store, InferenceRules.OwlSymmetric);
        reasoner.Materialize();

        Assert.True(TripleExists("<http://ex.org/Bob>", "<http://ex.org/sibling>", "<http://ex.org/Alice>"));
        Assert.True(TripleExists("<http://ex.org/Carol>", "<http://ex.org/sibling>", "<http://ex.org/Bob>"));
    }

    #endregion

    #region OWL Inverse Property Tests

    [Fact]
    public void OwlInverse_InfersInverseTriple()
    {
        // hasChild inverseOf hasParent
        _store.AddCurrent("<http://ex.org/hasChild>", OwlInverseOf, "<http://ex.org/hasParent>");
        // Alice hasChild Bob
        _store.AddCurrent("<http://ex.org/Alice>", "<http://ex.org/hasChild>", "<http://ex.org/Bob>");

        var reasoner = new OwlReasoner(_store, InferenceRules.OwlInverse);
        reasoner.Materialize();

        // Bob should hasParent Alice
        Assert.True(TripleExists("<http://ex.org/Bob>", "<http://ex.org/hasParent>", "<http://ex.org/Alice>"));
    }

    [Fact]
    public void OwlInverse_BidirectionalInference()
    {
        // employs inverseOf worksFor
        _store.AddCurrent("<http://ex.org/employs>", OwlInverseOf, "<http://ex.org/worksFor>");
        // Acme employs Alice
        _store.AddCurrent("<http://ex.org/Acme>", "<http://ex.org/employs>", "<http://ex.org/Alice>");
        // Bob worksFor Acme
        _store.AddCurrent("<http://ex.org/Bob>", "<http://ex.org/worksFor>", "<http://ex.org/Acme>");

        var reasoner = new OwlReasoner(_store, InferenceRules.OwlInverse);
        reasoner.Materialize();

        // Alice should worksFor Acme
        Assert.True(TripleExists("<http://ex.org/Alice>", "<http://ex.org/worksFor>", "<http://ex.org/Acme>"));
        // Acme should employ Bob
        Assert.True(TripleExists("<http://ex.org/Acme>", "<http://ex.org/employs>", "<http://ex.org/Bob>"));
    }

    #endregion

    #region OWL SameAs Tests

    [Fact]
    public void OwlSameAs_IsSymmetric()
    {
        _store.AddCurrent("<http://ex.org/Alice>", OwlSameAs, "<http://ex.org/AliceSmith>");

        var reasoner = new OwlReasoner(_store, InferenceRules.OwlSameAs);
        reasoner.Materialize();

        Assert.True(TripleExists("<http://ex.org/AliceSmith>", OwlSameAs, "<http://ex.org/Alice>"));
    }

    [Fact]
    public void OwlSameAs_CopiesTriples()
    {
        // Alice sameAs AliceSmith
        _store.AddCurrent("<http://ex.org/Alice>", OwlSameAs, "<http://ex.org/AliceSmith>");
        // Alice knows Bob
        _store.AddCurrent("<http://ex.org/Alice>", "<http://ex.org/knows>", "<http://ex.org/Bob>");
        // Carol knows Alice
        _store.AddCurrent("<http://ex.org/Carol>", "<http://ex.org/knows>", "<http://ex.org/Alice>");

        var reasoner = new OwlReasoner(_store, InferenceRules.OwlSameAs);
        reasoner.Materialize();

        // AliceSmith should also know Bob
        Assert.True(TripleExists("<http://ex.org/AliceSmith>", "<http://ex.org/knows>", "<http://ex.org/Bob>"));
        // Carol should also know AliceSmith
        Assert.True(TripleExists("<http://ex.org/Carol>", "<http://ex.org/knows>", "<http://ex.org/AliceSmith>"));
    }

    #endregion

    #region OWL Equivalent Class/Property Tests

    [Fact]
    public void OwlEquivalentClass_InfersMutualSubClass()
    {
        _store.AddCurrent("<http://ex.org/Person>", OwlEquivalentClass, "<http://ex.org/Human>");

        var reasoner = new OwlReasoner(_store, InferenceRules.OwlEquivalentClass);
        reasoner.Materialize();

        Assert.True(TripleExists("<http://ex.org/Person>", RdfsSubClassOf, "<http://ex.org/Human>"));
        Assert.True(TripleExists("<http://ex.org/Human>", RdfsSubClassOf, "<http://ex.org/Person>"));
    }

    [Fact]
    public void OwlEquivalentClass_IsSymmetric()
    {
        _store.AddCurrent("<http://ex.org/Person>", OwlEquivalentClass, "<http://ex.org/Human>");

        var reasoner = new OwlReasoner(_store, InferenceRules.OwlEquivalentClass);
        reasoner.Materialize();

        Assert.True(TripleExists("<http://ex.org/Human>", OwlEquivalentClass, "<http://ex.org/Person>"));
    }

    [Fact]
    public void OwlEquivalentProperty_InfersMutualSubProperty()
    {
        _store.AddCurrent("<http://ex.org/author>", OwlEquivalentProperty, "<http://ex.org/creator>");

        var reasoner = new OwlReasoner(_store, InferenceRules.OwlEquivalentProperty);
        reasoner.Materialize();

        Assert.True(TripleExists("<http://ex.org/author>", RdfsSubPropertyOf, "<http://ex.org/creator>"));
        Assert.True(TripleExists("<http://ex.org/creator>", RdfsSubPropertyOf, "<http://ex.org/author>"));
    }

    #endregion

    #region Combined Rules Tests

    [Fact]
    public void AllRdfs_CombinesRules()
    {
        // Setup ontology
        _store.AddCurrent("<http://ex.org/Dog>", RdfsSubClassOf, "<http://ex.org/Mammal>");
        _store.AddCurrent("<http://ex.org/hasPet>", RdfsDomain, "<http://ex.org/Person>");
        _store.AddCurrent("<http://ex.org/hasPet>", RdfsRange, "<http://ex.org/Animal>");
        _store.AddCurrent("<http://ex.org/Mammal>", RdfsSubClassOf, "<http://ex.org/Animal>");

        // Data
        _store.AddCurrent("<http://ex.org/Alice>", "<http://ex.org/hasPet>", "<http://ex.org/Fido>");
        _store.AddCurrent("<http://ex.org/Fido>", RdfType, "<http://ex.org/Dog>");

        var reasoner = new OwlReasoner(_store, InferenceRules.AllRdfs);
        reasoner.Materialize();

        // Alice should be a Person (domain inference)
        Assert.True(TripleExists("<http://ex.org/Alice>", RdfType, "<http://ex.org/Person>"));
        // Fido should be an Animal (range inference)
        Assert.True(TripleExists("<http://ex.org/Fido>", RdfType, "<http://ex.org/Animal>"));
        // Fido should be a Mammal (subclass type inference)
        Assert.True(TripleExists("<http://ex.org/Fido>", RdfType, "<http://ex.org/Mammal>"));
    }

    [Fact]
    public void AllRules_ComplexOntology()
    {
        // Class hierarchy
        _store.AddCurrent("<http://ex.org/Dog>", RdfsSubClassOf, "<http://ex.org/Mammal>");
        _store.AddCurrent("<http://ex.org/Mammal>", RdfsSubClassOf, "<http://ex.org/Animal>");

        // Symmetric property
        _store.AddCurrent("<http://ex.org/knows>", RdfType, OwlSymmetricProperty);

        // Inverse properties
        _store.AddCurrent("<http://ex.org/hasOwner>", OwlInverseOf, "<http://ex.org/owns>");

        // Data
        _store.AddCurrent("<http://ex.org/Fido>", RdfType, "<http://ex.org/Dog>");
        _store.AddCurrent("<http://ex.org/Alice>", "<http://ex.org/knows>", "<http://ex.org/Bob>");
        _store.AddCurrent("<http://ex.org/Fido>", "<http://ex.org/hasOwner>", "<http://ex.org/Alice>");

        var reasoner = new OwlReasoner(_store, InferenceRules.All);
        reasoner.Materialize();

        // Fido should be Animal
        Assert.True(TripleExists("<http://ex.org/Fido>", RdfType, "<http://ex.org/Animal>"));
        // Bob should know Alice
        Assert.True(TripleExists("<http://ex.org/Bob>", "<http://ex.org/knows>", "<http://ex.org/Alice>"));
        // Alice should own Fido
        Assert.True(TripleExists("<http://ex.org/Alice>", "<http://ex.org/owns>", "<http://ex.org/Fido>"));
    }

    [Fact]
    public void Materialize_ReturnsInferredCount()
    {
        _store.AddCurrent("<http://ex.org/knows>", RdfType, OwlSymmetricProperty);
        _store.AddCurrent("<http://ex.org/Alice>", "<http://ex.org/knows>", "<http://ex.org/Bob>");

        var reasoner = new OwlReasoner(_store, InferenceRules.OwlSymmetric);
        int inferred = reasoner.Materialize();

        Assert.True(inferred >= 1);
    }

    [Fact]
    public void Materialize_FixedPoint_StopsWhenNoNewFacts()
    {
        _store.AddCurrent("<http://ex.org/A>", RdfsSubClassOf, "<http://ex.org/B>");
        _store.AddCurrent("<http://ex.org/B>", RdfsSubClassOf, "<http://ex.org/C>");
        _store.AddCurrent("<http://ex.org/C>", RdfsSubClassOf, "<http://ex.org/D>");

        var reasoner = new OwlReasoner(_store, InferenceRules.RdfsSubClass);
        int inferred = reasoner.Materialize(maxIterations: 100);

        // Should complete and infer transitive subclass relations
        Assert.True(inferred > 0);
        Assert.True(TripleExists("<http://ex.org/A>", RdfsSubClassOf, "<http://ex.org/D>"));
    }

    #endregion

    #region Helper Methods

    private bool TripleExists(string subject, string predicate, string obj)
    {
        _store.AcquireReadLock();
        try
        {
            var results = _store.QueryCurrent(subject.AsSpan(), predicate.AsSpan(), obj.AsSpan());
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

    #endregion
}
