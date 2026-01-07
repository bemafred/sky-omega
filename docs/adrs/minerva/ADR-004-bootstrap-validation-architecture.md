# ADR-004: Bootstrap and Validation Architecture

## Status

Proposed

## Related ADRs

- [ADR-001: Weight and Tokenizer Format Support](ADR-001-weight-formats.md) - Model loading
- [ADR-002: Hardware Acceleration Strategy](ADR-002-hardware-acceleration-strategy.md) - Compute backends
- [ADR-003: Metal Compute Shader Architecture](ADR-003-metal-compute-architecture.md) - GPU inference

## Context

Sky Omega’s value proposition rests on Lucy – a semantically structured, epistemically grounded knowledge base. But Lucy must be populated. This ADR addresses the fundamental question: **How do we bootstrap Lucy with knowledge while maintaining epistemic integrity?**

### The Bootstrap Problem

An empty Lucy is useless. A Lucy filled with unvalidated content is dangerous. We need a process that:

1. Extracts knowledge from source material efficiently
1. Validates extracted knowledge before committing to Lucy
1. Maintains full provenance for every assertion
1. Learns from its own extraction quality over time
1. Operates within hardware and cost constraints

### Constraints

|Constraint         |Source                                 |Implication                                             |
|-——————|—————————————|———————————————————|
|Sovereign operation|Organizational requirements (VGR, etc.)|Cannot depend solely on external APIs for sensitive data|
|Epistemic integrity|EEE methodology                        |Every assertion must be traceable and falsifiable       |
|Hardware reality   |M4 Max 128 GB portable platform        |70B Q4 possible but tight; 405B requires API            |
|Cost awareness     |Practical deployment                   |API costs must be justified by value                    |
|Portability        |Demo and development needs             |Solution must run on laptop for workshops               |

## Decision

### Two-Phase Architecture

Sky Omega operates in two distinct phases with different resource profiles:

```
┌─────────────────────────────────────────────────────────────────┐
│                                                                 │
│  BOOTSTRAP PHASE                      OPERATIONAL PHASE         │
│  (Knowledge acquisition)              (Knowledge application)   │
│                                                                 │
│  ┌─────────────────────┐              ┌─────────────────────┐  │
│  │ Heavy inference     │              │ Light inference     │  │
│  │ 70B local + API     │              │ 8B-13B local        │  │
│  │ Long context        │    ────►     │ Lucy-augmented      │  │
│  │ Batch processing    │              │ Real-time response  │  │
│  │ Validation focus    │              │ Application focus   │  │
│  └─────────────────────┘              └─────────────────────┘  │
│                                                                 │
│  Triggered by:                        Triggered by:            │
│  • New domain onboarding              • User queries           │
│  • Document ingestion                 • Conversational flow    │
│  • Knowledge gap detection            • Routine operation      │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### Three-Layer Validation

Knowledge extraction uses a triangulation approach:

```
┌─────────────────────────────────────────────────────────────────┐
│                   EXTRACTION PIPELINE                           │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Source Material                                                │
│        │                                                        │
│        ├──────────────────┬──────────────────┐                  │
│        ▼                  ▼                  ▼                  │
│  ┌──────────┐      ┌──────────┐      ┌──────────┐              │
│  │ Layer 1  │      │ Layer 2  │      │ Layer 3  │              │
│  │ Local    │      │ Local    │      │ External │              │
│  │ 70B Q4   │      │ 8B-13B   │      │ API      │              │
│  │ Primary  │      │ Reference│      │ Frontier │              │
│  └────┬─────┘      └────┬─────┘      └────┬─────┘              │
│       │                 │                 │                     │
│       └─────────────────┼─────────────────┘                     │
│                         ▼                                       │
│              ┌─────────────────────┐                            │
│              │ Divergence Analysis │                            │
│              └──────────┬──────────┘                            │
│                         │                                       │
│       ┌─────────────────┼─────────────────┐                     │
│       ▼                 ▼                 ▼                     │
│  ┌─────────┐      ┌──────────┐      ┌──────────┐               │
│  │Consensus│      │Divergence│      │ Conflict │               │
│  │ ≥2 agree│      │ 1 differs│      │all differ│               │
│  └────┬────┘      └────┬─────┘      └────┬─────┘               │
│       │                │                 │                      │
│       ▼                ▼                 ▼                      │
│  ┌─────────┐      ┌──────────┐      ┌──────────┐               │
│  │  Lucy   │      │  Lucy    │      │ Escalate │               │
│  │ (high)  │      │ (flagged)│      │ (human)  │               │
│  └─────────┘      └──────────┘      └──────────┘               │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Layer Roles

