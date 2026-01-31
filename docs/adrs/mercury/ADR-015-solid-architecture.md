# ADR-015: Mercury.Solid Architecture

## Status

Accepted

## Context

Mercury needs to support the [W3C Solid Protocol](https://solidproject.org/TR/protocol) to enable personal online data stores (Pods) where users control their data via decentralized authentication and access control. Solid is built on RDF and SPARQL, making Mercury's existing capabilities a strong foundation.

### Key Requirements

1. **HTTP Resource Operations** - GET, PUT, PATCH, DELETE for RDF resources
2. **Container Semantics** - LDP (Linked Data Platform) containers for hierarchical data
3. **N3 Patch** - Solid's PATCH format for atomic graph modifications
4. **Access Control** - WAC (Web Access Control) and/or ACP (Access Control Policy)
5. **Authentication** - Solid-OIDC for decentralized identity (future phase)

### Existing Mercury Capabilities

| Capability | Status | Solid Requirement |
|-----------|--------|-------------------|
| RDF Storage (Quads) | Complete | Pods map to named graphs |
| SPARQL Query/Update | Complete | CRUD via SPARQL |
| RDF Formats | Complete | Content negotiation |
| HTTP Server | Partial (GET/POST) | Needs PUT/PATCH/DELETE |
| N3 Parser | Missing | Required for PATCH |
| Access Control | Missing | Required |

## Decision

### 1. Pod-to-Graph Mapping

Each Solid Pod maps to a QuadStore named graph:

```
Pod: https://alice.example.com/
  └─ Graph: <https://alice.example.com/>
      ├─ Resource: <https://alice.example.com/profile/card>
      └─ Container: <https://alice.example.com/inbox/>
```

**Rationale:**
- Leverages existing GSPO/GPOS/GOSP indexes for efficient per-pod queries
- Natural isolation between Pods
- Supports cross-Pod queries when authorized

### 2. HTTP Method Routing

Extend `SparqlHttpServer` pattern with resource-aware routing:

```csharp
switch (request.HttpMethod)
{
    case "GET": // Read resource
    case "PUT": // Create/replace
    case "PATCH": // Modify (N3 Patch)
    case "DELETE": // Remove
    case "POST": // Create in container
    case "HEAD": // Metadata only
}
```

**Design:** Each method has a dedicated handler class for separation of concerns.

### 3. N3 Patch Parser

N3 is a superset of Turtle with formulae `{ ... }` for graph patterns. Mercury's Turtle parser explicitly rejects N3 syntax, so we need a new parser for the Solid-specific subset:

```turtle
@prefix solid: <http://www.w3.org/ns/solid/terms#>.

_:patch a solid:InsertDeletePatch;
  solid:where   { ?person foaf:name ?n };
  solid:deletes { ?person foaf:mbox ?old };
  solid:inserts { ?person foaf:mbox <mailto:new@example.com> }.
```

**Implementation:**
- `N3PatchParser` - Parses `{ ... }` formula blocks with variables
- `N3Formula` - Represents graph patterns with variables
- `N3PatchExecutor` - Translates to DELETE/INSERT WHERE operations

**N3 Subset Required:**
- `{ triple1 . triple2 . }` - Formula blocks
- Variables `?x` bound across clauses
- NOT required: Rules `=>`, built-in functions

### 4. Access Control Abstraction

Support both WAC and ACP through a common interface:

```csharp
public interface IAccessPolicy
{
    ValueTask<AccessDecision> EvaluateAsync(
        string? agentWebId,
        string resourceUri,
        AccessMode requestedMode,
        CancellationToken ct = default);
}
```

**Modes:**
- `Read` - GET, HEAD
- `Write` - PUT, POST, PATCH, DELETE
- `Append` - POST only (subset of Write)
- `Control` - Modify ACL/ACP

**WAC Pattern:**
```turtle
<#auth> a acl:Authorization;
  acl:agent <webid:alice>;
  acl:accessTo <resource>;
  acl:mode acl:Read, acl:Write.
```

**ACP Pattern:**
```turtle
<#policy> a acp:Policy;
  acp:allow acp:Read, acp:Write;
  acp:allOf [ acp:agent <webid:alice> ].
```

### 5. Project Structure

```
src/Mercury.Solid/
├── SolidServer.cs              # HTTP server
├── Http/
│   ├── ResourceHandler.cs      # GET/PUT/DELETE
│   ├── PatchHandler.cs         # N3 Patch support
│   ├── ContainerHandler.cs     # LDP operations
│   └── LinkHeaderBuilder.cs    # Discovery headers
├── N3/
│   ├── N3Formula.cs            # Formula representation
│   ├── N3PatchParser.cs        # Parse N3 Patch
│   └── N3PatchExecutor.cs      # Apply patches
├── AccessControl/
│   ├── IAccessPolicy.cs        # Common interface
│   ├── AccessMode.cs           # Access modes
│   ├── AccessDecision.cs       # Decision result
│   ├── WebAccessControl.cs     # WAC implementation
│   └── AccessControlPolicy.cs  # ACP implementation
└── Models/
    ├── SolidResource.cs        # Resource model
    └── SolidContainer.cs       # LDP container
```

## Consequences

### Positive

1. **Reuses Mercury infrastructure** - Named graphs, SPARQL, RDF parsers
2. **Zero external dependencies** - Maintains Mercury sovereignty
3. **Pluggable authorization** - Easy to switch between WAC/ACP
4. **Standard-compliant** - Follows W3C Solid Protocol

### Negative

1. **N3 parser scope limited** - Only Solid subset, not full N3
2. **Authentication deferred** - Solid-OIDC requires token validation
3. **Container overhead** - LDP containment triples add storage

### Implementation Phases

| Phase | Component | Status |
|-------|-----------|--------|
| 1 | HTTP Methods (PUT/PATCH/DELETE) | Implemented |
| 2 | N3 Patch Parser | Implemented |
| 3 | Container Semantics (LDP) | Implemented |
| 4 | Access Control (WAC + ACP) | Implemented |
| 5 | Authentication (Solid-OIDC) | Planned |
| 6 | Notifications (WebSocket) | Planned |

## References

- [Solid Protocol](https://solidproject.org/TR/protocol)
- [Web Access Control](https://solidproject.org/TR/wac)
- [Access Control Policy](https://solidproject.org/TR/acp)
- [N3 Patch](https://solidproject.org/TR/n3-patch)
- [Linked Data Platform 1.0](https://www.w3.org/TR/ldp/)
- [Solid-OIDC](https://solidproject.org/TR/oidc)