|Layer        |Model         |Role                        |When Used                  |
|-————|—————|-—————————|—————————|
|**Primary**  |Local 70B Q4  |Main extraction, sovereign  |Always                     |
|**Reference**|Local 8B-13B  |Independent validation, fast|Always                     |
|**Frontier** |Claude/GPT API|Tiebreaker, complex cases   |On divergence or explicitly|

#### Divergence Categories

|Category         |Definition                                           |Action                                 |
|——————|——————————————————|—————————————|
|**Consensus**    |≥2 layers extract semantically equivalent triples    |Commit to Lucy with high confidence    |
|**Complementary**|Layers extract non-overlapping but compatible triples|Union with provenance, flag for review |
|**Refinement**   |One layer extracts more specific version             |Keep specific, log general as context  |
|**Divergence**   |Different but non-contradictory interpretations      |Both to Lucy with distinct provenance  |
|**Conflict**     |Contradictory assertions                             |Escalate to human review, do not commit|

### Provenance Model

Every triple in Lucy carries full extraction provenance:

```turtle
@prefix sky: <https://sky-omega.org/ontology#> .
@prefix xsd: <http://www.w3.org/2001/XMLSchema#> .
@prefix prov: <http://www.w3.org/ns/prov#> .

# The extracted statement (reified for provenance attachment)
sky:stmt_20250107_001 a rdf:Statement ;
    rdf:subject sky:Mercury ;
    rdf:predicate sky:implements ;
    rdf:object sky:SPARQL_1_1 ;
    
    # Confidence and status
    sky:confidence 0.95 ;
    sky:validationStatus sky:Consensus ;
    
    # Primary extraction
    sky:extractedBy [
        a sky:ExtractionEvent ;
        sky:model ”llama-3.1-70b-q4” ;
        sky:backend ”local-minerva” ;
        sky:timestamp ”2025-01-07T14:30:00Z”^^xsd:dateTime ;
        sky:sourceDocument sky:doc_mercury_readme ;
        sky:sourceSpan ”lines 45-52” ;
        sky:rawExtraction ”Mercury implements SPARQL 1.1 query language”
    ] ;
    
    # Validation
    sky:validatedBy [
        a sky:ValidationEvent ;
        sky:model ”llama-3.1-8b-q8” ;
        sky:backend ”local-minerva” ;
        sky:timestamp ”2025-01-07T14:30:15Z”^^xsd:dateTime ;
        sky:agreement sky:Confirmed ;
        sky:extractedTriple sky:stmt_20250107_001  # Same triple extracted
    ] ;
    
    # Optional: API cross-check
    sky:crossCheckedBy [
        a sky:ValidationEvent ;
        sky:model ”claude-sonnet-4-20250514” ;
        sky:backend ”anthropic-api” ;
        sky:timestamp ”2025-01-07T14:30:45Z”^^xsd:dateTime ;
        sky:agreement sky:Confirmed
    ] .

# Divergence example
sky:stmt_20250107_002 a rdf:Statement ;
    rdf:subject sky:Mercury ;
    rdf:predicate sky:supports ;
    rdf:object sky:FederatedQueries ;
    
    sky:confidence 0.72 ;
    sky:validationStatus sky:Divergent ;
    
    sky:extractedBy [
        sky:model ”llama-3.1-70b-q4” ;
        sky:rawExtraction ”Mercury supports SERVICE clause for federation”
    ] ;
    
    sky:divergentExtraction [
        sky:model ”llama-3.1-8b-q8” ;
        sky:rawExtraction ”Mercury federation support is planned but not implemented” ;
        sky:divergenceType sky:TemporalDisagreement ;
        sky:resolution sky:PendingHumanReview
    ] .
```

### Adaptive Routing

The system learns which extraction path works best for different domains:

```csharp
/// <summary>
/// Routes extraction requests based on historical performance per domain.
/// Implements learning loop: measure → adapt → improve.
/// </summary>
public sealed class ExtractionRouter
{
    private readonly DomainMetrics _metrics;
    
    public ExtractionStrategy Route(Document document)
    {
        var domain = _metrics.ClassifyDomain(document);
        var history = _metrics.GetHistoryFor(domain);
        
        // High consensus domain: trust local, sample API
        if (history.ConsensusRate > 0.95 && history.SampleSize > 100)
        {
            return new LocalPrimaryWithSampling(
                primaryModel: Model.Local70B,
                referenceModel: Model.Local8B,
                apiSampleRate: 0.05  // 5% spot-check
            );
        }
        
        // API adds significant value in this domain
        if (history.ApiEnrichmentRate > 0.25)
        {
            return new ApiEnrichedExtraction(
                primaryModel: Model.LocalAPI,  // Claude/GPT
                validationModel: Model.Local70B,
                referenceModel: Model.Local8B
            );
        }
        
        // High conflict domain: full triangulation + human review
        if (history.ConflictRate > 0.10)
        {
            return new FullTriangulationWithReview(
                requireHumanApproval: true,
                escalationThreshold: 0.7
            );
        }
        
        // Default: standard three-layer validation
        return new StandardTriangulation(
            primaryModel: Model.Local70B,
            referenceModel: Model.Local8B,
            tiebreaker: Model.API
        );
    }
}

/// <summary>
/// Metrics accumulated per domain over extraction history.
/// Enables system to learn its own reliability patterns.
/// </summary>
public sealed class DomainMetrics
{
    // Agreement rate between primary and reference
    public double ConsensusRate { get; }
    
    // How often API finds triples local models missed
    public double ApiEnrichmentRate { get; }
    
    // How often local finds triples API missed
    public double LocalEnrichmentRate { get; }
    
    // Contradictions requiring human resolution
    public double ConflictRate { get; }
    
    // Semantic similarity when extractions differ
    public double DivergenceSimilarity { get; }
    
    // Number of extractions in this domain
    public int SampleSize { get; }
}
```

### Extraction Metrics Dashboard

```csharp
/// <summary>
/// Queryable via SPARQL for transparency and debugging.
/// </summary>
public sealed class ExtractionMetricsStore
{
    public void RecordExtraction(ExtractionResult result)
    {
        // Store as RDF for queryability
        var triples = new List<Triple>
        {
            // Extraction event
            Triple(result.Id, RDF.Type, Sky.ExtractionEvent),
            Triple(result.Id, Sky.Timestamp, result.Timestamp),
            Triple(result.Id, Sky.Domain, result.Domain),
            Triple(result.Id, Sky.SourceDocument, result.SourceId),
            
            // Layer results
            Triple(result.Id, Sky.PrimaryExtracted, result.PrimaryCount),
            Triple(result.Id, Sky.ReferenceExtracted, result.ReferenceCount),
            Triple(result.Id, Sky.ApiExtracted, result.ApiCount),
            
            // Outcomes
            Triple(result.Id, Sky.ConsensusCount, result.Consensus),
            Triple(result.Id, Sky.DivergenceCount, result.Divergent),
            Triple(result.Id, Sky.ConflictCount, result.Conflicts),
            
            // Timing
            Triple(result.Id, Sky.PrimaryDuration, result.PrimaryMs),
            Triple(result.Id, Sky.ReferenceDuration, result.ReferenceMs),
            Triple(result.Id, Sky.ApiDuration, result.ApiMs),
            Triple(result.Id, Sky.ApiCost, result.ApiCostUsd)
        };
        
        _lucy.Assert(triples);
    }
}
```

Example SPARQL query for extraction quality:

```sparql
PREFIX sky: <https://sky-omega.org/ontology#>

SELECT ?domain 
       (AVG(?consensus / ?total) AS ?consensusRate)
       (AVG(?conflicts / ?total) AS ?conflictRate)
       (SUM(?apiCost) AS ?totalApiCost)
WHERE {
    ?extraction a sky:ExtractionEvent ;
        sky:domain ?domain ;
        sky:consensusCount ?consensus ;
        sky:conflictCount ?conflicts ;
        sky:apiCost ?apiCost .
    
    BIND(?consensus + ?divergence + ?conflicts AS ?total)
}
GROUP BY ?domain
ORDER BY DESC(?conflictRate)
```

## Hardware Strategy

### Target Configuration: MacBook Pro + DGX Spark Cluster

The recommended development and deployment topology combines Apple Silicon portability with NVIDIA Blackwell cluster capability:

```
┌─────────────────────────────────────────────────────────────────┐
│                    PRIMARY DEVELOPMENT RIG                      │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌─────────────────────┐                                       │
│  │  MacBook Pro M4 Max │                                       │
│  │  128 GB / 8 TB      │                                       │
│  ├─────────────────────┤                                       │
│  │ • Development (Rider, VS)                                   │
│  │ • Demo/portable presentations                               │
│  │ • Metal-backend validation                                  │
│  │ • 8B-70B local inference                                    │
│  │ • Lucy client/replica                                       │
│  └──────────┬──────────┘                                       │
│             │                                                   │
│             │ 10 GbE / Thunderbolt                              │
│             │                                                   │
│  ┌──────────┴──────────────────────────────────────────┐       │
│  │              DGX SPARK CLUSTER                       │       │
│  │                                                      │       │
│  │   ┌─────────────┐  200 Gbps   ┌─────────────┐       │       │
│  │   │  DGX Spark  │◄───────────►│  DGX Spark  │       │       │
│  │   │   128 GB    │    QSFP     │   128 GB    │       │       │
│  │   │   Node 1    │             │   Node 2    │       │       │
│  │   └─────────────┘             └─────────────┘       │       │
│  │                                                      │       │
│  │   Combined: 256 GB unified, Blackwell tensor cores  │       │
│  │                                                      │       │
│  ├──────────────────────────────────────────────────────┤       │
│  │ • Lucy master node                                   │       │
│  │ • Bootstrap engine (405B Q4 capable!)                │       │
│  │ • CUDA-backend validation                            │       │
│  │ • Always-on inference server                         │       │
│  │ • Primary extraction for three-layer validation      │       │
│  └──────────────────────────────────────────────────────┘       │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### Why This Topology

|Aspect              |MacBook + 2× Spark  |Mac Pro M4 Ultra (alternative)|
|———————|———————|——————————|
|**Total memory**    |128 + 256 = 384 GB  |256 GB                        |
|**405B Q4**         |✓ (on Spark cluster)|⚠️ Tight                       |
|**Parallel 70B**    |✓✓                  |✓                             |
|**Portability**     |✓ MacBook           |✗                             |
|**CUDA validation** |✓                   |✗                             |
|**Metal validation**|✓                   |✓                             |
|**Estimated cost**  |~165,000 SEK        |~200,000+ SEK                 |
|**Redundancy**      |Two separate systems|Single machine                |

### DGX Spark Specifications

NVIDIA DGX Spark (GB10 Grace Blackwell Superchip):

|Specification  |Value                                          |
|—————|————————————————|
|CPU            |20-core ARM (10× Cortex-X925 + 10× Cortex-A725)|
|GPU            |Blackwell, 5th gen Tensor Cores                |
|Memory         |128 GB unified (coherent CPU+GPU)              |
|AI Performance |1 PFLOP FP4, 1000 TOPS                         |
|Network        |2× QSFP (200 Gbps), 10 GbE, ConnectX-7         |
|Cluster        |Two units connect via QSFP for 256 GB combined |
|Form factor    |Compact desktop                                |
|Estimated price|~$3,000 USD per unit                           |

### Role Distribution

|Task                 |MacBook Pro|DGX Spark ×2    |
|———————|————|-—————|
|Development (IDE)    |✓ Primary  |                |
|On-site demo         |✓          |                |
|8B-13B fast iteration|✓          |                |
|70B validation       |✓ (tight)  |✓✓ (comfortable)|
|405B bootstrap       |           |✓ (exclusive)   |
|Lucy master          |           |✓               |
|CUDA backend test    |           |✓               |
|Metal backend test   |✓          |                |
|Always-on API        |           |✓               |

### Bootstrap Pipeline with 405B Capability

```
┌─────────────────────────────────────────────────────────────┐
│           THREE-LAYER VALIDATION + 405B                     │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  Source ──────┬────► [Spark: 405B Q4] ───► Candidates ──┐  │
│  Document     │        (frontier extraction)             │  │
│               │                                          │  │
│               ├────► [Spark: 70B Q4] ────► Reference 1 ──┤  │
│               │        (primary validation)              │  │
│               │                                          │  │
│               └────► [MacBook: 8B-13B] ──► Reference 2 ──┤  │
│                        (fast cross-check)                │  │
│                                                          │  │
│                              ┌───────────────────────────┘  │
│                              ▼                              │
│                    [Divergence Analysis]                    │
│                              │                              │
│                              ▼                              │
│                      Lucy (on Spark cluster)                │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

With 405B as primary extractor, the system achieves sovereign frontier-class capability without external API dependency.

### Cost Estimate

|Component                   |Estimated Price (SEK)|
|-—————————|———————|
|MacBook Pro M4 Max 128GB/8TB|~105,000             |
|DGX Spark ×2                |~60,000              |
|10 GbE switch + cables      |~5,000               |
|**Total**                   |**~170,000**         |

This compares favorably to:

- Mac Pro M2 Ultra alone: ~175,000 SEK (less capability)
- Mac Pro M4 Ultra projected: ~200,000+ SEK (no CUDA validation)

### Alternative: Portable-Only Configuration

For initial development or budget constraints, MacBook Pro M4 Max alone is viable:

|Capability     |Feasibility|Notes                               |
|—————|————|————————————|
|Local 70B Q4   |✓ Possible |~55 GB, 32K context, tight but works|
|Local 8B Q8    |✓ Easy     |~9 GB, runs alongside               |
|Bootstrap batch|✓ Works    |API supplements for 405B capability |
|Demo/workshop  |✓ Ideal    |Portable proof of concept           |
|Development    |✓ Excellent|Full stack locally                  |

The Spark cluster can be added later when budget allows or organizational deployment requires it.

### Organizational Deployment: VGR Example

For customer deployments requiring sovereign infrastructure:

```
┌─────────────────────────────────────────────────────────────┐
│              VGR SOVEREIGN DEPLOYMENT OPTIONS               │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  Option A: DGX Spark Cluster (Budget-conscious)             │
│  ┌─────────────┐  ┌─────────────┐                          │
│  │ DGX Spark 1 │──│ DGX Spark 2 │  ~60,000 SEK             │
│  └─────────────┘  └─────────────┘                          │
│  • 256 GB unified, 405B Q4 capable                         │
│  • NVIDIA stack, enterprise support available              │
│  • Compact, quiet, low power                               │
│                                                             │
│  Option B: Mac Studio M4 Ultra (When available)             │
│  ┌─────────────────────────────┐                           │
│  │     Mac Studio M4 Ultra     │  ~150,000 SEK?            │
│  └─────────────────────────────┘                           │
│  • 256 GB unified, 70B comfortable, 405B tight             │
│  • macOS simplicity                                        │
│  • Single unit, minimal setup                              │
│                                                             │
│  Option C: DGX Station (Premium)                            │
│  ┌─────────────────────────────┐                           │
│  │       DGX Station GB300     │  ~1,500,000 SEK           │
│  └─────────────────────────────┘                           │
│  • 784 GB coherent memory                                  │
│  • 405B FP16, training capable                             │
│  • Enterprise-grade                                        │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

### Cost-Benefit: API vs Local

```
Bootstrap scenario: 1000 documents, 10K tokens average

API-only (Claude):
  Input:  1000 × 10K × 3 passes = 30M tokens × $3/M = $90
  Output: 1000 × 2K × 3 passes  = 6M tokens × $15/M = $90
  Total: ~$180 per bootstrap cycle
  
  10 iterations for quality: ~$1,800
  
Local 70B (M4 Max):
  Hardware: 105,000 SEK (one-time)
  Power: negligible
  Time: slower, but unlimited iterations
  
Break-even: ~60 full bootstrap cycles

But the real value isn’t cost – it’s:
  • Sovereignty (no data leaves premises)
  • Availability (no API dependency)
  • Validation (local reference always available)
  • Iteration (unlimited refinement at zero marginal cost)
```

## Implementation

### Phase 1: Single-Layer Foundation

- [ ] Basic extraction pipeline with local 8B
- [ ] Provenance model in Lucy
- [ ] Simple confidence scoring
- [ ] SPARQL queries for extraction metrics

### Phase 2: Two-Layer Validation

- [ ] Add local 70B as primary extractor
- [ ] Divergence detection between 70B and 8B
- [ ] Consensus/divergence/conflict classification
- [ ] Flagging system for human review

### Phase 3: Three-Layer Triangulation

- [ ] API integration (Claude) for tiebreaker
- [ ] Cost tracking and budgeting
- [ ] Adaptive routing based on domain history
- [ ] Learning loop for extraction strategy

### Phase 4: Operational Maturity

- [ ] Lucy replication between machines
- [ ] Batch bootstrap pipeline
- [ ] Quality dashboard (SPARQL-queryable)
- [ ] Domain-specific extraction tuning

## Consequences

### Benefits

- **Epistemic integrity**: Every assertion traceable to source and validation
- **Self-improving**: System learns which paths work for which domains
- **Sovereign capable**: Full operation without external APIs when required
- **Cost-aware**: API used strategically, not by default
- **Portable**: Demo-able on laptop, scalable to server
- **Transparent**: All metrics queryable via SPARQL

### Costs

- **Complexity**: Three-layer validation is more complex than single extraction
- **Storage**: Provenance metadata adds ~30% to triple storage
- **Latency**: Validation adds time to bootstrap (acceptable for batch)
- **Hardware**: 70B local requires significant investment

### Risks and Mitigations

|Risk                            |Mitigation                                             |
|———————————|-——————————————————|
|70B and 8B agree on wrong answer|API spot-checking; human review for high-stakes domains|
|API changes behavior over time  |Local reference provides stable baseline               |
|Provenance bloat                |Archival policy for old extraction metadata            |
|Over-reliance on consensus      |Explicit handling of novel/uncertain domains           |

## Organizational Deployment Implications

For organizations like VGR deploying Sky Omega:

```
┌─────────────────────────────────────────────────────────────┐
│              ORGANIZATIONAL DEPLOYMENT                       │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  Regulatory requirement: Patient data never leaves premises │
│                                                             │
│  Therefore:                                                 │
│    • API layer DISABLED for patient-related extraction      │
│    • Local 70B + 8B validation only                         │
│    • Higher conflict rate → more human review               │
│    • Acceptable trade-off for data sovereignty              │
│                                                             │
│  Non-sensitive domains (policies, procedures, guidelines):  │
│    • API layer ENABLED for quality                          │
│    • Full three-layer triangulation                         │
│    • Faster bootstrap, higher confidence                    │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

The architecture explicitly supports **domain-specific API policies** – sovereignty where required, capability where permitted.

## Success Criteria

- [ ] Consensus rate ≥ 85% between primary and reference on test corpus
- [ ] Conflict rate ≤ 5% requiring human escalation
- [ ] Full provenance queryable for any triple in Lucy
- [ ] Extraction strategy adapts based on measured domain performance
- [ ] System operates fully offline when API disabled
- [ ] Bootstrap of 1000-document corpus completes in < 24 hours on M4 Max

## References

### Sky Omega Architecture

- Lucy: Semantic memory substrate
- James: Orchestration and routing
- EEE Methodology: Emergence → Epistemics → Engineering

### External

- PROV-O: W3C Provenance Ontology (https://www.w3.org/TR/prov-o/)
- RDF Reification: Attaching metadata to statements
- Epistemic Logic: Formal treatment of knowledge and belief

## Appendix: Minerva Backend Strategy

The MacBook + DGX Spark topology validates both compute backends:

```csharp
IComputeBackend
    ├── CpuBackend (BCL intrinsics)      // Fallback, all platforms
    ├── AppleBackend (Metal P/Invoke)    // MacBook Pro - validated
    └── CudaBackend (CUDA P/Invoke)      // DGX Spark - validated
        └── BlackwellOptimizations       // FP4/FP8 tensor core paths
```

This ensures Minerva is production-ready on both Apple Silicon and NVIDIA platforms, enabling flexible organizational deployment based on customer infrastructure preferences.